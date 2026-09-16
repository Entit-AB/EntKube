using EntKube.Telemetry;
using EntKube.Web.Services;

namespace EntKube.TelemetryNode;

/// <summary>
/// The querier's log backend: its own search over sealed segments from object storage, merged with the
/// indexer's search over the hot active index.
///
/// The split is forced by where the data physically is. Sealed segments are immutable archives in a bucket,
/// readable by any pod; the active index is an open Lucene writer that exists only in the process appending
/// to it. So neither pod can answer alone, and the results have to be combined here rather than inside one
/// Lucene searcher.
///
/// <para><b>Partial results are returned, not errors.</b> If the indexer is restarting, its hot tier is a
/// few minutes of logs; the sealed history is hours to months. Failing the whole query would turn a rolling
/// restart into a total log outage, so a failing half is logged and the other half is returned. The
/// opposite — object storage unreachable — is reported the same way, and there the hot tier alone is still
/// enough to see what a workload is doing right now.</para>
/// </summary>
public sealed class FederatedLogBackend(
    ILogBackend sealedTier,
    ILogBackend hotTier,
    ILogger<FederatedLogBackend> logger) : ILogBackend
{
    public bool IsEnabled => true;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        bool[] both = await Task.WhenAll(
            sealedTier.HasDataAsync(clusterId, ct),
            hotTier.HasDataAsync(clusterId, ct));
        return both[0] || both[1];
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetNamespacesAsync(clusterId, windowMinutes, ct),
            hotTier.GetNamespacesAsync(clusterId, windowMinutes, ct),
            MergeLabels, "namespaces");

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetPodsAsync(clusterId, namespaceName, windowMinutes, ct),
            hotTier.GetPodsAsync(clusterId, namespaceName, windowMinutes, ct),
            MergeLabels, "pods");

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetContainersAsync(clusterId, namespaceName, windowMinutes, ct),
            hotTier.GetContainersAsync(clusterId, namespaceName, windowMinutes, ct),
            MergeLabels, "containers");

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.QueryAsync(clusterId, filter, from, to, limit, ct),
            hotTier.QueryAsync(clusterId, filter, from, to, limit, ct),
            (a, b) => MergeStreams(a, b, limit), "search");

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.QueryByTraceAsync(clusterId, traceId, limit, ct),
            hotTier.QueryByTraceAsync(clusterId, traceId, limit, ct),
            (a, b) => MergeStreams(a, b, limit), "by-trace");

    public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetHistogramAsync(clusterId, filter, from, to, buckets, ct),
            hotTier.GetHistogramAsync(clusterId, filter, from, to, buckets, ct),
            MergeHistogram, "histogram");

    public Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
        => MergeAsync(
            sealedTier.CountAsync(clusterId, ns, matchText, minLevel, from, to, ct),
            hotTier.CountAsync(clusterId, ns, matchText, minLevel, from, to, ct),
            (a, b) => a + b, "count");

    // ── Merges ───────────────────────────────────────────────────────────────────────────────────────

    private static List<string> MergeLabels(List<string> sealedValues, List<string> hotValues) =>
        [.. sealedValues.Concat(hotValues).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Combines two sets of log streams. Streams are keyed by their label set, so the same pod's lines from
    /// both tiers land in one stream rather than appearing as two — which is what the log viewer groups on.
    /// Entries are re-sorted newest-first and the caller's limit re-applied to the merged result, because
    /// each half honoured that limit independently and their union would otherwise exceed it.
    ///
    /// Reference equality on entries is what makes the trim exact — see the comment inside.
    /// </summary>
    private static List<LokiLogStream> MergeStreams(
        List<LokiLogStream> sealedStreams, List<LokiLogStream> hotStreams, int limit)
    {
        Dictionary<string, LokiLogStream> merged = new(StringComparer.Ordinal);

        foreach (LokiLogStream stream in sealedStreams.Concat(hotStreams))
        {
            // Unit Separator between pairs: it cannot occur in a Kubernetes label value, so two different
            // label sets can never collide into one key by concatenation.
            string key = string.Join('\u001f', stream.Labels
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

            if (merged.TryGetValue(key, out LokiLogStream? existing))
                existing.Entries.AddRange(stream.Entries);
            else
                merged[key] = new LokiLogStream { Labels = stream.Labels, Entries = [.. stream.Entries] };
        }

        List<LokiLogStream> result = [.. merged.Values];

        int total = result.Sum(s => s.Entries.Count);
        if (total > limit)
        {
            // Trim to the newest `limit` ENTRIES across all streams. Selecting by timestamp value would
            // be wrong: log lines routinely share a millisecond, so a timestamp-based cut keeps every tie
            // and overshoots the limit — or drops a whole burst. Rank the entry objects themselves.
            HashSet<LokiLogEntry> keep = [.. result
                .SelectMany(s => s.Entries)
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)];

            foreach (LokiLogStream stream in result)
                stream.Entries.RemoveAll(e => !keep.Contains(e));
            result = [.. result.Where(s => s.Entries.Count > 0)];
        }

        // Newest-first within each stream, matching what a single-tier query returns.
        foreach (LokiLogStream stream in result)
            stream.Entries.Sort((x, y) => y.Timestamp.CompareTo(x.Timestamp));

        return result;
    }

    /// <summary>
    /// Adds the two histograms bucket by bucket. Both halves bucket the same requested window with the same
    /// bucket count, so their bucket starts line up and can be summed on that key.
    /// </summary>
    private static List<LogHistogramBucket> MergeHistogram(
        List<LogHistogramBucket> sealedBuckets, List<LogHistogramBucket> hotBuckets)
    {
        Dictionary<DateTime, (long Total, long Errors)> byStart = [];
        foreach (LogHistogramBucket b in sealedBuckets.Concat(hotBuckets))
        {
            (long total, long errors) = byStart.GetValueOrDefault(b.Start);
            byStart[b.Start] = (total + b.Total, errors + b.Errors);
        }
        return [.. byStart.OrderBy(kv => kv.Key)
            .Select(kv => new LogHistogramBucket(kv.Key, kv.Value.Total, kv.Value.Errors))];
    }

    /// <summary>
    /// Awaits both halves and combines them. One failing half is degraded to a warning and the other is
    /// returned; only both failing is a failed query, and then the sealed tier's error is reported since
    /// that is where the bulk of the data — and the more likely misconfiguration — lives.
    /// </summary>
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
            logger.LogWarning(
                "The indexer's hot tier failed for {What} ({Error}); returning sealed results only, so the "
                + "most recent unsealed events are missing from this answer.", what, hotResult.Error);
            return sealedResult;
        }

        if (hotResult.IsSuccess)
        {
            logger.LogWarning(
                "The sealed tier failed for {What} ({Error}); returning the indexer's hot results only, so "
                + "this answer covers only events not yet sealed.", what, sealedResult.Error);
            return hotResult;
        }

        return KubernetesOperationResult<T>.Failure(
            $"Both telemetry tiers failed for {what}. Sealed: {sealedResult.Error}. Hot: {hotResult.Error}.");
    }
}
