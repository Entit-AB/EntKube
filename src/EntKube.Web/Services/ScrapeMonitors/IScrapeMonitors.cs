using EntKube.Web.Services;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// What a page may do with a cluster's monitors: read the plan, and carry it out.
///
/// <para><b>Why there is an interface at all.</b> <see cref="ScrapeMonitorService"/> is a class with
/// no virtual member, so a component that injected it could only be tested against a whole cluster.
/// This is the seam for the Monitors panel and nothing more: the sweep keeps
/// <see cref="ScrapeMonitorService.ReconcileAllAsync"/>, which no page has any business calling.</para>
/// </summary>
public interface IScrapeMonitors
{
    /// <inheritdoc cref="ScrapeMonitorService.PlanAsync"/>
    Task<KubernetesOperationResult<ScrapeMonitorPlan>> PlanAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);

    /// <inheritdoc cref="ScrapeMonitorService.ApplyAsync"/>
    Task<KubernetesOperationResult<ScrapeMonitorPlan>> ApplyAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default);
}
