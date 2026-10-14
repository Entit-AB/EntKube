using System.Collections.Concurrent;
using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Hands out the log and trace backends for one asking tenant.
///
/// <para><b>Why the tenant is on the factory and not on the backend.</b> Routing has to know who is
/// asking, because part of the decision is a Catalog question (<see
/// cref="EntKubeTelemetryService.ManagementPlaneStillReceivesAsync"/>) and every method on that
/// contract is tenant-scoped. The obvious move — a <c>tenantId</c> on <see cref="ILogBackend"/> and
/// <see cref="ITraceQueryService"/> — is the wrong one: those interfaces live in
/// <c>EntKube.Telemetry</c> and are implemented <em>inside the in-cluster node</em>
/// (<c>HttpLogBackend</c>, <c>FederatedLogBackend</c>, <c>HttpTraceBackend</c>,
/// <c>FederatedTraceService</c>) and mapped onto its HTTP routes. A node serves exactly one cluster
/// and authenticates with a token; a tenant means nothing there, so the parameter would be ignored
/// by every implementation that is not the management plane's. It would also make a
/// management-plane authorization change into a change to the node's wire surface — which reaches no
/// cluster until the chart's <c>appVersion</c> and both catalog pins are bumped and a new image is
/// shipped.
/// </para>
///
/// <para>So the tenant is supplied where the other authorization inputs in this codebase are
/// supplied: by a factory that takes it, the same shape as
/// <c>IClusterClientFactory.ForAsync(tenantId, clusterId)</c>. Cluster ids still flow per call; the
/// asking tenant is fixed when the backend is obtained.</para>
/// </summary>
public interface ITelemetryBackends
{
    /// <summary>The log backend as <paramref name="tenantId"/> sees it.</summary>
    ILogBackend LogsFor(Guid tenantId);

    /// <summary>The trace backend as <paramref name="tenantId"/> sees it.</summary>
    ITraceQueryService TracesFor(Guid tenantId);
}

/// <summary>
/// The default <see cref="ITelemetryBackends"/>: per tenant, wraps the real backends in the
/// per-cluster routing decision described on <see cref="TelemetryRoute"/>.
///
/// <para>The route caches live here rather than on the per-tenant wrappers, which is the whole
/// reason the wrappers are memoized too. The decision costs a Service lookup against a remote API
/// server for a cluster with no node, and the viewers issue facade calls in parallel — a search and
/// its histogram together, every dropdown at once. A cache per wrapper would be a cache per call.
/// One per signal, keyed by (tenant, cluster), is shared by every wrapper this router hands out.</para>
/// </summary>
public sealed class TelemetryBackendRouter(
    NodeLogBackend inClusterLogs,
    SegmentLogService localLogs,
    NodeTraceService inClusterTraces,
    SegmentTraceService localTraces,
    TelemetryNodeClient nodes,
    EntKubeTelemetryService telemetry,
    ILoggerFactory loggers) : ITelemetryBackends
{
    // One per signal: the two decisions differ in their "does the node hold anything" probe, which is
    // the step that actually settles it.
    private readonly RouteCache logRoutes = new();
    private readonly RouteCache traceRoutes = new();

    private readonly ConcurrentDictionary<Guid, ILogBackend> logs = new();
    private readonly ConcurrentDictionary<Guid, ITraceQueryService> traces = new();

    public ILogBackend LogsFor(Guid tenantId) => logs.GetOrAdd(tenantId, t =>
        new ClusterRoutedLogBackend(t, inClusterLogs, localLogs, nodes, telemetry, logRoutes,
            loggers.CreateLogger<ClusterRoutedLogBackend>()));

    public ITraceQueryService TracesFor(Guid tenantId) => traces.GetOrAdd(tenantId, t =>
        new ClusterRoutedTraceService(t, inClusterTraces, localTraces, nodes, telemetry, traceRoutes,
            loggers.CreateLogger<ClusterRoutedTraceService>()));
}

