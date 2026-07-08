using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Persists the human lifecycle decisions layered over compute-on-read advisor
/// findings — acknowledge, snooze, dismiss, assign — plus first/last-seen tracking
/// used for aging and escalation. Everything is keyed by the finding's stable
/// synthetic id so state survives across the 60s recomputations.
/// </summary>
public class AdvisorStateService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>Stale state rows older than this (finding gone) are pruned so a recurrence starts fresh.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);

    public async Task<Dictionary<string, AdvisorFindingState>> GetStatesAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<AdvisorFindingState> rows = await db.AdvisorFindingStates
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);
        return rows.ToDictionary(s => s.FindingKey);
    }

    public Task AcknowledgeAsync(Guid tenantId, string key, string by, CancellationToken ct = default) =>
        MutateAsync(tenantId, key, s =>
        {
            s.Status = AdvisorFindingStatus.Acknowledged;
            s.AcknowledgedBy = by;
            s.AcknowledgedAt = DateTime.UtcNow;
            s.SnoozedUntil = null;
        }, ct);

    public Task SnoozeAsync(Guid tenantId, string key, DateTime until, string by, CancellationToken ct = default) =>
        MutateAsync(tenantId, key, s =>
        {
            s.Status = AdvisorFindingStatus.Snoozed;
            s.SnoozedUntil = until;
            s.AcknowledgedBy = by;
        }, ct);

    public Task DismissAsync(Guid tenantId, string key, string by, CancellationToken ct = default) =>
        MutateAsync(tenantId, key, s =>
        {
            s.Status = AdvisorFindingStatus.Dismissed;
            s.AcknowledgedBy = by;
            s.SnoozedUntil = null;
        }, ct);

    /// <summary>Return a finding to the Active list (clears ack/snooze/dismiss).</summary>
    public Task ReactivateAsync(Guid tenantId, string key, CancellationToken ct = default) =>
        MutateAsync(tenantId, key, s =>
        {
            s.Status = AdvisorFindingStatus.Active;
            s.SnoozedUntil = null;
            s.AcknowledgedAt = null;
        }, ct);

    public Task AssignAsync(Guid tenantId, string key, string? assignee, CancellationToken ct = default) =>
        MutateAsync(tenantId, key, s => s.AssignedTo = string.IsNullOrWhiteSpace(assignee) ? null : assignee.Trim(), ct);

    /// <summary>
    /// Records that these findings are currently present (first/last seen) and prunes
    /// state whose finding has been gone longer than <see cref="StaleAfter"/>. Called by the
    /// periodic scan so aging/escalation works without anyone opening the tab.
    /// </summary>
    public async Task ReconcileAsync(Guid tenantId, IReadOnlyCollection<string> currentKeys, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;

        List<AdvisorFindingState> existing = await db.AdvisorFindingStates
            .Where(s => s.TenantId == tenantId)
            .ToListAsync(ct);
        Dictionary<string, AdvisorFindingState> byKey = existing.ToDictionary(s => s.FindingKey);

        foreach (string key in currentKeys)
        {
            if (byKey.TryGetValue(key, out AdvisorFindingState? s))
            {
                s.LastSeenAt = now;
            }
            else
            {
                db.AdvisorFindingStates.Add(new AdvisorFindingState
                {
                    TenantId = tenantId,
                    FindingKey = key,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
        }

        // Prune rows whose finding has been absent long enough (so a recurrence is treated as new).
        var current = currentKeys as HashSet<string> ?? currentKeys.ToHashSet();
        DateTime cutoff = now - StaleAfter;
        foreach (AdvisorFindingState s in existing)
            if (!current.Contains(s.FindingKey) && s.LastSeenAt < cutoff)
                db.AdvisorFindingStates.Remove(s);

        await db.SaveChangesAsync(ct);
    }

    private async Task MutateAsync(Guid tenantId, string key, Action<AdvisorFindingState> mutate, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AdvisorFindingState? s = await db.AdvisorFindingStates
            .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.FindingKey == key, ct);

        if (s is null)
        {
            s = new AdvisorFindingState { TenantId = tenantId, FindingKey = key };
            db.AdvisorFindingStates.Add(s);
        }

        mutate(s);
        s.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
