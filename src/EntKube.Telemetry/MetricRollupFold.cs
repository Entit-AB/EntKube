namespace EntKube.Telemetry;

/// <summary>The scope a rollup row belongs to — everything but the numbers.</summary>
public readonly record struct MetricRollupKey(
    string Name, string? Service, string? Namespace, string? Pod, DateTime BucketStart);

/// <summary>The aggregates a bucket carries.</summary>
public readonly record struct MetricRollupValue(
    long Count, double Sum, double Min, double Max, double Last, DateTime LastAt);

/// <summary>
/// Folds metric points into fixed-width buckets. Pure, so the resolution rule can be exercised
/// without a database — and the resolution rule is the whole design decision of the metrics store
/// (see <see cref="MetricRollup"/>).
///
/// <para><b>Aggregating on WRITE is the point.</b> <c>IMetricsQuery</c> only ever asks for N
/// buckets over a range, so a store of raw points would aggregate on every read instead — which is
/// the shape recorded against the row store deleted in <c>82ec230</c>: ">5s log/trace queries and
/// <i>too many clients</i> ingest failures". Writing once and reading what was asked for is the
/// same work done at the cheaper end.</para>
/// </summary>
public static class MetricRollupFold
{
    /// <summary>
    /// Floor of <paramref name="timestamp"/> to a bucket of <paramref name="resolutionSeconds"/>.
    ///
    /// <para>Floored against the UNIX epoch rather than against anything in the batch, so the same
    /// instant always lands in the same bucket however it arrives: two collectors, a retry an hour
    /// later and a backfill all upsert the same row instead of three adjacent ones. A bucket
    /// boundary that depended on the batch would make the store's shape a function of delivery
    /// timing.</para>
    ///
    /// <para><b>An UNSPECIFIED timestamp is taken to be UTC, not local.</b> Only
    /// <see cref="DateTimeKind.Local"/> carries an offset worth applying;
    /// <c>ToUniversalTime()</c> would apply the server's offset to an Unspecified one as well, and
    /// then the SAME ROW would bucket differently depending on which machine or timezone the
    /// process happened to run in — the one thing epoch-floored bucketing exists to rule out. The
    /// store's own rows arrive Unspecified from providers that do not carry a kind (SQLite reads
    /// back TEXT), so this is not a hypothetical: it shifted a read by the host's offset until a
    /// coarse-step query went looking for buckets two hours in the past.</para>
    ///
    /// <para>⚠️ <c>RollupMetricsQuery</c> also stamps UTC on rows it reads, and by removal either
    /// change alone repairs that read — so this one is NOT the only thing standing between the
    /// store and a timezone shift. It is kept because it covers the write path too, where a
    /// wrongly-bucketed row is permanent rather than merely mis-drawn, and because an Unspecified
    /// timestamp has no offset to apply whoever hands it over.</para>
    /// </summary>
    public static DateTime BucketFor(DateTime timestamp, int resolutionSeconds)
    {
        int width = Clamp(resolutionSeconds);

        DateTime utc = timestamp.Kind == DateTimeKind.Local
            ? timestamp.ToUniversalTime()
            : timestamp;

        long ticks = width * TimeSpan.TicksPerSecond;
        return new DateTime(utc.Ticks - (utc.Ticks % ticks), DateTimeKind.Utc);
    }

    /// <summary>
    /// Folds <paramref name="points"/> into one entry per (scope, bucket).
    ///
    /// <para><c>Last</c> is the latest value BY TIMESTAMP, not by arrival: a batch can carry points
    /// out of order, and a gauge's "current" reading has to be the newest observation rather than
    /// whichever the parser happened to reach last. That is why <see cref="MetricRollupValue"/>
    /// carries <c>LastAt</c> — it is how a later batch decides whether its point supersedes a
    /// stored one.</para>
    /// </summary>
    public static Dictionary<MetricRollupKey, MetricRollupValue> Fold(
        IEnumerable<MetricPoint> points, int resolutionSeconds)
    {
        Dictionary<MetricRollupKey, MetricRollupValue> folded = [];

        foreach (MetricPoint p in points)
        {
            if (string.IsNullOrWhiteSpace(p.Name)) continue;

            // NaN and the infinities would poison Sum, Min and Max for the whole bucket, and a
            // stale counter reset is a common way for one to arrive. Dropped rather than stored:
            // a missing point is visible as a gap, a NaN makes every aggregate over it NaN.
            if (double.IsNaN(p.Value) || double.IsInfinity(p.Value)) continue;

            MetricRollupKey key = new(
                p.Name, Blank(p.Service), Blank(p.Namespace), Blank(p.Pod),
                BucketFor(p.Timestamp, resolutionSeconds));

            folded[key] = folded.TryGetValue(key, out MetricRollupValue cur)
                ? Merge(cur, p)
                : new MetricRollupValue(1, p.Value, p.Value, p.Value, p.Value, p.Timestamp);
        }

        return folded;
    }

    /// <summary>
    /// Adds one point to an existing bucket — the same arithmetic the upsert applies to a row
    /// already in the database, which is why it is here rather than inlined in <see cref="Fold"/>.
    /// </summary>
    public static MetricRollupValue Merge(MetricRollupValue cur, MetricPoint p) =>
        new(
            cur.Count + 1,
            cur.Sum + p.Value,
            Math.Min(cur.Min, p.Value),
            Math.Max(cur.Max, p.Value),
            p.Timestamp >= cur.LastAt ? p.Value : cur.Last,
            p.Timestamp >= cur.LastAt ? p.Timestamp : cur.LastAt);

    /// <summary>
    /// Bucket widths are seconds of a real clock, so nonsense is clamped rather than trusted: a
    /// mistyped 0 would divide by zero, and a mistyped 86400000 would put a decade in one row.
    /// One second is the floor because that is the finest any scrape interval reaches; an hour is
    /// the ceiling because a bucket wider than the default dashboard window cannot be plotted.
    /// </summary>
    public static int Clamp(int resolutionSeconds) => Math.Clamp(resolutionSeconds, 1, 3600);

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
