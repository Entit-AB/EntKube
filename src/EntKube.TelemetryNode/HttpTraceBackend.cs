using EntKube.Telemetry;
using EntKube.Web.Services;

namespace EntKube.TelemetryNode;

/// <summary>
/// An <see cref="ITraceQueryService"/> that calls another node's trace API over HTTP.
///
/// The querier creates two of these against the indexer: one on <c>internal/traces</c> (hot tier only, to
/// merge with its own sealed results) and one on <c>api/traces</c> (all tiers), for the handful of queries
/// that cannot be correctly merged from two halves — see <see cref="FederatedTraceService"/>.
/// </summary>
public sealed class HttpTraceBackend(NodeHttpApi api) : ITraceQueryService
{
    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        KubernetesOperationResult<List<string>> services = await GetServicesAsync(clusterId, ct);
        return services is { IsSuccess: true, Data.Count: > 0 };
    }

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => api.PostAsync<List<string>, TraceQueryBody>(
            "services", new TraceQueryBody { Namespaces = namespaces, PodPattern = podPattern, WindowMinutes = windowMinutes }, ct);

    public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => api.PostAsync<List<TraceSummary>, TraceQueryBody>("list", new TraceQueryBody
        {
            Service = service, From = from, To = to, MinDurationMs = minDurationMs,
            ErrorsOnly = errorsOnly, Limit = limit, Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => api.PostAsync<List<SpanRecord>, TraceQueryBody>(
            "trace", new TraceQueryBody { TraceId = traceId, Namespaces = namespaces }, ct);

    public Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => api.PostAsync<List<RedBucket>, TraceQueryBody>("red", new TraceQueryBody
        {
            Service = service, From = from, To = to, Buckets = buckets,
            Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => api.PostAsync<List<ServiceEdge>, TraceQueryBody>("map", new TraceQueryBody
        {
            From = from, To = to, Namespaces = namespaces, PodPattern = podPattern,
        }, ct);

    public Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
        => api.PostAsync<ServiceStats, TraceQueryBody>(
            "stats", new TraceQueryBody { Service = service, From = from, To = to }, ct);
}
