using EntKube.Contracts.Delivery;
using EntKube.Contracts.Support;

namespace EntKube.Web.Services;

/// <summary>
/// The SLO / error-budget standing of a single deployment over its SLA window.
/// Availability is measured from Delivery's health samples (fraction that were Healthy),
/// the objective from the resolved <see cref="SlaTargetSummary"/>.
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
/// Computes SLO error budgets for a tenant's managed deployments. Reuses the existing SLA
/// targets and health-sample history rather than introducing a new SLO store — the objective
/// already lives in Support and the achieved availability in Delivery.
///
/// <para>This service owns no table. Every row it reads belongs to another module: the
/// deployments and their health samples to Delivery, the objectives to Support. It held the
/// all-seeing context for exactly that reason, and now holds two contracts instead — which is
/// the whole shape of the decomposition in one file.</para>
///
/// <para>⚠️ The customer identity comes from Delivery's <em>app</em> list rather than from the
/// deployment, which is why there are two Delivery calls. <c>DeploymentSummary</c> carries no
/// customer and deliberately does not grow one: <c>AppSummary</c> already has
/// <c>CustomerId</c> and <c>CustomerName</c>, and a deployment already carries its
/// <c>AppId</c>, so the join costs one query against a small set and the alternative is a DTO
/// field added for a second consumer. <c>MessagingTab</c> resolves it the same way for the same
/// reason.</para>
/// </summary>
public class ErrorBudgetService(IDeliveryApi delivery, ISupportApi support)
{
    /// <summary>Window used to estimate the *current* burn rate (vs. the whole SLA window).</summary>
    private const int RecentBurnWindowDays = 1;

    public async Task<List<ErrorBudgetStatus>> GetTenantErrorBudgetsAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        IReadOnlyList<DeploymentSummary> all = await delivery.GetDeploymentsForTenantAsync(tenantId, ct);
        List<DeploymentSummary> deployments = [.. all.Where(d => d.IsManaged)];

        if (deployments.Count == 0) return [];

        IReadOnlyList<SlaTargetSummary> targets = await support.GetSlaTargetsAsync(tenantId, ct);

        Dictionary<Guid, AppSummary> apps = (await delivery.GetAppsAsync(tenantId, ct))
            .ToDictionary(a => a.Id);

        // Resolve each deployment's target up front so we know the widest window to load.
        var resolved = deployments
            .Select(d =>
            {
                apps.TryGetValue(d.AppId, out AppSummary? app);
                return (Deployment: d, App: app, Target: ResolveTarget(targets, app?.CustomerId, d.AppId));
            })
            .ToList();

        int maxWindow = resolved.Select(r => r.Target?.MeasurementWindowDays ?? 30).DefaultIfEmpty(30).Max();
        DateTime now = DateTime.UtcNow;
        DateTime from = now.AddDays(-maxWindow);

        List<Guid> deploymentIds = [.. deployments.Select(d => d.Id)];

        // One call for all samples in the widest window; sliced per-deployment in memory.
        IReadOnlyList<DeploymentHealthSample> allSamples =
            await delivery.GetHealthSamplesAsync(tenantId, deploymentIds, from, null, ct);

        Dictionary<Guid, List<DeploymentHealthSample>> byDeployment = allSamples
            .GroupBy(s => s.DeploymentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<ErrorBudgetStatus>(resolved.Count);
        foreach ((DeploymentSummary d, AppSummary? app, SlaTargetSummary? target) in resolved)
        {
            double targetPercent = target?.TargetPercent ?? 99.9;
            int windowDays = target?.MeasurementWindowDays ?? 30;
            double allowedErr = Math.Max(0, 100.0 - targetPercent);

            byDeployment.TryGetValue(d.Id, out List<DeploymentHealthSample>? samples);
            DateTime windowFrom = now.AddDays(-windowDays);
            List<DeploymentHealthSample> windowSamples =
                samples?.Where(s => s.SnapshotAt >= windowFrom).ToList() ?? [];

            double? achieved = Availability(windowSamples);
            double? consumed = null, burn = null, hoursToExhaust = null;

            if (achieved is double ach)
            {
                double actualErr = 100.0 - ach;
                consumed = allowedErr > 0 ? actualErr / allowedErr : (actualErr > 0 ? 2.0 : 0.0);

                DateTime recentFrom = now.AddDays(-RecentBurnWindowDays);
                double? recentAch = Availability(windowSamples.Where(s => s.SnapshotAt >= recentFrom).ToList());
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
                AppName = d.AppName,
                CustomerId = app?.CustomerId,
                CustomerName = app?.CustomerName,
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

    private static double? Availability(List<DeploymentHealthSample> samples)
    {
        if (samples.Count == 0) return null;
        int healthy = samples.Count(s => s.Health == HealthStatus.Healthy);
        return Math.Round((double)healthy / samples.Count * 100.0, 2);
    }

    /// <summary>
    /// Most-specific-wins: app-level &gt; customer-level &gt; tenant-level default.
    ///
    /// <para>Kept here rather than moved behind <see cref="ISupportApi"/> on purpose — that
    /// interface's own notes record why: the other implementation of this predicate, in
    /// <c>IncidentService</c>, admits rows this one rejects, and publishing either as the
    /// resolution would quietly decide which is right.</para>
    /// </summary>
    private static SlaTargetSummary? ResolveTarget(
        IReadOnlyList<SlaTargetSummary> targets, Guid? customerId, Guid appId)
    {
        return targets
            .Where(t => (t.AppId == null || t.AppId == appId)
                     && (t.CustomerId == null || t.CustomerId == customerId))
            .OrderByDescending(t => t.AppId.HasValue)
            .ThenByDescending(t => t.CustomerId.HasValue)
            .FirstOrDefault();
    }
}
