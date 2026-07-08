using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// The SLO / error-budget standing of a single deployment over its SLA window.
/// Availability is measured from <see cref="DeploymentHealthSnapshot"/> (fraction of
/// snapshots that were Healthy), the objective from the resolved <see cref="SlaTarget"/>.
/// </summary>
public sealed record ErrorBudgetStatus
{
    public required Guid DeploymentId { get; init; }
    public required string DeploymentName { get; init; }
    public required string AppName { get; init; }
    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public required Guid ClusterId { get; init; }

    public required double TargetPercent { get; init; }
    public required int WindowDays { get; init; }

    /// <summary>Measured availability over the window; null when there is no health data yet.</summary>
    public double? AchievedPercent { get; init; }

    /// <summary>Fraction of the error budget spent. 0 = pristine, 1.0 = exhausted, &gt;1 = breached.
    /// Null when there is no data.</summary>
    public double? BudgetConsumedFraction { get; init; }

    /// <summary>Recent burn rate — error rate over the last day ÷ the sustainable rate.
    /// &gt;1 means burning faster than the budget allows. Null when there is no recent data.</summary>
    public double? BurnRateRecent { get; init; }

    /// <summary>At the recent burn rate, hours until the remaining budget is gone.
    /// Null when not currently on track to exhaust (burn rate ≤ 1, or already breached, or no data).</summary>
    public double? HoursToExhaustion { get; init; }

    public bool HasData => AchievedPercent.HasValue;
    public bool IsBreached => BudgetConsumedFraction >= 1.0;
}

/// <summary>
/// Computes SLO error budgets for a tenant's managed deployments. Reuses the
/// existing SLA targets and health-snapshot history rather than introducing a new
/// SLO store — the objective already lives in <see cref="SlaTarget"/> and the
/// achieved availability in <see cref="DeploymentHealthSnapshot"/>.
/// </summary>
public class ErrorBudgetService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>Window used to estimate the *current* burn rate (vs. the whole SLA window).</summary>
    private const int RecentBurnWindowDays = 1;

    public async Task<List<ErrorBudgetStatus>> GetTenantErrorBudgetsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeployment> deployments = await db.AppDeployments
            .Include(d => d.App).ThenInclude(a => a.Customer)
            .Where(d => d.App.Customer.TenantId == tenantId && d.IsManaged)
            .ToListAsync(ct);

        if (deployments.Count == 0) return [];

        List<SlaTarget> targets = await db.SlaTargets
            .Where(t => t.TenantId == tenantId)
            .ToListAsync(ct);

        // Resolve each deployment's target up front so we know the widest window to load.
        var resolved = deployments
            .Select(d => (Deployment: d, Target: ResolveTarget(targets, d.App.CustomerId, d.AppId)))
            .ToList();

        int maxWindow = resolved.Select(r => r.Target?.MeasurementWindowDays ?? 30).DefaultIfEmpty(30).Max();
        DateTime now = DateTime.UtcNow;
        DateTime from = now.AddDays(-maxWindow);

        HashSet<Guid> deploymentIds = deployments.Select(d => d.Id).ToHashSet();

        // One query for all snapshots in the widest window; sliced per-deployment in memory.
        List<DeploymentHealthSnapshot> allSnapshots = await db.DeploymentHealthSnapshots
            .Where(s => deploymentIds.Contains(s.DeploymentId) && s.SnapshotAt >= from)
            .Select(s => new DeploymentHealthSnapshot
            {
                DeploymentId = s.DeploymentId,
                HealthStatus = s.HealthStatus,
                SnapshotAt = s.SnapshotAt,
            })
            .ToListAsync(ct);

        Dictionary<Guid, List<DeploymentHealthSnapshot>> byDeployment = allSnapshots
            .GroupBy(s => s.DeploymentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ErrorBudgetStatus>(resolved.Count);
        foreach ((AppDeployment d, SlaTarget? target) in resolved)
        {
            double targetPercent = target?.TargetPercent ?? 99.9;
            int windowDays = target?.MeasurementWindowDays ?? 30;
            double allowedErr = Math.Max(0, 100.0 - targetPercent);

            byDeployment.TryGetValue(d.Id, out List<DeploymentHealthSnapshot>? snaps);
            DateTime windowFrom = now.AddDays(-windowDays);
            List<DeploymentHealthSnapshot> windowSnaps =
                snaps?.Where(s => s.SnapshotAt >= windowFrom).ToList() ?? [];

            double? achieved = Availability(windowSnaps);
            double? consumed = null, burn = null, hoursToExhaust = null;

            if (achieved is double ach)
            {
                double actualErr = 100.0 - ach;
                consumed = allowedErr > 0 ? actualErr / allowedErr : (actualErr > 0 ? 2.0 : 0.0);

                DateTime recentFrom = now.AddDays(-RecentBurnWindowDays);
                double? recentAch = Availability(windowSnaps.Where(s => s.SnapshotAt >= recentFrom).ToList());
                if (recentAch is double rach)
                {
                    double recentErr = 100.0 - rach;
                    burn = allowedErr > 0 ? recentErr / allowedErr : (recentErr > 0 ? 2.0 : 0.0);

                    // Only forecast exhaustion when actively burning faster than sustainable.
                    if (consumed < 1.0 && burn > 1.0)
                    {
                        double remaining = 1.0 - consumed.Value;
                        hoursToExhaust = remaining * windowDays * 24.0 / burn.Value;
                    }
                }
            }

            result.Add(new ErrorBudgetStatus
            {
                DeploymentId = d.Id,
                DeploymentName = d.Name,
                AppName = d.App.Name,
                CustomerId = d.App.CustomerId,
                CustomerName = d.App.Customer?.Name,
                ClusterId = d.ClusterId,
                TargetPercent = targetPercent,
                WindowDays = windowDays,
                AchievedPercent = achieved,
                BudgetConsumedFraction = consumed,
                BurnRateRecent = burn,
                HoursToExhaustion = hoursToExhaust,
            });
        }

        return result;
    }

    private static double? Availability(List<DeploymentHealthSnapshot> snaps)
    {
        if (snaps.Count == 0) return null;
        int healthy = snaps.Count(s => s.HealthStatus == HealthStatus.Healthy);
        return Math.Round((double)healthy / snaps.Count * 100.0, 2);
    }

    /// <summary>Most-specific-wins: app-level &gt; customer-level &gt; tenant-level default.</summary>
    private static SlaTarget? ResolveTarget(List<SlaTarget> targets, Guid customerId, Guid appId)
    {
        return targets
            .Where(t => (t.AppId == null || t.AppId == appId)
                     && (t.CustomerId == null || t.CustomerId == customerId))
            .OrderByDescending(t => t.AppId.HasValue)
            .ThenByDescending(t => t.CustomerId.HasValue)
            .FirstOrDefault();
    }
}
