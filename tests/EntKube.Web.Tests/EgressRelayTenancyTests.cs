using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Agents;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Who may have an egress relay deployed into their cluster.
///
/// <para>The relay is nginx, running in the customer's cluster, forwarding EntKube's calls to a
/// cloud API that allowlists the provider's own network. Deploying one into the wrong cluster is
/// not a misconfiguration — it is a workload in somebody else's cluster that proxies traffic out
/// of it, which is the shape of the thing an allowlist exists to prevent.</para>
///
/// <para><b>Why this had no test before.</b> <c>ClusterEgressRelay</c> took a <c>string
/// kubeconfig</c>, so <c>StorageService</c> read one off the cluster row and handed it over, and a
/// test could only observe the kubeconfig that came out of a query it had itself seeded. The relay
/// now takes an <c>IClusterClient</c>, so the question "which cluster" has an answer the seam
/// gives rather than the caller — and these assert on what reached a cluster at all.</para>
/// </summary>
public class EgressRelayTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly StorageService sut;

    private Guid ours, theirs, ourCluster, theirCluster;

    public EgressRelayTenancyTests()
    {
        // InterceptingTestDb, not a plain factory: the materialization interceptor is what puts a
        // vault-stored kubeconfig back onto cluster.Kubeconfig, and without it ForAsync refuses
        // EVERY cluster — so every refusal below would pass for the wrong reason and the positive
        // case could not exist at all.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        Mock<IHttpClientFactory> innerHttp = new();
        innerHttp.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient());

        AgentRegistry agents = new(testDb.Factory, NullLogger<AgentRegistry>.Instance);
        OpenStackHttpFactory httpFactory = new(innerHttp.Object, agents);
        ClusterEgressRelay relay = new(NullLogger<ClusterEgressRelay>.Instance);
        ClusterEgressTunnel tunnel = new(NullLogger<ClusterEgressTunnel>.Instance);
        OpenStackKeystoneClient keystone = new(httpFactory, vaultService, tunnel, testDb.Factory);

        sut = new StorageService(
            testDb.Factory, vaultService, new OpenStackS3Service(vaultService, httpFactory, keystone),
            keystone, relay, tunnel, agents, k8s.Object,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new StorageLinkClientFactory(vaultService, testDb.Factory, httpFactory, keystone));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync()
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        ourCluster = await SeedTenantAsync(ours, "ours");
        theirCluster = await SeedTenantAsync(theirs, "theirs");
    }

    private async Task<Guid> SeedTenantAsync(Guid tenantId, string label)
    {
        Guid clusterId = Guid.NewGuid(), envId = Guid.NewGuid();

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
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vaultService, tenantId, clusterId, TestKubeconfig.Valid);

        return clusterId;
    }

    private async Task<Guid> GivenAConnectionRoutedViaAsync(Guid tenantId, Guid clusterId)
    {
        Guid id = Guid.NewGuid();
        db.OpenStackConnections.Add(new OpenStackConnection
        {
            Id = id, TenantId = tenantId, Name = "cleura",
            AuthUrl = "https://identity.example.com:5000/v3",
            Region = "Kna1",
            RouteViaClusterId = clusterId,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private int ManifestsApplied() => k8s.Invocations
        .Count(i => i.Method.Name == nameof(IKubernetesClientFactory.ApplyManifestAsync));

    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The relay reaches our own cluster. This exists so the refusals below cannot pass for the
    /// wrong reason: if the fixture could not resolve a kubeconfig at all, every assertion of
    /// "nothing was applied" would hold while proving nothing.
    /// </summary>
    [Fact]
    public async Task A_relay_is_deployed_into_our_own_cluster()
    {
        await SeedAsync();
        Guid connectionId = await GivenAConnectionRoutedViaAsync(ours, ourCluster);

        await sut.DeployEgressRelayAsync(ours, connectionId);

        ManifestsApplied().Should().BeGreaterThan(0,
            "the relay manifest is applied before authentication is attempted");
    }

    /// <summary>
    /// The refusal that matters: a connection whose <c>RouteViaClusterId</c> names a cluster
    /// belonging to someone else. Asserted on whether a manifest reached a cluster, not on the
    /// returned message — a failure reported after the relay was applied is still a relay running
    /// in a stranger's cluster.
    /// </summary>
    [Fact]
    public async Task A_relay_is_not_deployed_into_another_tenants_cluster()
    {
        await SeedAsync();
        Guid connectionId = await GivenAConnectionRoutedViaAsync(ours, theirCluster);

        (bool ok, string message) = await sut.DeployEgressRelayAsync(ours, connectionId);

        ok.Should().BeFalse(message);
        ManifestsApplied().Should().Be(0, "nothing may be applied to a cluster this tenant does not own");
    }

    /// <summary>
    /// And the connection itself is tenant-scoped, so our own cluster is no help to another
    /// tenant asking about our connection. Both halves have to hold: the connection is fetched
    /// under the tenant, and the cluster is resolved under it.
    /// </summary>
    [Fact]
    public async Task Another_tenant_cannot_deploy_using_our_connection()
    {
        await SeedAsync();
        Guid connectionId = await GivenAConnectionRoutedViaAsync(ours, ourCluster);

        (bool ok, _) = await sut.DeployEgressRelayAsync(theirs, connectionId);

        ok.Should().BeFalse();
        ManifestsApplied().Should().Be(0);
    }

    /// <summary>
    /// A connection not routed through any cluster is refused before anything is resolved — this
    /// is the branch that says so, and it must not be reachable by accident once the cluster
    /// lookup moved.
    /// </summary>
    [Fact]
    public async Task A_connection_not_routed_through_a_cluster_is_refused()
    {
        await SeedAsync();
        Guid connectionId = await GivenAConnectionRoutedViaAsync(ours, ourCluster);

        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            OpenStackConnection c = write.OpenStackConnections.Single(x => x.Id == connectionId);
            c.RouteViaClusterId = null;
            await write.SaveChangesAsync();
        }

        (bool ok, string message) = await sut.DeployEgressRelayAsync(ours, connectionId);

        ok.Should().BeFalse();
        message.Should().Contain("route through a cluster");
        ManifestsApplied().Should().Be(0);
    }
}
