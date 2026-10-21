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
    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate =
        (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();

    /// <summary>
    /// How many gated changes an action made. Asserted alongside the dialog count, because "one
    /// dialog" is trivially true of an action that makes one change — the RabbitMQ version of
    /// this test passed with its own fix removed for exactly that reason.
    /// </summary>
    private int gatedChanges;

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
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            gate);
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

    // ════════════════════════════════════════════════════════════════
    //  One action, one acknowledgment
    // ════════════════════════════════════════════════════════════════

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
    /// here — so without this the action reaches the gate zero times and a "one dialog" assertion
    /// passes while proving nothing. Every verb the action uses has to be gated, not just the
    /// applies: <c>EnsureNamespaceAsync</c> and the secret write are separate methods on the mock
    /// though the real factory reaches the gate through its own apply.</para>
    /// </summary>
    private void GateEveryWrite()
    {
        k8s.Setup(x => x.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                applied.Add(manifest);
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

        k8s.Setup(x => x.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string kind, string name, string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                {
                    Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Delete,
                    Kind = kind,
                    Name = name,
                    Namespace = ns,
                    Kubeconfig = kubeconfig,
                }, ct);
                return string.Empty;
            });
    }

    /// <summary>An app deployment on the tenant's own cluster, for a binding to point at.</summary>
    private async Task<Guid> SeedDeploymentAsync(Guid tenantId, Guid clusterId, string label)
    {
        Guid customerId = Guid.NewGuid(), appId = Guid.NewGuid(), deploymentId = Guid.NewGuid();
        Guid envId = db.KubernetesClusters.First(c => c.Id == clusterId).EnvironmentId;

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
    public async Task Syncing_a_binding_asks_the_operator_exactly_once()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");

        // No SASL: the credentials come from the cluster record alone, so the sync needs no live
        // secret read and the two gated changes are the namespace and the secret write.
        KafkaCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, false,
            null, null, null);
        Guid deploymentId = await SeedDeploymentAsync(tenantId, clusterId, "ours");
        KafkaBinding binding = await sut.CreateBindingAsync(
            tenantId, cluster.Id, deploymentId, null, "kafka-credentials");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();
        gatedChanges = 0;

        await sut.SyncBindingAsync(tenantId, binding.Id);

        gatedChanges.Should().BeGreaterThan(1,
            "the namespace and the credential write are both gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    [Fact]
    public async Task Creating_a_cluster_asks_the_operator_exactly_once()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);

        // The namespace, the node pool and the Kafka CR: three changes, one decision.
        gatedChanges.Should().BeGreaterThan(1, "the action must still make several gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    [Fact]
    public async Task Updating_a_cluster_asks_the_operator_exactly_once()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");
        KafkaCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();
        gatedChanges = 0;

        await sut.UpdateClusterAsync(
            tenantId, cluster.Id, 5, "3.7.0", "20Gi", null, null, null, null);

        gatedChanges.Should().BeGreaterThan(1, "the node pool and the cluster CR are both re-applied");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    /// <summary>
    /// ⚠️ <b>There is no counting test for <c>DeleteClusterAsync</c>, deliberately.</b>
    ///
    /// <para>The gate decides whether a delete is worth asking about by running a real
    /// <c>kubectl get</c> for the resource (<c>ComputeDeleteDiffAsync</c>): absent means the
    /// delete changes nothing, so it is skipped without a dialog — the same reasoning that
    /// stopped <c>EnsureNamespaceAsync</c> prompting for a namespace that already exists.</para>
    ///
    /// <para>This harness has no live cluster, so both deletes are no-ops and the sink is never
    /// asked. A test here would assert the harness rather than the fix, and worse, it would
    /// change answer depending on whether <c>kubectl</c> happens to be installed on the machine —
    /// if the binary is missing the diff throws, the gate falls back to asking anyway, and the
    /// test would pass for a reason that has nothing to do with the code under test.</para>
    ///
    /// <para><c>DeleteClusterAsync</c> carries the same <c>BeginOperation</c> as the two actions
    /// above and collapses by the same mechanism; what is untested is this harness's ability to
    /// show it, not the wrapping.</para>
    /// </summary>
    /// <summary>
    /// And the window closes with the action, so the operator's next press asks again — a fix
    /// that suppressed the question for the rest of the circuit would pass the tests above and
    /// quietly retire the gate.
    /// </summary>
    [Fact]
    public async Task The_next_action_asks_again()
    {
        (Guid tenantId, Guid clusterId) = await SeedAsync("ours");

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryWrite();

        KafkaCluster cluster = await sut.CreateClusterAsync(
            tenantId, clusterId, "events", "messaging", 3, "3.7.0", "10Gi", null, true,
            null, null, null);
        sink.Calls.Should().Be(1);

        await sut.UpdateClusterAsync(
            tenantId, cluster.Id, 5, "3.7.0", "20Gi", null, null, null, null);

        sink.Calls.Should().Be(2, "a second action is a second decision");
    }
}