/// <summary>
/// Chooses, per cluster, between reading telemetry from the cluster's own in-cluster node and reading it
/// from the management plane's local segment store, on behalf of one tenant.
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
///
/// <para>Obtained from <see cref="ITelemetryBackends"/>, never from DI: the tenant is a constructor
/// argument because it is an input to the decision, not something to be derived from the cluster
/// being asked about.</para>
/// </summary>
public sealed class ClusterRoutedLogBackend(
    Guid tenantId,
    NodeLogBackend inCluster,
    SegmentLogService local,
    TelemetryNodeClient nodes,
    EntKubeTelemetryService telemetry,
    RouteCache routes,
    ILogger<ClusterRoutedLogBackend> logger) : ILogBackend
{
    public bool IsEnabled => true;

    private async Task<ILogBackend> RouteAsync(Guid clusterId, CancellationToken ct)
        => await routes.UseInClusterAsync(tenantId, clusterId,
            () => nodes.IsAvailableAsync(clusterId, ct),
            () => inCluster.HasDataAsync(clusterId, ct),
            () => telemetry.ManagementPlaneStillReceivesAsync(tenantId, clusterId, ct),
            logger, ct) ? inCluster : local;

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
    Guid tenantId,
    NodeTraceService inCluster,
    SegmentTraceService local,
    TelemetryNodeClient nodes,
    EntKubeTelemetryService telemetry,
    RouteCache routes,
    ILogger<ClusterRoutedTraceService> logger) : ITraceQueryService
{
    private async Task<ITraceQueryService> RouteAsync(Guid clusterId, CancellationToken ct)
        => await routes.UseInClusterAsync(tenantId, clusterId,
            () => nodes.IsAvailableAsync(clusterId, ct),
            () => inCluster.HasDataAsync(clusterId, ct),
            () => telemetry.ManagementPlaneStillReceivesAsync(tenantId, clusterId, ct),
            logger, ct) ? inCluster : local;

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
///
/// <para><b>Keyed by (tenant, cluster), not by cluster.</b> A cluster has one owner, so the pair looks
/// redundant — but the decision now contains a question asked <em>as</em> a tenant: a tenant who may
/// not see the cluster gets nothing back from Catalog and therefore "no collector is exporting here",
/// which routes to the node. Cached under the cluster id alone, that answer would be handed to the
/// <em>owner</em> next, whose collector is still exporting to the management plane — and an empty node
/// answers successfully, so their logs and traces would go blank with no error anywhere. The two
/// answers are to different questions and must not share a key. <c>TelemetryRoutingTenancyTests</c>
/// asks the same pair in both orders, which is the only order sensitive to it.</para>
/// </summary>
public sealed class RouteCache
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid ClusterId), (DateTime At, bool InCluster)> _cache = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    /// <param name="nodePresent">Whether the cluster has an in-cluster telemetry node at all.</param>
    /// <param name="nodeHasData">Whether that node holds anything for this signal.</param>
    /// <param name="stillReceives">
    /// Whether the management plane is still this cluster's collector destination, <em>asked as
    /// <paramref name="tenantId"/></em> — which is the whole reason the key below carries the tenant.
    /// </param>
    /// <remarks>
    /// The three questions arrive as delegates rather than as the clients that answer them, for the
    /// same reason <see cref="TelemetryRoute"/> is separate from both: the caching rule and the
    /// precedence rule are each testable on their own, and neither needs a cluster, a node or a
    /// database to exercise.
    /// </remarks>
    public async Task<bool> UseInClusterAsync(
        Guid tenantId, Guid clusterId,
        Func<Task<bool>> nodePresent, Func<Task<bool>> nodeHasData, Func<Task<bool?>> stillReceives,
        ILogger logger, CancellationToken ct = default)
    {
        if (_cache.TryGetValue((tenantId, clusterId), out (DateTime At, bool InCluster) hit)
            && DateTime.UtcNow - hit.At < Ttl)
            return hit.InCluster;

        (bool inCluster, string why) = await TelemetryRoute.DecideAsync(nodePresent, nodeHasData, stillReceives);

        if (!_cache.TryGetValue((tenantId, clusterId), out (DateTime At, bool InCluster) prior)
            || prior.InCluster != inCluster)
        {
            logger.LogInformation("Telemetry for cluster {ClusterId} routes to {Target} — {Why}.",
                clusterId, inCluster ? "its in-cluster node" : "the management-plane store", why);
        }
        _cache[(tenantId, clusterId)] = (DateTime.UtcNow, inCluster);
        return inCluster;
    }
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
