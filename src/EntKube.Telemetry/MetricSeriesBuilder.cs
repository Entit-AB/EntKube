namespace EntKube.Telemetry;

/// <summary>One stored bucket, as the series builder needs it: a scope, a time, and the aggregates.</summary>
/// <param name="Scope">
/// Whatever distinguishes one series from another for this query — a pod name, a service, a
/// namespace. The builder never interprets it; it only groups by it, because a counter has to be
/// differenced WITHIN a series before anything is summed across series.
/// </param>
public readonly record struct MetricBucket(
    string Scope, DateTime BucketStart, long Count, double Sum, double Min, double Max, double Last);

/// <summary>A point of an answer: the bucket it covers and the value for it.</summary>
public readonly record struct MetricSeriesPoint(DateTime Timestamp, double Value);

/// <summary>
/// Turns stored rollup buckets into the series the metrics explorer plots.
///
/// <para><b>It exists to match Prometheus, not to invent an answer.</b> The dashboards were built
/// against <c>PromMetricsService</c>, so when a cluster's metrics start coming from the central
/// store instead, the same question has to produce the same shape of answer — otherwise flipping
/// the backend silently redraws every graph. Pure, so those semantics can be exercised against
/// known numbers rather than against a live Prometheus.</para>
///
/// <para><b>The two rules it has to reproduce</b> (see <c>PromMetricsQueryBuilder</c>):</para>
/// <list type="bullet">
/// <item><c>sum(metric)</c> for a gauge — summed across series, as sampled at that moment.</item>
/// <item><c>sum(rate(metric[w]))</c> for a counter — see <see cref="IsCounter"/> — a
/// per-second rate, differenced per series and only then summed.</item>
/// </list>
/// </summary>
public static class MetricSeriesBuilder
{
    /// <summary>
    /// Whether a metric is a counter, by the same rule the PromQL builder uses: the
    /// <c>_total</c> suffix, or a histogram's <c>_bucket</c> — cumulative per bucket, so summing it
    /// as a gauge charts every request since the process started. Kept identical deliberately,
    /// and <c>PromMetricsQueryBuilder.IsCounter</c> now delegates here — if the two ever disagree,
    /// one backend would rate a metric the other summed, and the graph would change by orders of
    /// magnitude when routing flipped.
    ///
    /// <para><c>_count</c> and <c>_sum</c> are not here: they are counters only inside a histogram
    /// or summary, and gauges with those endings are common (connection-pool counts). The PromQL
    /// path asks Prometheus which it is; this rule has no store to ask.</para>
    /// </summary>
    public static bool IsCounter(string metricName) =>
        metricName.EndsWith("_total", StringComparison.Ordinal)
        || metricName.EndsWith("_bucket", StringComparison.Ordinal);

    /// <summary>
    /// Collapses <paramref name="buckets"/> into one series: a gauge summed across scopes, a
    /// counter rated per scope and then summed.
    /// </summary>
    public static List<MetricSeriesPoint> Collapse(
        string metricName, IEnumerable<MetricBucket> buckets, int resolutionSeconds)
    {
        Dictionary<DateTime, double> byBucket = IsCounter(metricName)
            ? RatePerScopeThenSum(buckets, resolutionSeconds)
            : SumAcrossScopes(buckets);

        return [.. byBucket.OrderBy(kv => kv.Key).Select(kv => new MetricSeriesPoint(kv.Key, kv.Value))];
    }

    /// <summary>
    /// The same collapse, per scope, for the broken-down view — <c>topk(N, sum by (label)(…))</c>.
    ///
    /// <para>Ordered by each series' own peak and then truncated to <paramref name="maxSeries"/>,
    /// which is what <c>topk</c> does: the series worth looking at are the big ones, and a chart
    /// with two hundred lines shows nothing at all.</para>
    /// </summary>
    public static List<(string Scope, List<MetricSeriesPoint> Points)> CollapseByScope(
        string metricName, IEnumerable<MetricBucket> buckets, int resolutionSeconds, int maxSeries)
    {
        return [.. buckets
            .GroupBy(b => b.Scope, StringComparer.Ordinal)
            .Select(g => (Scope: g.Key, Points: Collapse(metricName, g, resolutionSeconds)))
            .Where(s => s.Points.Count > 0)
            .OrderByDescending(s => s.Points.Max(p => p.Value))
            .ThenBy(s => s.Scope, StringComparer.Ordinal)
            .Take(Math.Max(1, maxSeries))];
    }

    /// <summary>
    /// A gauge: each scope contributes its bucket's LAST value, and those are summed.
    ///
    /// <para><b>Last, not Sum.</b> <c>sum(metric)</c> in PromQL adds one reading per series at the
    /// sampled instant. A bucket holds every reading taken during it, so adding <c>Sum</c> would
    /// multiply the answer by however many times the exporter was scraped — a graph that moves
    /// when the scrape interval changes and the system does not. The newest reading in the bucket
    /// is the closest thing to "the value at that moment", which is also why the store keeps
    /// <c>Last</c> at all.</para>
    /// </summary>
    private static Dictionary<DateTime, double> SumAcrossScopes(IEnumerable<MetricBucket> buckets)
    {
        Dictionary<DateTime, double> sums = [];

        foreach (MetricBucket b in buckets)
        {
            sums[b.BucketStart] = sums.GetValueOrDefault(b.BucketStart) + b.Last;
        }

        return sums;
    }

