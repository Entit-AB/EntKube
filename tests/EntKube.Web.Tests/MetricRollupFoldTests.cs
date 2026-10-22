using EntKube.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The bucketing rule, which is the whole design decision of the central metrics store: metrics
/// are aggregated on WRITE at a configurable resolution, because <c>IMetricsQuery</c> only ever
/// asks for N buckets over a range and never for a sample.
///
/// <para>Set the resolution to the scrape interval and a bucket holds one point — raw samples,
/// which is what was asked for. Raise it and cardinality falls proportionally. These tests pin
/// that both ends of that dial behave, so the choice stays a setting rather than a migration.</para>
/// </summary>
public class MetricRollupFoldTests
{
    private static readonly DateTime T0 =
        new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static MetricPoint P(DateTime at, double value, string? pod = "pod-a") =>
        new("cpu", "svc", "ns", pod, at, value);

    // ── Where a bucket starts ──

    [Fact]
    public void A_bucket_is_floored_against_the_epoch_not_the_batch()
    {
        // The same instant has to land in the same bucket however it arrives — two collectors, a
        // retry an hour later and a backfill all upsert ONE row. A boundary derived from the batch
        // would make the store's shape a function of delivery timing.
        DateTime a = MetricRollupFold.BucketFor(T0.AddSeconds(7), 30);
        DateTime b = MetricRollupFold.BucketFor(T0.AddSeconds(29), 30);

        a.Should().Be(T0);
        b.Should().Be(T0);
        MetricRollupFold.BucketFor(T0.AddSeconds(30), 30).Should().Be(T0.AddSeconds(30));
    }

    [Fact]
    public void A_local_timestamp_is_bucketed_as_the_instant_it_is()
    {
        // A point that arrives in local time is the same moment as its UTC equivalent, and must not
        // land in a bucket hours away because of how it was spelled.
        DateTime local = new DateTime(2026, 10, 21, 12, 0, 7, DateTimeKind.Utc).ToLocalTime();

        MetricRollupFold.BucketFor(local, 30).Should().Be(T0);
    }

    /// <summary>
    /// The sibling of the test above, and the one that was missing.
    ///
    /// <para>An Unspecified timestamp carries NO offset, so there is nothing to convert — but
    /// <c>ToUniversalTime()</c> treats it as local and applies the host's offset anyway. That made
    /// the same row bucket differently depending on which machine the process ran on, which is
    /// precisely what flooring against the epoch exists to rule out. It is not hypothetical: rows
    /// come back Unspecified from any provider that does not store a kind — SQLite keeps timestamps
    /// as TEXT — so a read was looking for buckets two hours in the past and finding none.</para>
    ///
    /// <para>It stayed invisible because the shift only shows through
    /// <see cref="EntKube.Telemetry.MetricSeriesBuilder.ToStep"/>, which skips the bucketing
    /// entirely when the caller's step equals the stored resolution. Only a COARSER step reaches
    /// it.</para>
    /// </summary>
    [Fact]
    public void An_unspecified_timestamp_is_taken_as_utc_rather_than_shifted_by_the_hosts_offset()
    {
        DateTime unspecified = DateTime.SpecifyKind(T0.AddSeconds(7), DateTimeKind.Unspecified);

        MetricRollupFold.BucketFor(unspecified, 30).Should().Be(T0);

        // Not just equal in ticks: the result is usable as UTC by whatever compares it next.
        MetricRollupFold.BucketFor(unspecified, 30).Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void A_nonsense_resolution_is_clamped_rather_than_trusted()
    {
        // A mistyped 0 would divide by zero; a mistyped 86400000 would put a decade in one row.
        MetricRollupFold.Clamp(0).Should().Be(1);
        MetricRollupFold.Clamp(-30).Should().Be(1);
        MetricRollupFold.Clamp(86_400_000).Should().Be(3600);

        // And the clamp is reachable through the bucketing, not only as its own function.
        MetricRollupFold.BucketFor(T0.AddSeconds(7), 0).Should().Be(T0.AddSeconds(7));
    }

    // ── What a bucket holds ──

    [Fact]
    public void Points_in_one_bucket_become_one_row_with_its_aggregates()
    {
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [P(T0, 10), P(T0.AddSeconds(5), 30), P(T0.AddSeconds(10), 20)], 30);

        folded.Should().HaveCount(1);
        MetricRollupValue v = folded.Values.Single();

        v.Count.Should().Be(3);
        v.Sum.Should().Be(60);
        v.Min.Should().Be(10);
        v.Max.Should().Be(30);
        (v.Sum / v.Count).Should().Be(20, "count and sum are what give the mean");
    }

