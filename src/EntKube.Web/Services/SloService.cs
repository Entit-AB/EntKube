using EntKube.Telemetry;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>Where an objective stands over its window.</summary>
/// <param name="Good">Good events in the window.</param>
/// <param name="Total">All events in the window.</param>
/// <param name="AttainedPercent">Good as a share of total, or null with no events: unknown, not 0.</param>
/// <param name="BudgetSpentFraction">
/// Bad events over the bad events the target allows. 1 is exactly spent, which still meets the target;
/// over 1 is a breach. Null with no events.
/// </param>
/// <param name="BurnRate">
/// The latest day's bad share over the allowed share: above 1 is spending faster than the window can
/// afford. Null when that day had no events.
/// </param>
/// <param name="IsBreached">True only when the budget is overspent, and never without events.</param>
public sealed record SloStatus(
    long Good, long Total, double? AttainedPercent, double? BudgetSpentFraction, double? BurnRate, bool IsBreached);

/// <summary>An objective and where it stands.</summary>
public sealed record SloRow(ServiceLevelObjective Slo, SloStatus Status);

/// <summary>Objectives, their daily tallies, and the arithmetic of an error budget.</summary>
public sealed class SloService(IDbContextFactory<TelemetryDbContext> dbFactory, TelemetryAlertRuleService rules)
{
    public const double MinTargetPercent = 50;
    public const double MaxTargetPercent = 99.999;
    public const int MaxWindowDays = 90;
    public const int MaxPerTenant = 50;

    /// <summary>The slice a latency objective judges, which is also its RED bucket width.</summary>
    public static readonly TimeSpan LatencySlice = TimeSpan.FromMinutes(5);

    // ── The arithmetic ──

    /// <summary>
    /// Where an objective stands, from its tallies. Days outside the window are ignored; the window
    /// ends today and includes it.
    /// </summary>
    public static SloStatus Compute(double targetPercent, int windowDays, IEnumerable<SloDailyTally> tallies, DateOnly today)
    {
        DateOnly first = today.AddDays(-(windowDays - 1));
        List<SloDailyTally> inWindow = [.. tallies.Where(t => t.Day >= first && t.Day <= today)];

        long good = inWindow.Sum(t => t.Good);
        long total = inWindow.Sum(t => t.Total);

        // ⚠️ Decimal, not double. In double, 1 - 99.9/100 is a hair under 0.001, so a budget spent
        // EXACTLY came out as 1.00000000000011 and read as a breach of a target that was met. The
        // boundary is the one figure this has to get right, and decimal holds 99.9 exactly.
        decimal allowedBadShare = 1m - (decimal)targetPercent / 100m;

        if (total == 0) return new SloStatus(0, 0, null, null, BurnRate(inWindow, today, allowedBadShare), false);

        decimal spent = (total - good) / (total * allowedBadShare);

        return new SloStatus(
            good, total, good * 100.0 / total, (double)spent, BurnRate(inWindow, today, allowedBadShare), spent > 1m);
    }

    /// <summary>Over today, or over yesterday early in a day that has nothing yet.</summary>
    private static double? BurnRate(List<SloDailyTally> tallies, DateOnly today, decimal allowedBadShare)
    {
        SloDailyTally? latest = tallies.FirstOrDefault(t => t.Day == today && t.Total > 0)
            ?? tallies.FirstOrDefault(t => t.Day == today.AddDays(-1) && t.Total > 0);

        return latest is null ? null : (double)((latest.Total - latest.Good) / (decimal)latest.Total / allowedBadShare);
    }

    /// <summary>Requests that did not error, out of requests.</summary>
    public static (long Good, long Total) Availability(IEnumerable<RedBucket> buckets)
    {
        long total = buckets.Sum(b => b.Count);
        long errors = buckets.Sum(b => Math.Min(b.Errors, b.Count));
        return (total - errors, total);
    }

    /// <summary>Slices with traffic whose p95 was at or under the threshold, out of slices with traffic.</summary>
    public static (long Good, long Total) Latency(IEnumerable<RedBucket> buckets, double thresholdMs)
    {
        List<RedBucket> busy = [.. buckets.Where(b => b.Count > 0)];
        return (busy.Count(b => b.P95Ms <= thresholdMs), busy.Count);
    }

    // ── Objectives ──

