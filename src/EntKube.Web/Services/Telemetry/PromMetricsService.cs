using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Prometheus-backed <see cref="IMetricsQuery"/>: the metrics explorer / dashboards read app metrics from
/// the cluster's Prometheus via PromQL rather than a native store (apps write to Prometheus directly). The
/// metric-name and service dropdowns come from Prometheus label values; the charts come from range queries.
/// PromQL is assembled by <see cref="PromMetricsQueryBuilder"/> (both label spellings, counter detection);
/// returns the same DTOs the old PgMetricsService did, so the UI is unchanged.
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
        => prometheus.GetLabelValuesMatchingAnyAsync(tenantId, clusterId, "__name__",
            PromMetricsQueryBuilder.BuildSelectors(namespaces, podPattern, null), DiscoveryLookback, ct);

    /// <summary>
    /// The service dropdown: OpenTelemetry's <c>service_name</c> and, for scraped pods, the
    /// <c>app_kubernetes_io_name</c> the scrape job copies — the label a chosen service filters the
    /// scraped spelling by. Fails only when both lookups fail.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        IReadOnlyList<string> selectors = PromMetricsQueryBuilder.BuildSelectors(namespaces, podPattern, null);

        KubernetesOperationResult<List<string>>[] both = await Task.WhenAll(
            prometheus.GetLabelValuesMatchingAnyAsync(tenantId, clusterId,
                PromMetricsQueryBuilder.ServiceLabel, selectors, DiscoveryLookback, ct),
            prometheus.GetLabelValuesMatchingAnyAsync(tenantId, clusterId,
                PromMetricsQueryBuilder.ScrapedServiceLabel, selectors, DiscoveryLookback, ct));

        if (both.All(r => !r.IsSuccess)) return both[0];

        return KubernetesOperationResult<List<string>>.Success(
            [.. both.Where(r => r.IsSuccess).SelectMany(r => r.Data ?? [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid tenantId, Guid clusterId, string name, string? service, DateTime from, DateTime to,
        int buckets = 60, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
    {
        IReadOnlyList<string> selectors = PromMetricsQueryBuilder.BuildSelectors(namespaces, podPattern, service);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        bool counter = await IsCounterAsync(name, namespaces, podPattern, service,
            s => prometheus.GetLabelValuesMatchingAnyAsync(tenantId, clusterId, "__name__", s, DiscoveryLookback, ct));
        string promql = PromMetricsQueryBuilder.BuildSeriesQuery(name, selectors, rate, counter);

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
        IReadOnlyList<string> selectors = PromMetricsQueryBuilder.BuildSelectors(namespaces, podPattern, service);
        int rate = PromMetricsQueryBuilder.RateWindowSeconds(from, to, buckets);
        bool counter = await IsCounterAsync(name, namespaces, podPattern, service,
            s => prometheus.GetLabelValuesMatchingAnyAsync(tenantId, clusterId, "__name__", s, DiscoveryLookback, ct));
        string promql = PromMetricsQueryBuilder.BuildSeriesByQuery(name, selectors, dimension, rate, maxSeries, counter);

        KubernetesOperationResult<List<PrometheusTimeSeries>> res =
            await prometheus.GetMetricRangeAsync(tenantId, clusterId, promql, to - from, ct);
        if (!res.IsSuccess)
            return KubernetesOperationResult<List<MetricSeriesGroup>>.Failure(res.Error!);

        return KubernetesOperationResult<List<MetricSeriesGroup>>.Success(
            PromMetricsQueryBuilder.CollapseBreakdown(res.Data ?? [], dimension));
    }

    /// <summary>
    /// Whether to rate <paramref name="name"/>. The name decides for <c>_total</c> and
    /// <c>_bucket</c>; a <c>_count</c> or <c>_sum</c> is a counter only when Prometheus holds its
    /// family's <c>_bucket</c> series (histogram) or quantiles (summary) within the same scope.
    /// A lookup that fails answers "gauge", which is what every name got before.
    /// </summary>
    /// <param name="lookupNames">
    /// Metric names matching any of the given selectors — the label-values call, taken as a
    /// delegate so the decision is testable without a Prometheus.
    /// </param>
    public static async Task<bool> IsCounterAsync(
        string name, IReadOnlyList<string>? namespaces, string? podPattern, string? service,
        Func<IReadOnlyList<string>, Task<KubernetesOperationResult<List<string>>>> lookupNames)
    {
        if (PromMetricsQueryBuilder.IsCounter(name)) return true;

        string? family = PromMetricsQueryBuilder.HistogramFamily(name);
        if (family is null) return false;

        KubernetesOperationResult<List<string>> found = await lookupNames(
            PromMetricsQueryBuilder.FamilyMemberSelectors(family, namespaces, podPattern, service));

        return found.IsSuccess && found.Data is { Count: > 0 };
    }
}
