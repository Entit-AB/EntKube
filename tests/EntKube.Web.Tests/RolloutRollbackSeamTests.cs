using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Rollouts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What an automatic rollback asks of a cluster, now that it asks <see cref="IClusterClient"/>.
///
/// <para><b>Why this one needed a new verb.</b> Every earlier conversion in this programme found
/// an existing seam operation to move onto. A rollback had none: the seam can apply, delete,
/// patch, scale, restart and run helm, and <c>kubectl rollout undo</c> is none of those — nothing
/// in it describes the state being moved to, because the cluster is what remembers the revision.
/// So <c>UndoDeploymentsAsync</c> was added, along with a <c>ChangeVerb.Rollback</c> for the
/// acknowledgment to carry.</para>
///
/// <para><b>What the conversion removed.</b> <c>RolloutWatcherService</c> read
/// <c>deployment.Cluster.Kubeconfig</c> and handed it to <c>RollbackAsync</c>, which wrote it to a
/// temp file and ran kubectl itself — a cluster write that no operator could ever be asked about
/// and that <c>IClusterChangeRecorder</c> could not record. It also had no tenant: the credential
/// came off the row the deployment pointed at, so "which cluster" and "may I" were the same
/// answer. The watcher now loops tenants and passes one in.</para>
/// </summary>
public class RolloutRollbackSeamTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly RolloutService sut;

    /// <summary>Every namespace an undo was asked for, so a refusal can be told from a success.</summary>
    private readonly List<(string Namespace, string Kubeconfig)> undone = [];

    private WorkloadUndoResult result = new(true, "deployment.apps/api: rolled back");

    private Guid ours, theirs;
    private AppDeployment ourDeployment = null!, theirDeployment = null!;

    public RolloutRollbackSeamTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.UndoDeploymentsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string ns, string kc, string? _, CancellationToken _) => undone.Add((ns, kc)))
            .ReturnsAsync(() => result);

        // Only the rollback path is exercised here, and it touches none of the measurement
        // collaborators. Passing them as null is deliberate: a real PrometheusService would drag
        // the whole metrics stack into a test about who may roll back.
        sut = new RolloutService(
            null!, null!, null!, null!,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            NullLogger<RolloutService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(bool withKubeconfig = true)
    {
        ours = Guid.NewGuid();
        theirs = Guid.NewGuid();
        ourDeployment = await SeedTenantAsync(ours, "ours", withKubeconfig);
        theirDeployment = await SeedTenantAsync(theirs, "theirs", withKubeconfig);
    }

    private async Task<AppDeployment> SeedTenantAsync(Guid tenantId, string label, bool withKubeconfig)
    {
        Guid clusterId = Guid.NewGuid(), envId = Guid.NewGuid(),
             customerId = Guid.NewGuid(), appId = Guid.NewGuid();

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
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"{label}-customer" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(), AppId = appId, Name = "Billing API", Type = DeploymentType.Manual,
            EnvironmentId = envId, ClusterId = clusterId, Namespace = $"{label}-ns", IsManaged = true,
        };

        db.AppDeployments.Add(deployment);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }

        return deployment;
    }

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_rollback_undoes_the_deployments_namespace_on_our_own_cluster()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.RollbackAsync(ours, ourDeployment);

        ok.Should().BeTrue(output);
        undone.Should().ContainSingle()
            .Which.Namespace.Should().Be("ours-ns", "the namespace is the unit, not one workload");
        undone[0].Kubeconfig.Should().Be(TestKubeconfig.Valid,
            "the credential is supplied by the seam, from the vault, not carried by the caller");
    }

    /// <summary>
    /// The refusal the tenant parameter buys. Asserted on <c>undone</c> and not only on the
    /// returned flag: a rollback that reached the cluster and then reported failure would still
    /// have undone someone else's release.
    /// </summary>
    [Fact]
    public async Task A_rollback_is_refused_on_another_tenants_cluster()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.RollbackAsync(ours, theirDeployment);

        ok.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        undone.Should().BeEmpty("nothing may reach a cluster this tenant does not own");
    }

    [Fact]
    public async Task A_rollback_is_refused_when_the_cluster_has_no_kubeconfig()
    {
        await SeedAsync(withKubeconfig: false);

        (bool ok, _) = await sut.RollbackAsync(ours, ourDeployment);

        ok.Should().BeFalse();
        undone.Should().BeEmpty();
    }

    /// <summary>
    /// A rollback that undid two workloads and failed on the third is a failure whose output is
    /// the only record of what state the namespace is in. It must survive the trip back — which
    /// is why the seam returns a result rather than throwing.
    /// </summary>
    [Fact]
    public async Task A_partially_failed_rollback_keeps_its_per_workload_output()
    {
        await SeedAsync();
        result = new WorkloadUndoResult(false,
            "deployment.apps/api: rolled back\ndeployment.apps/worker: no rollout history found");

        (bool ok, string output) = await sut.RollbackAsync(ours, ourDeployment);

        ok.Should().BeFalse();
        output.Should().Contain("no rollout history found").And.Contain("rolled back");
    }

    // ════════════════════════════════════════════════════════════════
    //  The seam's own job
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Against the real factory, not a mock of it — the lesson from #144, where mutants that
    /// stopped supplying the credential and stopped raising the gate both passed a green suite
    /// because every test drove the callers through a mocked factory. This verb is new, so it is
    /// the one most able to be added without a gate at all.
    /// </summary>
    [Fact]
    public async Task The_factory_asks_before_it_rolls_anything_back()
    {
        ClusterChangeGate gate = new(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate, new KubernetesProxyClientPool(Microsoft.Extensions.Logging.Abstractions.NullLogger<KubernetesProxyClientPool>.Instance));

        Func<Task> act = () => factory.UndoDeploymentsAsync(
            "billing-ns", TestKubeconfig.Valid, "Roll back Billing API after a failed release");

        // The sink cancels, so throwing proves the acknowledgment was raised before kubectl ran.
        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Rollback);
        sink.Asked[0].Namespace.Should().Be("billing-ns");
        sink.Asked[0].Summary.Should().Contain("Billing API");

        // There is no manifest and no patch to show: a rollback names no target state, because
        // the revision being restored is the cluster's memory rather than EntKube's.
        sink.Asked[0].Manifest.Should().BeNull();
        sink.Asked[0].Patch.Should().BeNull();
    }

    private sealed class RecordingSink : IClusterChangeAckSink
    {
        public List<PlannedClusterChange> Asked { get; } = [];

        public Task<ClusterChangeDecision> RequestAsync(
            PlannedClusterChange change, ClusterChangeDiff diff, CancellationToken ct)
        {
            Asked.Add(change);
            return Task.FromResult(ClusterChangeDecision.Cancelled);
        }
    }

    /// <summary>
    /// An exception from the cluster is reported, not thrown: the caller is a background watcher
    /// mid-incident, and its next act is to record a verdict either way.
    /// </summary>
    [Fact]
    public async Task A_cluster_that_throws_is_reported_rather_than_propagated()
    {
        await SeedAsync();
        k8s.Setup(x => x.UndoDeploymentsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): unauthorized"));

        (bool ok, string output) = await sut.RollbackAsync(ours, ourDeployment);

        ok.Should().BeFalse();
        output.Should().Contain("unauthorized");
    }
}
