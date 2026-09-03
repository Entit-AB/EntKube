using EntKube.Telemetry;
using EntKube.Web.Services;

namespace EntKube.TelemetryNode;

/// <summary>
/// The querier's trace backend. Like <see cref="FederatedLogBackend"/> it combines its own sealed-segment
/// results with the indexer's hot tier — but traces force a distinction the log surface does not.
///
/// <para><b>Not every aggregate can be merged from two halves.</b> Counts and sums can: a service map edge
/// with 10 calls here and 5 there is 15 calls, and its average latency combines as a call-weighted mean.
/// <i>Percentiles cannot.</i> There is no function of two p95s that yields the p95 of the union — you need
/// the underlying distribution, which is exactly what an aggregate has thrown away. Merging them anyway
/// would produce a number that looks plausible, moves when the data moves, and is quietly wrong; latency
/// SLOs and alert thresholds are built on precisely these numbers.</para>
///
/// <para>So the percentile-bearing queries — RED buckets and service stats — are delegated whole to the
/// indexer's all-tier route, which computes them over one index and gets them right. That costs the
/// indexer a cold-segment scan for those two query types, which is the honest price of a correct number.
/// Everything else is merged here.</para>
/// </summary>
public sealed class FederatedTraceService(
    ITraceQueryService sealedTier,
    ITraceQueryService hotTier,
    ITraceQueryService allTiers,
    ILogger<FederatedTraceService> logger) : ITraceQueryService
{
    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        bool[] both = await Task.WhenAll(
            sealedTier.HasDataAsync(clusterId, ct), hotTier.HasDataAsync(clusterId, ct));
        return both[0] || both[1];
    }

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => MergeAsync(
            sealedTier.GetServicesAsync(clusterId, ct, namespaces, podPattern, windowMinutes),
            hotTier.GetServicesAsync(clusterId, ct, namespaces, podPattern, windowMinutes),
            (a, b) => [.. a.Concat(b).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            "services");

    public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => MergeAsync(
            sealedTier.ListTracesAsync(clusterId, service, from, to, minDurationMs, errorsOnly, limit, ct, namespaces, podPattern),
            hotTier.ListTracesAsync(clusterId, service, from, to, minDurationMs, errorsOnly, limit, ct, namespaces, podPattern),
            (a, b) => MergeTraceSummaries(a, b, limit), "trace list");

    public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => MergeAsync(
            sealedTier.GetTraceAsync(clusterId, traceId, ct, namespaces),
            hotTier.GetTraceAsync(clusterId, traceId, ct, namespaces),
            MergeSpans, "trace detail");

    public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => MergeAsync(
            sealedTier.GetServiceMapAsync(clusterId, from, to, ct, namespaces, podPattern),
            hotTier.GetServiceMapAsync(clusterId, from, to, ct, namespaces, podPattern),
            MergeEdges, "service map");

    // ── Delegated whole, because a percentile cannot be recovered from two percentiles ────────────────

    public Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => allTiers.GetServiceRedAsync(clusterId, service, from, to, buckets, ct, namespaces, podPattern);

    public Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
        => allTiers.GetServiceStatsAsync(clusterId, service, from, to, ct);

    // ── Merges ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Combines trace rows by trace id. A single trace can straddle the tiers — some of its spans sealed,
    /// the rest still in the active index — and would otherwise appear twice, each row under-reporting its
    /// span count. Counts add; the trace starts at the earliest span either half saw and lasts as long as
    /// the longer half measured; the root comes from whichever half actually holds the root span.
    /// </summary>
    private static List<TraceSummary> MergeTraceSummaries(
        List<TraceSummary> sealedRows, List<TraceSummary> hotRows, int limit)
    {
        Dictionary<string, TraceSummary> byTrace = new(StringComparer.Ordinal);

        foreach (TraceSummary row in sealedRows.Concat(hotRows))
        {
            if (!byTrace.TryGetValue(row.TraceId, out TraceSummary? existing))
            {
                byTrace[row.TraceId] = row;
                continue;
            }

            // An empty root service means that half never saw the root span, so prefer the half that did.
            bool takeRootFromNew = string.IsNullOrEmpty(existing.RootService) && !string.IsNullOrEmpty(row.RootService);
            byTrace[row.TraceId] = existing with
            {
                Start = existing.Start <= row.Start ? existing.Start : row.Start,
                DurationMs = Math.Max(existing.DurationMs, row.DurationMs),
                SpanCount = existing.SpanCount + row.SpanCount,
                ErrorCount = existing.ErrorCount + row.ErrorCount,
                RootService = takeRootFromNew ? row.RootService : existing.RootService,
                RootName = takeRootFromNew ? row.RootName : existing.RootName,
            };
        }

        return [.. byTrace.Values.OrderByDescending(t => t.Start).Take(limit)];
    }

    /// <summary>
    /// Unions a trace's spans across tiers, keeping one row per span id. The same span really can arrive
    /// from both halves — a segment sealed mid-query, say — and a duplicated span draws a duplicated bar in
    /// the waterfall.
    /// </summary>
    private static List<SpanRecord> MergeSpans(List<SpanRecord> sealedSpans, List<SpanRecord> hotSpans)
    {
        Dictionary<string, SpanRecord> bySpanId = new(StringComparer.Ordinal);
        foreach (SpanRecord span in sealedSpans.Concat(hotSpans))
            bySpanId.TryAdd(span.SpanId, span);
        return [.. bySpanId.Values.OrderBy(s => s.Start)];
    }

    /// <summary>
    /// Adds service-map edges. Calls and errors sum; the average latency is recombined as a call-weighted
    /// mean, which reconstructs the true average exactly — unlike averaging the two averages, which would
    /// let a handful of hot calls outweigh thousands of sealed ones.
    /// </summary>
    private static List<ServiceEdge> MergeEdges(List<ServiceEdge> sealedEdges, List<ServiceEdge> hotEdges)
    {
        Dictionary<(string From, string To), ServiceEdge> byEdge = [];
        foreach (ServiceEdge edge in sealedEdges.Concat(hotEdges))
        {
            (string, string) key = (edge.From, edge.To);
            if (!byEdge.TryGetValue(key, out ServiceEdge? existing))
            {
                byEdge[key] = edge;
                continue;
            }

            long calls = existing.Calls + edge.Calls;
            double avg = calls == 0
                ? 0
                : ((existing.AvgMs * existing.Calls) + (edge.AvgMs * edge.Calls)) / calls;
            byEdge[key] = existing with { Calls = calls, Errors = existing.Errors + edge.Errors, AvgMs = avg };
        }
        return [.. byEdge.Values.OrderByDescending(e => e.Calls)];
    }

    /// <summary>Same degradation policy as the log federation: one failing half is a warning, not an outage.</summary>
    private async Task<KubernetesOperationResult<T>> MergeAsync<T>(
        Task<KubernetesOperationResult<T>> sealedTask,
        Task<KubernetesOperationResult<T>> hotTask,
        Func<T, T, T> merge,
        string what)
    {
        await Task.WhenAll(sealedTask, hotTask);
        KubernetesOperationResult<T> sealedResult = sealedTask.Result;
        KubernetesOperationResult<T> hotResult = hotTask.Result;

        if (sealedResult.IsSuccess && hotResult.IsSuccess)
            return KubernetesOperationResult<T>.Success(merge(sealedResult.Data!, hotResult.Data!));

        if (sealedResult.IsSuccess)
        {
            logger.LogWarning("The indexer's hot tier failed for {What} ({Error}); returning sealed results only.",
                what, hotResult.Error);
            return sealedResult;
        }

        if (hotResult.IsSuccess)
        {
            logger.LogWarning("The sealed tier failed for {What} ({Error}); returning hot results only.",
                what, sealedResult.Error);
            return hotResult;
        }

        return KubernetesOperationResult<T>.Failure(
            $"Both telemetry tiers failed for {what}. Sealed: {sealedResult.Error}. Hot: {hotResult.Error}.");
    }
}
