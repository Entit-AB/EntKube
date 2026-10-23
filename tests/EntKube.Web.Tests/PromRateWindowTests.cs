using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The rate window a range query asks for, against the step it is sampled at.
///
/// <para><b>The defect.</b> Range queries computed <c>step = max(15, duration / 100)</c> and then
/// asked for <c>rate(...[5m])</c> — a window written into the query text, independent of the step.
/// Past about six hours the step overtakes it: 432s at 12h, 864s at 24h, against a 300s window. So
/// Prometheus looked at the last five minutes before each sample and ignored the rest, and roughly
/// two thirds of the 24h timeline was never examined.</para>
///
/// <para>⚠️ Nothing failed. The line stayed smooth, plausible and systematically low, which is
/// worse than a gap because a gap is visible. A ten-minute burst of three cores between two
/// samples plotted as zero.</para>
/// </summary>
public class PromRateWindowTests
{
    /// <summary>
    /// ⭐ The property the whole change exists for: the window is never shorter than the step, so
    /// no part of the timeline goes unexamined.
    ///
    /// <para>These are the real step values from <c>Math.Max(15, duration / 100)</c> at the windows
    /// the UI offers. The last two are the ones that were broken.</para>
    /// </summary>
    [Theory]
    [InlineData(3600, 36)]      // 1h
    [InlineData(21600, 216)]    // 6h  — was already inside 300s
    [InlineData(43200, 432)]    // 12h — was 69% covered
    [InlineData(86400, 864)]    // 24h — was 35% covered
    public void The_window_always_covers_its_whole_step(int durationSeconds, int expectedStep)
    {
        int step = Math.Max(15, durationSeconds / 100);
        step.Should().Be(expectedStep, "the step formula is the premise of this whole test");

        PromRateWindow.WindowSeconds(step).Should().BeGreaterThanOrEqualTo(step,
            "a window shorter than the step leaves the rest of every bucket unexamined, which is "
            + "how a ten-minute burst between samples plotted as zero");
    }

    /// <summary>
    /// The floor keeps short ranges and instant queries behaving exactly as before: 300s is the
    /// 5m every one of these queries already asked for.
    /// </summary>
    [Theory]
    [InlineData(0, 300)]        // instant query — no step at all
    [InlineData(15, 300)]       // the step floor
    [InlineData(36, 300)]       // 1h
    [InlineData(75, 300)]       // 4 * 75 == 300, the hinge
    [InlineData(76, 304)]       // first step where four steps exceeds the floor
    [InlineData(432, 1728)]     // 12h
    [InlineData(864, 3456)]     // 24h
    public void The_window_is_four_steps_or_the_floor(int step, int expected)
        => PromRateWindow.WindowSeconds(step).Should().Be(expected);

    /// <summary>
    /// ⚠️ A negative or nonsensical step must not produce a negative window, which would be
    /// invalid PromQL rather than a wrong number — the one failure mode here that WOULD be loud,
    /// and still worth not having.
    /// </summary>
    [Fact]
    public void A_nonsense_step_falls_back_to_the_floor()
        => PromRateWindow.WindowSeconds(-100).Should().Be(PromRateWindow.FloorSeconds);

    /// <summary>
    /// ⭐ <b>An operator's own query is passed through untouched.</b>
    ///
    /// <para>This is the reason the window is opt-in by token rather than a rewrite of every
    /// range query. The metrics explorer sends PromQL the operator typed; if they asked for
    /// <c>[5m]</c> they may mean it, and a product that silently edits the query it was handed
    /// cannot be reasoned about. Several callers of <c>GetMetricRangeAsync</c> pass exactly such
    /// a query.</para>
    /// </summary>
    [Fact]
    public void A_query_without_the_token_is_not_rewritten()
    {
        const string operatorTyped = "sum(rate(my_own_counter_total[5m]))";

        PromRateWindow.Resolve(operatorTyped, 864).Should().Be(operatorTyped,
            "silently rewriting somebody's PromQL is its own defect");
    }

    /// <summary>A product-owned query declares the token and gets the sized window.</summary>
    [Fact]
    public void A_query_with_the_token_is_resolved_at_the_call_sites_step()
    {
        string owned = $"sum(rate(container_cpu_usage_seconds_total[{PromRateWindow.Token}]))";

        PromRateWindow.Resolve(owned, 864)
            .Should().Be("sum(rate(container_cpu_usage_seconds_total[3456s]))");

        PromRateWindow.Resolve(owned, 0)
            .Should().Be("sum(rate(container_cpu_usage_seconds_total[300s]))",
                "an instant query has no step and keeps the 5m it always had");
    }

    /// <summary>
    /// ⚠️ Every occurrence, not just the first. Two of the product's own queries carry two rate
    /// windows — the latency ratio divides one rate by another — and a single-occurrence replace
    /// would leave the denominator reading <c>[$__rate]</c>, which is not valid PromQL and would
    /// fail the whole query rather than skew it.
    /// </summary>
    [Fact]
    public void Every_occurrence_is_replaced()
    {
        string ratio = $"sum(rate(a[{PromRateWindow.Token}])) / sum(rate(b[{PromRateWindow.Token}]))";

        PromRateWindow.Resolve(ratio, 432)
            .Should().Be("sum(rate(a[1728s])) / sum(rate(b[1728s]))")
            .And.NotContain(PromRateWindow.Token);
    }

    /// <summary>
    /// ⭐ The product's own CPU queries must actually carry the token, or the substitution above
    /// is correct and unreached.
    ///
    /// <para>This is the non-vacuity guard for the whole file: every test above exercises
    /// <see cref="PromRateWindow"/> in isolation and would keep passing if no query had been
    /// converted.</para>
    /// </summary>
    [Fact]
    public void The_product_queries_that_go_through_a_range_call_declare_the_token()
    {
        ClusterHealthQueries.NodeCpuPercent.Should().Contain(PromRateWindow.Token);
        ClusterHealthQueries.KubeletCpuPercent.Should().Contain(PromRateWindow.Token);

        ClusterHealthQueries.NodeCpuPercent.Should().NotContain("[5m]",
            "a hard-coded window here is the defect, and it is invisible at 1h");
        ClusterHealthQueries.KubeletCpuPercent.Should().NotContain("[5m]");
    }
}
