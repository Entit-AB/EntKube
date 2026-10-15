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

    /// <summary>Every status poll, as (resource asked for, credential it was asked with).</summary>
    private readonly List<(string Resource, string Kubeconfig)> polled = [];

    public KafkaServiceClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, CancellationToken>(
                (resource, _, kubeconfig, _, _) => polled.Add((resource, kubeconfig)))
            .ReturnsAsync("{}");

        k8s.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .ReturnsAsync(string.Empty);

        // Real factory over the intercepting test database — the vault-seeded kubeconfig resolves
        // as production resolves it, then reaches the same mock, so these assertions hold across
        // the conversion rather than needing to be rewritten by it.
        sut = new KafkaService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            vault, new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory));
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

        // Each tenant's kubeconfig is made identifiable, so an assertion can tell WHOSE credential
        // reached the cluster rather than only that one did. With the same literal for both, a
        // sweep that polled every broker with the first tenant's credential would look correct.
        await testDb.SeedKubeconfigAsync(
            vault, tenantId, clusterId,
            TestKubeconfig.Valid.Replace("https://k8s.example.com", $"https://{label}.example.com"));

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

    /// <summary>
    /// The status sweep polls every tenant's brokers, each with its own tenant's credential.
    ///
    /// <para>This is the assertion the sweep conversion needed and could not have before: the loop
    /// used to select every broker in the installation and take the tenant off each row, so the
    /// tenant reaching <c>ForAsync</c> was the row's own claim. Pairing the resource polled with
    /// the credential it was polled with is what shows the two never cross — a test that only
    /// counted polls would pass against a sweep that used one tenant's credential for all of
    /// them.</para>
    /// </summary>
    [Fact]
    public async Task The_status_sweep_polls_each_tenants_broker_with_that_tenants_credential()
    {
        (Guid ourTenant, Guid ourCluster) = await SeedAsync("ours");
        (Guid theirTenant, Guid theirCluster) = await SeedAsync("theirs");

        await sut.CreateClusterAsync(
            ourTenant, ourCluster, "ours-events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);
        await sut.CreateClusterAsync(
            theirTenant, theirCluster, "theirs-events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);
        polled.Clear();

        await sut.ReconcileAllAsync();

        polled.Should().HaveCount(2, "both tenants' brokers are polled");

        polled.Should().Contain(p => p.Resource.Contains("ours-events") && p.Kubeconfig.Contains("ours.example.com"));
        polled.Should().Contain(p => p.Resource.Contains("theirs-events") && p.Kubeconfig.Contains("theirs.example.com"));

        polled.Should().NotContain(p => p.Resource.Contains("ours-events") && p.Kubeconfig.Contains("theirs.example.com"),
            "one tenant's credential must never be the one that reads another's broker");
        polled.Should().NotContain(p => p.Resource.Contains("theirs-events") && p.Kubeconfig.Contains("ours.example.com"));
    }
}
