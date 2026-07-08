using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Prometheus-backed <see cref="IMetricsQuery"/>: the metrics explorer / dashboards read app metrics from
/// the cluster's Prometheus via PromQL rather than a native store (apps write to Prometheus directly). The
/// metric-name and service dropdowns come from Prometheus label values; the charts come from range queries.
/// PromQL is assembled by <see cref="PromMetricsQueryBuilder"/> (OTel→Prometheus label conventions, counter
/// detection by <c>_total</c> suffix). Returns the same DTOs the old PgMetricsService did, so the UI is
/// unchanged.
/// </summary>
public sealed class PromMetricsService(PrometheusService prometheus, ILogger<PromMetricsService> logger) : IMetricsQuery
{
    private static readonly TimeSpan DiscoveryLookback = TimeSpan.FromHours(24);

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        var names = await prometheus.GetLabelValuesAsync(clusterId, "__name__", null, DiscoveryLookback, ct);
        return names.IsSuccess && names.Data is { Count: > 0 };
    }

    public Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => prometheus.GetLabelValuesAsync(clusterId, "__name__",
            NullIfEmpty(PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, null)), DiscoveryLookback, ct);

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => prometheus.GetLabelValuesAsync(clusterId, PromMetricsQueryBuilder.ServiceLabel,
            NullIfEmpty(PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, null)), DiscoveryLookback, ct);

    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid clusterId, string name, string? service, DateTime from, DateTime to, int buckets = 60,
        CancellationToken ct = default, IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        string selector = PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, service);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        string promql = PromMetricsQueryBuilder.BuildSeriesQuery(name, selector, rate);

        KubernetesOperationResult<List<PrometheusTimeSeries>> res =
            await prometheus.GetMetricRangeAsync(clusterId, promql, to - from, ct);
        if (!res.IsSuccess)
            return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Failure(res.Error!);

        // sum(...) collapses to a single series (or none).
        List<TimeSeriesDataPoint> points = res.Data is { Count: > 0 } ? res.Data[0].DataPoints : [];
        return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Success(points);
    }

    public async Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
        Guid clusterId, string name, string? service, string dimension, DateTime from, DateTime to,
        int buckets = 60, int maxSeries = 24, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        string selector = PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, service);
        string label = PromMetricsQueryBuilder.BreakdownLabel(dimension);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        string promql = PromMetricsQueryBuilder.BuildSeriesByQuery(name, selector, label, rate, maxSeries);

        KubernetesOperationResult<List<PrometheusTimeSeries>> res =
            await prometheus.GetMetricRangeAsync(clusterId, promql, to - from, ct);
        if (!res.IsSuccess)
            return KubernetesOperationResult<List<MetricSeriesGroup>>.Failure(res.Error!);

        List<MetricSeriesGroup> groups = (res.Data ?? [])
            .Select(s => new MetricSeriesGroup(
                s.Labels.TryGetValue(label, out string? v) && !string.IsNullOrEmpty(v) ? v : "(unlabeled)",
                s.DataPoints))
            .ToList();
        return KubernetesOperationResult<List<MetricSeriesGroup>>.Success(groups);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
