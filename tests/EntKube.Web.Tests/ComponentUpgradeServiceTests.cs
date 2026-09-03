using EntKube.Web.Services;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the fleet upgrade planner: Helm repository index parsing, the
/// installed-vs-published comparison rules, and the Kubernetes end-of-life
/// calendar. Covers the judgement calls that decide whether an operator is
/// told to act — including the cases where we deliberately stay silent.
/// </summary>
public class ComponentUpgradeServiceTests
{
    // A trimmed index.yaml in the exact shape Helm repositories publish, deliberately
    // listed out of order so the parser's sorting is actually exercised.
    private const string SampleIndex = """
        apiVersion: v1
        entries:
          traefik:
            - name: traefik
              version: 34.1.0
              appVersion: v3.2.1
              created: "2025-01-15T10:00:00.000Z"
            - name: traefik
              version: 33.0.0
              appVersion: v3.1.0
              created: "2024-11-01T10:00:00.000Z"
            - name: traefik
              version: 34.2.0-rc.1
              appVersion: v3.3.0
              created: "2025-02-01T10:00:00.000Z"
            - name: traefik
              version: 32.1.1
              appVersion: v3.0.4
              created: "2024-09-01T10:00:00.000Z"
              deprecated: true
          cert-manager:
            - name: cert-manager
              version: v1.16.2
              appVersion: v1.16.2
              created: "2025-01-02T10:00:00.000Z"
        """;

    private static ChartIndexResult Index(string chart)
    {
        IReadOnlyDictionary<string, List<ChartRelease>>? parsed = HelmRepoIndexClient.ParseIndex(SampleIndex);
        parsed.Should().NotBeNull();
        return new ChartIndexResult { Success = true, Releases = parsed![chart] };
    }

    private static ComponentUpgrade Evaluate(
        string installedVersion, string chart = "traefik", CatalogEntry? catalog = null)
        => ComponentUpgradeService.Evaluate(
            clusterId: Guid.NewGuid(),
            clusterName: "prod-eu-west-1",
            componentId: Guid.NewGuid(),
            componentName: chart,
            installedVersion: installedVersion,
            repoUrl: "https://traefik.github.io/charts",
            chartName: chart,
            catalog: catalog,
            index: Index(chart));

    // ── Index parsing ──

    [Fact]
    public void Parses_every_chart_and_version_from_a_repository_index()
    {
        IReadOnlyDictionary<string, List<ChartRelease>>? parsed = HelmRepoIndexClient.ParseIndex(SampleIndex);

        parsed.Should().NotBeNull();
        parsed!.Should().ContainKeys("traefik", "cert-manager");
        parsed["traefik"].Should().HaveCount(4);
    }

    [Fact]
    public void Sorts_published_versions_newest_first_regardless_of_file_order()
    {
        List<ChartRelease> releases = HelmRepoIndexClient.ParseIndex(SampleIndex)!["traefik"];

        releases.Select(r => r.Version).Should()
            .ContainInOrder("34.2.0-rc.1", "34.1.0", "33.0.0", "32.1.1");
    }

    [Fact]
    public void Carries_appversion_publish_date_and_deprecation_through_from_the_index()
    {
        ChartRelease deprecated = HelmRepoIndexClient.ParseIndex(SampleIndex)!["traefik"]
            .Single(r => r.Version == "32.1.1");

        deprecated.Deprecated.Should().BeTrue();
        deprecated.AppVersion.Should().Be("v3.0.4");
        deprecated.Created.Should().NotBeNull();
    }

    [Fact]
    public void Parses_v_prefixed_chart_versions()
    {
        HelmRepoIndexClient.ParseIndex(SampleIndex)!["cert-manager"]
            .Single().Parsed!.Minor.Should().Be(16);
    }

    [Fact]
    public void Returns_null_for_yaml_that_is_not_a_repository_index()
    {
        HelmRepoIndexClient.ParseIndex("apiVersion: v1\nsomethingElse: true\n").Should().BeNull();
    }

    // ── Comparison rules ──

