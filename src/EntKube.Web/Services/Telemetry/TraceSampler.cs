using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Head-samples a batch of spans down to the raw spans worth keeping for waterfalls, WITHOUT thinning the
/// trace list (the caller still feeds every span to the trace-summary index). The decision is per trace:
/// <list type="bullet">
/// <item>a trace with any <b>error</b> span (OTLP status 2) or any span at/above the slow threshold is
///   always kept — those are the ones an operator opens a waterfall for;</item>
/// <item>every other trace is kept with probability <c>rate%</c> via a <b>deterministic</b> hash of its
///   trace id, so the choice is stable across ingest batches (all of a trace's spans share its fate even
///   when they arrive in different batches — no cross-batch state needed) and reproducible.</item>
/// </list>
/// At <c>rate = 100</c> it returns the input unchanged (the default: no sampling). This is head sampling —
/// a sampled-out trace's raw spans are dropped at ingest; its summary (start/duration/counts/service) still
/// lands, so it remains searchable in the trace list, just without a deep waterfall.
/// </summary>
public static class TraceSampler
{
    /// <summary>
    /// Returns the subset of <paramref name="records"/> whose trace is kept under the sampling policy. Order
    /// is preserved. <paramref name="ratePercent"/> is clamped to 1..100; 100 short-circuits to the input.
    /// </summary>
    public static IReadOnlyList<SpanIngestRecord> Sample(
        IReadOnlyList<SpanIngestRecord> records, int ratePercent, double minKeepDurationMs)
    {
        int rate = Math.Clamp(ratePercent, 1, 100);
        if (rate >= 100 || records.Count == 0) return records;

        // First pass: per trace, is it "interesting" (any error/slow span) — those are always kept.
        var interesting = new HashSet<string>();
        foreach (SpanIngestRecord r in records)
            if (r.StatusCode == 2 || r.DurationMs >= minKeepDurationMs)
                interesting.Add(r.TraceId);

        var kept = new List<SpanIngestRecord>(records.Count);
        foreach (SpanIngestRecord r in records)
            if (interesting.Contains(r.TraceId) || KeepBySample(r.TraceId, rate))
                kept.Add(r);
        return kept;
    }

    // Deterministic keep decision: FNV-1a of the trace id mapped into [0,100). Stable across processes and
    // batches (unlike string.GetHashCode, which is randomized per run), so a trace is kept or dropped whole.
    private static bool KeepBySample(string traceId, int rate)
    {
        const uint FnvOffset = 2166136261, FnvPrime = 16777619;
        uint hash = FnvOffset;
        foreach (char c in traceId) { hash = (hash ^ c) * FnvPrime; }
        return hash % 100 < (uint)rate;
    }
}
