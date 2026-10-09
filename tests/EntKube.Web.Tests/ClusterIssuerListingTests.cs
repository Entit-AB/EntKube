using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Listing a cluster's cert-manager ClusterIssuers, which four pages do to fill a picker.
///
/// <para><b>It took no tenant and its lookup had no tenant predicate</b>, so any cluster id in
/// the installation listed its issuers. A read rather than a write — but a ClusterIssuer name is a
/// piece of another tenant's configuration, and it arrived in our UI. Every one of the four
/// callers is a page that already held a tenant, so the argument was available and unused, which
/// is the same shape as the fourteen closed in #153.</para>
///
/// <para>The empty-list-on-failure behaviour is load-bearing and deliberately preserved: this
/// populates a dropdown, so an unreachable cluster has to look like a cluster with no issuers
/// rather than take the page down. That makes the cross-tenant case indistinguishable from a
/// failure <em>to the caller</em> — which is fine, and is why the test asserts the cluster was
/// never reached at all rather than only that the list came back empty.</para>
/// </summary>
public class ClusterIssuerListingTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ComponentLifecycleService sut;

    /// <summary>Every resource listing asked of a cluster, as (resource, kubeconfig).</summary>
    private readonly List<(string Resource, string Kubeconfig)> listed = [];

    private Guid ours, theirs, ourCluster, theirCluster;

    public ClusterIssuerListingTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string resource, string _, string kubeconfig, string _, CancellationToken _) =>
                listed.Add((resource, kubeconfig)))
            .ReturnsAsync("""
                {"items":[
                    {"metadata":{"name":"letsencrypt-prod"}},
                    {"metadata":{"name":"letsencrypt-staging"}}
                ]}
                """);

        sut = TestServices.BuildLifecycle(testDb, k8s.Object);
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
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

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

    [Fact]
    public async Task Our_own_clusters_issuers_are_listed()
    {
        await SeedAsync();

        List<string> issuers = await sut.ListClusterIssuersAsync(ours, ourCluster);

        issuers.Should().Equal("letsencrypt-prod", "letsencrypt-staging");

        // Cluster-scoped resources take no namespace, and the credential comes from the seam
        // rather than from a temp file this service wrote.
        listed.Should().ContainSingle().Which.Resource
            .Should().Be("clusterissuers.cert-manager.io");
    }

    [Fact]
    public async Task Another_tenants_cluster_is_never_reached()
    {
        await SeedAsync();

        List<string> issuers = await sut.ListClusterIssuersAsync(ours, theirCluster);

        issuers.Should().BeEmpty();
        listed.Should().BeEmpty(
            "an empty list could also mean a cluster with no issuers, so what this pins is that "
            + "the other tenant's cluster was not contacted at all");
    }

    /// <summary>
    /// A cluster with no stored credential is the same answer as one with no issuers. Keeping that
    /// is the point — four pages call this while rendering, and an exception would take them down.
    /// </summary>
    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_lists_nothing_without_throwing()
    {
        ours = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = ours, Name = "bare", Slug = $"bare-{ours:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = ours, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = ours, EnvironmentId = envId,
            Name = "bare-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        await db.SaveChangesAsync();
        await vault.InitializeVaultAsync(ours);

        List<string> issuers = await sut.ListClusterIssuersAsync(ours, clusterId);

        issuers.Should().BeEmpty();
        listed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_cluster_that_will_not_answer_lists_nothing_without_throwing()
    {
        await SeedAsync();

        k8s.Setup(x => x.GetJsonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): connection refused"));

        List<string> issuers = await sut.ListClusterIssuersAsync(ours, ourCluster);

        issuers.Should().BeEmpty();
    }
}