    [Fact]
    public void Reports_an_upgrade_when_a_newer_stable_version_is_published()
    {
        ComponentUpgrade result = Evaluate("33.0.0");

        result.Status.Should().Be(UpgradeStatus.UpgradeAvailable);
        result.LatestVersion.Should().Be("34.1.0");
        result.Lag.Should().Be(VersionLag.Major);
        result.IsActionable.Should().BeTrue();
    }

    [Fact]
    public void Counts_how_many_published_versions_sit_between_installed_and_latest()
    {
        // 32.1.1 → 33.0.0 → 34.1.0 (the rc is excluded for a stable install).
        Evaluate("32.1.1").VersionsBehind.Should().Be(2);
    }

    [Fact]
    public void Running_the_newest_stable_version_is_up_to_date()
    {
        ComponentUpgrade result = Evaluate("34.1.0");

        result.Status.Should().Be(UpgradeStatus.UpToDate);
        result.Lag.Should().Be(VersionLag.UpToDate);
        result.IsActionable.Should().BeFalse();
    }

    [Fact]
    public void Never_offers_a_prerelease_upgrade_to_a_stable_install()
    {
        // 34.2.0-rc.1 is newer than 34.1.0 but must not be recommended here.
        Evaluate("34.1.0").Status.Should().Be(UpgradeStatus.UpToDate);
        Evaluate("33.0.0").LatestVersion.Should().Be("34.1.0");
    }

    [Fact]
    public void Offers_prerelease_upgrades_to_someone_already_running_a_prerelease()
    {
        ComponentUpgrade result = Evaluate("34.0.0-rc.1");

        result.LatestVersion.Should().Be("34.2.0-rc.1");
        result.Status.Should().Be(UpgradeStatus.UpgradeAvailable);
    }

    [Fact]
    public void An_installed_version_ahead_of_the_repository_is_not_flagged()
    {
        // Operator pinned a chart newer than the repo index we can see — not a finding.
        Evaluate("35.0.0").Status.Should().Be(UpgradeStatus.UpToDate);
    }

    [Fact]
    public void Flags_a_deprecated_installed_version_even_when_nothing_newer_applies()
    {
        ComponentUpgrade result = ComponentUpgradeService.Evaluate(
            Guid.NewGuid(), "c", Guid.NewGuid(), "traefik",
            installedVersion: "32.1.1",
            repoUrl: "r", chartName: "traefik", catalog: null,
            index: new ChartIndexResult
            {
                Success = true,
                // Only the deprecated version is published, so there is no upgrade to offer.
                Releases = HelmRepoIndexClient.ParseIndex(SampleIndex)!["traefik"]
                    .Where(r => r.Version == "32.1.1").ToList(),
            });

        result.Status.Should().Be(UpgradeStatus.Deprecated);
        result.IsActionable.Should().BeTrue();
        result.Note.Should().Contain("deprecated");
    }

