using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Watches for critical/warning incidents that remain Active (unacknowledged) past a
/// configurable threshold and re-notifies once via the tenant's channels.
/// Configure: Escalation:Enabled (default true), Escalation:ThresholdMinutes (default 15).
/// </summary>
public class AlertEscalationService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<AlertEscalationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!configuration.GetValue("Escalation:Enabled", true))
        {
            logger.LogInformation("Alert escalation is disabled.");
            return;
        }

        // Give other startup services time to settle
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Escalation check failed"); }

            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        int thresholdMinutes = configuration.GetValue("Escalation:ThresholdMinutes", 15);
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-thresholdMinutes);

        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("alert-escalation");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TelemetryDbContext>>();
        var notificationService = scope.ServiceProvider.GetRequiredService<NotificationService>();

        // One tenant at a time, outermost (docs/decomposition.md §4.0.1.2). This used to select every
        // unacknowledged incident in the installation and read `i.Cluster.TenantId` off each row — and
        // that tenant is who the escalation is then NOTIFIED AS. An incident whose ClusterId pointed at
        // another tenant's cluster would have re-notified that tenant's channels about an alert from
        // somewhere else. Asking per tenant makes the tenant an input, so the candidates can only be
        // the asking tenant's own.
        List<Guid> tenants;
        using (TelemetryDbContext db = dbFactory.CreateDbContext())
            tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);

        foreach (Guid tenantId in tenants)
        {
            // Load candidates: Active, un-escalated, past threshold, critical or warning
            List<IncidentRef> candidates;
            using (TelemetryDbContext db = dbFactory.CreateDbContext())
            {
            candidates = await DueFor(db, tenantId, cutoff)
                    .Select(i => new IncidentRef(i.Id, i.AlertName, i.Severity))
                    .ToListAsync(ct);
            }

            if (candidates.Count == 0) continue;

            logger.LogInformation("Escalating {Count} incident(s) unacknowledged for >{Minutes}m",
                candidates.Count, thresholdMinutes);

            foreach (IncidentRef c in candidates)
            {
                try
                {
                    int notified = await notificationService.ReNotifyAsync(c.Id, tenantId, ct);

                    using TelemetryDbContext db = dbFactory.CreateDbContext();
                    AlertIncident? incident = await db.AlertIncidents.FindAsync([c.Id], ct);
                    if (incident is null) continue;

                    incident.EscalatedAt = DateTime.UtcNow;
                    incident.UpdatedAt = DateTime.UtcNow;

                    db.IncidentNotes.Add(new IncidentNote
                    {
                        IncidentId = c.Id,
                        Author = "system",
                        Content = $"Auto-escalated: no acknowledgement after {thresholdMinutes} min. " +
                                  $"Re-notified {notified} channel{(notified == 1 ? "" : "s")}."
                    });

                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Escalated {Alert} ({Severity})", c.AlertName, c.Severity);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to escalate incident {Id}", c.Id);
                }
            }
        }
    }

    /// <summary>
    /// The incidents one tenant has left unacknowledged past the threshold — critical or warning,
    /// still Active, not escalated before.
    ///
    /// <para>Public and static so the scoping can be asserted without running the sweep: the whole
    /// point of the change that introduced it is that these are <em>one tenant's</em> incidents, and
    /// the previous version (every incident in the installation, tenant read off each row) would pass
    /// any test that only counted what came back. No <c>Include</c>: a navigation in a <c>Where</c>
    /// translates on its own.</para>
    /// </summary>
    public static IQueryable<AlertIncident> DueFor(TelemetryDbContext db, Guid tenantId, DateTime cutoff)
        => db.AlertIncidents
            .Where(i => i.Cluster.TenantId == tenantId
                     && i.Status == IncidentStatus.Active
                     && i.EscalatedAt == null
                     && i.StartsAt <= cutoff
                     && (i.Severity == "critical" || i.Severity == "warning"));

    /// <summary>
    /// One incident to escalate. It carries no tenant any more: the tenant is the loop's, and a record
    /// that carried its own would be the derived-from-the-row answer travelling on under a new name.
    /// </summary>
    private record IncidentRef(Guid Id, string AlertName, string Severity);
}
