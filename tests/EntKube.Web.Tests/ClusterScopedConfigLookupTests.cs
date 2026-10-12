using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Mail;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// "The configs for components on this cluster", asked of <c>ICatalogApi</c> instead of through a
/// correlated subquery on Catalog's table.
///
/// <para><b>The subquery never checked the cluster.</b> All three sites looked like
/// <c>db.ClusterComponents.Any(comp =&gt; comp.Id == c.ClusterComponentId &amp;&amp; comp.ClusterId
/// == kubernetesClusterId)</c>: the <em>configs</em> were filtered by tenant, and the components
/// were whatever those ids pointed at. A cluster id belonging to another tenant matched its
/// components quite happily, so passing one listed that tenant's Harbor and Keycloak
/// configuration. <c>GetComponentsForClusterAsync</c> resolves the cluster tenant-first, which
/// closes it without anyone having to remember a predicate.</para>
///
/// <para>These assert the refusal <em>and</em> that the normal case still returns — because an
/// empty list is also what a cluster with no components returns, so asserting emptiness alone
/// would pass against a conversion that simply broke the lookup.</para>
/// </summary>
public class ClusterScopedConfigLookupTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;

    private Guid ours, theirs, ourCluster, theirCluster, ourComponent, theirComponent;

    public ClusterScopedConfigLookupTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private EntKube.Web.Modules.Api.CatalogApi Catalog() => new(testDb.Factory);

    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        (ourCluster, ourComponent) = await SeedTenantAsync(ours, "ours");
        (theirCluster, theirComponent) = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<(Guid ClusterId, Guid ComponentId)> SeedTenantAsync(Guid tenantId, string label)
    {
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid componentId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "harbor",
            ComponentType = "Helm", HelmChartName = "harbor", ReleaseName = "harbor",
            Namespace = "registry", Status = ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        db.Set<HarborComponentConfig>().Add(new HarborComponentConfig
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterComponentId = componentId,
            RegistryUrl = $"https://registry.{label}.example.com",
        });
        db.Set<KeycloakComponentConfig>().Add(new KeycloakComponentConfig
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterComponentId = componentId,
            DisplayName = $"{label} keycloak",
        });
        await db.SaveChangesAsync();

        return (clusterId, componentId);
    }

    private HarborService Harbor() =>
        new(testDb.Factory, testDb.CreateVaultService(), null!, null!, null!, Catalog(),
            new Mock<IHttpClientFactory>().Object);

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Harbor_configs_are_returned_for_our_own_cluster()
    {
        await SeedAsync();

        List<HarborComponentConfig> configs =
            await Harbor().GetConfigsForClusterAsync(ours, ourCluster);

        configs.Should().ContainSingle()
            .Which.RegistryUrl.Should().Be("https://registry.ours.example.com");
    }

    /// <summary>
    /// The hole the subquery left. Our tenant, their cluster id — which the old query happily
    /// matched, listing their registry URL into our form.
    /// </summary>
    [Fact]
    public async Task Harbor_configs_are_not_returned_for_another_tenants_cluster()
    {
        await SeedAsync();

        List<HarborComponentConfig> configs =
            await Harbor().GetConfigsForClusterAsync(ours, theirCluster);

        configs.Should().BeEmpty();
    }

    /// <summary>
    /// And the config rows themselves are still tenant-filtered, so asking with the other
    /// tenant's id for our cluster gets nothing either. Both halves have to hold: the components
    /// come from Catalog scoped by tenant, the configs from a query scoped by tenant.
    /// </summary>
    [Fact]
    public async Task Harbor_configs_are_not_returned_for_another_tenant_on_our_cluster()
    {
        await SeedAsync();

        List<HarborComponentConfig> configs =
            await Harbor().GetConfigsForClusterAsync(theirs, ourCluster);

        configs.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  The detection listing, including failed installs
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Harbor whose install failed must still be listed — it is exactly what an operator opens
    /// the Registry tab to look at. The predicate accepts Installed and Failed and nothing else.
    /// </summary>
    [Theory]
    [InlineData(ComponentStatus.Installed, true)]
    [InlineData(ComponentStatus.Failed, true)]
    [InlineData(ComponentStatus.Installing, false)]
    [InlineData(ComponentStatus.NotInstalled, false)]
    public async Task Harbor_detection_lists_installed_and_failed_only(
        ComponentStatus status, bool listed)
    {
        await SeedAsync();

        await using ApplicationDbContext write = testDb.CreateContext();
        ClusterComponent comp = write.ClusterComponents.Single(c => c.Id == ourComponent);
        comp.Status = status;
        await write.SaveChangesAsync();

        List<DetectedHarbor> detected = await Harbor().GetDetectedHarborInstancesAsync(ours);

        detected.Should().HaveCount(listed ? 1 : 0);

        if (listed)
        {
            DetectedHarbor one = detected.Single();
            one.ComponentId.Should().Be(ourComponent);
            one.ClusterDisplayName.Should().Be("ours-cluster",
                "the contract carries the cluster name inline, which is what the DTO now exposes");
            one.IsConfigured.Should().BeTrue("a config was seeded for it");
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Keycloak's suggested admin URL — the Include that was NOT dead
    // ════════════════════════════════════════════════════════════════

    private KeycloakService Keycloak() =>
        new(testDb.Factory, testDb.CreateVaultService(),
            new Mock<IHttpClientFactory>().Object, null!, Catalog(),
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, new Mock<IKubernetesClientFactory>().Object));

    private async Task GivenARouteAsync(Guid componentId, string hostname, DateTime createdAt)
    {
        await using ApplicationDbContext write = testDb.CreateContext();
        write.ExternalRoutes.Add(new ExternalRoute
        {
            Id = Guid.NewGuid(), ComponentId = componentId, Hostname = hostname,
            ServiceName = "keycloak", ServicePort = 80, PathPrefix = "/",
            TlsMode = TlsMode.ClusterIssuer, CreatedAt = createdAt,
        });
        await write.SaveChangesAsync();
    }

    /// <summary>
    /// The suggested admin URL is built from the component's external hostnames. That used to come
    /// from an <c>.Include(c =&gt; c.ExternalRoutes)</c> on the component query; the component now
    /// comes from Catalog, which carries no routes, so it is a separate query keyed by component
    /// id. The URL has to come out the same.
    ///
    /// <para><b>Earliest route wins</b>, which the old code got from
    /// <c>OrderBy(r =&gt; r.CreatedAt)</c>. Two routes are seeded out of insertion order on
    /// purpose: a conversion that lost the ordering would pick whichever the database happened to
    /// return and pre-fill the operator's admin URL with the wrong hostname.</para>
    /// </summary>
    [Fact]
    public async Task The_suggested_admin_url_uses_the_earliest_route()
    {
        await SeedAsync();
        await MakeItAKeycloakAsync(ourComponent);

        await GivenARouteAsync(ourComponent, "later.example.com", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        await GivenARouteAsync(ourComponent, "earliest.example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DetectedKeycloak one = (await Keycloak().GetDetectedKeycloaksAsync(ours))
            .Single(k => k.Component is not null);

        one.SuggestedAdminUrl.Should().Be("https://earliest.example.com/auth",
            "earliest route, and /auth is the default relativePath");
    }

    /// <summary>
    /// The relative path from the component's Helm values is appended. Keycloak serves its admin
    /// console under <c>http.relativePath</c>, so a URL without it lands on a 404 and the operator
    /// concludes the instance is broken.
    /// </summary>
    [Fact]
    public async Task The_suggested_admin_url_appends_the_helm_relative_path()
    {
        await SeedAsync();
        await MakeItAKeycloakAsync(ourComponent, helmValues: "http:\n  relativePath: /sso\n");

        await GivenARouteAsync(ourComponent, "id.example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DetectedKeycloak one = (await Keycloak().GetDetectedKeycloaksAsync(ours))
            .Single(k => k.Component is not null);

        one.SuggestedAdminUrl.Should().Be("https://id.example.com/sso");
    }

    [Fact]
    public async Task A_keycloak_with_no_route_suggests_nothing_from_routes()
    {
        await SeedAsync();
        await MakeItAKeycloakAsync(ourComponent);

        DetectedKeycloak one = (await Keycloak().GetDetectedKeycloaksAsync(ours))
            .Single(k => k.Component is not null);

        one.SuggestedAdminUrl.Should().BeNull(
            "there is no route and the seeded config carries no AdminUrl to fall back to");
    }

    /// <summary>
    /// Another tenant's routes must not reach our suggestion. The routes query is keyed by
    /// component id, and the component ids come from Catalog scoped to the tenant — so this holds
    /// only because those ids were already filtered.
    /// </summary>
    [Fact]
    public async Task Another_tenants_route_does_not_suggest_a_url_for_us()
    {
        await SeedAsync();
        await MakeItAKeycloakAsync(ourComponent);
        await MakeItAKeycloakAsync(theirComponent);

        await GivenARouteAsync(theirComponent, "theirs.example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DetectedKeycloak one = (await Keycloak().GetDetectedKeycloaksAsync(ours))
            .Single(k => k.Component is not null);

        one.SuggestedAdminUrl.Should().BeNull();
    }

    private async Task MakeItAKeycloakAsync(Guid componentId, string? helmValues = null)
    {
        await using ApplicationDbContext write = testDb.CreateContext();
        ClusterComponent comp = write.ClusterComponents.Single(c => c.Id == componentId);
        comp.Name = "keycloak";
        comp.HelmChartName = "keycloak";
        comp.HelmValues = helmValues;
        await write.SaveChangesAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  The Stalwart server picker — an EF Join replaced by a dictionary
    // ════════════════════════════════════════════════════════════════

    private SupportMailboxService Mailboxes() =>
        new(testDb.Factory, testDb.CreateVaultService(), null!, null!, null!, Catalog(),
            NullLogger<SupportMailboxService>.Instance);

    private async Task GivenAStalwartAsync(
        Guid tenantId, Guid? componentId, string hostname,
        bool imap = true, StalwartAuthMode auth = StalwartAuthMode.Internal)
    {
        await using ApplicationDbContext write = testDb.CreateContext();
        write.StalwartComponentConfigs.Add(new StalwartComponentConfig
        {
            Id = Guid.NewGuid(), TenantId = tenantId, ClusterComponentId = componentId,
            Hostname = hostname, ImapEnabled = imap, AuthMode = auth,
        });
        await write.SaveChangesAsync();
    }

    /// <summary>
    /// The label is <c>hostname (cluster)</c>, and the cluster half is the part this conversion
    /// could lose: it used to come from a navigation on the joined component, and now comes from
    /// <c>InstalledComponent.ClusterName</c>. Without it, two servers for the same domain on
    /// different clusters are indistinguishable in the picker.
    /// </summary>
    [Fact]
    public async Task A_stalwart_server_is_labelled_with_its_hostname_and_cluster()
    {
        await SeedAsync();
        await GivenAStalwartAsync(ours, ourComponent, "mail.ours.example.com");

        List<SupportMailboxService.StalwartMailServerOption> options =
            await Mailboxes().GetStalwartServersAsync(ours);

        SupportMailboxService.StalwartMailServerOption one = options.Should().ContainSingle().Subject;
        one.ComponentId.Should().Be(ourComponent);
        one.Label.Should().Be("mail.ours.example.com (ours-cluster)");
        one.AuthMode.Should().Be(StalwartAuthMode.Internal);
    }

    /// <summary>
    /// The hole the join left, in the same shape as Harbor's: the configs were filtered by tenant
    /// and the components were whatever their ids pointed at. A config row carrying another
    /// tenant's component id listed that tenant's cluster name into our picker — and selecting it
    /// would have pointed a support mailbox at a server we have no business reading.
    /// </summary>
    [Fact]
    public async Task A_config_pointing_at_another_tenants_component_is_not_offered()
    {
        await SeedAsync();
        await GivenAStalwartAsync(ours, theirComponent, "mail.theirs.example.com");

        (await Mailboxes().GetStalwartServersAsync(ours)).Should().BeEmpty();
    }

    /// <summary>
    /// Only installed servers, because the picker's whole purpose is choosing one to connect to.
    /// The status now comes from the contract instead of the joined row.
    /// </summary>
    [Fact]
    public async Task A_server_whose_component_is_not_installed_is_not_offered()
    {
        await SeedAsync();
        await GivenAStalwartAsync(ours, ourComponent, "mail.ours.example.com");

        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            ClusterComponent comp = write.ClusterComponents.Single(c => c.Id == ourComponent);
            comp.Status = ComponentStatus.Failed;
            await write.SaveChangesAsync();
        }

        (await Mailboxes().GetStalwartServersAsync(ours)).Should().BeEmpty(
            "unlike the detection lists, a Failed server is not something to connect a mailbox to");
    }

    /// <summary>
    /// An externally-deployed Stalwart has no <c>ClusterComponentId</c> at all. It cannot be
    /// offered, because the option is keyed by component id — and a conversion that defaulted the
    /// missing id to <c>Guid.Empty</c> and looked it up would either throw or match nothing in
    /// particular.
    /// </summary>
    [Fact]
    public async Task An_external_server_with_no_component_is_not_offered()
    {
        await SeedAsync();
        await GivenAStalwartAsync(ours, null, "mail.external.example.com");

        (await Mailboxes().GetStalwartServersAsync(ours)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_with_imap_disabled_is_not_offered()
    {
        await SeedAsync();
        await GivenAStalwartAsync(ours, ourComponent, "mail.ours.example.com", imap: false);

        (await Mailboxes().GetStalwartServersAsync(ours)).Should().BeEmpty(
            "a support mailbox is read over IMAP");
    }

    [Fact]
    public async Task Harbor_detection_does_not_cross_tenants()
    {
        await SeedAsync();

        (await Harbor().GetDetectedHarborInstancesAsync(ours))
            .Should().ContainSingle().Which.ComponentId.Should().Be(ourComponent);

        (await Harbor().GetDetectedHarborInstancesAsync(theirs))
            .Should().ContainSingle().Which.ComponentId.Should().Be(theirComponent);
    }
}