    // ── Staying silent when we don't know ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void An_unrecorded_or_unparseable_installed_version_is_unknown_not_up_to_date(string? installed)
    {
        // Claiming "up to date" here would be a false all-clear on a release we can't rank.
        ComponentUpgrade result = ComponentUpgradeService.Evaluate(
            Guid.NewGuid(), "c", Guid.NewGuid(), "traefik", installed,
            "r", "traefik", null, Index("traefik"));

        result.Status.Should().Be(UpgradeStatus.Unknown);
        result.IsActionable.Should().BeFalse();
        result.Note.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_unreachable_repository_surfaces_as_unknown_with_the_reason()
    {
        ComponentUpgrade result = ComponentUpgradeService.Evaluate(
            Guid.NewGuid(), "c", Guid.NewGuid(), "traefik", "33.0.0",
            "r", "traefik", null, ChartIndexResult.Failed("Could not reach repository: timeout"));

        result.Status.Should().Be(UpgradeStatus.Unknown);
        result.Note.Should().Contain("timeout");
    }

    // ── Catalog pin staleness (a maintainer signal, not an operator one) ──

    [Fact]
    public void Marks_the_catalog_pin_stale_when_it_lags_upstream()
    {
        CatalogEntry catalog = CatalogEntryFor("33.0.0");

        ComponentUpgrade result = Evaluate("33.0.0", catalog: catalog);

        result.CatalogPinnedVersion.Should().Be("33.0.0");
        result.CatalogPinStale.Should().BeTrue();
    }

    [Fact]
    public void Does_not_mark_the_catalog_pin_stale_when_it_matches_the_newest_release()
    {
        Evaluate("33.0.0", catalog: CatalogEntryFor("34.1.0")).CatalogPinStale.Should().BeFalse();
    }

    [Fact]
    public void An_unpinned_catalog_entry_is_never_stale()
    {
        Evaluate("33.0.0", catalog: CatalogEntryFor(null)).CatalogPinStale.Should().BeFalse();
    }

    private static CatalogEntry CatalogEntryFor(string? pinnedVersion) => new()
    {
        Key = "traefik",
        DisplayName = "Traefik",
        Description = "Ingress",
        Icon = "bi-signpost",
        Category = "Ingress",
        HelmRepoUrl = "https://traefik.github.io/charts",
        HelmChartName = "traefik",
        HelmChartVersion = pinnedVersion,
        DefaultNamespace = "traefik",
    };

    // ── Kubernetes end-of-life calendar ──

    [Fact]
    public void Reports_a_supported_kubernetes_version_as_supported()
    {
        KubernetesVersionStatus status =
            KubernetesReleaseCalendar.Classify("v1.34.1", new DateOnly(2026, 1, 1));

        status.State.Should().Be(KubernetesSupportState.Supported);
        status.MinorVersion.Should().Be("1.34");
    }

    [Fact]
    public void Reports_a_version_past_its_end_of_life_date()
    {
        KubernetesVersionStatus status =
            KubernetesReleaseCalendar.Classify("v1.30.2", new DateOnly(2026, 1, 1));

        status.State.Should().Be(KubernetesSupportState.EndOfLife);
        status.DaysRemaining.Should().BeNegative();
    }

    [Fact]
    public void Warns_inside_the_end_of_life_window()
    {
        // 1.32 goes EOL 2026-02-28; 30 days out is inside the 90-day warning window.
        KubernetesVersionStatus status =
            KubernetesReleaseCalendar.Classify("v1.32.0", new DateOnly(2026, 1, 29));

        status.State.Should().Be(KubernetesSupportState.NearingEndOfLife);
        status.DaysRemaining.Should().Be(30);
    }

    [Theory]
    [InlineData("v1.30.2+k3s1")]
    [InlineData("v1.30.2-eks-1234")]
    [InlineData("1.30.2")]
    public void Strips_distribution_suffixes_before_matching_the_upstream_minor(string reported)
    {
        KubernetesReleaseCalendar.Classify(reported, new DateOnly(2026, 1, 1))
            .MinorVersion.Should().Be("1.30");
    }

    [Fact]
    public void A_version_newer_than_the_calendar_is_unknown_rather_than_wrongly_eol()
    {
        // Failing to "unknown" is the safe direction: never tell an operator a
        // current cluster is end-of-life just because this build is old.
        KubernetesReleaseCalendar.Classify("v1.99.0", new DateOnly(2026, 1, 1))
            .State.Should().Be(KubernetesSupportState.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public void An_unreadable_kubelet_version_is_unknown(string? reported)
    {
        KubernetesReleaseCalendar.Classify(reported, new DateOnly(2026, 1, 1))
            .State.Should().Be(KubernetesSupportState.Unknown);
    }

    [Fact]
    public void Counts_how_many_minor_releases_behind_the_newest_known_version()
    {
        KubernetesVersionStatus status =
            KubernetesReleaseCalendar.Classify("v1.31.0", new DateOnly(2026, 1, 1));

        status.LatestKnownMinor.Should().Be("1.34");
        status.MinorsBehind.Should().Be(3);
    }
}
