using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Dispatches firing/resolved incident notifications through a tenant's enabled channels, honoring active
/// maintenance windows. Used by the native telemetry alert evaluator (mirrors AlertSyncService's dispatch,
/// minus per-rule routing — each channel self-filters by severity/firing in NotificationService.SendAsync).
/// Sends are best-effort and logged so one bad channel can't abort the batch.
/// </summary>
public class IncidentDispatcher(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    NotificationService notifications,
    StormSuppressionService stormSuppression,
    ILogger<IncidentDispatcher> logger)
{
    public async Task DispatchAsync(
        Guid tenantId, Guid clusterId,
        IReadOnlyList<AlertIncident> firing, IReadOnlyList<AlertIncident> resolved, CancellationToken ct = default)
    {
        if (firing.Count == 0 && resolved.Count == 0) return;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;

        bool inMaintenance = await db.MaintenanceWindows.AnyAsync(
            w => w.TenantId == tenantId && (w.ClusterId == null || w.ClusterId == clusterId)
                 && w.StartsAt <= now && w.EndsAt >= now, ct);
        if (inMaintenance)
        {
            logger.LogInformation(
                "Suppressing {Firing} firing / {Resolved} resolved telemetry notifications for cluster {Cluster} (maintenance window active).",
                firing.Count, resolved.Count, clusterId);
            return;
        }

        List<NotificationChannel> channels = await db.NotificationChannels
            .Where(c => c.TenantId == tenantId && c.CustomerId == null && c.IsEnabled)
            .ToListAsync(ct);
        if (channels.Count == 0) return;

        // Collapse a storm to one notification per root cause (throttled across cycles).
        HashSet<Guid> notifiable = await stormSuppression.SelectNotifiableAsync(firing, ct);
        int suppressed = firing.Count - notifiable.Count;
        if (suppressed > 0)
            logger.LogInformation(
                "Storm suppression: notifying {Notify} of {Total} firing telemetry incidents on cluster {Cluster} ({Suppressed} correlated/throttled).",
                notifiable.Count, firing.Count, clusterId, suppressed);

        foreach (AlertIncident inc in firing)
        {
            if (!notifiable.Contains(inc.Id)) continue;
            foreach (NotificationChannel ch in channels)
                await SafeSendAsync(inc, ch, isFiring: true, ct);
        }

        foreach (AlertIncident inc in resolved)
            foreach (NotificationChannel ch in channels)
                await SafeSendAsync(inc, ch, isFiring: false, ct);
    }

    private async Task SafeSendAsync(AlertIncident inc, NotificationChannel ch, bool isFiring, CancellationToken ct)
    {
        try { await notifications.SendAsync(inc, ch, isFiring, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Notification send failed (incident {Id}, channel {Channel})", inc.Id, ch.Id); }
    }
}
