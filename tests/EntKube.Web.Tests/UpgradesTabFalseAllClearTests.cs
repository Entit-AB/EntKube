using System.Net;
using System.Net.Http;
using AngleSharp.Dom;
using Bunit;
using EntKube.Web.Components.Pages.Tenants;
using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What an operator reads off the fleet upgrade planner.
///
/// <para>⭐ Every defect pinned here is the same shape: the page stated something it had not
/// established. A volume preflight that could not run rendered the green "no planned downtime"
/// check; a deprecated row with nothing newer published offered an "Upgrade" to the version
/// already installed; one node reporting an unreadable kubelet version made the whole cluster
/// report "Unknown" and hid a genuinely end-of-life minor behind a grey badge; and an empty
/// filtered table said "everything here is up to date" directly underneath a card counting the
/// updates the filter had excluded. None of them fails, logs, or looks broken.</para>
///
/// <para>⚠️ <b>Why the harness builds real services rather than mocking them.</b>
/// <c>ComponentUpgradeService</c>, <c>ReleaseVolumeGuard</c> and <c>ComponentUpgradeRunner</c>
/// are concrete classes with no interface and no virtual member, so there is nothing to
/// substitute. They are built over the test SQLite database with their two outside edges faked at
/// the seams that ARE interfaces: <see cref="IHttpClientFactory"/> for the Helm repository index,
/// and <see cref="IClusterClientFactory"/> for cluster access. That is what makes the preflight's
/// failure path reachable at all — it is exactly the "cluster is not this tenant's, or it has no
/// kubeconfig" branch of <c>InspectAsync</c>.</para>
///
/// <para>⚠️ <c>ComponentUpgradeRunner</c> is constructed with nulls for its lifecycle service and
/// install orchestrator. Those two drag in Harbor, OpenLDAP, Velero, Stalwart and Headscale, and
/// nothing here runs an upgrade — the tests stop at the dialog, which is where the false statement
/// was. If a test ever calls <c>UpgradeAsync</c> it will NRE immediately and loudly, which is the
/// right failure for a dependency a test deliberately did not supply.</para>
/// </summary>
public class UpgradesTabFalseAllClearTests : BunitContext, IDisposable
{
    private const string RepoUrl = "https://charts.invalid.test/probe";

    /// <summary>A chart name no catalog entry resolves, so the row is a plain adopted release.</summary>
    private const string ChartName = "upgrades-tab-probe";

    /// <summary>Fixed so the support calendar's verdicts below never drift with the wall clock.</summary>
    private static readonly DateOnly Today = new(2026, 10, 23);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();

    public UpgradesTabFalseAllClearTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Guid envId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = $"entit-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-eu-west-1", ApiServerUrl = "https://c:6443",
        });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
    }

    public new void Dispose()
    {
        connection.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────────────────────────── harness ────────────────────────────

    /// <summary>Records one installed Helm release for the report to measure.</summary>
    private void SeedInstalledRelease(string installedVersion)
    {
        using ApplicationDbContext db = factory.CreateDbContext();
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = clusterId,
            Name = ChartName,
            ComponentType = "HelmChart",
            Status = ComponentStatus.Installed,
            Namespace = "probe",
            ReleaseName = ChartName,
            HelmRepoUrl = RepoUrl,
            HelmChartName = ChartName,
            HelmChartVersion = installedVersion,
        });
        db.SaveChanges();
    }

    private sealed class YamlHandler(string yaml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(yaml),
            });
    }

    /// <summary>
    /// A cluster EntKube can reach, whose namespace holds no Deployments at all — so the preflight
    /// genuinely runs and genuinely finds nothing blocked. This is the positive control for the
    /// all-clear banner.
    /// </summary>
    private static IClusterClientFactory ReachableAndEmpty()
    {
        Mock<IClusterClient> client = new();
        client.SetupGet(c => c.ClusterId).Returns(Guid.NewGuid());
        client.SetupGet(c => c.ClusterName).Returns("prod-eu-west-1");
        client.Setup(c => c.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("{\"items\":[]}");

        Mock<IClusterClientFactory> access = new();
        access.Setup(a => a.ForAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return access.Object;
    }

    private IRenderedComponent<UpgradesTab> Mount(string indexYaml, IClusterClientFactory access)
    {
        Mock<IHttpClientFactory> http = new();
        http.Setup(h => h.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new YamlHandler(indexYaml)));

        ReleaseVolumeGuard guard = new(
            factory, access, TestServices.NewGate(), NullLogger<ReleaseVolumeGuard>.Instance);

        Services.AddEveryContext(factory);
        Services.AddSingleton(new ComponentUpgradeService(
            factory,
            new HelmRepoIndexClient(http.Object, NullLogger<HelmRepoIndexClient>.Instance),
            NullLogger<ComponentUpgradeService>.Instance));
        Services.AddSingleton(guard);
        Services.AddSingleton(new ComponentUpgradeRunner(
            factory, null!, null!, guard, access, NullLogger<ComponentUpgradeRunner>.Instance));
        Services.AddSingleton(new NodeManagementService(
            factory,
            new AuditService(factory, new EntKube.Web.Modules.Api.DeliveryApi(factory)),
            access,
            NullLogger<NodeManagementService>.Instance));

        return Render<UpgradesTab>(p => p
            .Add(c => c.TenantId, tenantId)
            .Add(c => c.TenantSlug, "entit"));
    }

    /// <summary>An index publishing 34.2.0 and 34.1.0, so an installed 34.1.0 is one minor behind.</summary>
    private static readonly string UpgradeableIndex = $"""
        apiVersion: v1
        entries:
          {ChartName}:
            - name: {ChartName}
              version: 34.2.0
              created: "2026-02-01T10:00:00.000Z"
            - name: {ChartName}
              version: 34.1.0
              created: "2026-01-15T10:00:00.000Z"
        """;

    /// <summary>
    /// An index whose ONLY published version is the installed one, marked deprecated upstream.
    /// This is the shape that makes <c>Status == Deprecated</c>: deprecation is only reported when
    /// the lag is UpToDate, i.e. there is nothing newer to move to.
    /// </summary>
    private static readonly string DeprecatedOnlyIndex = $"""
        apiVersion: v1
        entries:
          {ChartName}:
            - name: {ChartName}
              version: 32.1.1
              created: "2024-09-01T10:00:00.000Z"
              deprecated: true
        """;

    private static IElement? UpgradeButton(IRenderedComponent<UpgradesTab> cut) =>
        cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == "Upgrade");

    // ─────────────── the volume preflight's all-clear (line 330) ───────────────

    /// <summary>
    /// ⭐ A preflight that could not run must not render the green check.
    ///
    /// <para>The all-clear used to be the bare <c>else</c> of <c>preflight.RequiresScaleDown</c>,
    /// and <c>RequiresScaleDown</c> is <c>Blocked.Count &gt; 0</c> — which is also what every
    /// FAILURE of the preflight produces, because <c>VolumePreflight.Failure</c> sets Error and
    /// leaves Blocked empty. So the amber "the volume check could not run" banner and a green
    /// "Helm can roll it in place with no planned downtime" rendered one above the other, about
    /// the same uninspected release.</para>
    ///
    /// <para>⚠️ The first assertion is not decoration. Asserting only the ABSENCE of the green
    /// sentence would also pass if the dialog never opened, if the click did nothing, or if the
    /// banner were deleted outright — so the error path has to be shown to be the path taken
    /// before its silence means anything.</para>
    /// </summary>
    [Fact]
    public void A_volume_check_that_could_not_run_is_not_reported_as_no_planned_downtime()
    {
        SeedInstalledRelease("34.1.0");
        IRenderedComponent<UpgradesTab> cut = Mount(UpgradeableIndex, TestServices.NoClusterAccess);

        UpgradeButton(cut).Should().NotBeNull("the row is one minor behind, so it is upgradeable");
        UpgradeButton(cut)!.Click();

        // The path taken: ForAsync answered null, so InspectAsync returned Failure(...).
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("The volume check could not run"));

        cut.Markup.Should().NotContain("no planned downtime",
            "nothing was inspected, so the dialog cannot assert that nothing is blocked");
        cut.Markup.Should().NotContain("is blocked by an exclusive volume");

        // And the button the operator presses says which of the three cases this is.
        cut.Markup.Should().Contain("Upgrade without the volume check");
        cut.Markup.Should().NotContain("Upgrade to 34.2.0");
    }

    /// <summary>
    /// The positive control for the test above: when the preflight really runs and really finds
    /// nothing blocked, the green all-clear is exactly right and must still be there. Without
    /// this, deleting the banner would pass.
    /// </summary>
    [Fact]
    public void A_preflight_that_ran_clean_still_renders_the_all_clear()
    {
        SeedInstalledRelease("34.1.0");
        IRenderedComponent<UpgradesTab> cut = Mount(UpgradeableIndex, ReachableAndEmpty());

        UpgradeButton(cut).Should().NotBeNull();
        UpgradeButton(cut)!.Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("no planned downtime"));

        cut.Markup.Should().NotContain("The volume check could not run");
        cut.Markup.Should().Contain("Upgrade to 34.2.0",
            "an inspected, unblocked release is the one case that may be offered as a plain upgrade");
    }

    // ─────────────── the deprecated row's Upgrade button (line 157) ───────────────

    /// <summary>
    /// ⭐ A deprecated release with nothing newer published has no upgrade to offer.
    ///
    /// <para><c>IsActionable</c> is UpgradeAvailable OR Deprecated, and Deprecated is only set when
    /// the lag is UpToDate — so <c>LatestVersion</c> equals <c>InstalledVersion</c>. Gating the
    /// button on <c>IsActionable</c> alone produced a dialog reading "32.1.1 → 32.1.1", a footer
    /// reading "Upgrade to 32.1.1", a re-apply of the identical chart, and the success line
    /// "… is now on 32.1.1" over a row that was still red.</para>
    ///
    /// <para>⚠️ The row must still be LISTED — it needs attention, it just has no action — so this
    /// asserts the badge is present in the same breath as the button being gone.</para>
    /// </summary>
    [Fact]
    public void A_deprecated_row_with_nothing_newer_offers_no_upgrade_to_its_own_version()
    {
        SeedInstalledRelease("32.1.1");
        IRenderedComponent<UpgradesTab> cut = Mount(DeprecatedOnlyIndex, TestServices.NoClusterAccess);

        // Positive half: the row is there, flagged, and in the default "Needs attention" filter.
        cut.Markup.Should().Contain("Deprecated");
        cut.Markup.Should().Contain(ChartName);

        UpgradeButton(cut).Should().BeNull(
            "the only published version is the one already installed, so there is nothing to upgrade to");
        cut.Markup.Should().Contain("No newer version published",
            "the row has to say why it offers no action, or the missing button reads as a bug");
    }

    /// <summary>
    /// The positive control: a row that genuinely is behind still gets its button. Without this,
    /// gating the button on something always-false would pass the test above.
    /// </summary>
    [Fact]
    public void A_row_that_is_genuinely_behind_still_offers_the_upgrade()
    {
        SeedInstalledRelease("34.1.0");
        IRenderedComponent<UpgradesTab> cut = Mount(UpgradeableIndex, TestServices.NoClusterAccess);

        UpgradeButton(cut).Should().NotBeNull();
        cut.Markup.Should().NotContain("No newer version published");
    }

    // ─────────────── the empty filtered table (line 92) ───────────────

    /// <summary>
    /// An empty filtered table is a statement about the FILTER, not about the tenant.
    ///
    /// <para>The summary cards are computed over the whole report, so with "Major behind only"
    /// selected the page used to print "everything here is up to date" directly underneath a card
    /// reading "Updates available 1".</para>
    /// </summary>
    [Fact]
    public void An_empty_filter_result_does_not_claim_everything_is_up_to_date()
    {
        SeedInstalledRelease("34.1.0");
        IRenderedComponent<UpgradesTab> cut = Mount(UpgradeableIndex, TestServices.NoClusterAccess);

        // Positive half: the default filter shows the row, so the harness is not simply empty.
        cut.Markup.Should().Contain("Minor behind");

        // "Major behind only" — a strict subset of actionable, and this row is a minor behind.
        cut.FindAll("select")[1].Change("major");

        cut.Markup.Should().Contain("Nothing matches the current filters");
        cut.Markup.Should().NotContain("everything here is up to date",
            "the cards above still count the updates this filter excluded");
    }

    // ─────────────── the cluster's reported kubelet version (line 515) ───────────────

    /// <summary>
    /// ⭐ One unreadable node must not decide the whole cluster's support badge.
    ///
    /// <para>The selection used to be <c>.OrderBy(v =&gt; SemVer.Parse(v)).FirstOrDefault()</c>.
    /// <c>SemVer.Parse</c> returns null for anything it cannot read, and
    /// <c>Comparer&lt;SemVer&gt;.Default</c> orders null first — so the unreadable string was
    /// selected as the cluster's "oldest", <c>Classify</c> returned Unknown for it, and a cluster
    /// on an end-of-life minor rendered a grey "Unknown" badge with "—" for End of life.</para>
    ///
    /// <para>This is asserted against the calendar rather than against the raw string, because the
    /// defect was never about the string: it was about which badge the operator sees.</para>
    /// </summary>
    [Fact]
    public void An_unreadable_kubelet_version_does_not_hide_an_end_of_life_cluster()
    {
        (string? version, string? note) =
            UpgradesTab.SelectOldestKubelet(["fake", "v1.28.9"], Today);

        version.Should().Be("v1.28.9");
        KubernetesReleaseCalendar.Classify(version, Today).State
            .Should().Be(KubernetesSupportState.EndOfLife);

        note.Should().NotBeNull("narrowing to the nodes that could be placed has to be disclosed");
        note.Should().Contain("'fake'");
    }

    /// <summary>
    /// The positive control: when every node is readable the oldest one still wins, and nothing is
    /// disclosed because nothing was skipped. Asserting only the case above would pass if the
    /// method always returned the newest version, or always returned a note.
    /// </summary>
    [Fact]
    public void The_oldest_readable_kubelet_decides_the_badge_and_says_nothing_extra()
    {
        (string? version, string? note) =
            UpgradesTab.SelectOldestKubelet(["v1.34.1", "v1.28.9", "v1.31.0"], Today);

        version.Should().Be("v1.28.9");
        note.Should().BeNull("every node was placed, so there is nothing to qualify");
    }

    /// <summary>
    /// A dev-build kubelet is the second route to the same grey badge: <c>v0.0.0-master+…</c>
    /// parses, so it would win an ordering on version numbers alone, but 0.0.0 is in no support
    /// window. Only versions the calendar can place may decide the badge.
    /// </summary>
    [Fact]
    public void A_dev_build_kubelet_does_not_win_the_ordering()
    {
        (string? version, string? note) = UpgradesTab.SelectOldestKubelet(
            ["v0.0.0-master+$Format:%H$", "v1.28.9"], Today);

        version.Should().Be("v1.28.9");
        note.Should().NotBeNull();
    }

    /// <summary>
    /// ⚠️ When NOTHING can be placed, "Unknown" is the honest answer and no note is invented. The
    /// row still shows what the nodes reported, which is the one case the old behaviour got right.
    /// </summary>
    [Fact]
    public void A_cluster_nothing_can_be_said_about_still_reports_what_the_nodes_said()
    {
        (string? version, string? note) =
            UpgradesTab.SelectOldestKubelet(["fake", "also-fake"], Today);

        version.Should().NotBeNull();
        KubernetesReleaseCalendar.Classify(version, Today).State
            .Should().Be(KubernetesSupportState.Unknown);
        note.Should().BeNull("the badge is not based on a subset — it is based on nothing, and says so");
    }

    /// <summary>A cluster that reported no versions at all has nothing to show and nothing to say.</summary>
    [Fact]
    public void No_reported_versions_yields_no_version_and_no_note()
    {
        (string? version, string? note) =
            UpgradesTab.SelectOldestKubelet([null, "", "   "], Today);

        version.Should().BeNull();
        note.Should().BeNull();
    }
}
