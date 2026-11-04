using System.Net;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Environment = EntKube.Web.Data.Environment;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether the public API's tenant confinement actually holds, asked over HTTP with a real token.
///
/// <para><c>ApiTenantConfinementTests</c> declares, per operation, that it is confined — read out
/// of the handlers and their services. That is a measurement frozen at authoring time: delete a
/// tenant predicate inside a service and nothing in that file fails. A scan can say a predicate is
/// present; only a foreign id can say it works. These tests seed two tenants and ask tenant A's
/// token for tenant B's row.</para>
///
/// <para>⭐ <b>Every probe has two arms, and the owner arm is the load-bearing one.</b> A test that
/// only asserts "stranger's id → 404" passes against a route that answers 404 for everything,
/// including one whose handler is never entered because an earlier guard rejected the request.
/// <c>RouteTenantScopingTests</c> records this repo being bitten by exactly that: a test that
/// "could not have told a correct implementation from a deleted one" because the request left a
/// field at its default and threw before reaching the check. So each probe below first proves the
/// owning tenant gets a SUCCESS, then asks for the stranger's row and expects a refusal — and the
/// two statuses have to differ.</para>
///
/// <para>⚠️ Foreign keys are enforced here, so the whole graph has to exist: Tenant → Environment
/// → KubernetesCluster, and Tenant → Customer → App → AppDeployment. A cluster with a dangling
/// <c>EnvironmentId</c> cannot be inserted at all, which is why these fixtures are as long as they
/// are.</para>
///
/// <para>⚠️ <b>Seeded into the shared host, and cleaned up.</b> This suite is in the
/// <c>api-surface</c> collection because a second <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// is not a free choice — this test project has already exhausted a 7.8 GB CI runner by holding too
/// many hosts alive, which is why that fixture is a collection fixture rather than a class fixture.
/// xUnit runs classes within one collection sequentially and constructs this class once per test,
/// so each test seeds its own rows under a unique suffix and deletes them in
/// <see cref="Dispose"/>. Names carry that suffix because clusters, environments, apps and
/// deployments all have uniqueness constraints that would otherwise collide between tests.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public sealed class CrossTenantProbeTests : IDisposable
{
    private readonly GeneratedOpenApiDocumentFixture fixture;

    /// <summary>Distinguishes this test's rows from every other test's. See the class doc.</summary>
    private readonly string suffix = Guid.NewGuid().ToString("N")[..8];

    private readonly Guid ownerTenantId;
    private readonly Guid strangerTenantId = Guid.NewGuid();

    private readonly Guid ownerClusterId = Guid.NewGuid();
    private readonly Guid strangerClusterId = Guid.NewGuid();
    private readonly Guid ownerAppId = Guid.NewGuid();
    private readonly Guid strangerAppId = Guid.NewGuid();
    private readonly Guid ownerDeploymentId = Guid.NewGuid();
    private readonly Guid strangerDeploymentId = Guid.NewGuid();

    public CrossTenantProbeTests(GeneratedOpenApiDocumentFixture fixture)
    {
        this.fixture = fixture;
        ownerTenantId = fixture.TokenTenantId;

        Seed();
    }

    /// <summary>
    /// Both graphs. The owner's tenant row already exists — the fixture created it to mint the
    /// tokens — so only the stranger's tenant is added.
    /// </summary>
    private void Seed()
    {
        using ApplicationDbContext db = NewContext();

        db.Tenants.Add(new Tenant
        {
            Id = strangerTenantId,
            Name = $"stranger {suffix}",
            Slug = $"stranger-{suffix}",
        });

        // ⚠️ Saved in stages, principals before dependents. EF's batch ordering can place a row
        // ahead of the row it points at, and with foreign keys enforced SQLite then rejects the
        // WHOLE SaveChanges — ComponentTenantReachTests:177 records this repo hitting it.
        db.SaveChanges();

        foreach ((Guid tenantId, Guid clusterId, Guid appId, Guid deploymentId, string who) in Sides())
        {
            Guid environmentId = Guid.NewGuid();
            Guid customerId = Guid.NewGuid();

            db.Environments.Add(new Environment
            {
                Id = environmentId,
                TenantId = tenantId,
                Name = $"env-{who}-{suffix}",
            });

            db.Customers.Add(new Customer
            {
                Id = customerId,
                TenantId = tenantId,
                Name = $"customer-{who}-{suffix}",
            });

            db.SaveChanges();

            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = clusterId,
                TenantId = tenantId,
                EnvironmentId = environmentId,
                Name = $"cluster-{who}-{suffix}",
                // Never dialled on any path these probes reach.
                ApiServerUrl = "https://127.0.0.1:6443",
            });

            db.Apps.Add(new App
            {
                Id = appId,
                CustomerId = customerId,
                Name = $"app-{who}-{suffix}",
            });

            db.SaveChanges();

            db.ClusterCostRates.Add(new ClusterCostRate
            {
                Id = Guid.NewGuid(),
                ClusterId = clusterId,
                Currency = "USD",
                CpuCoreHourCost = 0.03m,
            });

            // So the owner arm can assert it got the RIGHT rows rather than merely a different
            // status. Named, not kube-prometheus-stack: nothing in this host sweeps for it
            // anyway now that singleton work is off, but a component that a sweep would claim is
            // still the wrong thing to leave lying in a shared database.
            db.ClusterComponents.Add(new ClusterComponent
            {
                Id = Guid.NewGuid(),
                ClusterId = clusterId,
                Name = $"component-{who}-{suffix}",
                ComponentType = "probe",
            });

            db.AppDeployments.Add(new AppDeployment
            {
                Id = deploymentId,
                AppId = appId,
                Name = $"deploy-{who}-{suffix}",
                Namespace = $"ns-{who}-{suffix}",
                EnvironmentId = environmentId,
                ClusterId = clusterId,
                Type = DeploymentType.Manual,
            });
        }

        db.SaveChanges();
    }

    private IEnumerable<(Guid TenantId, Guid ClusterId, Guid AppId, Guid DeploymentId, string Who)> Sides() =>
    [
        (ownerTenantId, ownerClusterId, ownerAppId, ownerDeploymentId, "owner"),
        (strangerTenantId, strangerClusterId, strangerAppId, strangerDeploymentId, "stranger"),
    ];

    private ApplicationDbContext NewContext() =>
        fixture.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    // ─── the probes ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠️ Read this before the probes. It proves the two arms produce DIFFERENT statuses on a
    /// route taken from the middle of the surface. Without it, every "stranger gets 404" below
    /// would be satisfied by a route that refuses everyone.
    /// </summary>
    [Fact]
    public async Task The_owner_and_the_stranger_get_different_answers_at_all()
    {
        HttpStatusCode owner = await GetAsync(
            $"/api/v1/clusters/{ownerClusterId}/components", ApiScopes.FleetRead);
        HttpStatusCode stranger = await GetAsync(
            $"/api/v1/clusters/{strangerClusterId}/components", ApiScopes.FleetRead);

        owner.Should().Be(HttpStatusCode.OK,
            "the owning tenant's own cluster must be reachable, or the refusal below proves "
            + "nothing about tenancy — only that the route refuses everyone");

        stranger.Should().NotBe(owner);
    }

    /// <summary>
    /// ⚠️ <b>This pins Fleet's predicate, not Catalog's, and the distinction is worth stating.</b>
    /// The handler establishes cluster ownership through <c>FleetApi.Scoped</c> and only then
    /// reads components. Deleting Catalog's own <c>c.Cluster.TenantId == tenantId</c> would change
    /// nothing observable here, because the ownership check plus the <c>ClusterId</c> filter
    /// already confine the result. So this test says "the route is confined"; it does not say
    /// "both predicates are load-bearing", and a reader who assumed the latter would be wrong.
    /// </summary>
    [Fact]
    public async Task A_stranger_s_cluster_cannot_have_its_components_listed()
    {
        string owned = await GetBodyAsync(
            $"/api/v1/clusters/{ownerClusterId}/components", ApiScopes.FleetRead);

        owned.Should().Contain($"component-owner-{suffix}",
            "the owner's own component must come back — a 200 carrying an empty array would "
            + "leave the refusal below unable to tell confinement from an unreachable handler");
        owned.Should().NotContain($"component-stranger-{suffix}");

        (await GetAsync($"/api/v1/clusters/{strangerClusterId}/components", ApiScopes.FleetRead))
            .Should().Be(HttpStatusCode.NotFound,
                "the cluster exists and has a component, so a 404 here can only come from the "
                + "tenant predicate");
    }

    [Fact]
    public async Task A_stranger_s_price_sheet_cannot_be_read()
    {
        (await GetAsync($"/api/v1/cost-rates/{ownerClusterId}", ApiScopes.ConfigRead))
            .Should().Be(HttpStatusCode.OK);

        (await GetAsync($"/api/v1/cost-rates/{strangerClusterId}", ApiScopes.ConfigRead))
            .Should().Be(HttpStatusCode.NotFound,
                "a rate row exists for that cluster — the refusal is the tenant predicate, not "
                + "an absent row");
    }

    [Fact]
    public async Task A_stranger_s_price_sheet_cannot_be_rewritten()
    {
        const string body = """
            {"cpuCoreHourCost":9.99,"memoryGiBHourCost":1,"storageGiBMonthCost":1,
             "clusterMonthlyOverhead":1,"loadBalancerMonthlyCost":1,"publicIpMonthlyCost":1}
            """;

        (await fixture.SendRawAsync("PUT", $"/api/v1/cost-rates/{ownerClusterId}", ApiScopes.ConfigWrite, body))
            .Should().Be(HttpStatusCode.OK);

        (await fixture.SendRawAsync("PUT", $"/api/v1/cost-rates/{strangerClusterId}", ApiScopes.ConfigWrite, body))
            .Should().Be(HttpStatusCode.NotFound,
                "pricing another tenant's cluster would change what that tenant is billed");

        // ⚠️ And the write did not land. A 404 that still wrote would be the worst outcome of the
        // three, and the status alone cannot tell the difference.
        using ApplicationDbContext db = NewContext();
        ClusterCostRate? rate = await db.ClusterCostRates
            .FirstOrDefaultAsync(r => r.ClusterId == strangerClusterId);

        rate!.CpuCoreHourCost.Should().Be(0.03m, "the refused PUT must not have written anything");
    }

    [Fact]
    public async Task A_stranger_s_price_sheet_cannot_be_deleted()
    {
        (await fixture.SendRawAsync("DELETE", $"/api/v1/cost-rates/{strangerClusterId}", ApiScopes.ConfigWrite, null))
            .Should().Be(HttpStatusCode.NotFound);

        (await fixture.SendRawAsync("DELETE", $"/api/v1/cost-rates/{ownerClusterId}", ApiScopes.ConfigWrite, null))
            .Should().Be(HttpStatusCode.NoContent,
                "the owner arm runs second here on purpose: the delete consumes the row, so "
                + "proving the refusal first means the row was still there to refuse");

        using ApplicationDbContext db = NewContext();
        (await db.ClusterCostRates.AnyAsync(r => r.ClusterId == strangerClusterId))
            .Should().BeTrue("the refused DELETE must not have removed the stranger's row");
    }

    [Fact]
    public async Task A_stranger_s_app_cannot_have_its_secrets_pushed()
    {
        (await fixture.SendRawAsync(
            "POST", $"/api/v1/apps/{ownerAppId}/secrets/sync", ApiScopes.AppsWrite, null))
            .Should().Be(HttpStatusCode.OK,
                "the app has no vault secrets, so the sync reports nothing to do — which is a "
                + "success, and is what makes the refusal below meaningful");

        (await fixture.SendRawAsync(
            "POST", $"/api/v1/apps/{strangerAppId}/secrets/sync", ApiScopes.AppsWrite, null))
            .Should().Be(HttpStatusCode.NotFound,
                "this route pushes vaulted secret material into a cluster; being able to aim it "
                + "at another tenant's app would be the most serious reach on the surface");
    }

    /// <summary>
    /// ⚠️ The one probe with no refusal status, and that is a property of the route rather than a
    /// gap in the test: <c>appId</c> is a query FILTER, and a filter matching nothing is an empty
    /// 200, not a 404. So the assertion is about the BODY — the stranger's deployment must be
    /// absent from it — and the owner arm proves the filter works at all.
    /// </summary>
    [Fact]
    public async Task A_stranger_s_app_id_filters_to_nothing_rather_than_to_their_deployments()
    {
        string owned = await GetBodyAsync(
            $"/api/v1/deployments?appId={ownerAppId}", ApiScopes.AppsRead);
        string foreign = await GetBodyAsync(
            $"/api/v1/deployments?appId={strangerAppId}", ApiScopes.AppsRead);

        owned.Should().Contain(ownerDeploymentId.ToString(),
            "the owner's own deployment must come back, or the filter is not working and the "
            + "emptiness below means nothing");

        foreign.Should().NotContain(strangerDeploymentId.ToString());
        JsonDocument.Parse(foreign).RootElement.GetArrayLength().Should().Be(0);
    }

    private Task<HttpStatusCode> GetAsync(string url, string scope) =>
        fixture.SendRawAsync("GET", url, scope, null);

    private Task<string> GetBodyAsync(string url, string scope) =>
        fixture.SendForBodyAsync("GET", url, scope, null);

    /// <summary>
    /// Removes this test's rows. Children first: foreign keys are enforced, and several are
    /// Restrict rather than Cascade.
    /// </summary>
    public void Dispose()
    {
        using ApplicationDbContext db = NewContext();

        db.AppDeployments.RemoveRange(db.AppDeployments.Where(d => d.Name.EndsWith(suffix)));
        db.SaveChanges();

        db.Apps.RemoveRange(db.Apps.Where(a => a.Name.EndsWith(suffix)));
        db.ClusterComponents.RemoveRange(db.ClusterComponents.Where(c => c.Name.EndsWith(suffix)));
        db.ClusterCostRates.RemoveRange(
            db.ClusterCostRates.Where(r => r.ClusterId == ownerClusterId || r.ClusterId == strangerClusterId));
        db.SaveChanges();

        db.KubernetesClusters.RemoveRange(db.KubernetesClusters.Where(c => c.Name.EndsWith(suffix)));
        db.Customers.RemoveRange(db.Customers.Where(c => c.Name.EndsWith(suffix)));
        db.SaveChanges();

        db.Environments.RemoveRange(db.Environments.Where(e => e.Name.EndsWith(suffix)));
        db.Tenants.RemoveRange(db.Tenants.Where(t => t.Id == strangerTenantId));
        db.SaveChanges();
    }
}
