namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// What a page may ask of the monitor engine: what the monitors on one cluster are, and to make them
/// so now. <see cref="ScrapeMonitorService"/> is the one implementation; the interface exists so a
/// panel can be rendered over a stand-in, and so the panels that read the plan and the panels that
/// apply it ask the same thing.
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
