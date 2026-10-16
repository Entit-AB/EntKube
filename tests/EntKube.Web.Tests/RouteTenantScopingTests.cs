using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Every public entry point on the two route services refuses an id belonging to another tenant.
///
/// <para><b>What this found.</b> <see cref="AppRouteService"/> had <b>twelve</b> instance methods
/// and not one of them took a tenant. Every lookup was keyed by a bare id — an app id, a route id,
/// a deployment-route id — so any id from any tenant in the installation resolved. These rows are
/// not incidental: an <c>AppRoute</c> carries a customer-facing hostname and its TLS material, and
/// an <c>AppDeploymentRoute</c> carries the path prefix, the backend service and the canary weight.
/// Changing one redirects live traffic. <see cref="AppL4RouteService"/> had the same shape across
/// five of its six.</para>
///
/// <para><b>Every caller already had a tenant.</b> All five Razor pages and both services —
/// including the two customer-facing portal pages — had one in scope at the call, and
/// <c>CustomerPortal</c> was passing <c>customer.TenantId</c> to Keycloak and Harbor two lines
/// above the unscoped route call. Nothing had to be plumbed; the services simply never asked.
/// That is the same finding as <see cref="ComponentTenantScopingTests"/>: before writing an
/// exemption, open the caller.</para>
///
/// <para>⚠️ <c>ValidateClientCertificateAsync</c> deserves a correction rather than a headline.
/// It checked that a route's mTLS trust anchor belonged to "the app's own tenant" and derived
/// that tenant by reading it back off the caller-supplied app id — the vacuous shape
/// <see cref="VacuousTenantCheckTests"/> exists to name. I recorded it as an independent hole:
/// name another tenant's app, get that tenant's id, and that tenant's anchor compares equal.
/// <b>Reintroducing the old code proved that wrong.</b> The derivation was only ever exploitable
/// <em>through</em> the unscoped lookup that fed it, so it is the same hole as the missing
/// predicates and not a second one; with the lookups scoped, an app id that reaches this method
/// is already the caller's, and deriving the tenant from it gives the right answer. The check is
/// now redundant rather than wrong, and passing the caller's tenant makes it independent of its
/// callers' diligence instead of inheriting it. The test below cannot tell the two
/// implementations apart — which is the finding, and why it says so out loud instead of claiming
/// a catch it does not make.</para>
///
/// <para>These are behavioural tests rather than a source scan on purpose: a scan can say a
/// predicate is present, and only a foreign id can say it works. Each test seeds two tenants and
/// asks tenant B for tenant A's row.</para>
/// </summary>
public class RouteTenantScopingTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly ApplicationDbContext db;
    private readonly AppRouteService routes;
    private readonly AppL4RouteService l4Routes;

    private readonly Guid tenantA = Guid.NewGuid();
    private readonly Guid tenantB = Guid.NewGuid();
    private readonly Guid appA = Guid.NewGuid();
    private readonly Guid deploymentA = Guid.NewGuid();
    private readonly Guid routeA = Guid.NewGuid();
    private readonly Guid deploymentRouteA = Guid.NewGuid();
    private readonly Guid l4RouteA = Guid.NewGuid();
    private readonly Guid bundleA = Guid.NewGuid();
    private readonly Guid appB = Guid.NewGuid();

    public RouteTenantScopingTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        db = factory.CreateDbContext();
        db.Database.EnsureCreated();

        Guid customerA = Guid.NewGuid();
        Guid envA = Guid.NewGuid();
        Guid clusterA = Guid.NewGuid();

        // Two tenants. B owns nothing in this test; it is the stranger asking the questions.
        db.Tenants.AddRange(
            new Tenant { Id = tenantA, Name = "Alpha", Slug = "alpha" },
            new Tenant { Id = tenantB, Name = "Beta", Slug = "beta" });
        db.Customers.Add(new Customer { Id = customerA, TenantId = tenantA, Name = "Alpha AB" });
        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = envA, TenantId = tenantA, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterA, TenantId = tenantA, EnvironmentId = envA,
            Name = "alpha-1", ApiServerUrl = "https://k8s.alpha.example",
        });
        // Tenant B owns an app of its own. Without it, the trust-anchor test below would be
        // answered by AddRouteAsync's app-ownership guard and never reach the anchor check at
        // all — it passed against the reintroduced bug until this app existed.
        Guid customerB = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = customerB, TenantId = tenantB, Name = "Beta AB" });
        db.Apps.AddRange(
            new App { Id = appA, CustomerId = customerA, Name = "Records" },
            new App { Id = appB, CustomerId = customerB, Name = "Beta Records" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentA, AppId = appA, ClusterId = clusterA, EnvironmentId = envA,
            Name = "records-prod", Namespace = "records",
        });
        db.AppRoutes.Add(new AppRoute
        {
            Id = routeA, AppId = appA, Hostname = "records.alpha.example",
            TlsMode = TlsMode.ClusterIssuer, ClusterIssuerName = "letsencrypt",
        });
        db.AppDeploymentRoutes.Add(new AppDeploymentRoute
        {
            Id = deploymentRouteA, AppRouteId = routeA, AppDeploymentId = deploymentA,
            PathPrefix = "/", ServiceName = "records", ServicePort = 80,
            GatewayName = "gw", GatewayNamespace = "istio-ingress",
        });
        db.AppL4Routes.Add(new AppL4Route
        {
            Id = l4RouteA, AppId = appA, AppDeploymentId = deploymentA,
            Protocol = L4Protocol.Tcp, ExternalPort = 5432,
            ServiceName = "pg", ServicePort = 5432,
            GatewayName = "gw-l4", GatewayNamespace = "istio-ingress",
        });
        db.ClientCaBundles.Add(new ClientCaBundle
        {
            Id = bundleA, TenantId = tenantA, Name = "Alpha clients",
        });
        db.SaveChanges();

        var catalog = new Mock<EntKube.Contracts.Catalog.ICatalogApi>();
        catalog.Setup(c => c.GetComponentsForClusterAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        routes = new AppRouteService(
            factory, catalog.Object, NullLogger<AppRouteService>.Instance);
        l4Routes = new AppL4RouteService(
            factory, catalog.Object, NullLogger<AppL4RouteService>.Instance);
    }

    // ── Reads: a stranger sees nothing rather than someone else's hostnames ──

    [Fact]
    public async Task A_stranger_listing_an_app_s_routes_gets_nothing()
    {
        (await routes.GetRoutesForAppAsync(tenantB, appA)).Should().BeEmpty(
            "the app id is tenant A's; asked as tenant B it must answer about nothing");

        (await l4Routes.GetRoutesForAppAsync(tenantB, appA)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_owner_still_sees_its_own_routes()
    {
        (await routes.GetRoutesForAppAsync(tenantA, appA)).Should().HaveCount(1,
            "a predicate that also blanks the owner is not scoping, it is breakage — the half of "
            + "this pair that catches an over-eager filter");

        (await l4Routes.GetRoutesForAppAsync(tenantA, appA)).Should().HaveCount(1);
    }

    // ── Writes: a stranger is refused, by name ──

    [Fact]
    public async Task A_stranger_cannot_update_or_delete_an_app_route()
    {
        AppRouteRequest request = new() { Hostname = "stolen.beta.example" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.UpdateRouteAsync(tenantB, routeA, request));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.DeleteRouteAsync(tenantB, routeA));

        // The hostname is the thing that would have moved, so assert it did not.
        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.AppRoutes.FirstAsync(r => r.Id == routeA)).Hostname
            .Should().Be("records.alpha.example");
    }

    /// <summary>
    /// ⚠️ The request here is <b>valid in every respect except the tenant</b>, and that is not
    /// incidental. The first draft passed only <c>Hostname</c>, which leaves <c>TlsMode</c> at its
    /// default of <c>ClusterIssuer</c> with no issuer name — so <c>AddRouteAsync</c> threw
    /// "ClusterIssuer name is required" long before it looked at who owned the app, and the test
    /// went green against a build with the ownership guard deleted. A test whose request is
    /// invalid for an unrelated reason asserts nothing about the reason it names.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_add_a_route_to_someone_else_s_app()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.AddRouteAsync(tenantB, appA, new AppRouteRequest
            {
                Hostname = "stolen.beta.example",
                TlsMode = TlsMode.ClusterIssuer,
                ClusterIssuerName = "letsencrypt",
            }));

        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.AppRoutes.CountAsync(r => r.AppId == appA)).Should().Be(1);
    }

    [Fact]
    public async Task A_stranger_cannot_touch_a_deployment_route()
    {
        AppDeploymentRouteRequest request = new() { ServiceName = "theirs", ServicePort = 8080 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.UpdateDeploymentRouteAsync(tenantB, deploymentRouteA, request));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.UpdateDeploymentRouteTimeoutAsync(tenantB, deploymentRouteA, 5));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.UpdateDeploymentRouteSessionAffinityAsync(
                tenantB, deploymentRouteA, SessionAffinityMode.Cookie, "sid", 60));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.UpdateDeploymentRouteCanaryAsync(tenantB, deploymentRouteA, "canary", 50));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.DeleteDeploymentRouteAsync(tenantB, deploymentRouteA));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.GenerateManifestYamlAsync(tenantB, deploymentRouteA));

        // Canary weight is the one that silently moves live traffic, so check it by value.
        await using ApplicationDbContext check = factory.CreateDbContext();
        AppDeploymentRoute dr = await check.AppDeploymentRoutes.FirstAsync(r => r.Id == deploymentRouteA);
        dr.ServiceName.Should().Be("records");
        dr.CanaryWeight.Should().Be(0);
        dr.CanaryServiceName.Should().BeNull();
    }

    [Fact]
    public async Task A_stranger_cannot_link_a_deployment_route_to_someone_else_s_hostname()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.AddDeploymentRouteAsync(tenantB, routeA, deploymentA,
                new AppDeploymentRouteRequest { ServiceName = "theirs", ServicePort = 80 }));
    }

    [Fact]
    public async Task A_stranger_cannot_touch_an_l4_route()
    {
        // 15999 rather than a reserved Istio port, and a real service name: valid in every
        // respect except the tenant, for the reason given on the AddRoute test above.
        AppL4RouteRequest request = new()
        {
            Protocol = L4Protocol.Tcp, ExternalPort = 15999, ServiceName = "pg", ServicePort = 5432,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => l4Routes.UpdateRouteAsync(tenantB, l4RouteA, request));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => l4Routes.DeleteRouteAsync(tenantB, l4RouteA));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => l4Routes.GenerateManifestYamlAsync(tenantB, l4RouteA));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => l4Routes.AddRouteAsync(tenantB, appA, deploymentA, request));

        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.AppL4Routes.FirstAsync(r => r.Id == l4RouteA)).ExternalPort.Should().Be(5432);
    }

    /// <summary>
    /// A route may not point at another tenant's mTLS trust anchor: doing so would publish it on
    /// that anchor's listener port and accept that tenant's client certificates.
    ///
    /// <para>⚠️ This passes against the old derived-tenant implementation too — see the class
    /// note. It pins the rule, not the fix.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_point_a_route_at_another_tenant_s_trust_anchor()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => routes.AddRouteAsync(tenantB, appB, new AppRouteRequest
            {
                Hostname = "mtls.beta.example",
                RequireClientCertificate = true,
                ClientCaBundleId = bundleA,
            }));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
