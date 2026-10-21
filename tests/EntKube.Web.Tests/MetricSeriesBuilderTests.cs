using EntKube.Telemetry;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Turning stored rollup buckets into the series the explorer plots.
///
/// <para><b>Why these are worth more than most.</b> Every dashboard was built against
/// <c>PromMetricsService</c>. When a cluster's metrics start coming from the central store, the
/// same question must produce the same shape of answer — and a mistake here does not fail, it
/// draws a different graph. Wrong numbers with no error anywhere is the worst failure this code
/// can have, so the semantics are pinned against arithmetic that can be checked by hand.</para>
/// </summary>
public class MetricSeriesBuilderTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A bucket where every aggregate is the same value — the common gauge case.</summary>
    private static MetricBucket B(string scope, int bucketIndex, double value, long count = 1) =>
        new(scope, T0.AddSeconds(bucketIndex * 30), count, value * count, value, value, value);

    // ── Which rule applies ──

    [Fact]
    public void A_counter_is_recognised_by_the_same_rule_the_promql_builder_uses()
    {
        // If these two ever disagree, one backend would rate a metric the other summed and the
        // graph would change by orders of magnitude when routing flipped.
        MetricSeriesBuilder.IsCounter("requests_total").Should().BeTrue();
        MetricSeriesBuilder.IsCounter("memory_bytes").Should().BeFalse();
        MetricSeriesBuilder.IsCounter("total_requests").Should().BeFalse("the rule is the suffix");
    }

    // ── Gauges ──

    [Fact]
    public void A_gauge_sums_one_reading_per_scope_not_every_reading_in_the_bucket()
    {
        // sum(metric) in PromQL adds ONE reading per series. A bucket holds every scrape taken
        // during it, so adding Sum would multiply the answer by the scrape count — a graph that
        // moves when the scrape interval changes and the system does not.
        List<MetricBucket> buckets =
        [
            // pod-a was scraped three times in this bucket, last reading 10.
            new("pod-a", T0, Count: 3, Sum: 27, Min: 8, Max: 10, Last: 10),
            new("pod-b", T0, Count: 3, Sum: 15, Min: 4, Max: 6, Last: 5),
        ];

        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse("memory_bytes", buckets, 30);

        series.Should().ContainSingle();
        series[0].Value.Should().Be(15, "10 + 5, the last reading of each scope");
        series[0].Timestamp.Should().Be(T0);
    }

    [Fact]
    public void A_gauge_series_is_ordered_oldest_first()
    {
        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            "memory_bytes", [B("pod-a", 2, 30), B("pod-a", 0, 10), B("pod-a", 1, 20)], 30);

        series.Select(p => p.Value).Should().Equal([10, 20, 30]);
    }

    // ── Counters: the part that would go wrong silently ──

    [Fact]
    public void A_counter_becomes_a_per_second_rate()
    {
        // A cumulative counter climbing 60 per 30-second bucket is 2/s.
        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            "requests_total", [B("pod-a", 0, 100), B("pod-a", 1, 160), B("pod-a", 2, 220)], 30);

        series.Select(p => p.Value).Should().Equal([2, 2]);
    }

    [Fact]
    public void The_first_bucket_of_a_counter_has_no_rate_and_is_absent_rather_than_zero()
    {
        // A zero would draw a line through the floor at the left edge of every chart, which reads
        // as "nothing was happening" rather than "we cannot know yet".
        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            "requests_total", [B("pod-a", 0, 100), B("pod-a", 1, 160)], 30);

        series.Should().ContainSingle();
        series[0].Timestamp.Should().Be(T0.AddSeconds(30));
    }

    /// <summary>
    /// The case that makes the ordering matter, and the reason this file exists.
    ///
    /// <para>Summing across pods and then differencing looks equivalent to differencing per pod
    /// and then summing. It is not: when a pod restarts, its counter returns to zero, the SUM
    /// drops, and the difference of the sum is a large negative number — a hole or a spike where
    /// the truth is "one pod rolled and the others kept serving".</para>
    /// </summary>
    [Fact]
    public void A_pod_restarting_does_not_corrupt_the_other_pods_rate()
    {
        List<MetricBucket> buckets =
        [
            B("pod-a", 0, 1000), B("pod-a", 1, 1060),   // steady: +60 per bucket → 2/s
            B("pod-b", 0, 5000), B("pod-b", 1, 30),     // restarted: counter reset, 30 since
        ];

        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse("requests_total", buckets, 30);

        // pod-a's 2/s survives, and pod-b contributes the 30 it has counted since restarting
        // (1/s) rather than a negative four-thousand-something.
        series.Should().ContainSingle();
        series[0].Value.Should().Be(3, "2/s from the steady pod plus 1/s from the restarted one");
    }

    [Fact]
    public void A_reset_is_counted_as_its_own_increase_rather_than_dropped()
    {
        // Dropping the bucket would lose real traffic every time a pod rolled.
        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            "requests_total", [B("pod-a", 0, 900), B("pod-a", 1, 60)], 30);

        series.Should().ContainSingle();
        series[0].Value.Should().Be(2, "60 counted since the restart, over 30 seconds");
    }

    [Fact]
    public void A_gap_in_the_data_is_divided_by_the_elapsed_time_not_by_one_bucket()
    {
        // Otherwise a pod that stopped exporting for five minutes and came back reports a rate
        // ten times too high for one bucket — a spike that never happened.
        List<MetricSeriesPoint> series = MetricSeriesBuilder.Collapse(
            "requests_total", [B("pod-a", 0, 100), B("pod-a", 10, 400)], 30);

        // 300 more requests over 300 seconds is 1/s, not 10/s.
        series.Should().ContainSingle();
        series[0].Value.Should().Be(1);
    }

    // ── Broken down by scope ──

    [Fact]
    public void A_breakdown_keeps_each_scope_separate()
    {
        List<(string Scope, List<MetricSeriesPoint> Points)> byScope =
            MetricSeriesBuilder.CollapseByScope(
                "memory_bytes", [B("pod-a", 0, 10), B("pod-b", 0, 5)], 30, maxSeries: 24);

        byScope.Should().HaveCount(2);
        byScope.Select(s => s.Scope).Should().BeEquivalentTo(["pod-a", "pod-b"]);
        byScope.Single(s => s.Scope == "pod-a").Points[0].Value.Should().Be(10);
    }

    [Fact]
    public void A_breakdown_keeps_the_biggest_series_which_is_what_topk_does()
    {
        // A chart with two hundred lines shows nothing. topk(N) keeps the ones worth looking at.
        //
        // ⚠️ The names are chosen so ALPHABETICAL order disagrees with magnitude order. They did
        // not used to be: with "small"/"huge"/"middling" the two orders happen to pick the same
        // top two, so this assertion held even with the ordering deleted — it was testing the
        // fixture, not the code.
        List<MetricBucket> buckets =
        [
            B("a-tiny", 0, 1), B("z-huge", 0, 1000), B("m-middling", 0, 50),
        ];

        List<(string Scope, List<MetricSeriesPoint> Points)> byScope =
            MetricSeriesBuilder.CollapseByScope("memory_bytes", buckets, 30, maxSeries: 2);

        byScope.Select(s => s.Scope).Should().Equal(["z-huge", "m-middling"]);
    }

    [Fact]
    public void A_breakdown_of_a_counter_rates_each_scope_on_its_own()
    {
        List<MetricBucket> buckets =
        [
            B("pod-a", 0, 1000), B("pod-a", 1, 1060),
            B("pod-b", 0, 5000), B("pod-b", 1, 30),
        ];

        List<(string Scope, List<MetricSeriesPoint> Points)> byScope =
            MetricSeriesBuilder.CollapseByScope("requests_total", buckets, 30, maxSeries: 24);

        byScope.Single(s => s.Scope == "pod-a").Points.Single().Value.Should().Be(2);
        byScope.Single(s => s.Scope == "pod-b").Points.Single().Value.Should().Be(1);
    }

    // ── Steps: coarser yes, finer never ──

    [Fact]
    public void A_coarser_step_merges_stored_buckets()
    {
        // Three 30-second buckets into one 90-second step.
        List<MetricBucket> merged = MetricSeriesBuilder.ToStep(
            [B("pod-a", 0, 10), B("pod-a", 1, 20), B("pod-a", 2, 30)],
            storedResolutionSeconds: 30, stepSeconds: 90);

        merged.Should().ContainSingle();
        merged[0].Count.Should().Be(3);
        merged[0].Min.Should().Be(10);
        merged[0].Max.Should().Be(30);
        merged[0].Last.Should().Be(30, "the newest fine bucket's last, not the maximum");
    }

    /// <summary>
    /// A caller asking for sixty buckets over five minutes wants five-second steps, and a store
    /// written at thirty seconds does not have them.
    ///
    /// <para>⚠️ <b>The early return is not what prevents interpolation — the grouping is.</b>
    /// Removing <c>if (step &lt;= stored)</c> changes nothing observable, which a removal check
    /// showed: <see cref="MetricSeriesBuilder.ToStep"/> groups buckets, and grouping can only
    /// merge rows, never invent them. The guard is a shortcut, and this test documents the
    /// property that actually holds rather than crediting the line above it.</para>
    /// </summary>
    [Fact]
    public void A_finer_step_cannot_add_detail_the_store_does_not_have()
    {
        List<MetricBucket> stored = [B("pod-a", 0, 10), B("pod-a", 1, 20)];

        List<MetricBucket> same = MetricSeriesBuilder.ToStep(stored, 30, stepSeconds: 5);

        same.Should().HaveCount(2, "returned at the stored resolution, not expanded");
        same.Select(b => b.Last).Should().BeEquivalentTo([10.0, 20.0]);
    }

    [Fact]
    public void The_step_a_caller_implies_comes_from_its_bucket_count()
    {
        MetricSeriesBuilder.StepFor(T0, T0.AddHours(1), buckets: 60).Should().Be(60);
        MetricSeriesBuilder.StepFor(T0, T0.AddMinutes(5), buckets: 60).Should().Be(5);

        // A range of nothing cannot imply a step; it must not divide by zero either.
        MetricSeriesBuilder.StepFor(T0, T0, buckets: 60).Should().Be(1);
    }

    [Fact]
    public void Nothing_stored_is_an_empty_series_not_a_zero_one()
    {
        MetricSeriesBuilder.Collapse("memory_bytes", [], 30).Should().BeEmpty();
        MetricSeriesBuilder.Collapse("requests_total", [], 30).Should().BeEmpty();
        MetricSeriesBuilder.CollapseByScope("memory_bytes", [], 30, 24).Should().BeEmpty();
    }
}