    public async Task<List<SloRow>> ListAsync(Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<ServiceLevelObjective> slos = await db.ServiceLevelObjectives.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        DateOnly today = DateOnly.FromDateTime(now);
        DateOnly earliest = today.AddDays(-MaxWindowDays);
        Guid[] ids = [.. slos.Select(s => s.Id)];

        List<SloDailyTally> tallies = await db.SloDailyTallies.AsNoTracking()
            .Where(t => ids.Contains(t.SloId) && t.Day >= earliest)
            .ToListAsync(ct);

        return [.. slos.Select(s => new SloRow(
            s, Compute(s.TargetPercent, s.WindowDays, tallies.Where(t => t.SloId == s.Id), today)))];
    }

    /// <summary>Creates or updates an objective and its alert rule. Returns the problem, or null when saved.</summary>
    public async Task<string?> SaveAsync(Guid tenantId, ServiceLevelObjective input, CancellationToken ct = default)
    {
        string name = input.Name.Trim();
        if (name.Length is 0 or > 200) return "Give the objective a name of at most 200 characters.";
        if (input.TargetPercent is < MinTargetPercent or > MaxTargetPercent)
            return $"A target must be from {MinTargetPercent} to {MaxTargetPercent} percent. 100 leaves no budget to measure.";
        if (input.WindowDays is not (7 or 14 or 28 or 30 or 90)) return "A window is 7, 14, 28, 30 or 90 days.";
        if (input.AlertAtBudgetSpentPercent is <= 0 or > 1000) return "Alert at a share of the budget above 0 and at most 1000 percent.";

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Guid clusterId;

        if (input.Kind == SloKind.SyntheticUptime)
        {
            SyntheticCheck? check = await db.SyntheticChecks.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == input.CheckId && c.TenantId == tenantId, ct);
            if (check is null) return "Choose a synthetic check.";
            clusterId = check.ClusterId;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(input.Service)) return "Name the service, as it appears in traces.";
            if (!await db.KubernetesClusters.AnyAsync(c => c.Id == input.ClusterId && c.TenantId == tenantId, ct))
                return "Choose the cluster the service runs on.";
            if (input.Kind == SloKind.ServiceLatency && input.LatencyThresholdMs is not > 0)
                return "A latency objective needs a threshold in milliseconds.";
            clusterId = input.ClusterId;
        }

        DateTime now = DateTime.UtcNow;
        ServiceLevelObjective? slo = await db.ServiceLevelObjectives
            .FirstOrDefaultAsync(s => s.Id == input.Id && s.TenantId == tenantId, ct);

        if (slo is null)
        {
            if (input.Id != Guid.Empty) return "That objective no longer exists.";
            if (await db.ServiceLevelObjectives.CountAsync(s => s.TenantId == tenantId, ct) >= MaxPerTenant)
                return $"A tenant can have at most {MaxPerTenant} objectives.";

            slo = new ServiceLevelObjective { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = now };
            db.ServiceLevelObjectives.Add(slo);
        }
        else if (slo.Kind != input.Kind || slo.CheckId != input.CheckId || slo.Service != input.Service
                 || slo.ClusterId != clusterId || slo.LatencyThresholdMs != input.LatencyThresholdMs)
        {
            // What is being measured changed, so the old days measured something else.
            await db.SloDailyTallies.Where(t => t.SloId == slo.Id).ExecuteDeleteAsync(ct);
        }

        slo.Name = name;
        slo.Kind = input.Kind;
        slo.TargetPercent = input.TargetPercent;
        slo.WindowDays = input.WindowDays;
        slo.CheckId = input.Kind == SloKind.SyntheticUptime ? input.CheckId : null;
        slo.ClusterId = clusterId;
        slo.Service = input.Kind == SloKind.SyntheticUptime ? null : input.Service!.Trim();
        slo.LatencyThresholdMs = input.Kind == SloKind.ServiceLatency ? input.LatencyThresholdMs : null;
        slo.AlertAtBudgetSpentPercent = input.AlertAtBudgetSpentPercent;
        slo.Severity = input.Severity is "critical" or "warning" or "info" ? input.Severity : "warning";
        slo.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await SyncRuleAsync(db, slo, ct);
        return null;
    }

    /// <summary>The objective's alert, kept in step the same way a synthetic check's is.</summary>
    private async Task SyncRuleAsync(TelemetryDbContext db, ServiceLevelObjective slo, CancellationToken ct)
    {
        TelemetryAlertRule? rule = await db.TelemetryAlertRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == slo.TenantId && r.SloId == slo.Id, ct);

        if (slo.AlertAtBudgetSpentPercent is not { } threshold)
        {
            if (rule is not null) await rules.DeleteAsync(slo.TenantId, rule.Id, ct);
            return;
        }

        rule ??= new TelemetryAlertRule { Name = "", Kind = TelemetryAlertKind.SloBudget, SloId = slo.Id };
        rule.Name = $"SLO: {slo.Name}";
        rule.ClusterId = slo.ClusterId;
        rule.Threshold = threshold;
        rule.Severity = slo.Severity;
        rule.IsEnabled = true;

        await rules.UpsertAsync(slo.TenantId, rule, ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Guid? ruleId = await db.TelemetryAlertRules
            .Where(r => r.TenantId == tenantId && r.SloId == id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);

        int deleted = await db.ServiceLevelObjectives.Where(s => s.Id == id && s.TenantId == tenantId).ExecuteDeleteAsync(ct);

        if (deleted > 0 && ruleId is { } rule) await rules.DeleteAsync(tenantId, rule, ct);
    }

    /// <summary>The objective's status for its alert, read from the tallies alone.</summary>
    public static async Task<SloStatus?> StatusAsync(
        TelemetryDbContext db, Guid tenantId, Guid sloId, DateTime now, CancellationToken ct)
    {
        ServiceLevelObjective? slo = await db.ServiceLevelObjectives.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sloId && s.TenantId == tenantId, ct);
        if (slo is null) return null;

        DateOnly today = DateOnly.FromDateTime(now);
        DateOnly first = today.AddDays(-(slo.WindowDays - 1));
        List<SloDailyTally> tallies = await db.SloDailyTallies.AsNoTracking()
            .Where(t => t.SloId == sloId && t.Day >= first).ToListAsync(ct);

        return Compute(slo.TargetPercent, slo.WindowDays, tallies, today);
    }

    // ── Tallies ──

    /// <summary>
    /// The days a pass should compute: today and yesterday always, because the data for them is still
    /// arriving or just settled, and any day in the window never computed that the source still holds.
    /// </summary>
    public static List<DateOnly> DaysToCompute(
        DateOnly today, int windowDays, int sourceRetentionDays, IReadOnlySet<DateOnly> computed)
    {
        List<DateOnly> days = [today, today.AddDays(-1)];
        int reach = Math.Min(windowDays, sourceRetentionDays) - 1;

        for (int i = 2; i <= reach; i++)
        {
            DateOnly day = today.AddDays(-i);
            if (!computed.Contains(day)) days.Add(day);
        }

        return days;
    }

    public async Task<List<ServiceLevelObjective>> ForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ServiceLevelObjectives.AsNoTracking().Where(s => s.TenantId == tenantId).ToListAsync(ct);
    }

    public async Task<HashSet<DateOnly>> ComputedDaysAsync(Guid sloId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return [.. await db.SloDailyTallies.AsNoTracking().Where(t => t.SloId == sloId).Select(t => t.Day).ToListAsync(ct)];
    }

    /// <summary>Synthetic runs on one day, read from the check's results.</summary>
    public async Task<(long Good, long Total)> SyntheticDayAsync(
        Guid tenantId, Guid checkId, DateOnly day, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        DateTime from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime to = from.AddDays(1);

        var counts = await db.SyntheticCheckResults.AsNoTracking()
            .Where(r => r.CheckId == checkId && r.At >= from && r.At < to
                        && db.SyntheticChecks.Any(c => c.Id == checkId && c.TenantId == tenantId))
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.LongCount(), Good = g.LongCount(r => r.Passed) })
            .FirstOrDefaultAsync(ct);

        return counts is null ? (0, 0) : (counts.Good, counts.Total);
    }

    /// <summary>Writes a day's tally, replacing what was there: recomputed, never added to.</summary>
    public async Task SetTallyAsync(Guid tenantId, Guid sloId, DateOnly day, long good, long total, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.ServiceLevelObjectives.AnyAsync(s => s.Id == sloId && s.TenantId == tenantId, ct)) return;

        SloDailyTally? tally = await db.SloDailyTallies.FirstOrDefaultAsync(t => t.SloId == sloId && t.Day == day, ct);

        if (tally is null)
        {
            tally = new SloDailyTally { SloId = sloId, Day = day };
            db.SloDailyTallies.Add(tally);
        }

        tally.Good = good;
        tally.Total = total;
        tally.ComputedAt = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance wrote the same day first. Both computed it from the same data.
        }
    }

    /// <summary>Deletes tallies older than any window can reach. Names no tenant.</summary>
    public async Task<int> PruneAsync(DateTime now, CancellationToken ct = default)
    {
        DateOnly cutoff = DateOnly.FromDateTime(now).AddDays(-(MaxWindowDays + 1));
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SloDailyTallies.Where(t => t.Day < cutoff).ExecuteDeleteAsync(ct);
    }
}
