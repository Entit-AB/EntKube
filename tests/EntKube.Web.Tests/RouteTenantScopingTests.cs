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
/// callers' diligence instead of inheriting it.</para>
///
/// <para>⚠️ <b>That note used to end by saying the test "cannot tell the two implementations
/// apart", and gave the wrong reason.</b> It could not tell them apart because it never called the
/// method: the request left <c>TlsMode</c> at its default, so an earlier guard threw and
/// <c>ValidateClientCertificateAsync</c> was not entered — it could not have told a correct
/// implementation from a deleted one either. A caveat that names the wrong cause is worse than
/// none, because it reads as "pinned, just not precisely". See the test itself for what it took
/// to actually reach the check.</para>
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
    private readonly Guid routeB = Guid.NewGuid();
    private readonly Guid bundleB = Guid.NewGuid();

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
        // B owns a hostname of its own. Without it, the deployment-route test below could only
        // ever reach the FIRST of the two tenant predicates in AddDeploymentRouteAsync — a route
        // id B passes legitimately is the only way to put the second one on trial.
        db.AppRoutes.Add(new AppRoute
        {
            Id = routeB, AppId = appB, Hostname = "records.beta.example",
            TlsMode = TlsMode.ClusterIssuer, ClusterIssuerName = "letsencrypt",
        });
        db.ClientCaBundles.AddRange(
            new ClientCaBundle { Id = bundleA, TenantId = tenantA, Name = "Alpha clients" },
            new ClientCaBundle { Id = bundleB, TenantId = tenantB, Name = "Beta clients" });
        // ⚠️ BOTH ANCHORS CARRY A CA, and that is load-bearing rather than tidy. An anchor with no
        // certificate is refused by its own guard in ValidateClientCertificateAsync — the one
        // below the tenant predicate — with the same exception type. While bundleA was seeded
        // empty, the trust-anchor test was answered by that guard instead and would have passed
        // with the tenant predicate deleted.
        db.ClientCaCertificates.AddRange(
            new ClientCaCertificate
            {
                Id = Guid.NewGuid(), BundleId = bundleA, Name = "Alpha Root CA",
                Pem = "-----BEGIN CERTIFICATE-----\nQUxQSEE=\n-----END CERTIFICATE-----",
            },
            new ClientCaCertificate
            {
                Id = Guid.NewGuid(), BundleId = bundleB, Name = "Beta Root CA",
                Pem = "-----BEGIN CERTIFICATE-----\nQkVUQQ==\n-----END CERTIFICATE-----",
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

    /// <summary>
    /// ⚠️ <b>PathPrefix is set, and the message is asserted</b>, for the same reason the AddRoute
    /// test sets a TLS mode. The first version passed neither: <c>PathPrefix</c> then defaults to
    /// <c>"/"</c> — twice over, at <c>AppDeploymentRouteRequest</c>'s own default and again at
    /// <c>AddDeploymentRouteAsync</c>'s null-or-whitespace fallback — and the fixture already
    /// seeds <c>deploymentRouteA</c> on <c>routeA</c> at <c>"/"</c>. So the collision guard
    /// <em>below</em> both tenant predicates threw the same <see cref="InvalidOperationException"/>,
    /// and the test passed against a build with both predicates deleted.
    ///
    /// <para>Two refusals and one type is a test that cannot say which guard answered. Pinning the
    /// message is what makes the difference, because the collision guard's wording ("Path prefix
    /// '...' is already in use on this hostname") shares no phrase with a tenant refusal.</para>
    ///
    /// <para>⚠️ <b>The phrase has to name its own guard.</b> Asserting the shared tail "does not
    /// belong to this tenant" was not enough and was caught by mutation: both predicates end with
    /// it, so deleting the <c>AppRoute</c> one left this case green, answered by the
    /// <c>AppDeployment</c> one. The two cases assert "App route not found" and "Deployment not
    /// found" respectively — the halves that differ.</para>
    ///
    /// <para><c>routeB</c> is the second half: tenant B passes a route id of its own, which clears
    /// the first predicate, so only the <c>AppDeployment</c> one can refuse it. Without that case
    /// either predicate alone still covers the other, and the pair can only catch their total
    /// removal.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_link_a_deployment_route_to_someone_else_s_hostname()
    {
        // "/api", not the default "/": a free prefix, so no collision can answer in a tenant
        // predicate's place.
        AppDeploymentRouteRequest request = new()
        {
            PathPrefix = "/api", ServiceName = "theirs", ServicePort = 80,
        };

        (await routes.Invoking(r => r.AddDeploymentRouteAsync(tenantB, routeA, deploymentA, request))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("App route not found",
                "the type alone is also thrown by the path-collision guard below both predicates — "
                + "and \"does not belong to this tenant\" is not enough either, because BOTH "
                + "predicates say that: asserting the shared phrase left this case green with the "
                + "AppRoute predicate deleted, answered by the deployment one instead");

        // B's own hostname, A's deployment: clears the AppRoute predicate so the AppDeployment
        // one is the only thing left that can refuse.
        (await routes.Invoking(r => r.AddDeploymentRouteAsync(tenantB, routeB, deploymentA, request))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Deployment not found",
                "a route id the caller legitimately owns must still not reach another tenant's "
                + "deployment — this is the half the first case cannot reach");

        // The siblings in this file all check the post-state; this one did not. A method that
        // swallowed the error and returned would otherwise graft a rule onto A's hostname unseen.
        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.AppDeploymentRoutes.CountAsync(r => r.AppRouteId == routeA)).Should().Be(1);
        (await check.AppDeploymentRoutes.CountAsync(r => r.AppRouteId == routeB)).Should().Be(0);
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
    /// that anchor's listener port and accept that tenant's client certificates. Anchors are
    /// isolated by <c>ListenerPort</c> rather than by hostname, and bundles sharing a port are
    /// merged into one trust store, so a foreign anchor admits its signer on every host there.
    ///
    /// <para>⚠️ <b>THIS TEST DID NOT REACH THE CHECK IT NAMES.</b> It is kept with the full
    /// request it needs rather than quietly corrected, because the way it failed is the lesson.
    /// It passed <c>Hostname</c>, <c>RequireClientCertificate</c> and <c>ClientCaBundleId</c> and
    /// nothing else, which leaves <c>TlsMode</c> at <c>ClusterIssuer</c> with no issuer name — so
    /// <c>AddRouteAsync</c> threw "ClusterIssuer name is required when using automatic TLS." four
    /// guards above <c>ValidateClientCertificateAsync</c>, and the method under test was never
    /// entered. <b>The warning against exactly this is eighty lines up</b>, on
    /// <see cref="A_stranger_cannot_add_a_route_to_someone_else_s_app"/>, which sets a TLS mode
    /// for this reason.</para>
    ///
    /// <para>⚠️ A second blocker sat behind the first: <c>bundleA</c> was seeded with no CA, and
    /// the empty-anchor guard <em>below</em> the tenant predicate throws the same exception type.
    /// Supplying the TLS mode alone would have moved the test from one wrong guard to another, so
    /// the fixture now gives both anchors a certificate. Three guards answered one
    /// <c>ThrowsAsync&lt;InvalidOperationException&gt;</c>; the message is asserted so that only
    /// one of them can.</para>
    ///
    /// <para>The class note above says this test "cannot tell the two implementations apart". That
    /// was true for a reason the note gets wrong: not because the derivation happens to give the
    /// right answer, but because the method never ran. It could not tell a correct implementation
    /// from a deleted one.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_point_a_route_at_another_tenant_s_trust_anchor()
    {
        (await routes.Invoking(r => r.AddRouteAsync(tenantB, appB, new AppRouteRequest
                {
                    Hostname = "mtls.beta.example",
                    TlsMode = TlsMode.ClusterIssuer,
                    ClusterIssuerName = "letsencrypt",
                    RequireClientCertificate = true,
                    ClientCaBundleId = bundleA,
                }))
                .Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Trust anchor not found for this tenant.",
                "the type alone is also thrown by the TLS-mode guard above this check and the "
                + "empty-anchor guard below it — only the message says which one answered");

        await using ApplicationDbContext check = factory.CreateDbContext();
        (await check.AppRoutes.CountAsync(r => r.Hostname == "mtls.beta.example")).Should().Be(0);
    }

    /// <summary>
    /// The owner half of the pair. A predicate that refuses <em>every</em> anchor also passes the
    /// test above, so one of these without the other proves nothing: this one fails if the check
    /// over-rejects, that one fails if it under-rejects.
    /// </summary>
    [Fact]
    public async Task The_owner_may_point_a_route_at_its_own_trust_anchor()
    {
        AppRoute route = await routes.AddRouteAsync(tenantB, appB, new AppRouteRequest
        {
            Hostname = "mtls.beta.example",
            TlsMode = TlsMode.ClusterIssuer,
            ClusterIssuerName = "letsencrypt",
            RequireClientCertificate = true,
            ClientCaBundleId = bundleB,
        });

        route.ClientCaBundleId.Should().Be(bundleB,
            "tenant B's own anchor must survive the check that rejects tenant A's");
        route.RequireClientCertificate.Should().BeTrue();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
