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
}
