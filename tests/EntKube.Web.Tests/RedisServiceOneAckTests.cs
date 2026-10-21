using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// One operator action, one acknowledgment — for Redis.
///
/// <para>Creating a cluster made three gated changes (the namespace, the auth Secret, the
/// RedisCluster CR) and syncing a binding made two (the namespace and the credential write), so
/// each button raised that many dialogs in a row. An operator who is asked three times for one
/// decision learns to click through without reading, which costs the gate the only thing it is
/// for.</para>
///
/// <para><b>Each test asserts two things, and the second is the one that matters.</b>
/// <c>sink.Calls == 1</c> is trivially true of an action that makes a single change — the
/// RabbitMQ version of this test once passed with its own fix removed for exactly that reason —
/// so every case also requires <c>gatedChanges &gt; 1</c>, i.e. that there was something to
/// collapse in the first place.</para>
/// </summary>
public class RedisServiceOneAckTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly RedisService sut;

    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    /// <summary>How many gated changes the action made — see the class summary.</summary>
    private int gatedChanges;

    public RedisServiceOneAckTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        sut = new RedisService(
            testDb.Factory,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            vault,
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            gate);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class CountingSink : EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink
    {
        public int Calls { get; private set; }

        public Task<EntKube.Web.Services.ClusterChanges.ClusterChangeDecision> RequestAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            EntKube.Web.Services.ClusterChanges.ClusterChangeDiff diff,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(
                EntKube.Web.Services.ClusterChanges.ClusterChangeDecision.Acknowledged);
        }
    }

    /// <summary>
    /// Makes the mocked factory gate its writes the way the real one does.
    ///
    /// <para>The gate lives inside the real <see cref="KubernetesClientFactory"/>, which is mocked
    /// here — so without this the action reaches the gate zero times and "one dialog" passes while
    /// proving nothing. Every verb the action uses has to be gated, not only the applies.</para>
    /// </summary>
    private void GateEveryWrite()
    {
        k8s.Setup(x => x.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = manifest,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });

        k8s.Setup(x => x.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n",
                    Kubeconfig = kubeconfig,
                }, ct);
            });

        k8s.Setup(x => x.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string name, string ns, IReadOnlyDictionary<string, string?> keys,
                            IReadOnlyDictionary<string, string>? annotations, string kubeconfig,
                            string? summary, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                    Manifest = $"apiVersion: v1\nkind: Secret\nmetadata:\n  name: {name}\n",
                    Kubeconfig = kubeconfig,
                    Summary = summary,
                }, ct);
                return string.Empty;
            });
    }

    private async Task<(Guid TenantId, Guid ClusterId, Guid EnvId)> SeedAsync(string label)
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

        return (tenantId, clusterId, envId);
    }

    private async Task<Guid> SeedDeploymentAsync(Guid tenantId, Guid clusterId, Guid envId, string label)
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label} customer" });
        await db.SaveChangesAsync();
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = $"{label}-app" });
        await db.SaveChangesAsync();
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = $"{label}-deploy",
            Type = DeploymentType.Manual, ClusterId = clusterId, EnvironmentId = envId,
            Namespace = $"{label}-ns",
        });
        await db.SaveChangesAsync();

        return deploymentId;
    }

    [Fact]
    public async Task Creating_a_cluster_asks_the_operator_exactly_once()
    {
        (Guid tenantId, Guid clusterId, _) = await SeedAsync("ours");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        await sut.CreateClusterAsync(
            tenantId, clusterId, "cache", "caching", 3, "7.2", "10Gi", null, true);

        // The namespace, the auth Secret and the RedisCluster CR.
        gatedChanges.Should().BeGreaterThan(1, "the action must still make several gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    [Fact]
    public async Task Syncing_a_cache_binding_asks_the_operator_exactly_once()
    {
        (Guid tenantId, Guid clusterId, Guid envId) = await SeedAsync("ours");

        // Creating the cluster is what writes REDIS_HOST/PORT/PASSWORD into the vault, which the
        // sync then reads — so this is the real order of events rather than a hand-seeded vault.
        RedisCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "cache", "caching", 3, "7.2", "10Gi", null, true);
        Guid deploymentId = await SeedDeploymentAsync(tenantId, clusterId, envId, "ours");
        CacheBinding binding = await sut.CreateCacheBindingAsync(
            tenantId, cluster.Id, deploymentId, "redis-credentials");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();
        gatedChanges = 0;

        await sut.SyncCacheBindingAsync(tenantId, binding.Id);

        gatedChanges.Should().BeGreaterThan(1,
            "the namespace and the credential write are both gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    /// <summary>
    /// And the window closes with the action, so the operator's next press asks again — a fix that
    /// suppressed the question for the rest of the circuit would pass the tests above and quietly
    /// retire the gate.
    ///
    /// <para>This also pins the NESTED case, which is the one that could have gone wrong in the
    /// other direction: <c>CreateCacheBindingAsync</c> calls <c>SyncCacheBindingAsync</c>, so one
    /// operation is opened inside another. Nesting is a no-op by design — the outer window owns
    /// the action — so creating a binding asks once, not twice.</para>
    /// </summary>
    [Fact]
    public async Task Each_action_asks_once_and_the_next_one_asks_again()
    {
        (Guid tenantId, Guid clusterId, Guid envId) = await SeedAsync("ours");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        RedisCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "cache", "caching", 3, "7.2", "10Gi", null, true);
        sink.Calls.Should().Be(1, "creating the cluster is one action");

        Guid deploymentId = await SeedDeploymentAsync(tenantId, clusterId, envId, "ours");

        CacheBinding binding = await sut.CreateCacheBindingAsync(
            tenantId, cluster.Id, deploymentId, "redis-credentials");
        sink.Calls.Should().Be(2,
            "creating a binding syncs it, and the operation nested inside that sync must not ask "
            + "a second time for the same press");

        await sut.SyncCacheBindingAsync(tenantId, binding.Id);
        sink.Calls.Should().Be(3, "a later re-sync is a separate decision and asks again");
    }
}
