using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Outbound;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>A check and how it has done.</summary>
/// <param name="Check">The check.</param>
/// <param name="UptimePercent">Runs passed over the last 24 hours, or null with no runs: unknown, not 0.</param>
public sealed record SyntheticCheckRow(SyntheticCheck Check, double? UptimePercent);

/// <summary>What a check's state means to its alert rule.</summary>
public enum SyntheticAlertState { Clear, Fire, Hold }

/// <summary>Manages checks, records runs, and keeps each check's alert rule in step.</summary>
public sealed class SyntheticCheckService(
    IDbContextFactory<TelemetryDbContext> dbFactory,
    TelemetryAlertRuleService rules,
    IConfiguration config)
{
    public const int MinIntervalSeconds = 60;
    public const int MaxIntervalSeconds = 3600;
    public const int MaxChecksPerTenant = 100;
    public const int MaxUrlLength = 2000;
    public const int MaxExpectedTextLength = 500;

    private bool AllowPrivateTargets => config.GetValue("Synthetics:AllowPrivateTargets", false);

    public async Task<List<SyntheticCheckRow>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<SyntheticCheck> checks = await db.SyntheticChecks.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        DateTime since = DateTime.UtcNow.AddHours(-24);
        Guid[] ids = [.. checks.Select(c => c.Id)];

        // Grouped in the database: at one run a minute that is 1,440 rows for each check.
        var counts = await db.SyntheticCheckResults.AsNoTracking()
            .Where(r => ids.Contains(r.CheckId) && r.At >= since)
            .GroupBy(r => r.CheckId)
            .Select(g => new { CheckId = g.Key, Runs = g.Count(), Passed = g.Count(r => r.Passed) })
            .ToListAsync(ct);

        return [.. checks.Select(c =>
        {
            var count = counts.FirstOrDefault(x => x.CheckId == c.Id);
            return new SyntheticCheckRow(c, count is null ? null : count.Passed * 100.0 / count.Runs);
        })];
    }

    /// <summary>
    /// Creates or updates a check and its alert rule. Returns the problem, or null when saved.
    /// The URL is judged here for the person typing it; the probe judges it again at every connection.
    /// </summary>
    public async Task<string?> SaveAsync(Guid tenantId, SyntheticCheck input, CancellationToken ct = default)
    {
        string name = input.Name.Trim();
        string url = input.Url.Trim();

        if (name.Length is 0 or > 200) return "Give the check a name of at most 200 characters.";
        if (url.Length > MaxUrlLength) return $"A URL can be at most {MaxUrlLength} characters.";
        if (input.ExpectedText?.Length > MaxExpectedTextLength) return $"Expected text can be at most {MaxExpectedTextLength} characters.";
        if (input.ExpectedStatus is < 100 or > 599) return "An expected status must be from 100 to 599.";

        OutboundUrlVerdict verdict = await OutboundUrlGuard.ValidateAsync(url, AllowPrivateTargets, ct);
        if (!verdict.IsAllowed) return verdict.Reason;

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.KubernetesClusters.AnyAsync(c => c.Id == input.ClusterId && c.TenantId == tenantId, ct))
        {
            return "Choose a cluster to file incidents against.";
        }

        DateTime now = DateTime.UtcNow;
        SyntheticCheck? check = await db.SyntheticChecks
            .FirstOrDefaultAsync(c => c.Id == input.Id && c.TenantId == tenantId, ct);

        if (check is null)
        {
            if (input.Id != Guid.Empty) return "That check no longer exists.";

            if (await db.SyntheticChecks.CountAsync(c => c.TenantId == tenantId, ct) >= MaxChecksPerTenant)
            {
                return $"A tenant can have at most {MaxChecksPerTenant} checks.";
            }

            check = new SyntheticCheck { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = now, NextRunAt = now };
            db.SyntheticChecks.Add(check);
        }

        check.ClusterId = input.ClusterId;
        check.Name = name;
        check.Url = url;
        check.ExpectedStatus = input.ExpectedStatus;
        check.ExpectedText = string.IsNullOrEmpty(input.ExpectedText) ? null : input.ExpectedText;
        check.IntervalSeconds = Math.Clamp(input.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        check.FailuresBeforeAlert = Math.Clamp(input.FailuresBeforeAlert, 1, 100);
        check.Severity = input.Severity is "critical" or "warning" or "info" ? input.Severity : "warning";
        check.IsEnabled = input.IsEnabled;
        check.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await SyncRuleAsync(db, check, ct);
        return null;
    }

    /// <summary>The rule is what raises the incident, so it must always agree with the check.</summary>
    private async Task SyncRuleAsync(TelemetryDbContext db, SyntheticCheck check, CancellationToken ct)
    {
        TelemetryAlertRule? rule = await db.TelemetryAlertRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == check.TenantId && r.CheckId == check.Id, ct);

        rule ??= new TelemetryAlertRule { Name = "", Kind = TelemetryAlertKind.SyntheticCheck, CheckId = check.Id };

        bool wasEnabled = rule.IsEnabled;

        rule.Name = $"Synthetic: {check.Name}";
        rule.ClusterId = check.ClusterId;
        rule.Threshold = check.FailuresBeforeAlert;
        rule.Severity = check.Severity;
        rule.IsEnabled = check.IsEnabled;

        await rules.UpsertAsync(check.TenantId, rule, ct);

        // SetEnabledAsync is what resolves a firing incident, which UpsertAsync does not.
        if (wasEnabled && !check.IsEnabled) await rules.SetEnabledAsync(check.TenantId, rule.Id, false, ct);
    }

    /// <summary>Deletes a check, its history and its rule, resolving any open incident.</summary>
    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Guid? ruleId = await db.TelemetryAlertRules
            .Where(r => r.TenantId == tenantId && r.CheckId == id)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

        int deleted = await db.SyntheticChecks.Where(c => c.Id == id && c.TenantId == tenantId).ExecuteDeleteAsync(ct);

        if (deleted > 0 && ruleId is { } rule) await rules.DeleteAsync(tenantId, rule, ct);
    }

    /// <summary>The tenant's checks that are due, oldest first, and enabled.</summary>
    public async Task<List<SyntheticCheck>> DueAsync(Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.SyntheticChecks.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsEnabled && c.NextRunAt <= now)
            .OrderBy(c => c.NextRunAt)
            .ToListAsync(ct);
    }

    /// <summary>Records a run and moves the check's schedule and failure streak on.</summary>
    public async Task RecordAsync(
        Guid tenantId, Guid checkId, SyntheticProbeResult result, DateTime now, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SyntheticCheck? check = await db.SyntheticChecks.FirstOrDefaultAsync(c => c.Id == checkId && c.TenantId == tenantId, ct);
        if (check is null) return;   // deleted while its request was in flight

        check.State = result.Passed ? SyntheticCheckState.Passing : SyntheticCheckState.Failing;
        check.ConsecutiveFailures = result.Passed ? 0 : check.ConsecutiveFailures + 1;
        check.LastRunAt = now;
        check.NextRunAt = now.AddSeconds(check.IntervalSeconds);
        check.LastStatusCode = result.StatusCode;
        check.LastDurationMs = result.DurationMs;
        check.LastFailure = result.Failure;

        db.SyntheticCheckResults.Add(new SyntheticCheckResult
        {
            CheckId = check.Id,
            At = now,
            Passed = result.Passed,
            StatusCode = result.StatusCode,
            DurationMs = result.DurationMs,
        });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// What a check says about its incident.
    ///
    /// <para>⚠️ Stale is a hold, not a clear. If the runner stops, the last answer is old news, and a
    /// failing check going quiet must not resolve its incident. Between one failure and the threshold
    /// it also holds: neither raising an incident yet, nor clearing one a blip has not cured.</para>
    /// </summary>
    public static SyntheticAlertState Decide(SyntheticCheck check, DateTime now)
    {
        if (check.LastRunAt is not { } lastRun) return SyntheticAlertState.Hold;

        if (now - lastRun > TimeSpan.FromSeconds(check.IntervalSeconds * 3L + 60)) return SyntheticAlertState.Hold;

        if (check.ConsecutiveFailures >= check.FailuresBeforeAlert) return SyntheticAlertState.Fire;

        return check.ConsecutiveFailures == 0 ? SyntheticAlertState.Clear : SyntheticAlertState.Hold;
    }

    /// <summary>Deletes old runs by age alone, which names no tenant. Returns how many went.</summary>
    public async Task<int> PruneAsync(DateTime now, CancellationToken ct = default)
    {
        int days = Math.Max(1, config.GetValue<int?>("Synthetics:ResultRetentionDays") ?? 14);
        DateTime cutoff = now.AddDays(-days);

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SyntheticCheckResults.Where(r => r.At < cutoff).ExecuteDeleteAsync(ct);
    }
}
