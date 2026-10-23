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

    /// <summary>
    /// Whether this cluster's Prometheus knows any metric name at all.
    ///
    /// <para>⚠️ <b>A false from here is not evidence that the cluster has no metrics.</b> The bool
    /// has no room for a reason, so every failure of the query lands on it identically: no
    /// prometheus component on the cluster, no kubeconfig for the cluster, no ready Prometheus pod,
    /// a 401/403 or a timeout on the API-server pod-proxy — all indistinguishable from a healthy
    /// Prometheus that happens to know nothing. A caller that renders an empty state must therefore
    /// <b>not</b> gate on this method. MetricsExplorer did, and for every one of those outages it
    /// showed "no metrics for this cluster yet" with instructions to go and instrument your apps;
    /// it now runs <see cref="GetMetricNamesAsync"/> itself and renders
    /// <c>KubernetesOperationResult.Error</c> when the query fails, which is what every other
    /// Prometheus consumer in the UI does.</para>
    ///
    /// <para>The method is kept because it is part of <see cref="IMetricsQuery"/> (widening the
    /// seam to a result type is a bigger change than this fix, and nothing in the UI calls it any
    /// more), but the reason is no longer thrown away in silence: it goes to the log.</para>
    /// </summary>
    public async Task<bool> HasDataAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        KubernetesOperationResult<List<string>> names = await prometheus.GetLabelValuesAsync(
            tenantId, clusterId, "__name__", null, DiscoveryLookback, ct);

        if (!names.IsSuccess)
        {
            logger.LogWarning(
                "Metric discovery failed for cluster {ClusterId} (tenant {TenantId}): {Error}. " +
                "Reporting 'no data', which a caller cannot tell from an empty Prometheus",
                clusterId, tenantId, names.Error);
            return false;
        }

        return names.Data is { Count: > 0 };
    }

    public Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => prometheus.GetLabelValuesAsync(tenantId, clusterId, "__name__",
            NullIfEmpty(PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, null)), DiscoveryLookback, ct);

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => prometheus.GetLabelValuesAsync(tenantId, clusterId, PromMetricsQueryBuilder.ServiceLabel,
            NullIfEmpty(PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, null)), DiscoveryLookback, ct);

    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid tenantId, Guid clusterId, string name, string? service, DateTime from, DateTime to,
        int buckets = 60, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        string selector = PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, service);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        string promql = PromMetricsQueryBuilder.BuildSeriesQuery(name, selector, rate);

        KubernetesOperationResult<List<PrometheusTimeSeries>> res =
            await prometheus.GetMetricRangeAsync(tenantId, clusterId, promql, to - from, ct);
        if (!res.IsSuccess)
            return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Failure(res.Error!);

        // sum(...) collapses to a single series (or none).
        List<TimeSeriesDataPoint> points = res.Data is { Count: > 0 } ? res.Data[0].DataPoints : [];
        return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Success(points);
    }

    public async Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
        Guid tenantId, Guid clusterId, string name, string? service, string dimension,
        DateTime from, DateTime to, int buckets = 60, int maxSeries = 24,
        CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        string selector = PromMetricsQueryBuilder.BuildSelector(namespaces, podPattern, service);
        string label = PromMetricsQueryBuilder.BreakdownLabel(dimension);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        string promql = PromMetricsQueryBuilder.BuildSeriesByQuery(name, selector, label, rate, maxSeries);

        KubernetesOperationResult<List<PrometheusTimeSeries>> res =
            await prometheus.GetMetricRangeAsync(tenantId, clusterId, promql, to - from, ct);
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
