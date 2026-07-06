namespace EntKube.Web.Services;

/// <summary>
/// Backend-agnostic log query facade the log viewers inject instead of a concrete backend.
/// For each cluster it routes to the native Postgres telemetry store when that store holds
/// recent data for the cluster, otherwise to Loki. The decision is data-driven, so:
///   • paid/BYO-Loki clusters (which ship to Loki, not the native store) → Loki,
///   • clusters not yet cut over → Loki,
///   • clusters shipping to the native store → Postgres,
/// all with no per-cluster flag or configuration. The method surface mirrors LokiService and
/// PgLogService exactly, so switching a viewer's injection to this type is a drop-in.
/// </summary>
public class LogQueryService(PgLogService native, LokiService loki, TelemetryStore store)
{
    // The routing probe (HasDataAsync = tenant resolve + SELECT 1) would otherwise run before EVERY
    // facade call — ~5 extra round-trips per log-panel load. Memoize per cluster for a few seconds:
    // long enough for one page render to share a single probe, short enough to pick up newly-arriving
    // native data. The service is scoped (per circuit), so this cache is per user session.
    private readonly Dictionary<Guid, (bool UseNative, DateTime At)> _routeCache = [];

    private async Task<bool> UseNativeAsync(Guid clusterId, CancellationToken ct)
    {
        if (!store.IsEnabled) return false;
        if (_routeCache.TryGetValue(clusterId, out (bool UseNative, DateTime At) c)
            && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5))
            return c.UseNative;

        bool useNative = await native.HasDataAsync(clusterId, ct);
        _routeCache[clusterId] = (useNative, DateTime.UtcNow);
        return useNative;
    }

    public async Task<bool> IsAvailableAsync(Guid clusterId, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct) || await loki.IsAvailableAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct)
            ? await native.GetNamespacesAsync(clusterId, ct)
            : await loki.GetNamespacesAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct)
            ? await native.GetPodsAsync(clusterId, namespaceName, ct)
            : await loki.GetPodsAsync(clusterId, namespaceName, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct)
            ? await native.GetContainersAsync(clusterId, namespaceName, ct)
            : await loki.GetContainersAsync(clusterId, namespaceName, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct)
            ? await native.QueryAsync(clusterId, filter, from, to, limit, ct)
            // Loki path: minLevel is re-applied client-side by the viewer; attribute filters are native-only.
            : await loki.QueryRangeMultiAsync(clusterId, filter.Namespaces, filter.Pod, filter.Container, filter.Text, from, to, limit, ct);

    /// <summary>
    /// Log-volume histogram for the native store; returns an empty set for Loki-backed clusters
    /// (the viewer then simply omits the volume strip). Attribute filters are native-only.
    /// </summary>
    public async Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, ct)
            ? await native.GetHistogramAsync(clusterId, filter, from, to, buckets, ct)
            : await loki.GetHistogramAsync(clusterId, filter, from, to, buckets, ct);
}
