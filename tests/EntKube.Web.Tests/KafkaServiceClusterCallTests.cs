using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What KafkaService sends to a cluster, and whose cluster it is willing to send it to.
///
/// <para><b>Why this exists.</b> KafkaService had no tests at all, and fifteen of its lines take a
/// cluster's credential out of the row to hand to the client factory. The credential-custody work
/// (docs/decomposition.md §4.0.1) rewrites every one of them, and a rewrite of that kind fails in
/// two ways a database assertion cannot see: a manifest that is no longer applied, and a manifest
/// applied to the wrong tenant's cluster. So these assert on the call, not the row.</para>
///
/// <para>Deliberately narrow. This is a safety net for one refactor, not an attempt to cover a
/// service that deserves its own suite.</para>
/// </summary>
public class KafkaServiceClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KafkaService sut;
    private readonly List<string> applied = [];

    public KafkaServiceClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .Returns(Task.CompletedTask);

        // Real factory over the intercepting test database — the vault-seeded kubeconfig resolves
        // as production resolves it, then reaches the same mock, so these assertions hold across
        // the conversion rather than needing to be rewritten by it.
        sut = new KafkaService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            vault);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<(Guid TenantId, Guid ClusterId)> SeedAsync(string label)
    {
        Guid tenantId = Guid.NewGuid(), envId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = label });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-k8s", ApiServerUrl = $"https://{label}.example",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        return (tenantId, clusterId);
    }

    [Fact]
    public async Task Creating_a_cluster_applies_a_Kafka_manifest_to_the_cluster()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");

        KafkaCluster result = await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);

        result.Name.Should().Be("events");
        applied.Should().NotBeEmpty("the Strimzi CR has to reach the cluster, not just the database");
        applied.Should().Contain(m => m.Contains("Kafka"));
    }

    [Fact]
    public async Task Creating_a_topic_applies_it_to_the_clusters_own_cluster()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");

        KafkaCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);
        applied.Clear();

        await sut.CreateTopicAsync(tenantId, cluster.Id, "orders", 3, 3, null);

        applied.Should().ContainSingle().Which.Should().Contain("KafkaTopic");
    }

    /// <summary>
    /// The tenant is what stops one customer creating topics on another's broker, and it is the
    /// kind of filter a refactor can drop with nothing else noticing.
    /// </summary>
    [Fact]
    public async Task Another_tenants_kafka_cluster_is_refused_and_nothing_is_applied()
    {
        (Guid ourTenant, _) = await SeedAsync("ours");
        (Guid theirTenant, Guid theirCluster) = await SeedAsync("theirs");

        KafkaCluster theirs = await sut.CreateClusterAsync(
            theirTenant, theirCluster, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);
        applied.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateTopicAsync(ourTenant, theirs.Id, "orders", 3, 3, null));

        applied.Should().BeEmpty("nothing should reach anyone's cluster");
    }
}
