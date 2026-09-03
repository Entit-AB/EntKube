using EntKube.Telemetry;
using EntKube.Web.Services;

namespace EntKube.TelemetryNode;

/// <summary>
/// An <see cref="ILogBackend"/> that calls another node's log query API over HTTP.
///
/// The querier uses it to reach the indexer's hot tier — the unsealed active index, which exists only in
/// the process writing it and therefore cannot be read from object storage. Everything else the querier
/// reads itself. See <see cref="FederatedLogBackend"/> for how the two halves are combined.
///
/// Failures surface as failed results rather than exceptions, because the caller is a federation that can
/// still return the half it owns: an indexer that is restarting should cost you the most recent minute of
/// logs, not the whole search.
/// </summary>
public sealed class HttpLogBackend(NodeHttpApi api) : ILogBackend
{
    public bool IsEnabled => true;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        KubernetesOperationResult<List<string>> namespaces = await GetNamespacesAsync(clusterId, 60 * 24 * 7, ct);
        return namespaces is { IsSuccess: true, Data.Count: > 0 };
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
        => api.GetAsync<List<string>>($"namespaces?windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => api.GetAsync<List<string>>($"pods?ns={Uri.EscapeDataString(namespaceName)}&windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => api.GetAsync<List<string>>($"containers?ns={Uri.EscapeDataString(namespaceName)}&windowMinutes={windowMinutes}", ct);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => api.PostAsync<List<LokiLogStream>, LogSearchBody>("search", LogSearchBody.ForFilter(filter, from, to, limit: limit), ct);

    public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => api.PostAsync<List<LogHistogramBucket>, LogSearchBody>("histogram", LogSearchBody.ForFilter(filter, from, to, buckets: buckets), ct);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => api.GetAsync<List<LokiLogStream>>($"by-trace?traceId={Uri.EscapeDataString(traceId)}&limit={limit}", ct);

    public Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
    {
        LogQueryFilter filter = new()
        {
            Namespaces = ns is null ? [] : [ns],
            Text = matchText,
            MinLevel = minLevel,
        };
        return api.PostAsync<long, LogSearchBody>("count", LogSearchBody.ForFilter(filter, from, to), ct);
    }


}