    /// <summary>
    /// A counter: differenced within each scope, divided by the elapsed seconds, then summed.
    ///
    /// <para><b>Per scope FIRST, and that ordering is the whole point.</b> A cumulative counter
    /// only means anything as a difference against its own previous value. Summing across pods
    /// and then differencing looks equivalent and is not: when a pod restarts its counter returns
    /// to zero, so the sum drops, and the difference of the sum is a large negative number that
    /// becomes a hole or a spike in the graph. Differencing per scope lets that pod's reset be
    /// recognised as a reset and the other pods' rates survive it.</para>
    ///
    /// <para><b>A reset counts as its own increase.</b> When this bucket's value is BELOW the
    /// previous one the counter restarted, and what it has counted since is simply its current
    /// value — so that is the increase attributed to the bucket. Prometheus' extrapolated
    /// <c>rate()</c> reaches the same conclusion by a different route. The alternative, dropping
    /// the bucket, would lose real traffic every time a pod rolled.</para>
    /// </summary>
    private static Dictionary<DateTime, double> RatePerScopeThenSum(
        IEnumerable<MetricBucket> buckets, int resolutionSeconds)
    {
        int width = MetricRollupFold.Clamp(resolutionSeconds);
        Dictionary<DateTime, double> rates = [];

        foreach (IGrouping<string, MetricBucket> scope in
                 buckets.GroupBy(b => b.Scope, StringComparer.Ordinal))
        {
            MetricBucket? previous = null;

            foreach (MetricBucket b in scope.OrderBy(b => b.BucketStart))
            {
                if (previous is MetricBucket prev)
                {
                    double increase = b.Last >= prev.Last ? b.Last - prev.Last : b.Last;

                    // Elapsed time between the two readings, not the nominal width: a gap in the
                    // data (a pod asleep, an export that failed) would otherwise be reported as a
                    // rate several times too high, because the whole gap's increase would be
                    // divided by one bucket's seconds.
                    double seconds = (b.BucketStart - prev.BucketStart).TotalSeconds;
                    if (seconds <= 0) seconds = width;

                    rates[b.BucketStart] = rates.GetValueOrDefault(b.BucketStart) + (increase / seconds);
                }

                previous = b;
            }
        }

        // The first bucket of each scope has no predecessor, so it has no rate — and is absent
        // rather than zero. A zero would draw a line through the floor at the left edge of every
        // chart, which reads as "nothing was happening" instead of "we cannot know yet".
        return rates;
    }

    /// <summary>
    /// Re-buckets stored rows to the step the caller asked for, when the caller asked for a
    /// coarser one.
    ///
    /// <para><b>It cannot go finer, and the early return is not why.</b> A caller asking for sixty
    /// buckets over five minutes wants five-second steps, and a store written at thirty seconds
    /// does not have them. Returning fewer points than requested is the honest answer —
    /// interpolating would invent readings no exporter ever reported, and nothing downstream could
    /// tell them from measurements.
    ///
    /// <para>The guarantee is structural: this method GROUPS buckets, and grouping only ever
    /// merges rows. The <c>step &lt;= stored</c> check below is a shortcut, not the protection —
    /// deleting it changes nothing observable, which is how it was measured.</para></para>
    /// </summary>
    public static List<MetricBucket> ToStep(
        IEnumerable<MetricBucket> buckets, int storedResolutionSeconds, int stepSeconds)
    {
        int stored = MetricRollupFold.Clamp(storedResolutionSeconds);
        int step = MetricRollupFold.Clamp(stepSeconds);

        if (step <= stored) return [.. buckets];

        return [.. buckets
            .GroupBy(b => (b.Scope, Bucket: MetricRollupFold.BucketFor(b.BucketStart, step)))
            .Select(g => new MetricBucket(
                g.Key.Scope,
                g.Key.Bucket,
                g.Sum(b => b.Count),
                g.Sum(b => b.Sum),
                g.Min(b => b.Min),
                g.Max(b => b.Max),
                // The last of the coarse bucket is the last of its newest fine bucket — not the
                // maximum, and not the last one enumerated.
                g.OrderBy(b => b.BucketStart).Last().Last))];
    }

    /// <summary>
    /// The step, in seconds, that <paramref name="buckets"/> buckets over [from, to) implies —
    /// the caller's side of <see cref="ToStep"/>.
    /// </summary>
    public static int StepFor(DateTime from, DateTime to, int buckets)
    {
        double span = (to - from).TotalSeconds;
        if (span <= 0) return 1;
        return (int)Math.Max(1, span / Math.Max(1, buckets));
    }
}
