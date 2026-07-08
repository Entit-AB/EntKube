using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Decides which of a batch of firing incidents should actually page, so a storm
/// (one root cause fanning out into many incidents) sends ONE notification instead
/// of N. Two reductions are applied:
///   1. Correlation — incidents are grouped by (cluster, alert name) and only the
///      most-severe representative of each group is notified.
///   2. Cooldown — if a group was already paged within the cooldown window, the whole
///      group is throttled, so a persistent storm doesn't re-page every eval cycle.
/// Both incident-dispatch paths (telemetry <see cref="IncidentDispatcher"/> and the
/// Prometheus <see cref="AlertSyncService"/>) run their firing lists through this first.
/// </summary>
public class StormSuppressionService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>How long after paging a group we stay quiet about it, even if it keeps firing.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Returns the ids of the incidents that should be notified — one representative per
    /// correlation group, excluding groups already paged within the cooldown. Callers filter
    /// their firing list to this set before sending. Resolved notifications are not affected.
    /// </summary>
    public async Task<HashSet<Guid>> SelectNotifiableAsync(
        IReadOnlyList<AlertIncident> firing, CancellationToken ct = default)
    {
        var notifiable = new HashSet<Guid>();
        if (firing.Count == 0) return notifiable;

        List<IncidentGroup> groups = IncidentCorrelationService.Correlate(firing);

        // Which (cluster, alert name) groups have already paged recently?
        DateTime cutoff = DateTime.UtcNow - Cooldown;
        HashSet<Guid> clusterIds = firing.Select(i => i.ClusterId).ToHashSet();

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        var recent = await db.NotificationDeliveries
            .Where(d => d.IsFiring && d.Success && d.SentAt >= cutoff
                     && clusterIds.Contains(d.Incident.ClusterId))
            .Select(d => new { d.Incident.ClusterId, d.Incident.AlertName })
            .Distinct()
            .ToListAsync(ct);

        HashSet<string> recentlyPaged = recent
            .Select(r => $"{r.ClusterId}:{r.AlertName}")
            .ToHashSet();

        foreach (IncidentGroup g in groups)
        {
            if (recentlyPaged.Contains(g.Key)) continue;   // already paged for this root cause
            notifiable.Add(g.LeadIncidentId);              // one representative for the group
        }

        return notifiable;
    }
}
