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
    ILogger<FederatedLogBackend> logger,
    TimeSpan? halfBudget = null,
    TimeSpan? sealedBudget = null) : ILogBackend
{
    public bool IsEnabled => true;

    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        // Budgeted like every other merge, and this one matters most: it is the read-routing probe, so it
        // runs before the first thing an operator sees. A hot half that cannot answer must cost a few
        // seconds, not its client's whole timeout, in front of a page that has not started loading yet.
        KubernetesOperationResult<bool>[] both = await Task.WhenAll(
            WithinBudgetAsync(Wrap(sealedTier.HasDataAsync(clusterId, ct)), "sealed", "has-data", _sealedBudget),
            WithinBudgetAsync(Wrap(hotTier.HasDataAsync(clusterId, ct)), "hot", "has-data", _hotBudget));
        return both.Any(r => r is { IsSuccess: true, Data: true });

        static async Task<KubernetesOperationResult<bool>> Wrap(Task<bool> probe)
            => KubernetesOperationResult<bool>.Success(await probe);
    }

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetNamespacesAsync(clusterId, windowMinutes, ct),
            hotTier.GetNamespacesAsync(clusterId, windowMinutes, ct),
            MergeLabels, "namespaces", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetPodsAsync(clusterId, namespaceName, windowMinutes, ct),
            hotTier.GetPodsAsync(clusterId, namespaceName, windowMinutes, ct),
            MergeLabels, "pods", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, int windowMinutes = 60, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetContainersAsync(clusterId, namespaceName, windowMinutes, ct),
            hotTier.GetContainersAsync(clusterId, namespaceName, windowMinutes, ct),
            MergeLabels, "containers", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.QueryAsync(clusterId, filter, from, to, limit, ct),
            hotTier.QueryAsync(clusterId, filter, from, to, limit, ct),
            (a, b) => MergeStreams(a, b, limit), "search", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.QueryByTraceAsync(clusterId, traceId, limit, ct),
            hotTier.QueryByTraceAsync(clusterId, traceId, limit, ct),
            (a, b) => MergeStreams(a, b, limit), "by-trace", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
        => MergeAsync(
            sealedTier.GetHistogramAsync(clusterId, filter, from, to, buckets, ct),
            hotTier.GetHistogramAsync(clusterId, filter, from, to, buckets, ct),
            MergeHistogram, "histogram", static v => v.Count == 0);

    public Task<KubernetesOperationResult<long>> CountAsync(
        Guid clusterId, string? ns, string? matchText, LogLevel minLevel, DateTime from, DateTime to,
        CancellationToken ct = default)
        => MergeAsync(
            sealedTier.CountAsync(clusterId, ns, matchText, minLevel, from, to, ct),
            hotTier.CountAsync(clusterId, ns, matchText, minLevel, from, to, ct),
            (a, b) => a + b, "count", static v => v == 0);

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
    /// <summary>
    /// How long a half may keep the other waiting before it is counted as failed for this query.
    ///
    /// <para>Returning the working half was always the intent; paying the full HTTP client timeout first
    /// was not. The hot tier is one in-cluster hop to an index bounded by the roll trigger — under a
    /// second when it is healthy — but its client's ceiling is 30s, and a federation that awaits both
    /// halves spends every one of those seconds on every query before it hands back a sealed answer it
    /// already had. That is not a degraded search, it is a page that looks hung, and it is what a
    /// wedged or unreachable indexer costs each time an operator opens a log view.</para>
    ///
    /// <para>The overrunning half is left running rather than cancelled: it is already in flight, and its
    /// failure is worth logging. The client timeout stays as the hard ceiling behind this one.</para>
    /// </summary>
    private readonly TimeSpan _hotBudget = halfBudget ?? TimeSpan.FromSeconds(5);

    /// <summary>
    /// The sealed tier's own, larger budget.
    ///
    /// <para>One budget for both halves was wrong, because the two halves are not the same kind of thing.
    /// The reasoning above — "one in-cluster hop, under a second when healthy" — describes the HOT tier and
    /// only the hot tier. The sealed tier reads immutable archives out of object storage, and the first
    /// query over a window on a cold querier downloads and opens them. That is not a fault, it is the
    /// design; five seconds is simply not the right ceiling for it.</para>
    ///
    /// <para>Holding it to the hot tier's budget produced the worst possible outcome. The sealed half was
    /// abandoned, the healthy hot half came back holding nothing (everything being searched was already
    /// sealed), and the merge reported a successful, empty answer — which reads in the viewer as "this
    /// namespace has no logs". Searching again then worked, because the abandoned query had finished in
    /// the background and left its segment readers open. First search empty, second search fine, and
    /// nothing anywhere saying why.</para>
    /// </summary>
    private readonly TimeSpan _sealedBudget = sealedBudget ?? TimeSpan.FromSeconds(30);

    /// <param name="isEmpty">
    /// Whether a surviving half's answer contains nothing. Supplied per call because emptiness is
    /// type-shaped — no streams, no buckets, no labels, a count of zero — and because without it the
    /// partial-results rule below cannot tell the two cases apart that matter most.
    /// </param>
    private async Task<KubernetesOperationResult<T>> MergeAsync<T>(
        Task<KubernetesOperationResult<T>> sealedTask,
        Task<KubernetesOperationResult<T>> hotTask,
        Func<T, T, T> merge,
        string what,
        Func<T, bool>? isEmpty = null)
    {
        // Started together, waited on together. These were awaited one after the other, which meant the hot
        // tier's budget did not begin until the sealed tier had answered or given up: two slow halves cost
        // the SUM of the budgets, not the larger of them, and the ceiling was quietly double what it said.
        Task<KubernetesOperationResult<T>> sealedBudgeted = WithinBudgetAsync(sealedTask, "sealed", what, _sealedBudget);
        Task<KubernetesOperationResult<T>> hotBudgeted = WithinBudgetAsync(hotTask, "hot", what, _hotBudget);
        await Task.WhenAll(sealedBudgeted, hotBudgeted);

        KubernetesOperationResult<T> sealedResult = sealedBudgeted.Result;
        KubernetesOperationResult<T> hotResult = hotBudgeted.Result;

        if (sealedResult.IsSuccess && hotResult.IsSuccess)
            return KubernetesOperationResult<T>.Success(merge(sealedResult.Data!, hotResult.Data!));

        if (sealedResult.IsSuccess || hotResult.IsSuccess)
        {
            bool sealedSurvived = sealedResult.IsSuccess;
            KubernetesOperationResult<T> survivor = sealedSurvived ? sealedResult : hotResult;
            string lostTier = sealedSurvived ? "hot" : "sealed";
            string? lostError = sealedSurvived ? hotResult.Error : sealedResult.Error;

            // A partial answer that happens to be empty is not a partial answer, it is a wrong one. The
            // viewer has no way to show "nothing, from half the data" differently from "nothing" — so an
            // operator reads it as the definitive absence of logs and stops looking. Returning the failure
            // instead costs them an error message, which is the honest thing we have.
            if (survivor.Data is not null && isEmpty is not null && isEmpty(survivor.Data))
            {
                logger.LogWarning(
                    "The {Lost} tier failed for {What} ({Error}) and the {Kept} tier holds nothing for this "
                    + "query, so there is no answer to give — reporting the failure rather than an empty result.",
                    lostTier, what, lostError, sealedSurvived ? "sealed" : "hot");

                return KubernetesOperationResult<T>.Failure(
                    $"The {lostTier} telemetry tier failed for {what} ({lostError}), and the "
                    + $"{(sealedSurvived ? "sealed" : "hot")} tier holds nothing for this query — so this is "
                    + "not a complete answer. Retry, or widen the time range.");
            }

            logger.LogWarning(
                sealedSurvived
                    ? "The indexer's hot tier failed for {What} ({Error}); returning sealed results only, so "
                      + "the most recent unsealed events are missing from this answer."
                    : "The sealed tier failed for {What} ({Error}); returning the indexer's hot results only, "
                      + "so this answer covers only events not yet sealed.",
                what, lostError);
            return survivor;
        }

        return KubernetesOperationResult<T>.Failure(
            $"Both telemetry tiers failed for {what}. Sealed: {sealedResult.Error}. Hot: {hotResult.Error}.");
    }

    /// <summary>
    /// Waits <paramref name="budget"/> for one half and treats an overrun as that half failing, so the
    /// query costs the budget rather than the slow half's own timeout.
    /// </summary>
    private async Task<KubernetesOperationResult<T>> WithinBudgetAsync<T>(
        Task<KubernetesOperationResult<T>> half, string which, string what, TimeSpan budget)
    {
        if (half == await Task.WhenAny(half, Task.Delay(budget))) return await half;

        // Nobody awaits it now, so its exception would be unobserved — and what it eventually says is the
        // only record of why this half was missing from the answer.
        _ = half.ContinueWith(
            t => logger.LogWarning(t.Exception,
                "The {Which} tier finished {What} after the federation stopped waiting ({Budget}s).",
                which, what, budget.TotalSeconds),
            TaskScheduler.Default);

        return KubernetesOperationResult<T>.Failure(
            $"the {which} tier did not answer within {budget.TotalSeconds:0}s");
    }
}
