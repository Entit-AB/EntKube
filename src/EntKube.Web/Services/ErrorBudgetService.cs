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
    /// <summary>
    /// ⚠️ <b>Strictly greater than 1.0</b>, which is what the contract three lines above says:
    /// "1.0 = exhausted, &gt;1 = breached". This was <c>&gt;= 1.0</c>, so a budget exactly spent
    /// — availability exactly equal to the objective, which <em>meets</em> it — reported a breach.
    /// </summary>
    public bool IsBreached => BudgetConsumedFraction > 1.0;
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

        DateTime now = DateTime.UtcNow;

        // ONE CALL PER DISTINCT SLA WINDOW, aggregated in the database, instead of one call for
        // every sample in the widest window sliced per-deployment here. This service never wanted
        // the samples: it computes healthy/total twice per deployment and throws them away. At
        // five-minute sampling a 30-day window is ~8,600 rows per deployment, and the measurement
        // window can be 90.
        //
        // The windows are a handful of values in practice — SLA targets are written as 7, 30 or
        // 90 days — and each call returns one row per deployment, so the call count buys back
        // several orders of magnitude of rows. Deployments sharing a window share a call.
        Dictionary<int, Dictionary<Guid, DeploymentHealthRatio>> byWindow = [];

        foreach (IGrouping<int, Guid> group in resolved
            .GroupBy(r => r.Target?.MeasurementWindowDays ?? 30, r => r.Deployment.Id))
        {
            byWindow[group.Key] = (await delivery.GetHealthRatiosAsync(
                    tenantId, [.. group], now.AddDays(-group.Key), null, ct))
                .ToDictionary(r => r.DeploymentId);
        }

        // The burn window is the same for every deployment, so it is one call regardless.
        Dictionary<Guid, DeploymentHealthRatio> recent = (await delivery.GetHealthRatiosAsync(
                tenantId, [.. deployments.Select(d => d.Id)],
                now.AddDays(-RecentBurnWindowDays), null, ct))
            .ToDictionary(r => r.DeploymentId);

        var result = new List<ErrorBudgetStatus>(resolved.Count);
        foreach ((DeploymentSummary d, AppSummary? app, SlaTargetSummary? target) in resolved)
        {
            double targetPercent = target?.TargetPercent ?? 99.9;
            int windowDays = target?.MeasurementWindowDays ?? 30;
            double allowedErr = Math.Max(0, 100.0 - targetPercent);

            // Raw, not rounded: see RawAvailability. Rounding here is what turned a met
            // objective into a reported breach.
            double? achieved = RawAvailability(
                byWindow[windowDays].GetValueOrDefault(d.Id));
            double? consumed = null, burn = null, hoursToExhaust = null;

            if (achieved is double ach)
            {
                double actualErr = 100.0 - ach;
                consumed = allowedErr > 0 ? actualErr / allowedErr : (actualErr > 0 ? 2.0 : 0.0);

                double? recentAch = RawAvailability(recent.GetValueOrDefault(d.Id));
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
                // Rounded here and nowhere earlier: this one is read, not divided.
                AchievedPercent = achieved is double shown ? Math.Round(shown, 2) : null,
                BudgetConsumedFraction = consumed,
                BurnRateRecent = burn,
                HoursToExhaustion = hoursToExhaust,
            });
        }

        return result;
    }

    /// <summary>
    /// The achieved percentage, or null when nothing was observed.
    ///
    /// <para>Null rather than 0 is the point: "no samples in the window" and "no sample was
    /// healthy" are different answers, and only one of them is a service-level failure. A
    /// deployment with no samples is absent from the contract's result, which is why this takes a
    /// nullable.</para>
    /// </summary>
    private static double? Availability(DeploymentHealthRatio? ratio)
        => RawAvailability(ratio) is double raw ? Math.Round(raw, 2) : null;

    /// <summary>
    /// The same figure <b>unrounded</b>, for arithmetic.
    ///
    /// <para>⚠️ <b>Rounding before the budget division produced false breaches.</b> The error
    /// budget is <c>(100 - achieved) / (100 - target)</c>, and both terms are small differences of
    /// numbers near 100. Rounding <c>achieved</c> to two places first snaps any availability
    /// within 0.005 of a two-decimal target onto the target exactly — after which numerator and
    /// denominator are the <em>same expression</em>, bit for bit, and the quotient is exactly
    /// 1.0.</para>
    ///
    /// <para>2014 healthy samples of 2016 is 99.9008%, which <b>meets</b> a 99.9% objective with
    /// 99.2% of the budget spent. Rounded first it became 99.9, consumed came out 1.0, and
    /// <c>OperationsAdvisor</c> raised a Critical, Overdue "SLO breached" finding whose own text
    /// read "Availability 99.9% is below the 99.9% objective" — served over
    /// <c>GET /api/v1/advisor/findings</c>. Five-minute sampling over seven days is 2016 samples,
    /// so two bad ones reach it.</para>
    ///
    /// <para>Null rather than 0 for the reason <see cref="Availability"/> gives: no samples and no
    /// healthy sample are different answers.</para>
    /// </summary>
    private static double? RawAvailability(DeploymentHealthRatio? ratio)
        => ratio is null || ratio.TotalSamples == 0
            ? null
            : (double)ratio.HealthySamples / ratio.TotalSamples * 100.0;

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
