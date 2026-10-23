using System.Globalization;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="MultiSeriesChart"/> actually draws on a comma-decimal host.
///
/// <para><b>The defect, and the comment that hid it.</b> This file's header claimed it "Mirrors
/// MetricChart's invariant-culture coords". Its gridlines did — <c>gy.ToString("F1", Inv)</c> —
/// but <c>LinePoints</c> built its pairs with plain interpolation, <c>$"{x:F1},{y:F1}"</c>, which
/// formats with <see cref="CultureInfo.CurrentCulture"/>. EntKube.Web pins no culture, so on this
/// product's own Swedish hosts <c>(3, 98.5)</c> was emitted as <c>3,0,98,5</c>.</para>
///
/// <para>⚠️ <b>Nothing throws.</b> SVG's number grammar accepts a comma OR whitespace between
/// numbers, so the renderer reads four coordinates where two were meant, every later pair shifts
/// by one, and a plausible smooth line appears that was never plotted from the data — while the
/// gridlines and axis labels stay correct, which is exactly what makes it read as a real
/// measurement. A dot-decimal host hides it completely.</para>
///
/// <para>Found when <see cref="MetricChart"/> was fixed for the identical defect: the two charts
/// are separate components and only one was in that change's scope. This is the other one.</para>
/// </summary>
public class MultiSeriesChartCultureTests : BunitContext
{
    /// <summary>
    /// Two series whose coordinates land on one decimal digit each, so a separator is visible in
    /// every number rather than only in some.
    /// </summary>
    private static List<MetricSeriesGroup> TwoSeries()
    {
        DateTime at = new(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc);

        return
        [
            new MetricSeriesGroup("web", [
                new TimeSeriesDataPoint { Timestamp = at, Value = 25 },
                new TimeSeriesDataPoint { Timestamp = at.AddMinutes(5), Value = 100 },
            ]),
            new MetricSeriesGroup("worker", [
                new TimeSeriesDataPoint { Timestamp = at, Value = 50 },
                new TimeSeriesDataPoint { Timestamp = at.AddMinutes(5), Value = 75 },
            ]),
        ];
    }

    /// <summary>
    /// ⭐ The geometry must be invariant even where the labels are not.
    ///
    /// <para>⚠️ The non-vacuity guard is asserted FIRST and matters as much as the assertion it
    /// guards: a culture test whose culture never reached the renderer passes exactly like one
    /// that proved something. So this first checks a value that is <em>supposed</em> to be
    /// localized — <c>AxisLabel</c> writes <c>ToString("F1")</c> with no culture, so the
    /// half-scale label must read <c>50,0</c> under sv-SE. If THAT fails, the test is reporting
    /// that it never ran under a comma culture, not that the chart is sound.</para>
    /// </summary>
    [Fact]
    public void Polyline_coordinates_stay_invariant_on_a_comma_decimal_host()
    {
        CultureInfo previousCurrent = CultureInfo.CurrentCulture;
        CultureInfo? previousDefault = CultureInfo.DefaultThreadCurrentCulture;

        try
        {
            CultureInfo swedish = CultureInfo.GetCultureInfo("sv-SE");
            CultureInfo.CurrentCulture = swedish;
            CultureInfo.DefaultThreadCurrentCulture = swedish;

            IRenderedComponent<MultiSeriesChart> cut = Render<MultiSeriesChart>(ps => ps
                .Add(p => p.Series, TwoSeries()));

            string html = cut.Markup;

            // The guard. Max is 100, so the mid label is 50 -> "50,0" under sv-SE.
            html.Should().Contain("50,0",
                "AxisLabel formats without a culture on purpose, so a Swedish operator reads "
                + "50,0 — and if this is absent the culture never reached the renderer, which "
                + "would make every assertion below vacuous rather than reassuring");

            // Now the geometry: every points attribute must be dot-decimal.
            IEnumerable<string> pointAttrs = cut.FindAll("polyline")
                .Select(e => e.GetAttribute("points") ?? "");

            pointAttrs.Should().NotBeEmpty("the chart must actually have drawn its series");

            foreach (string points in pointAttrs)
            {
                points.Should().Contain(".",
                    "a coordinate with a decimal digit must carry a dot separator");

                // Each whitespace-separated token is one "x,y" pair: exactly one comma, and both
                // halves must parse as invariant numbers. A localized pair like "3,0,98,5" has
                // three commas and fails here, which is the whole defect.
                foreach (string pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    pair.Count(c => c == ',').Should().Be(1,
                        $"'{pair}' must be one x,y pair — a comma decimal separator turns one "
                        + "pair into what SVG reads as two coordinates, shifting every later "
                        + "point and drawing a line nobody plotted");

                    foreach (string half in pair.Split(','))
                    {
                        double.TryParse(half, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                            .Should().BeTrue($"'{half}' must be an invariant number");
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrent;
            CultureInfo.DefaultThreadCurrentCulture = previousDefault;
        }
    }

    /// <summary>
    /// The same chart on a dot-decimal host, which is why this survived review for so long: the
    /// defect is invisible here, so a reviewer on en-US sees a correct chart either way.
    /// </summary>
    [Fact]
    public void The_same_chart_is_unchanged_on_a_dot_decimal_host()
    {
        CultureInfo previousCurrent = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");

            IRenderedComponent<MultiSeriesChart> cut = Render<MultiSeriesChart>(ps => ps
                .Add(p => p.Series, TwoSeries()));

            foreach (string points in cut.FindAll("polyline").Select(e => e.GetAttribute("points") ?? ""))
            {
                foreach (string pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    pair.Count(c => c == ',').Should().Be(1);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCurrent;
        }
    }
}
