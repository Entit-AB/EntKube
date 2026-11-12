using System.Text.Json;
using EntKube.Web.Services.PublicApi;
using EntKube.Web.Services.SupplyChain;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;
using Xunit;
using CatalogComponent = EntKube.Contracts.Catalog.ComponentUpgradeSummary;
using CatalogLag = EntKube.Contracts.Catalog.UpgradeLag;
using CatalogStatus = EntKube.Contracts.Catalog.UpgradeAvailability;
using CatalogSummary = EntKube.Contracts.Catalog.UpgradeReportSummary;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /upgrades</c>, <c>GET /drift</c> and <c>GET /supply-chain</c> serialise exactly as the
/// anonymous objects they replaced.
///
/// <para>⚠️ <b>The oracles are the OLD code, cut out of <c>origin/main</c> rather than
/// retyped.</b> A record's mapping and its expected values written from one reading of the handler
/// would agree on a consistent misreading of it.</para>
///
/// <para>⚠️ <b>Every enum on these three routes has a dangerous ZERO member.</b>
/// <c>UpgradeAvailable</c>, <c>Major</c>, <c>Drifted</c> and <c>Vulnerable</c> are all ordinal 0 —
/// so typing any of the four fields as its enum rather than as <c>string</c> would publish
/// <c>0</c> for the most consequential value on each route, and a reader scanning for "is
/// anything wrong" would see a number where a word belongs. All members are asserted as literal
/// JSON text, because <c>CatalogUpgradeContractTests</c> compares the two declarations <b>to each
/// other</b> and so cannot catch a rename applied to both.</para>
/// </summary>
public class UpgradeScanWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);
    private static readonly DateTime At = new(2026, 11, 12, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// ⚠️ Every nullable member SET and every string distinct, because the point is a byte
    /// comparison. <c>Status</c> and <c>Lag</c> are non-zero members so a fixture on the defaults
    /// could not pass while reading the wrong enum.
    /// </summary>
    private static CatalogComponent Component() => new(
        ClusterId: Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
        ClusterName: "prod-north",
        ComponentId: Guid.Parse("c1000000-0000-0000-0000-00000000000d"),
        DisplayName: "Prometheus stack",
        ChartName: "kube-prometheus-stack",
        InstalledVersion: "62.3.0",
        LatestVersion: "65.1.0",
        Status: CatalogStatus.Deprecated,
        Lag: CatalogLag.Minor,
        VersionsBehind: 3);

    private static DriftResult Result() => new()
    {
        DeploymentId = Guid.Parse("d0000000-0000-0000-0000-00000000000e"),
        DeploymentName = "billing-api-prod",
        AppName = "billing",
        ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
        ClusterName = "prod-north",
        Namespace = "billing-prod",
        EnvironmentName = "must-not-ship",
        CustomerName = "must-not-ship",
        State = DriftState.Unknown,
        DiffText = "must-not-ship-this-is-the-manifest-diff",
        ChangedLines = 14,
        Note = "image tag differs from the declared manifest",
        CheckedAt = At,
    };

    private static RunningImage Image() => new()
    {
        Image = "ghcr.io/acme/billing:1.4.2",
        ClusterId = Guid.Parse("c0000000-0000-0000-0000-00000000000c"),
        ClusterName = "prod-north",
        Workloads = ["deployment/billing-api", "cronjob/billing-sweep"],
        State = ImageScanState.Unscanned,
        IsDigestPinned = true,
        Note = "must-not-ship",
        Registry = "must-not-ship",
        Scan = new() { Critical = 2, High = 7, Fixable = 5 },
    };

    [Fact]
    public void An_upgrade_report_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        CatalogSummary report = new(
            Components: [Component()],
            UpgradeCount: 4, MajorCount: 2, DeprecatedCount: 1, UnknownCount: 7);

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            upgradeCount = report.UpgradeCount,
            majorCount = report.MajorCount,
            deprecatedCount = report.DeprecatedCount,
            components = report.Components.Select(c => new
            {
                cluster = c.ClusterName,
                component = c.DisplayName,
                chart = c.ChartName,
                installed = c.InstalledVersion,
                latest = c.LatestVersion,
                status = c.Status.ToString(),
                lag = c.Lag.ToString(),
                versionsBehind = c.VersionsBehind,
            }),
        };

        JsonSerializer.Serialize(UpgradeReportResponse.From(report), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void A_drift_report_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        DriftReport report = new() { GeneratedAt = At, Results = [Result()] };

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            generatedAt = report.GeneratedAt,
            driftedCount = report.DriftedCount,
            unknownCount = report.UnknownCount,
            inSyncCount = report.InSyncCount,
            results = report.Results.Select(r => new
            {
                deploymentId = r.DeploymentId,
                app = r.AppName,
                deployment = r.DeploymentName,
                cluster = r.ClusterName,
                ns = r.Namespace,
                state = r.State.ToString(),
                changedLines = r.ChangedLines,
                note = r.Note,
            }),
        };

        JsonSerializer.Serialize(DriftReportResponse.FromReport(report), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void A_supply_chain_report_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        SupplyChainReport report = new()
        {
            GeneratedAt = At,
            Images = [Image()],
            Warnings = ["one cluster had no Harbor registry configured"],
        };

        // Copied verbatim from origin/main's handler.
        object asItWas = new
        {
            generatedAt = report.GeneratedAt,
            vulnerableCount = report.VulnerableCount,
            unscannedCount = report.UnscannedCount,
            unmanagedCount = report.UnmanagedCount,
            cleanCount = report.CleanCount,
            totalCritical = report.TotalCritical,
            totalHigh = report.TotalHigh,
            warnings = report.Warnings,
            images = report.Images.Select(i => new
            {
                image = i.Image,
                cluster = i.ClusterName,
                state = i.State.ToString(),
                critical = i.CriticalCount,
                high = i.HighCount,
                fixable = i.FixableCount,
                digestPinned = i.IsDigestPinned,
                workloads = i.Workloads,
            }),
        };

        JsonSerializer.Serialize(SupplyChainReportResponse.FromReport(report), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    [Fact]
    public void The_keys_are_the_ones_clients_already_read()
    {
        Keys(UpgradeReportResponse.From(new CatalogSummary([Component()], 1, 1, 0, 0)))
            .Should().Equal("upgradeCount", "majorCount", "deprecatedCount", "components");

        Nested(UpgradeReportResponse.From(new CatalogSummary([Component()], 1, 1, 0, 0)), "components")
            .Should().Equal(
                "cluster", "component", "chart", "installed", "latest", "status", "lag",
                "versionsBehind");

        DriftReport drift = new() { GeneratedAt = At, Results = [Result()] };
        Keys(DriftReportResponse.FromReport(drift)).Should().Equal(
            "generatedAt", "driftedCount", "unknownCount", "inSyncCount", "results");

        Nested(DriftReportResponse.FromReport(drift), "results").Should().Equal(
            "deploymentId", "app", "deployment", "cluster", "ns", "state", "changedLines", "note");

        SupplyChainReport chain = new() { GeneratedAt = At, Images = [Image()] };
        Keys(SupplyChainReportResponse.FromReport(chain)).Should().Equal(
            "generatedAt", "vulnerableCount", "unscannedCount", "unmanagedCount", "cleanCount",
            "totalCritical", "totalHigh", "warnings", "images");

        Nested(SupplyChainReportResponse.FromReport(chain), "images").Should().Equal(
            "image", "cluster", "state", "critical", "high", "fixable", "digestPinned",
            "workloads");
    }

    private static IEnumerable<string> Keys<T>(T value) =>
        JsonSerializer.SerializeToNode(value, AsTheApiSerialises)!.AsObject().Select(p => p.Key);

    private static IEnumerable<string> Nested<T>(T value, string property) =>
        JsonSerializer.SerializeToNode(value, AsTheApiSerialises)!
            .AsObject()[property]!.AsArray()[0]!.AsObject().Select(p => p.Key);

    /// <summary>
    /// ⚠️ <b>Two counts exist on the sources and are NOT on the wire</b>, and both are reproduced
    /// deliberately. This fact is what keeps them off: a byte comparison catches an addition only
    /// while the old literal remains the oracle, whereas a key list says so forever.
    ///
    /// <para><c>UpgradeReportSummary.UnknownCount</c> and
    /// <c>SupplyChainReport.MutableTagCount</c>. Both are recoverable by a client from the items
    /// it already receives — <c>status == "Unknown"</c> for the first,
    /// <c>!digestPinned &amp;&amp; state != "Unmanaged"</c> for the second. ⚠️ Note the second
    /// needs BOTH conditions: counting un-pinned images alone gives a larger number than the UI
    /// shows, because unmanaged images are excluded.</para>
    /// </summary>
    [Fact]
    public void The_two_counts_the_sources_carry_and_the_wire_does_not_stay_off_it()
    {
        CatalogSummary upgrades = new([Component()], 4, 2, 1, UnknownCount: 7);

        string upgradeJson =
            JsonSerializer.Serialize(UpgradeReportResponse.From(upgrades), AsTheApiSerialises);

        Keys(UpgradeReportResponse.From(upgrades)).Should().NotContain(
            "unknownCount", "this route has never published it");
        upgradeJson.Should().NotContain(
            ":7", "and the distinctive value proves the key was not merely renamed");

        // Two images: one un-pinned and Unscanned (counted by MutableTagCount) and one un-pinned
        // but Unmanaged (NOT counted). So the source's figure is 1 while "un-pinned" is 2 —
        // which is exactly the arithmetic a client gets wrong by reading digestPinned alone.
        SupplyChainReport chain = new()
        {
            GeneratedAt = At,
            Images =
            [
                Image() with { IsDigestPinned = false, State = ImageScanState.Unscanned },
                Image() with { IsDigestPinned = false, State = ImageScanState.Unmanaged },
            ],
        };

        chain.MutableTagCount.Should().Be(1, "unmanaged images are excluded from the source's count");
        chain.Images.Count(i => !i.IsDigestPinned).Should().Be(
            2, "whereas un-pinned alone is 2 — the two figures genuinely differ");

        Keys(SupplyChainReportResponse.FromReport(chain)).Should().NotContain("mutableTagCount");
    }

    /// <summary>
    /// ⚠️ <b>Hard-coded expectations.</b> All four of these enums have a consequential ordinal-0
    /// member, so a number on the wire would be read as data rather than as a defect.
    /// </summary>
    [Theory]
    [InlineData(CatalogStatus.UpgradeAvailable, "UpgradeAvailable")]
    [InlineData(CatalogStatus.Deprecated, "Deprecated")]
    [InlineData(CatalogStatus.UpToDate, "UpToDate")]
    [InlineData(CatalogStatus.Unknown, "Unknown")]
    public void An_upgrade_status_is_published_by_name(CatalogStatus status, string expected)
    {
        JsonSerializer.Serialize(
            ComponentUpgradeResponse.From(Component() with { Status = status }), AsTheApiSerialises)
            .Should().Contain($"\"status\":\"{expected}\"");
    }

    [Theory]
    [InlineData(CatalogLag.Major, "Major")]
    [InlineData(CatalogLag.Minor, "Minor")]
    [InlineData(CatalogLag.Patch, "Patch")]
    [InlineData(CatalogLag.UpToDate, "UpToDate")]
    public void An_upgrade_lag_is_published_by_name(CatalogLag lag, string expected)
    {
        JsonSerializer.Serialize(
            ComponentUpgradeResponse.From(Component() with { Lag = lag }), AsTheApiSerialises)
            .Should().Contain($"\"lag\":\"{expected}\"");
    }

    [Theory]
    [InlineData(DriftState.Drifted, "Drifted")]
    [InlineData(DriftState.Unknown, "Unknown")]
    [InlineData(DriftState.InSync, "InSync")]
    public void A_drift_state_is_published_by_name(DriftState state, string expected)
    {
        DriftResultResponse row = DriftResultResponse.From(Result() with { State = state });

        row.State.Should().Be(expected);
        JsonSerializer.Serialize(row, AsTheApiSerialises)
            .Should().Contain($"\"state\":\"{expected}\"");
    }

    [Theory]
    [InlineData(ImageScanState.Vulnerable, "Vulnerable")]
    [InlineData(ImageScanState.Unscanned, "Unscanned")]
    [InlineData(ImageScanState.Unmanaged, "Unmanaged")]
    [InlineData(ImageScanState.Clean, "Clean")]
    public void An_image_state_is_published_by_name(ImageScanState state, string expected)
    {
        SupplyChainImageResponse row =
            SupplyChainImageResponse.From(Image() with { State = state });

        row.State.Should().Be(expected);
        JsonSerializer.Serialize(row, AsTheApiSerialises)
            .Should().Contain($"\"state\":\"{expected}\"");
    }

    /// <summary>
    /// ⚠️ <b>An unscanned image reports <c>critical: 0</c>, and that 0 means UNKNOWN.</b> The
    /// three counts are <c>Scan?.X ?? 0</c> on the source, so an image with no scan is
    /// indistinguishable from a clean one by those fields alone — <c>state</c> is the only thing
    /// that separates them. A client sorting by <c>critical</c> files every unscanned image with
    /// the genuinely clear ones, which is the wrong end of the list.
    /// </summary>
    [Fact]
    public void An_unscanned_image_reports_zero_findings_which_is_not_the_same_as_clean()
    {
        SupplyChainImageResponse unscanned = SupplyChainImageResponse.From(
            Image() with { State = ImageScanState.Unscanned, Scan = null });

        unscanned.Critical.Should().Be(0);
        unscanned.High.Should().Be(0);
        unscanned.Fixable.Should().Be(0);
        unscanned.State.Should().Be(
            "Unscanned", "which is the ONLY field distinguishing this from a clean image");

        SupplyChainImageResponse clean = SupplyChainImageResponse.From(
            Image() with { State = ImageScanState.Clean, Scan = new() });

        clean.Critical.Should().Be(0, "a measured, clear image reports the same three zeroes");
        clean.State.Should().Be("Clean");

        // ⚠️ Stated as an assertion rather than a comment: the two differ in exactly one field.
        //
        // Compared as SERIALISED JSON, not with record equality. A record's generated Equals uses
        // reference equality for a collection member, and `Workloads` is a fresh list per fixture
        // call — so `unscanned with { State = "Clean" } == clean` is FALSE even though every
        // published value matches. The wire is what this fact is about, so compare the wire.
        JsonSerializer.Serialize(unscanned with { State = "Clean" }, AsTheApiSerialises)
            .Should().Be(
                JsonSerializer.Serialize(clean, AsTheApiSerialises),
                "every other published field is identical — state is the only thing a client has "
                + "to tell 'not measured' from 'measured and clear', which is the hazard");
    }

    /// <summary>
    /// ⚠️ The source members these routes have never published, asserted against recognisable
    /// values. <c>DiffText</c> matters most: the diff of a deployment's manifests can contain
    /// whatever the manifests do.
    /// </summary>
    [Fact]
    public void The_members_these_routes_have_never_published_are_still_absent()
    {
        string drift = JsonSerializer.Serialize(
            DriftReportResponse.FromReport(new DriftReport { GeneratedAt = At, Results = [Result()] }),
            AsTheApiSerialises);

        drift.Should().NotContain(
            "must-not-ship",
            "DiffText, EnvironmentName and CustomerName are on DriftResult and have never been on "
            + "this route — a manifest diff especially");
        drift.Should().NotContain("c0000000", "nor ClusterId; the cluster is published by name");

        string chain = JsonSerializer.Serialize(
            SupplyChainReportResponse.FromReport(
                new SupplyChainReport { GeneratedAt = At, Images = [Image()] }),
            AsTheApiSerialises);

        chain.Should().NotContain(
            "must-not-ship",
            "RunningImage.Note and Registry are on the source and not on the wire — note being "
            + "the field that would explain an Unscanned state, which is stated in the record");
    }

    /// <summary>
    /// ⚠️ The counters, and how they relate. The drift states partition the rows; the upgrade
    /// counts do NOT — <c>majorCount</c> is a subset of <c>upgradeCount</c> — and the supply-chain
    /// severity totals are sums, not counts. Asserting the relationships means a factory that
    /// recomputed any of them differently fails.
    /// </summary>
    [Fact]
    public void The_envelope_counters_relate_to_their_lists_as_their_records_claim()
    {
        DriftReport drift = new()
        {
            GeneratedAt = At,
            Results =
            [
                Result() with { State = DriftState.Drifted },
                Result() with { State = DriftState.Drifted },
                Result() with { State = DriftState.Unknown },
                Result() with { State = DriftState.InSync },
            ],
        };

        DriftReportResponse driftReport = DriftReportResponse.FromReport(drift);

        driftReport.DriftedCount.Should().Be(2);
        driftReport.UnknownCount.Should().Be(1);
        driftReport.InSyncCount.Should().Be(1);
        (driftReport.DriftedCount + driftReport.UnknownCount + driftReport.InSyncCount)
            .Should().Be(4, "every drift result has exactly one state, so these DO partition");

        SupplyChainReport chain = new()
        {
            GeneratedAt = At,
            Images =
            [
                Image() with { State = ImageScanState.Vulnerable, Scan = new() { Critical = 2, High = 7, Fixable = 5 } },
                Image() with { State = ImageScanState.Vulnerable, Scan = new() { Critical = 1, High = 1, Fixable = 0 } },
                Image() with { State = ImageScanState.Clean, Scan = new() },
            ],
        };

        SupplyChainReportResponse chainReport = SupplyChainReportResponse.FromReport(chain);

        chainReport.VulnerableCount.Should().Be(2, "a COUNT of images");
        chainReport.TotalCritical.Should().Be(3, "a SUM of findings across images, not a count");
        chainReport.TotalHigh.Should().Be(8);
        chainReport.TotalHigh.Should().BeGreaterThan(
            chain.Images.Count,
            "which is the point: a severity total can exceed the number of images and says "
            + "nothing about how many are affected");
    }
}
