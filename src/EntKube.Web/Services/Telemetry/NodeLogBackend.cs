using EntKube.Telemetry;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Reads a cluster's logs from its own in-cluster telemetry node instead of from the management plane's
/// store — one HTTP request per user action, answered next to the data, rather than a Lucene scan across
/// every segment the management plane holds for the tenant.
/// </summary>
public sealed class NodeLogBackend(TelemetryNodeClient client) : ILogBackend
{
    public bool IsEnabled => true;

    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => client.IsAvailableAsync(clusterId, ct);

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
        => client.PostAsync<List<LokiLogStream>>(clusterId, "api/logs/search",
            LogSearchBody.ForFilter(filter, from, to, limit: limit), ct);

    public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => client.PostAsync<List<LogHistogramBucket>>(clusterId, "api/logs/histogram",
            LogSearchBody.ForFilter(filter, from, to, buckets: buckets), ct);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => client.GetAsync<List<LokiLogStream>>(clusterId,
            $"api/logs/by-trace?traceId={Uri.EscapeDataString(traceId)}&limit={limit}", ct);

    public Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
        => client.PostAsync<long>(clusterId, "api/logs/count", LogSearchBody.ForFilter(
            new LogQueryFilter { Namespaces = ns is null ? [] : [ns], Text = matchText, MinLevel = minLevel },
            from, to), ct);
}

/// <summary>
/// Reads a cluster's traces from its in-cluster telemetry node. The node handles the tier split and, where
/// an aggregate cannot be merged from two halves, the delegation — none of which is visible from here.
/// </summary>
public sealed class NodeTraceService(TelemetryNodeClient client) : ITraceQueryService
{
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => client.IsAvailableAsync(clusterId, ct);

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => client.PostAsync<List<string>>(clusterId, "api/traces/services", new TraceQueryBody
        {
            Namespaces = namespaces, PodPattern = podPattern, WindowMinutes = windowMinutes,
        }, ct);

    public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.PostAsync<List<TraceSummary>>(clusterId, "api/traces/list", new TraceQueryBody
        {
            Service = service, From = from, To = to, MinDurationMs = minDurationMs,
            ErrorsOnly = errorsOnly, Limit = limit, Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => client.PostAsync<List<SpanRecord>>(clusterId, "api/traces/trace",
            new TraceQueryBody { TraceId = traceId, Namespaces = namespaces }, ct);

    public Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.PostAsync<List<RedBucket>>(clusterId, "api/traces/red", new TraceQueryBody
        {
            Service = service, From = from, To = to, Buckets = buckets,
            Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => client.PostAsync<List<ServiceEdge>>(clusterId, "api/traces/map", new TraceQueryBody
        {
            From = from, To = to, Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
        => client.PostAsync<ServiceStats>(clusterId, "api/traces/stats",
            new TraceQueryBody { Service = service, From = from, To = to }, ct);
}
