using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

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
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var notificationService = scope.ServiceProvider.GetRequiredService<NotificationService>();

        // Load candidates: Active, un-escalated, past threshold, critical or warning
        List<IncidentRef> candidates;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            var rows = await db.AlertIncidents
                .Include(i => i.Cluster)
                .Where(i => i.Status == IncidentStatus.Active
                         && i.EscalatedAt == null
                         && i.StartsAt <= cutoff
                         && (i.Severity == "critical" || i.Severity == "warning"))
                .Select(i => new { i.Id, TenantId = i.Cluster.TenantId, i.AlertName, i.Severity })
                .ToListAsync(ct);

            candidates = rows.Select(r => new IncidentRef(r.Id, r.TenantId, r.AlertName, r.Severity)).ToList();
        }

        if (candidates.Count == 0) return;

        logger.LogInformation("Escalating {Count} incident(s) unacknowledged for >{Minutes}m",
            candidates.Count, thresholdMinutes);

        foreach (IncidentRef c in candidates)
        {
            try
            {
                int notified = await notificationService.ReNotifyAsync(c.Id, c.TenantId, ct);

                using ApplicationDbContext db = dbFactory.CreateDbContext();
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

    private record IncidentRef(Guid Id, Guid TenantId, string AlertName, string Severity);
}