    [Fact]
    public void Last_is_the_newest_by_timestamp_not_by_arrival()
    {
        // A batch can carry points out of order. A gauge's "current" reading is the newest
        // OBSERVATION — memory in use at the end of the minute — not whichever the parser reached
        // last, and the explorer plots gauges and counters through the same call.
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [P(T0.AddSeconds(20), 99), P(T0.AddSeconds(5), 1)], 30);

        folded.Values.Single().Last.Should().Be(99);
    }

    [Fact]
    public void Each_scope_gets_its_own_row()
    {
        // pod, service and namespace are the three breakdowns the product can ask for
        // (PromMetricsQueryBuilder.BreakdownLabel), so each has to stay separable.
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [P(T0, 1, pod: "pod-a"), P(T0, 2, pod: "pod-b")], 30);

        folded.Should().HaveCount(2);
        folded.Values.Select(v => v.Sum).Order().Should().Equal([1, 2]);
    }

    [Fact]
    public void Separate_buckets_stay_separate()
    {
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [P(T0, 1), P(T0.AddSeconds(45), 2)], 30);

        folded.Should().HaveCount(2);
        folded.Keys.Select(k => k.BucketStart).Order()
            .Should().Equal([T0, T0.AddSeconds(30)]);
    }

    // ── The dial the decision rests on ──

    [Fact]
    public void At_the_scrape_interval_every_point_is_its_own_row()
    {
        // This is "raw samples in Postgres", which is what was asked for: resolution equal to the
        // scrape interval means a bucket of one, with Count 1 and Sum the value itself.
        List<MetricPoint> scrapes =
            [.. Enumerable.Range(0, 6).Select(i => P(T0.AddSeconds(i * 15), i))];

        Dictionary<MetricRollupKey, MetricRollupValue> folded =
            MetricRollupFold.Fold(scrapes, resolutionSeconds: 15);

        folded.Should().HaveCount(6);
        folded.Values.Should().OnlyContain(v => v.Count == 1);
        folded.Values.Should().OnlyContain(v => v.Min == v.Max && v.Min == v.Last);
    }

    [Fact]
    public void Raising_the_resolution_cuts_the_rows_proportionally()
    {
        // The escape hatch §5 of the design note asked for: the same points, one setting, a tenth
        // of the rows — and no schema change between the two.
        List<MetricPoint> scrapes =
            [.. Enumerable.Range(0, 60).Select(i => P(T0.AddSeconds(i * 15), i))];

        MetricRollupFold.Fold(scrapes, 15).Should().HaveCount(60);
        MetricRollupFold.Fold(scrapes, 150).Should().HaveCount(6);

        // And nothing is lost that the query surface can ask for: the totals still agree.
        double byScrape = MetricRollupFold.Fold(scrapes, 15).Values.Sum(v => v.Sum);
        double byTenth = MetricRollupFold.Fold(scrapes, 150).Values.Sum(v => v.Sum);
        byTenth.Should().Be(byScrape);
    }

    // ── What never reaches a row ──

    [Fact]
    public void A_NaN_or_infinite_value_is_dropped_rather_than_stored()
    {
        // One of these would poison Sum, Min and Max for the whole bucket — and a counter reset is
        // a common way for one to arrive. A gap is visible; an aggregate that is NaN for ever is
        // not, and nothing downstream can recover from it.
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [P(T0, 10), P(T0.AddSeconds(1), double.NaN),
             P(T0.AddSeconds(2), double.PositiveInfinity), P(T0.AddSeconds(3), 20)], 30);

        MetricRollupValue v = folded.Values.Single();
        v.Count.Should().Be(2);
        v.Sum.Should().Be(30);
        v.Max.Should().Be(20);
    }

    [Fact]
    public void A_nameless_point_is_dropped()
    {
        // Nothing could ever query it, and it would occupy a row per bucket for ever.
        MetricRollupFold.Fold([new MetricPoint("", null, null, null, T0, 1)], 30)
            .Should().BeEmpty();
    }

    [Fact]
    public void Blank_scope_labels_are_stored_as_absent()
    {
        // "" and null describe the same thing — a point that carried no pod — and storing both
        // would split one series into two that no query could reunite, because the unique index
        // treats them as different scopes.
        Dictionary<MetricRollupKey, MetricRollupValue> folded = MetricRollupFold.Fold(
            [new MetricPoint("cpu", "", "   ", null, T0, 1),
             new MetricPoint("cpu", null, null, null, T0.AddSeconds(1), 2)], 30);

        folded.Should().HaveCount(1);
        folded.Values.Single().Count.Should().Be(2);
    }
}
