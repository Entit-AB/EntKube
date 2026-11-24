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
}
