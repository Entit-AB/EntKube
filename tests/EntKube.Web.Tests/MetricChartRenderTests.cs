using System.Globalization;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="MetricChart"/> actually draws, and what it dares to call unhealthy.
///
/// <para><b>Defect 1 — the geometry was localized.</b> The polyline points and the area path were
/// built with plain interpolation (<c>$"{x:F1},{y:F1}"</c>), which formats with
/// <see cref="CultureInfo.CurrentCulture"/>. EntKube.Web pins no culture anywhere, so on this
/// product's own Swedish hosts the pair <c>(2, 70.4)</c> was emitted as <c>"2,0,70,4"</c>. SVG does
/// not complain: its number grammar accepts a comma <i>or</i> whitespace between numbers, so the
/// renderer reads four coordinates where two were meant, every later pair shifts, and a smooth line
/// appears that was never plotted from the data — while the gridlines and axis labels stayed right,
/// because those already used the invariant culture. A dot-decimal host hides it completely and a
/// comma-decimal host breaks every single point, which is why it lived here unnoticed.</para>
///
/// <para>⚠️ The culture test would be worthless if the culture it sets did not reach the renderer,
/// and a passing assertion looks identical either way. So it first asserts on something that is
/// <i>supposed</i> to be localized: the <c>MaxVal / 2</c> axis label, which the component writes
/// with <c>ToString("F1")</c> and no culture, and which must therefore read <c>50,0</c>. If that
/// fails, the test is telling you it never ran under a comma culture — not that the chart is fine.
/// Only then does it assert the geometry, which must be <c>2.0,70.4</c> regardless.</para>
///
/// <para><b>Defect 2 — a percent verdict on numbers that are not percentages.</b> The header
/// readout was coloured by <c>&gt;= 90 → danger, &gt;= 70 → warning, else success</c> applied to
/// the raw last value of any series at all. MetricsExplorer charts whatever metric the operator
/// picked from Prometheus with no Unit, so a byte count of 95 was alarming red and 12 errors per
/// second was a reassuring green. Every other 90/70 comparison in the repo sits on a value named
/// Percent/pct; this was the only unconstrained one.</para>
/// </summary>
public class MetricChartRenderTests : BunitContext
{
    /// <summary>
    /// Two points at 10 and 100 against <c>MaxValue=100</c>, which land on coordinates with one
    /// decimal digit each so the separator is visible in every one of them:
    /// x = 2 and 2 + (300 - 2*2) = 298; y = 78 - 0.10*76 = 70.4 and 78 - 76 = 2.
    /// </summary>
    private static List<TimeSeriesDataPoint> TenThenFull()
    {
        DateTime at = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);
        return
        [
            new TimeSeriesDataPoint { Timestamp = at, Value = 10 },
            new TimeSeriesDataPoint { Timestamp = at.AddMinutes(5), Value = 100 },
        ];
    }

    [Fact]
    public void Svg_coordinates_stay_invariant_on_a_comma_decimal_host()
    {
        CultureInfo previousCurrent = CultureInfo.CurrentCulture;
        CultureInfo? previousDefault = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            CultureInfo swedish = CultureInfo.GetCultureInfo("sv-SE");
            CultureInfo.CurrentCulture = swedish;
            CultureInfo.DefaultThreadCurrentCulture = swedish;

            IRenderedComponent<MetricChart> cut = Render<MetricChart>(ps => ps
                .Add(p => p.Title, "CPU Usage")
                .Add(p => p.DataPoints, TenThenFull())
                .Add(p => p.MaxValue, 100d)
                .Add(p => p.ShowAxes, true));

            // ⚠️ Non-vacuity guard, asserted first: the mid axis label is written with a plain
            // ToString("F1") on purpose (labels are for humans and should be localized), so it
            // reads "50,0" if and only if this render really happened under a comma culture.
            cut.Markup.Should().Contain(
                "50,0",
                "the axis label proves the renderer ran under sv-SE — without that, the coordinate "
                + "assertions below would pass on any dot-decimal host no matter what the code does");

            cut.Find("polyline").GetAttribute("points").Should().Be(
                "2.0,70.4 298.0,2.0",
                "SVG only knows '.' as a decimal point; under the old interpolation this was "
                + "\"2,0,70,4 298,0,2,0\" — four numbers per point, read as a different line");

            cut.Find("path").GetAttribute("d").Should().Be(
                "M 2.0,78.0 L 2.0,70.4 L 298.0,2.0 L 298.0,78.0 Z",
                "the area path under the line has the same hazard in its moveto and every lineto");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrent;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
        }
    }

    [Fact]
    public void A_percentage_reading_is_still_coloured()
    {
        IRenderedComponent<MetricChart> cut = Render<MetricChart>(ps => ps
            .Add(p => p.Title, "CPU Usage")
            .Add(p => p.DataPoints, TenThenFull())
            .Add(p => p.Unit, "%")
            .Add(p => p.MaxValue, 100d));

        cut.Markup.Should().Contain(
            "text-danger",
            "ClusterMonitoring's CPU and Memory charts pass Unit=\"%\" MaxValue=\"100\" and 100% "
            + "really is the top of the scale — this is the case the thresholds were written for");
    }

    [Fact]
    public void A_non_percentage_reading_gets_no_health_verdict()
    {
        // The control for the negative assertions: the same last value, as a percentage, is red
        // (asserted above). So if these come out uncoloured it is the unit doing it, not an
        // unrendered readout.
        IRenderedComponent<MetricChart> podCount = Render<MetricChart>(ps => ps
            .Add(p => p.Title, "Pod Count")
            .Add(p => p.DataPoints, TenThenFull()));

        podCount.Markup.Should().Contain("Pod Count").And.Contain(
            "fw-bold", "the readout itself must be on screen for its colour to be worth asserting");
        podCount.Markup.Should().NotContain(
            "text-danger", "95 pods, or 100, is not 100% of anything — ClusterMonitoring's Pod Count "
            + "chart passes no Unit and used to turn red above 90 pods");
        podCount.Markup.Should().NotContain("text-warning").And.NotContain("text-success");

        IRenderedComponent<MetricChart> heap = Render<MetricChart>(ps => ps
            .Add(p => p.Title, "Heap used")
            .Add(p => p.DataPoints, TenThenFull())
            .Add(p => p.Unit, "B"));

        heap.Markup.Should().Contain("Heap used");
        heap.Markup.Should().NotContain(
            "text-danger", "KeycloakMonitoringPanel charts heap in bytes — red above 90 bytes meant "
            + "red always, which is the same as no signal at all");
    }

    // ── Gaps: a step with no value is a gap, not a missing step ─────────────

    private static List<TimeSeriesDataPoint> Series(params double[] values)
    {
        DateTime at = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        return [.. values.Select((v, i) => new TimeSeriesDataPoint { Timestamp = at.AddMinutes(i), Value = v })];
    }

    private IRenderedComponent<MetricChart> RenderInvariant(List<TimeSeriesDataPoint> points)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return Render<MetricChart>(ps => ps
                .Add(p => p.Title, "p95 latency")
                .Add(p => p.DataPoints, points)
                .Add(p => p.Unit, "ms")
                .Add(p => p.ShowAxes, true));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// ⭐ Pinned against the renderer as it was before gaps existed, captured by running this very
    /// test on that code (ad45bcb4): every chart in the product draws finite data, and teaching the
    /// component about gaps must not move a single coordinate of any of them. Five points, peak 5:
    /// x = 2 + i·74, y = 78 − v/5·76.
    /// </summary>
    [Fact]
    public void A_series_without_gaps_draws_exactly_what_it_drew_before_gaps_were_supported()
    {
        IRenderedComponent<MetricChart> cut = RenderInvariant(Series(1, 3, 2, 5, 4));

        cut.FindAll("polyline").Should().ContainSingle();
        cut.Find("polyline").GetAttribute("points").Should().Be("2.0,62.8 76.0,32.4 150.0,47.6 224.0,2.0 298.0,17.2");
        cut.Find("path").GetAttribute("d").Should().Be(
            "M 2.0,78.0 L 2.0,62.8 L 76.0,32.4 L 150.0,47.6 L 224.0,2.0 L 298.0,17.2 L 298.0,78.0 Z");
        cut.Find(".chart-axis-y").TextContent.Should().Contain("5.00 ms").And.Contain("2.50 ms");
        cut.Find(".fw-bold").TextContent.Should().Be("4.0ms");
    }

    /// <summary>
    /// ⚠️ The p95 series is NaN at every step that served no request (histogram_quantile of 0/0).
    /// The panel used to drop those steps before handing the series here, and this component
    /// places points by list index, so the surviving points were spread over the whole width:
    /// a p95 spike at 12:03 was drawn under the Requests chart's 12:04, beside it on the same row.
    /// A gap keeps its step: the line breaks, and the points after it stay where their time is.
    /// </summary>
    [Fact]
    public void A_NaN_step_breaks_the_line_and_the_points_after_it_keep_their_time()
    {
        IRenderedComponent<MetricChart> cut = RenderInvariant(Series(1, 3, double.NaN, 5, 4));

        cut.FindAll("polyline").Select(l => l.GetAttribute("points")).Should().Equal(
            ["2.0,62.8 76.0,32.4", "224.0,2.0 298.0,17.2"],
            "two segments, and 5 and 4 still sit at the x of steps 3 and 4 — the same x as in the "
            + "gap-free series above, not squeezed into the width that four points would span");
        cut.Find("path").GetAttribute("d").Should().Be(
            "M 2.0,78.0 L 2.0,62.8 L 76.0,32.4 L 76.0,78.0 Z M 224.0,78.0 L 224.0,2.0 L 298.0,17.2 L 298.0,78.0 Z",
            "the area under the line closes at each gap rather than bridging it");
        cut.Markup.Should().NotContain("NaN");
    }

    [Fact]
    public void The_scale_and_the_readout_come_from_finite_values_only()
    {
        IRenderedComponent<MetricChart> cut = RenderInvariant(
            Series(1, double.PositiveInfinity, 3, 5, double.NaN));

        cut.Find(".chart-axis-y").TextContent.Should().Contain("5.00 ms",
            "an infinite step would otherwise be the peak, and every finite point would sit on the floor");
        cut.Find(".fw-bold").TextContent.Should().Be("5.0ms",
            "the latest value is the latest one measured — the trailing NaN is a step without requests");
        cut.Markup.Should().NotContain("NaN").And.NotContain("Infinity").And.NotContain("∞");
    }

    /// <summary>An isolated point between two gaps would be a one-point line, which draws nothing.</summary>
    [Fact]
    public void A_point_between_two_gaps_is_still_drawn()
    {
        IRenderedComponent<MetricChart> cut = RenderInvariant(Series(1, 3, double.NaN, 5, double.NaN));

        cut.FindAll("polyline").Select(l => l.GetAttribute("points")).Should().Equal(
            ["2.0,62.8 76.0,32.4", "224.0,2.0 224.0,2.0"],
            "a zero-length segment with a round cap renders as a dot, at its own time");
    }

    [Fact]
    public void A_series_of_only_NaN_is_the_empty_state_not_a_broken_path()
    {
        IRenderedComponent<MetricChart> cut = RenderInvariant(Series(double.NaN, double.NaN, double.NaN));

        cut.Markup.Should().Contain("Insufficient data");
        cut.FindAll("svg").Should().BeEmpty();
        cut.Markup.Should().NotContain("NaN");
    }
}
