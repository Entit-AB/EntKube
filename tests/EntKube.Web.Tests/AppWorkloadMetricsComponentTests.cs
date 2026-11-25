using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The two parts of the app metrics panel that decide what an absence means: an unmounted volume
/// claim, and an app with no HTTP series. Both used to be the kind of state that renders as a zero
/// — 0 bytes used, a flat request line — and both must say what they are instead.
///
/// <para>The panel itself (<c>AppWorkloadMetricsPanel</c>) only fetches and hands these the
/// results; that wiring needs a live Prometheus and is not tested here. The queries it sends are
/// pinned in <see cref="AppWorkloadQueriesTests"/>.</para>
/// </summary>
public sealed class AppWorkloadMetricsComponentTests : BunitContext
{
    [Fact]
    public void PvcTable_UnmountedClaim_ShowsRequestedSize_AndSaysNotMounted()
    {
        IRenderedComponent<PvcUsageTable> cut = Render<PvcUsageTable>(ps => ps.Add(p => p.Rows,
        [
            new PvcUsageRow("billing", "data", 256d * 1024 * 1024, 1024d * 1024 * 1024, 768d * 1024 * 1024, 1024d * 1024 * 1024, Mounted: true),
            new PvcUsageRow("billing", "archive", null, null, null, 5d * 1024 * 1024 * 1024, Mounted: false),
        ]));

        AngleSharp.Dom.IElement archive = cut.Find("tr[data-claim='archive']");
        archive.TextContent.Should().Contain("not mounted").And.Contain("5 GiB");
        archive.TextContent.Should().NotContain("0 B", "an unmounted claim has no usage, which is not zero usage");

        AngleSharp.Dom.IElement data = cut.Find("tr[data-claim='data']");
        data.TextContent.Should().Contain("256 MiB").And.Contain("1 GiB").And.Contain("768 MiB").And.Contain("25%");
        data.TextContent.Should().NotContain("not mounted");
    }

    /// <summary>
    /// A mounted claim whose storage does not measure itself: requested size, a badge saying usage
    /// is not reported, and neither "not mounted" nor a 0 that reads as an empty disk.
    /// </summary>
    [Fact]
    public void PvcTable_MountedClaimWithoutUsage_SaysUsageNotReported()
    {
        IRenderedComponent<PvcUsageTable> cut = Render<PvcUsageTable>(ps => ps.Add(p => p.Rows,
        [
            new PvcUsageRow("shop", "shop-data", null, null, null, 1024d * 1024 * 1024, Mounted: true),
        ]));

        string text = cut.Find("tr[data-claim='shop-data']").TextContent;
        text.Should().Contain("usage not reported").And.Contain("1 GiB");
        text.Should().NotContain("not mounted").And.NotContain("0 B").And.NotContain("%");
    }

    [Fact]
    public void PvcTable_NoClaims_SaysSo()
        => Render<PvcUsageTable>(ps => ps.Add(p => p.Rows, []))
            .Markup.Should().Contain("No persistent volume claims");

    [Fact]
    public void HttpCharts_NoSeries_ExplainsWhereTheyComeFrom()
    {
        IRenderedComponent<AppHttpMetricsCharts> cut = Render<AppHttpMetricsCharts>(ps => ps.Add(p => p.Available, false));

        string text = cut.Find("[data-empty='http']").TextContent;
        text.Should().Contain("No HTTP metrics yet")
            .And.Contain("eBPF agent")
            .And.Contain("apply the agent again after installing Prometheus");
    }

    /// <summary>The opposite half, so the test above cannot pass by rendering the empty state always.</summary>
    [Fact]
    public void HttpCharts_WithSeries_ChartsThem_AndShowsNoEmptyState()
    {
        DateTime t = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        List<TimeSeriesDataPoint> pts = [new() { Timestamp = t, Value = 1 }, new() { Timestamp = t.AddMinutes(1), Value = 2 }];

        IRenderedComponent<AppHttpMetricsCharts> cut = Render<AppHttpMetricsCharts>(ps => ps
            .Add(p => p.Available, true).Add(p => p.Rate, pts).Add(p => p.Errors, pts).Add(p => p.P95Ms, pts));

        cut.FindAll("[data-empty='http']").Should().BeEmpty();
        cut.Markup.Should().Contain("Requests").And.Contain("5xx errors").And.Contain("p95 latency");
        cut.FindAll("svg").Should().HaveCount(3);
    }

    /// <summary>
    /// ⚠️ A failed request query used to arrive here as "no series", and the reader was told to
    /// install Prometheus and apply the eBPF agent again — on a cluster running both, whose
    /// Prometheus had merely not answered. A failure says so, and advises nothing.
    /// </summary>
    [Fact]
    public void HttpCharts_QueriesFailed_SayCouldNotBeLoaded_AndGiveNoInstallAdvice()
    {
        IRenderedComponent<AppHttpMetricsCharts> cut = Render<AppHttpMetricsCharts>(ps => ps
            .Add(p => p.Available, false)
            .Add(p => p.LoadError, "Response status code does not indicate success: 503 (Service Unavailable)."));

        string text = cut.Find("[data-failed='http']").TextContent;
        text.Should().Contain("could not be loaded").And.Contain("503 (Service Unavailable)");
        cut.Markup.Should().NotContain("No HTTP metrics yet")
            .And.NotContain("apply the agent again")
            .And.NotContain("installing Prometheus");
        cut.FindAll("[data-empty='http']").Should().BeEmpty();
    }

    /// <summary>
    /// The 5xx query alone failing: the Requests chart still draws, and the 5xx column says it
    /// could not be loaded — not a chart, which would be a line of zeros or "Insufficient data".
    /// </summary>
    [Fact]
    public void HttpCharts_Only5xxFailed_ChartsTheRest_AndSaysWhichOneFailed()
    {
        DateTime t = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        List<TimeSeriesDataPoint> pts = [new() { Timestamp = t, Value = 1 }, new() { Timestamp = t.AddMinutes(1), Value = 2 }];

        IRenderedComponent<AppHttpMetricsCharts> cut = Render<AppHttpMetricsCharts>(ps => ps
            .Add(p => p.Available, true).Add(p => p.Rate, pts).Add(p => p.P95Ms, pts)
            .Add(p => p.ErrorsLoadError, "query timed out"));

        cut.Find("[data-failed='5xx errors']").TextContent.Should()
            .Contain("Could not be loaded").And.Contain("query timed out");
        cut.FindAll("svg").Should().HaveCount(2, "Requests and p95 still chart");
        cut.Markup.Should().NotContain("Insufficient data");
    }

    /// <summary>The p95 query alone failing: its own column says so, and the other two still chart.</summary>
    [Fact]
    public void HttpCharts_OnlyP95Failed_ChartsTheRest_AndSaysWhichOneFailed()
    {
        DateTime t = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        List<TimeSeriesDataPoint> pts = [new() { Timestamp = t, Value = 1 }, new() { Timestamp = t.AddMinutes(1), Value = 2 }];

        IRenderedComponent<AppHttpMetricsCharts> cut = Render<AppHttpMetricsCharts>(ps => ps
            .Add(p => p.Available, true).Add(p => p.Rate, pts).Add(p => p.Errors, pts)
            .Add(p => p.P95LoadError, "query timed out"));

        cut.Find("[data-failed='p95 latency']").TextContent.Should()
            .Contain("Could not be loaded").And.Contain("query timed out");
        cut.FindAll("svg").Should().HaveCount(2, "Requests and 5xx still chart");
        cut.Markup.Should().NotContain("Insufficient data");
    }
}
