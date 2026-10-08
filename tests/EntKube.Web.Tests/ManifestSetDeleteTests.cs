using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Deleting a whole manifest set through the seam.
///
/// <para><b>The gap this closed.</b> <c>DeleteManifestAsync</c> removes one named resource. A
/// caller holding a multi-document manifest could not express itself with that without parsing the
/// YAML to find every kind and name, so those callers kept spawning their own kubectl — which
/// means they kept the credential and stayed outside the acknowledgment gate. That was the single
/// remaining structural gap behind both numbers, rather than a list of unrelated call sites.</para>
/// </summary>
public class ManifestSetDeleteTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();

    private readonly List<string> applied = [];
    private readonly List<string> deletedSets = [];

    private Guid tenantId, clusterId;

    public ManifestSetDeleteTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, CancellationToken _) => applied.Add(m))
            .ReturnsAsync("applied");

        k8s.Setup(x => x.DeleteManifestSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, CancellationToken _) => deletedSets.Add(m))
            .ReturnsAsync("deleted");
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<KubernetesCluster> SeedClusterAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        KubernetesCluster cluster = new()
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        };
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }

        return cluster;
    }

    /// <summary>
    /// A ComponentLifecycleService over the intercepting database, so <c>cluster.Kubeconfig</c>
    /// resolves through the vault interceptor as it does in production. Built here rather than via
    /// a shared helper to keep this change independent of the one that adds one.
    /// </summary>
    private ComponentLifecycleService Lifecycle()
    {
        IConfiguration config = TestServices.TestConfiguration();
        IngestTokenService tokens = new(config);
        IHttpClientFactory httpFactory = new Mock<IHttpClientFactory>().Object;
        ClusterClientFactory clusters = new(testDb.Factory, k8s.Object);
        CnpgService cnpg = new(testDb.Factory, vault, clusters);

        return new ComponentLifecycleService(
            testDb.Factory, vault,
            new KeycloakService(testDb.Factory, vault, httpFactory, cnpg, k8s.Object),
            tokens,
            new EntKubeTelemetryService(testDb.Factory, vault, tokens, config),
            config,
            clusters,
            NullLogger<ComponentLifecycleService>.Instance);
    }

    private const string TwoDocuments =
        "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: strongswan\n"
        + "---\napiVersion: v1\nkind: Secret\nmetadata:\n  name: strongswan-psk\n";

    // ════════════════════════════════════════════════════════════════
    //  ApplyRawManifestAsync — the VPN path
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Applying_a_raw_manifest_sends_every_document_in_one_call()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ComponentLifecycleService sut = Lifecycle();

        HelmExecutionResult result = await sut.ApplyRawManifestAsync(cluster, TwoDocuments);

        result.Success.Should().BeTrue(result.Output);

        // One call with both documents, not one call per document: kubectl apply -f takes a
        // multi-document file, and splitting it would make the set non-atomic for no reason.
        applied.Should().ContainSingle()
            .Which.Should().Contain("kind: ConfigMap").And.Contain("kind: Secret");
        deletedSets.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_a_raw_manifest_removes_the_whole_set()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        ComponentLifecycleService sut = Lifecycle();

        HelmExecutionResult result = await sut.ApplyRawManifestAsync(cluster, TwoDocuments, delete: true);

        result.Success.Should().BeTrue(result.Output);

        deletedSets.Should().ContainSingle()
            .Which.Should().Contain("kind: ConfigMap").And.Contain("kind: Secret");
        applied.Should().BeEmpty("delete must not also apply");
    }

    [Fact]
    public async Task A_cluster_with_no_kubeconfig_is_refused_rather_than_attempted()
    {
        KubernetesCluster cluster = await SeedClusterAsync(withKubeconfig: false);
        ComponentLifecycleService sut = Lifecycle();

        HelmExecutionResult result = await sut.ApplyRawManifestAsync(cluster, TwoDocuments);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("kubeconfig");
        applied.Should().BeEmpty();
        deletedSets.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_from_the_cluster_stays_a_failed_result()
    {
        KubernetesCluster cluster = await SeedClusterAsync();
        k8s.Setup(x => x.ApplyManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): admission webhook denied"));

        ComponentLifecycleService sut = Lifecycle();

        // VpnService inspects the result rather than catching, so an exception here would surface
        // as an unhandled error in the UI instead of a message on the page.
        HelmExecutionResult result = await sut.ApplyRawManifestAsync(cluster, TwoDocuments);

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("admission webhook denied");
    }

    // ════════════════════════════════════════════════════════════════
    //  The seam's own job
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Against the real factory, not a mock of it — the lesson from #144, where mutants that
    /// stopped supplying the credential and stopped raising the gate both passed a green suite
    /// because every test drove the callers through a mocked factory.
    /// </summary>
    [Fact]
    public async Task The_factory_asks_before_it_deletes_a_set()
    {
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);
        RecordingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);

        KubernetesClientFactory factory = new(gate);

        Func<Task> act = () => factory.DeleteManifestSetAsync(TwoDocuments, TestKubeconfig.Valid);

        // The sink cancels, so throwing proves the acknowledgment was raised before kubectl ran.
        await act.Should().ThrowAsync<OperationCanceledException>();

        sink.Asked.Should().ContainSingle();
        sink.Asked[0].Verb.Should().Be(ChangeVerb.Delete);

        // A composite delete has no single kind and name, so the manifest IS the preview — the
        // gate's own delete path falls back to showing it for exactly this case.
        sink.Asked[0].Manifest.Should().Contain("kind: ConfigMap").And.Contain("kind: Secret");
        sink.Asked[0].Kind.Should().BeNull("there is no one kind in a set");
    }

    /// <summary>
    /// <c>--ignore-not-found</c> asserted directly, because a mutant that dropped it passed
    /// everything else here: these tests reach the cluster through a mocked factory, so the real
    /// command line never runs. Same gap, same fix as the helm argv in #144.
    /// </summary>
    [Fact]
    public void The_set_delete_tolerates_resources_that_are_already_gone()
    {
        string argv = KubernetesClientFactory.BuildDeleteSetArguments("/tmp/m.yaml", "/tmp/kc.yaml");

        argv.Should().Be("delete -f /tmp/m.yaml --kubeconfig=/tmp/kc.yaml --ignore-not-found");
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
}
