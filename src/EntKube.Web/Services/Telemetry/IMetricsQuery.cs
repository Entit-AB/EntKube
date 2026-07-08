using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The metrics query surface the metrics explorer / dashboards inject. Since app metrics now live in
/// Prometheus (apps write there directly; EntKube only visualizes), the sole implementation is
/// <see cref="PromMetricsService"/> — the native metrics table and <c>PgMetricsService</c> are gone.
/// Kept as an interface so the UI depends on an abstraction and an alternative backend could be slotted in.
/// Mirrors the old PgMetricsService surface one-for-one (same DTOs).
/// </summary>
public interface IMetricsQuery
{
    Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null);

    Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null);

    Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid clusterId, string name, string? service, DateTime from, DateTime to, int buckets = 60,
        CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null);

    Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
        Guid clusterId, string name, string? service, string dimension, DateTime from, DateTime to,
        int buckets = 60, int maxSeries = 24, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null);
}
