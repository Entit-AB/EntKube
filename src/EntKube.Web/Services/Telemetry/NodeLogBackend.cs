using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

// Every call here is a GET, including the ones that logically carry a body. Requests reach a node through
// the Kubernetes API server's proxy, which maps HTTP methods onto RBAC verbs on services/proxy: a GET needs
// `get`, a POST needs `create`. A kubeconfig that reads a cluster perfectly well can lack the latter, and
// the refusal arrives as a bare 401/403 that looks like the node rejecting a token. Loki and Prometheus
// never meet this because their APIs are GET-only; ours has bodies, so they travel in the URL instead.

/// <summary>
/// Reads a cluster's logs from its own in-cluster telemetry node instead of from the management plane's
/// store — one HTTP request per user action, answered next to the data, rather than a Lucene scan across
/// every segment the management plane holds for the tenant.
/// </summary>
public sealed class NodeLogBackend(TelemetryNodeClient client) : ILogBackend
{
    public bool IsEnabled => true;

    /// <summary>
    /// Whether the node holds any of this cluster's logs — asked of the node, not inferred from the fact
    /// that it answered. The distinction is the whole difference between "this cluster is quiet" and "the
    /// data is going somewhere else", and it is what the read routing turns on.
    /// </summary>
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => AskAsync(client, clusterId, "api/logs/has-data", ct);

    /// <summary>
    /// False on any failure, deliberately. A node that cannot be reached, or one running an image from
    /// before this route existed, has not told us it holds data — and treating silence as "yes" is exactly
    /// how reads get pointed at an empty store.
    /// </summary>
    internal static async Task<bool> AskAsync(
        TelemetryNodeClient client, Guid clusterId, string path, CancellationToken ct)
    {
        KubernetesOperationResult<bool> result = await client.GetAsync<bool>(clusterId, path, ct);
        return result.IsSuccess && result.Data;
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
        => client.GetAsync<List<string>>(clusterId, $"api/logs/namespaces?windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => client.GetAsync<List<string>>(clusterId,
            $"api/logs/pods?ns={Uri.EscapeDataString(namespaceName)}&windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => client.GetAsync<List<string>>(clusterId,
            $"api/logs/containers?ns={Uri.EscapeDataString(namespaceName)}&windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => client.GetAsync<List<LokiLogStream>>(clusterId,
            $"api/logs/search?{NodeQuery.Parameter}={NodeQuery.Encode(LogSearchBody.ForFilter(filter, from, to, limit: limit))}", ct);

    public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => client.GetAsync<List<LogHistogramBucket>>(clusterId,
            $"api/logs/histogram?{NodeQuery.Parameter}={NodeQuery.Encode(LogSearchBody.ForFilter(filter, from, to, buckets: buckets))}", ct);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => client.GetAsync<List<LokiLogStream>>(clusterId,
            $"api/logs/by-trace?traceId={Uri.EscapeDataString(traceId)}&limit={limit}", ct);

    public Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
        => client.GetAsync<long>(clusterId, $"api/logs/count?{NodeQuery.Parameter}={NodeQuery.Encode(LogSearchBody.ForFilter(
            new LogQueryFilter { Namespaces = ns is null ? [] : [ns], Text = matchText, MinLevel = minLevel },
            from, to))}", ct);
}

/// <summary>
/// Reads a cluster's traces from its in-cluster telemetry node. The node handles the tier split and, where
/// an aggregate cannot be merged from two halves, the delegation — none of which is visible from here.
/// </summary>
public sealed class NodeTraceService(TelemetryNodeClient client) : ITraceQueryService
{
    /// <summary>Whether the node holds any of this cluster's spans. See NodeLogBackend.HasDataAsync.</summary>
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => NodeLogBackend.AskAsync(client, clusterId, "api/traces/has-data", ct);

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => client.GetAsync<List<string>>(clusterId,
            $"api/traces/services?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody
        {
            Namespaces = namespaces, PodPattern = podPattern, WindowMinutes = windowMinutes,
        })}", ct);

    public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.GetAsync<List<TraceSummary>>(clusterId,
            $"api/traces/list?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody
        {
            Service = service, From = from, To = to, MinDurationMs = minDurationMs,
            ErrorsOnly = errorsOnly, Limit = limit, Namespaces = namespaces, PodPattern = podPattern,
        })}", ct);

    public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => client.GetAsync<List<SpanRecord>>(clusterId,
            $"api/traces/trace?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody { TraceId = traceId, Namespaces = namespaces })}", ct);

    public Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.GetAsync<List<RedBucket>>(clusterId,
            $"api/traces/red?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody
        {
            Service = service, From = from, To = to, Buckets = buckets,
            Namespaces = namespaces, PodPattern = podPattern,
        })}", ct);

    public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.GetAsync<List<ServiceEdge>>(clusterId,
            $"api/traces/map?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody
        {
            From = from, To = to, Namespaces = namespaces, PodPattern = podPattern,
        })}", ct);

    public Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
        => client.GetAsync<ServiceStats>(clusterId,
            $"api/traces/stats?{NodeQuery.Parameter}={NodeQuery.Encode(new TraceQueryBody { Service = service, From = from, To = to })}", ct);
}
