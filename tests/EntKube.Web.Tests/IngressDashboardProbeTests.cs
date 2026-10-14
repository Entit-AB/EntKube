using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What the tenant-wide ingress dashboard reports per cluster, and whose clusters it reads.
///
/// <para><b>Why there was none.</b> The service had no tests at all — not because it was
/// unreachable from one (it took <c>IKubernetesClientFactory</c>, which mocks fine) but because
/// nobody wrote any. It reads every registered cluster in a tenant live, which makes the question
/// "whose clusters" worth an assertion rather than a reading of the query.</para>
///
/// <para><b>The behaviour most worth pinning is the probe.</b> A cluster that cannot be reached
/// must appear in the list carrying its reason. If it silently vanished, the dashboard would show
/// a tenant their reachable clusters and nothing to say the rest were not consulted — and a
/// missing cluster reads as a cluster with no ingress problems.</para>
/// </summary>
public class IngressDashboardProbeTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly IngressDashboardService sut;

    /// <summary>Every resource asked of a cluster, so a refusal differs from an empty answer.</summary>
    private readonly List<string> queried = [];

    private Guid ours, theirs, ourCluster, theirCluster;

    public IngressDashboardProbeTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.GetJsonAllNamespacesAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string resource, string _, string _, CancellationToken _) => queried.Add(resource))
            .ReturnsAsync("""{"items":[]}""");

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        sut = new IngressDashboardService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            // Constructed rather than null: the traffic path asks it per route, and a null here
            // would be a landmine for the first test that produces one.
            new PrometheusService(
                new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
                new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
                new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
                new PromQueryCache(),
                NullLogger<PrometheusService>.Instance),
            NullLogger<IngressDashboardService>.Instance);
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

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        return clusterId;
    }

    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// The positive case, which the refusals below depend on: a reachable cluster is probed and
    /// reports so. Without this, a fixture that could resolve no credential at all would make
    /// every "not reached" assertion pass while proving nothing.
    /// </summary>
    [Fact]
    public async Task Our_own_cluster_is_probed_and_reported_reachable()
    {
        await SeedAsync();

        IngressDashboard board = await sut.LoadAsync(ours);

        ClusterProbe probe = board.Clusters.Should().ContainSingle().Subject;
        probe.ClusterId.Should().Be(ourCluster);
        probe.Reachable.Should().BeTrue();
        probe.Error.Should().BeNull();
        queried.Should().Contain("ingresses.networking.k8s.io");
    }

    [Fact]
    public async Task The_dashboard_reads_only_this_tenants_clusters()
    {
        await SeedAsync();

        IngressDashboard board = await sut.LoadAsync(ours);

        board.Clusters.Should().ContainSingle()
            .Which.ClusterId.Should().NotBe(theirCluster);
    }

    /// <summary>
    /// A cluster whose credential cannot be resolved is still listed, carrying its reason. The
    /// seam answering null is what produces this now; it used to be an empty
    /// <c>cluster.Kubeconfig</c>.
    ///
    /// <para>Note the cluster has to keep its <c>KubeconfigSecretId</c> to appear at all — the
    /// query filters on it — so this is the case where the row says a credential was stored and
    /// the vault cannot produce it, which is exactly the state an operator needs told.</para>
    /// </summary>
    [Fact]
    public async Task A_cluster_whose_credential_cannot_be_resolved_is_listed_with_a_reason()
    {
        await SeedAsync();

        await using (ApplicationDbContext write = testDb.CreateContext())
        {
            // Point the cluster at a vault secret that is not there.
            KubernetesCluster c = write.KubernetesClusters.Single(x => x.Id == ourCluster);
            c.KubeconfigSecretId = Guid.NewGuid();
            await write.SaveChangesAsync();
        }

        IngressDashboard board = await sut.LoadAsync(ours);

        ClusterProbe probe = board.Clusters.Should().ContainSingle().Subject;
        probe.Reachable.Should().BeFalse();
        probe.Error.Should().NotBeNullOrWhiteSpace(
            "a cluster that was not consulted must say so — vanishing reads as 'no problems here'");
        probe.RouteCount.Should().Be(0);
        queried.Should().BeEmpty("nothing should have been asked of a cluster we cannot reach");
    }

    /// <summary>
    /// A tenant with no clusters gets an empty board rather than an exception — the dashboard is
    /// the first page a new tenant opens.
    /// </summary>
    [Fact]
    public async Task A_tenant_with_no_clusters_gets_an_empty_board()
    {
        Guid empty = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = empty, Name = "new", Slug = $"new-{empty:N}" });
        await db.SaveChangesAsync();

        IngressDashboard board = await sut.LoadAsync(empty);

        board.Clusters.Should().BeEmpty();
        board.Routes.Should().BeEmpty();
    }
}
