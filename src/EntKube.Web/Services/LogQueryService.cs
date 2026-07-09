using EntKube.Web.Data;
using EntKube.Web.Services.Telemetry;
using Microsoft.EntityFrameworkCore;

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
public class LogQueryService(ILogBackend native, LokiService loki, IDbContextFactory<ApplicationDbContext> dbFactory)
{
    // The routing probes (component check + HasDataAsync) would otherwise run before EVERY facade
    // call — ~5 extra round-trips per log-panel load. Memoize per cluster for a few seconds: long
    // enough for one page render to share a single probe, short enough to pick up newly-arriving
    // native data. The service is scoped (per circuit), so this cache is per user session.
    private readonly Dictionary<Guid, (bool UseNative, DateTime At)> _routeCache = [];
    private readonly Dictionary<Guid, (bool Installed, DateTime At)> _installedCache = [];

    // True when the EntKube telemetry collector (otel-collector) is installed on the cluster.
    // This is the signal that EntKube native telemetry OWNS logs/traces for the cluster — so it
    // takes over even before the first batch lands, and even when Loki is also installed.
    private async Task<bool> NativeCollectorInstalledAsync(Guid clusterId, CancellationToken ct)
    {
        if (_installedCache.TryGetValue(clusterId, out (bool Installed, DateTime At) c)
            && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5))
            return c.Installed;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        bool installed = await db.ClusterComponents.AnyAsync(x =>
            x.ClusterId == clusterId
            && x.Status == ComponentStatus.Installed
            && ((x.HelmChartName != null && x.HelmChartName.Contains("opentelemetry-collector"))
                || (x.ReleaseName != null && x.ReleaseName.Contains("otel-collector"))
                || x.Name.Contains("otel-collector")), ct);
        _installedCache[clusterId] = (installed, DateTime.UtcNow);
        return installed;
    }

    // Auto = policy default; Native/Loki = the viewer's explicit override (a cluster can have both).
    private async Task<bool> UseNativeAsync(Guid clusterId, LogBackendKind backend, CancellationToken ct)
    {
        if (!native.IsEnabled) return false;
        if (backend == LogBackendKind.Native) return true;
        if (backend == LogBackendKind.Loki) return false;

        if (_routeCache.TryGetValue(clusterId, out (bool UseNative, DateTime At) c)
            && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5))
            return c.UseNative;

        // Auto: EntKube takes over when its collector is installed (even before data arrives);
        // otherwise use native only if it already holds data, else fall back to Loki.
        bool useNative = await NativeCollectorInstalledAsync(clusterId, ct)
            || await native.HasDataAsync(clusterId, ct);
        _routeCache[clusterId] = (useNative, DateTime.UtcNow);
        return useNative;
    }

    /// <summary>Which log backends are usable for this cluster — drives the viewer's source picker.
    /// Native counts as usable when its collector is installed (it owns the cluster) or it already
    /// holds data; Loki when its component is installed.</summary>
    public async Task<(bool Native, bool Loki)> ProbeBackendsAsync(Guid clusterId, CancellationToken ct = default)
        => (native.IsEnabled && (await NativeCollectorInstalledAsync(clusterId, ct) || await native.HasDataAsync(clusterId, ct)),
            await loki.IsAvailableAsync(clusterId, ct));

    public async Task<bool> IsAvailableAsync(Guid clusterId, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, LogBackendKind.Auto, ct) || await loki.IsAvailableAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, LogBackendKind backend = LogBackendKind.Auto, int windowMinutes = 60, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, backend, ct)
            ? await native.GetNamespacesAsync(clusterId, windowMinutes, ct)
            : await loki.GetNamespacesAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, LogBackendKind backend = LogBackendKind.Auto, int windowMinutes = 60, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, backend, ct)
            ? await native.GetPodsAsync(clusterId, namespaceName, windowMinutes, ct)
            : await loki.GetPodsAsync(clusterId, namespaceName, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, LogBackendKind backend = LogBackendKind.Auto, int windowMinutes = 60, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, backend, ct)
            ? await native.GetContainersAsync(clusterId, namespaceName, windowMinutes, ct)
            : await loki.GetContainersAsync(clusterId, namespaceName, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200,
        LogBackendKind backend = LogBackendKind.Auto, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, backend, ct)
            ? await native.QueryAsync(clusterId, filter, from, to, limit, ct)
            // Loki path: minLevel is re-applied client-side by the viewer; attribute filters are native-only.
            : await loki.QueryRangeMultiAsync(clusterId, filter.Namespaces, filter.Pod, filter.Container, filter.Text, from, to, limit, ct);

    /// <summary>
    /// Log-volume histogram for the native store; returns an empty set for Loki-backed clusters
    /// (the viewer then simply omits the volume strip). Attribute filters are native-only.
    /// </summary>
    public async Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48,
        LogBackendKind backend = LogBackendKind.Auto, CancellationToken ct = default)
        => await UseNativeAsync(clusterId, backend, ct)
            ? await native.GetHistogramAsync(clusterId, filter, from, to, buckets, ct)
            : await loki.GetHistogramAsync(clusterId, filter, from, to, buckets, ct);
}

/// <summary>Which log backend the viewer queries. Auto picks by where the data is.</summary>
public enum LogBackendKind { Auto, Native, Loki }
