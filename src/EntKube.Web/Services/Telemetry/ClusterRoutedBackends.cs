using System.Collections.Concurrent;
using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Chooses, per cluster, between reading telemetry from the cluster's own in-cluster node and reading it
/// from the management plane's local segment store.
///
/// This sits behind <see cref="LogQueryService"/>'s existing "native vs Loki" decision rather than beside
/// it, so nothing above changes: the viewers still see one <see cref="ILogBackend"/>, and the migration is
/// a routing detail rather than a new axis in every caller.
///
/// The rule is <b>read from wherever the data is</b> — see <see cref="TelemetryRoute"/>. It used to be
/// "in-cluster if a node is present", which is a statement about configuration rather than about data, and
/// installing the indexer made it wrong: a node exists from that moment while ingest only moves when the
/// collector is re-applied, so every view read an empty node and showed nothing, with no error. Data
/// already in the management plane's store stays readable for its full retention on every cluster that has
/// not been cut over.
/// </summary>
public sealed class ClusterRoutedLogBackend(
    NodeLogBackend inCluster,
    SegmentLogService local,
    TelemetryNodeClient nodes,
    EntKubeTelemetryService telemetry,
    ILogger<ClusterRoutedLogBackend> logger) : ILogBackend
{
    private readonly RouteCache _routes = new();

    public bool IsEnabled => true;

    private async Task<ILogBackend> RouteAsync(Guid clusterId, CancellationToken ct)
        => await _routes.UseInClusterAsync(clusterId, nodes, telemetry,
            token => inCluster.HasDataAsync(clusterId, token), logger, ct) ? inCluster : local;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).HasDataAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).GetNamespacesAsync(clusterId, windowMinutes, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).GetPodsAsync(clusterId, namespaceName, windowMinutes, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).GetContainersAsync(clusterId, namespaceName, windowMinutes, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).QueryAsync(clusterId, filter, from, to, limit, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).QueryByTraceAsync(clusterId, traceId, limit, ct);

    public async Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).GetHistogramAsync(clusterId, filter, from, to, buckets, ct);

    public async Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).CountAsync(clusterId, ns, matchText, minLevel, from, to, ct);
}

/// <summary>The trace equivalent of <see cref="ClusterRoutedLogBackend"/>, on the same rule.</summary>
public sealed class ClusterRoutedTraceService(
    NodeTraceService inCluster,
    SegmentTraceService local,
    TelemetryNodeClient nodes,
    EntKubeTelemetryService telemetry,
    ILogger<ClusterRoutedTraceService> logger) : ITraceQueryService
{
    private readonly RouteCache _routes = new();

    private async Task<ITraceQueryService> RouteAsync(Guid clusterId, CancellationToken ct)
        => await _routes.UseInClusterAsync(clusterId, nodes, telemetry,
            token => inCluster.HasDataAsync(clusterId, token), logger, ct) ? inCluster : local;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).HasDataAsync(clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => await (await RouteAsync(clusterId, ct)).GetServicesAsync(clusterId, ct, namespaces, podPattern, windowMinutes);

    public async Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => await (await RouteAsync(clusterId, ct)).ListTracesAsync(
            clusterId, service, from, to, minDurationMs, errorsOnly, limit, ct, namespaces, podPattern);

    public async Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => await (await RouteAsync(clusterId, ct)).GetTraceAsync(clusterId, traceId, ct, namespaces);

    public async Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => await (await RouteAsync(clusterId, ct)).GetServiceRedAsync(
            clusterId, service, from, to, buckets, ct, namespaces, podPattern);

    public async Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => await (await RouteAsync(clusterId, ct)).GetServiceMapAsync(clusterId, from, to, ct, namespaces, podPattern);

    public async Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
        => await (await RouteAsync(clusterId, ct)).GetServiceStatsAsync(clusterId, service, from, to, ct);
}

/// <summary>
/// Memoizes the per-cluster routing decision. Without it the decision is re-made on every method of every
/// panel — and for a cluster with no node that decision costs a Service lookup against a remote API
/// server, which is precisely the per-call round-trip this whole change exists to remove.
/// </summary>
internal sealed class RouteCache
{
    private readonly ConcurrentDictionary<Guid, (DateTime At, bool InCluster)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async Task<bool> UseInClusterAsync(
        Guid clusterId, TelemetryNodeClient nodes, EntKubeTelemetryService telemetry,
        Func<CancellationToken, Task<bool>> nodeHasData, ILogger logger, CancellationToken ct)
    {
        if (_cache.TryGetValue(clusterId, out (DateTime At, bool InCluster) hit)
            && DateTime.UtcNow - hit.At < Ttl)
            return hit.InCluster;

        (bool inCluster, string why) = await DecideAsync(clusterId, nodes, telemetry, nodeHasData, ct);

        if (!_cache.TryGetValue(clusterId, out (DateTime At, bool InCluster) prior) || prior.InCluster != inCluster)
        {
            logger.LogInformation("Telemetry for cluster {ClusterId} routes to {Target} — {Why}.",
                clusterId, inCluster ? "its in-cluster node" : "the management-plane store", why);
        }
        _cache[clusterId] = (DateTime.UtcNow, inCluster);
        return inCluster;
    }

    private static Task<(bool InCluster, string Why)> DecideAsync(
        Guid clusterId, TelemetryNodeClient nodes, EntKubeTelemetryService telemetry,
        Func<CancellationToken, Task<bool>> nodeHasData, CancellationToken ct)
        => TelemetryRoute.DecideAsync(
            () => nodes.IsAvailableAsync(clusterId, ct),
            () => nodeHasData(ct),
            () => telemetry.ManagementPlaneStillReceivesAsync(clusterId, ct));
}

/// <summary>
/// Where a cluster's telemetry actually is.
///
/// <para>Existence of a node is NOT the answer, though it used to be. Installing the indexer makes a node
/// exist immediately; it does not move ingest, which keeps arriving at the management plane until the
/// collector is re-applied. A node with nothing in it answers every query successfully and empty, so
/// routing on existence blanks every log and trace view at the moment of install, with no error anywhere
/// to explain it.</para>
///
/// <para>So the node is asked whether it holds anything, and that answer wins: it is a fact about the data
/// rather than an inference from configuration, which means a cluster whose stored collector values are out
/// of date — cut over by an earlier build that only rendered the repoint instead of recording it — still
/// reads from the node that has its logs. Configuration is consulted only when the node is empty and cannot
/// settle it, and there the question is whether the management plane is still the collector's
/// destination.</para>
///
/// <para>Separated from the caching and the concrete clients so the precedence, which is the part with the
/// rules in it, can be read and tested on its own.</para>
/// </summary>
public static class TelemetryRoute
{
    public static async Task<(bool InCluster, string Why)> DecideAsync(
        Func<Task<bool>> nodePresent, Func<Task<bool>> nodeHasData, Func<Task<bool?>> managementPlaneStillReceives)
    {
        if (!await nodePresent())
            return (false, "no in-cluster telemetry node is installed on it");

        if (await nodeHasData())
            return (true, "its node holds the data");

        // The node is there and empty. Either nothing has been ingested yet, or ingest never moved.
        return await managementPlaneStillReceives() == true
            ? (false, "its telemetry node is empty and its collector still exports to the management "
                      + "plane — re-apply the collector to complete the cutover")
            : (true, "its node is the destination for this cluster's telemetry, and has nothing yet");
    }
}
