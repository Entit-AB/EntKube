using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The native log query surface, implemented by the Lucene/S3 segment engine (<see cref="SegmentLogService"/>).
/// <see cref="LogQueryService"/> depends on this interface and routes each cluster to it or to Loki, so the
/// log viewers stay backend-agnostic. Every method is scoped by cluster (and, inside, by the cluster's tenant).
/// </summary>
public interface ILogBackend
{
    /// <summary>True when this native backend is configured/usable; false means always fall back to Loki.</summary>
    bool IsEnabled { get; }

    /// <summary>True when this backend holds any log for the cluster (drives native-vs-Loki routing).</summary>
    Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default);

    Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default);

    Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to, CancellationToken ct = default);
}
