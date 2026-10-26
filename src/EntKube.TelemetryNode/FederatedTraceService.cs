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
    ILogger<FederatedTraceService> logger,
    TimeSpan? halfBudget = null,
    TimeSpan? sealedBudget = null) : ITraceQueryService
{
    public async Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
    {
        // Budgeted: this is what the Traces view asks before it renders anything, so an unanswerable half
        // must not hold the page for its client's whole timeout.
        KubernetesOperationResult<bool>[] results = await Task.WhenAll(
            WithinBudgetAsync(Wrap(sealedTier.HasDataAsync(clusterId, ct)), "sealed", "has-data", _sealedBudget),
            WithinBudgetAsync(Wrap(hotTier.HasDataAsync(clusterId, ct)), "hot", "has-data", _hotBudget));
        bool[] both = [.. results.Select(r => r is { IsSuccess: true, Data: true })];

        static async Task<KubernetesOperationResult<bool>> Wrap(Task<bool> probe)
            => KubernetesOperationResult<bool>.Success(await probe);
        return both[0] || both[1];
    }

    public Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null,
        string? podPattern = null, int windowMinutes = 60)
        => MergeAsync(
            sealedTier.GetServicesAsync(clusterId, ct, namespaces, podPattern, windowMinutes),
            hotTier.GetServicesAsync(clusterId, ct, namespaces, podPattern, windowMinutes),
            (a, b) => [.. a.Concat(b).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            "services", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => MergeAsync(
            sealedTier.ListTracesAsync(clusterId, service, from, to, minDurationMs, errorsOnly, limit, ct, namespaces, podPattern),
            hotTier.ListTracesAsync(clusterId, service, from, to, minDurationMs, errorsOnly, limit, ct, namespaces, podPattern),
            (a, b) => MergeTraceSummaries(a, b, limit), "trace list", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
        => MergeAsync(
            sealedTier.GetTraceAsync(clusterId, traceId, ct, namespaces),
            hotTier.GetTraceAsync(clusterId, traceId, ct, namespaces),
            MergeSpans, "trace detail", static v => v.Count == 0);

    public Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null, string? podPattern = null)
        => MergeAsync(
            sealedTier.GetServiceMapAsync(clusterId, from, to, ct, namespaces, podPattern),
            hotTier.GetServiceMapAsync(clusterId, from, to, ct, namespaces, podPattern),
            MergeEdges, "service map", static v => v.Count == 0);

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
    /// span count. Counts add; the trace starts at the earliest span either half saw; the root comes from
    /// whichever half actually holds the root span.
    ///
    /// <para>⭐ <b>Duration is the wall-clock extent of the union</b> — latest end minus earliest start —
    /// which is how every other site that reassembles a trace's duration computes it:
    /// <c>SegmentTraceService.Summarize</c> (<c>maxEnd - minStart</c>, spans to summary, with a comment
    /// naming max-single-duration as the wrong answer) and <c>SegmentTraceService.MergePartials</c>
    /// (<c>end - start</c>, the intra-tier twin of this very merge). The detail view of the same screen
    /// agrees: <c>TraceExplorer</c> draws its waterfall against
    /// <c>spans.Max(s =&gt; (s.Start - traceStart) + s.DurationMs)</c>.</para>
    ///
    /// <para>⚠️ This used to read <c>DurationMs = Math.Max(existing.DurationMs, row.DurationMs)</c> — the
    /// longer half's own duration. That equals the union's duration only when both halves start at the same
    /// instant, which is the one thing that cannot happen to a trace split across tiers: the hot half is by
    /// definition the later one. A trace whose early spans sealed at T spanning 120 ms and whose hot spans
    /// ran from T+4.0s for 60 ms really lasted ~4.06 s and was badged "120 ms" — so it read as fast, and
    /// both the latency hunt and the page's own <c>minDurationMs</c> floor (which is matched against this
    /// number) went straight past it.</para>
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
            // Wall-clock extent of the union — see the note above. Both ends are measured as offsets from
            // the merged start, so the half that started later contributes its own lead time plus its span.
            DateTime start = existing.Start <= row.Start ? existing.Start : row.Start;
            double extentMs = Math.Max(
                (existing.Start - start).TotalMilliseconds + existing.DurationMs,
                (row.Start - start).TotalMilliseconds + row.DurationMs);

            byTrace[row.TraceId] = existing with
            {
                Start = start,
                DurationMs = extentMs,
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

    /// <summary>Same budget as the log federation, for the same reason — see <c>FederatedLogBackend</c>.
    /// A half that cannot answer costs this many seconds, not its client's 30s ceiling.</summary>
    private readonly TimeSpan _hotBudget = halfBudget ?? DefaultHotBudget;

    /// <summary>
    /// ⚠️ The shipped configuration, as on the log side — <c>Program.cs</c> constructs this with
    /// four arguments and no budgets. Named so the sealed-longer-than-hot invariant can be
    /// asserted; the trace side had no budget test of ANY kind, so both these values and the
    /// per-half routing were unpinned.
    /// </summary>
    public static readonly TimeSpan DefaultHotBudget = TimeSpan.FromSeconds(5);

    /// <inheritdoc cref="DefaultHotBudget"/>
    public static readonly TimeSpan DefaultSealedBudget = TimeSpan.FromSeconds(30);

    /// <summary>The sealed tier's larger budget, and the same reasoning as the log side: this half reads
    /// archives out of object storage, so a cold first query is slow by design rather than by fault.</summary>
    private readonly TimeSpan _sealedBudget = sealedBudget ?? DefaultSealedBudget;

    /// <summary>Same degradation policy as the log federation, including the one exception it makes: a
    /// surviving half that is EMPTY is reported as a failure rather than as an answer, because a trace list
    /// showing nothing is read as "there are no traces" and there is no way for the UI to say otherwise.</summary>
    private async Task<KubernetesOperationResult<T>> MergeAsync<T>(
        Task<KubernetesOperationResult<T>> sealedTask,
        Task<KubernetesOperationResult<T>> hotTask,
        Func<T, T, T> merge,
        string what,
        Func<T, bool>? isEmpty = null)
    {
        // Concurrently, so two slow halves cost the larger budget rather than their sum.
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

            if (survivor.Data is not null && isEmpty is not null && isEmpty(survivor.Data))
            {
                logger.LogWarning(
                    "The {Lost} tier failed for {What} ({Error}) and the other tier holds nothing for this "
                    + "query — reporting the failure rather than an empty result.", lostTier, what, lostError);

                return KubernetesOperationResult<T>.Failure(
                    $"The {lostTier} telemetry tier failed for {what} ({lostError}), and the other tier holds "
                    + "nothing for this query — so this is not a complete answer. Retry, or widen the time range.");
            }

            logger.LogWarning("The {Lost} tier failed for {What} ({Error}); returning the other tier's results only.",
                lostTier, what, lostError);
            return survivor;
        }

        return KubernetesOperationResult<T>.Failure(
            $"Both telemetry tiers failed for {what}. Sealed: {sealedResult.Error}. Hot: {hotResult.Error}.");
    }

    /// <summary>Waits <paramref name="budget"/> for one half and treats an overrun as that half failing.</summary>
    private async Task<KubernetesOperationResult<T>> WithinBudgetAsync<T>(
        Task<KubernetesOperationResult<T>> half, string which, string what, TimeSpan budget)
    {
        if (half == await Task.WhenAny(half, Task.Delay(budget))) return await half;

        _ = half.ContinueWith(
            t => logger.LogWarning(t.Exception,
                "The {Which} tier finished {What} after the federation stopped waiting ({Budget}s).",
                which, what, budget.TotalSeconds),
            TaskScheduler.Default);

        return KubernetesOperationResult<T>.Failure(
            $"the {which} tier did not answer within {budget.TotalSeconds:0}s");
    }
}
