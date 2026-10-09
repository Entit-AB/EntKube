using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using EntKube.Web.Services.Upgrades;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What a drift sweep asks of a cluster.
///
/// <para><b>Why there was none.</b> <c>DriftDetectionTests</c> has twenty-eight tests and not one
/// of them constructs <see cref="DriftDetectionService"/> — they exercise the deprecated-API
/// scanner and the shaping of the report. The service's own cluster interaction was untested
/// because it ran <c>kubectl diff</c> through a <c>private static</c> process call that nothing
/// could observe. Moving it onto <see cref="IClusterClient"/> is what made these writable, the
/// same way it did for the routing paths in #137.</para>
///
/// <para>The behaviour most worth pinning is the third state. "Could not tell" must never read as
/// "no difference": a sweep that reports a drifted deployment as healthy the moment a cluster stops
/// answering is worse than one that reports nothing, because someone will believe it.</para>
/// </summary>
public class DriftSweepClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly DriftDetectionService sut;

    /// <summary>Every diff asked of a cluster, as (manifest, namespace).</summary>
    private readonly List<(string Manifest, string Namespace)> diffs = [];

    /// <summary>How many times a server version was read — once per cluster, not per deployment.</summary>
    private int versionReads;

    private Guid tenantId, clusterId;

    public DriftSweepClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        Matches();

        k8s.Setup(x => x.GetServerVersionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => versionReads++)
            .ReturnsAsync("v1.29.4");

        sut = new DriftDetectionService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            NullLogger<DriftDetectionService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Answers(ManifestDiff diff) =>
        k8s.Setup(x => x.DiffManifestAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, CancellationToken _) => diffs.Add((m, ns)))
            .ReturnsAsync(diff);

    private void Matches() => Answers(new ManifestDiff(DiffOutcome.Matches));

    private async Task SeedAsync(int deployments = 1, bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid(), customerId = Guid.NewGuid(), appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });

        for (int i = 0; i < deployments; i++)
        {
            Guid deploymentId = Guid.NewGuid();
            db.AppDeployments.Add(new AppDeployment
            {
                Id = deploymentId, AppId = appId, Name = $"deploy-{i}", Type = DeploymentType.Manual,
                EnvironmentId = envId, ClusterId = clusterId, Namespace = $"ns-{i}", IsManaged = true,
            });
            db.DeploymentManifests.Add(new DeploymentManifest
            {
                Id = Guid.NewGuid(), DeploymentId = deploymentId, SortOrder = 0,
                Kind = "Deployment", Name = $"deploy-{i}.yaml",
                YamlContent = $"apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: deploy-{i}\n",
            });
        }

        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_matching_cluster_reports_in_sync()
    {
        await SeedAsync();

        DriftReport report = await sut.GetTenantDriftAsync(tenantId, DateTime.UtcNow);

        report.Results.Should().ContainSingle().Which.State.Should().Be(DriftState.InSync);

        (string manifest, string ns) = diffs.Should().ContainSingle().Subject;

        // The stored manifest is what gets compared, in the deployment's own namespace.
        manifest.Should().Contain("kind: Deployment").And.Contain("name: deploy-0");
        ns.Should().Be("ns-0");

        // And the desired state leads with the Namespace itself, which is not incidental: without
        // it the first sweep of a deployment whose namespace does not exist yet would diff every
        // document against nothing and call the whole thing drift.
        manifest.Should().Contain("kind: Namespace");
    }

    [Fact]
    public async Task A_differing_cluster_reports_the_drift_it_was_told_about()
    {
        await SeedAsync();
        Answers(new ManifestDiff(DiffOutcome.Differs, "- replicas: 1\n+ replicas: 3\n"));

        DriftReport report = await sut.GetTenantDriftAsync(tenantId, DateTime.UtcNow);

        DriftResult result = report.Results.Should().ContainSingle().Subject;
        result.State.Should().Be(DriftState.Drifted);
        result.DiffText.Should().Contain("replicas: 3");
    }

    /// <summary>
    /// The one that matters. The service's own comment says an uncomputable diff must be Unknown
    /// "so a broken connection never reads as a clean bill of health" — this is that sentence as a
    /// test, because the failure it describes is silent.
    /// </summary>
    [Fact]
    public async Task A_cluster_that_cannot_answer_is_unknown_and_never_in_sync()
    {
        await SeedAsync();
        Answers(new ManifestDiff(DiffOutcome.Unknown, Error: "connection refused"));

        DriftReport report = await sut.GetTenantDriftAsync(tenantId, DateTime.UtcNow);

        DriftResult result = report.Results.Should().ContainSingle().Subject;
        result.State.Should().Be(DriftState.Unknown);
        result.State.Should().NotBe(DriftState.InSync);
        result.Note.Should().Contain("connection refused");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_unknown_and_is_never_asked()
    {
        await SeedAsync(withKubeconfig: false);

        DriftReport report = await sut.GetTenantDriftAsync(tenantId, DateTime.UtcNow);

        report.Results.Should().ContainSingle().Which.State.Should().Be(DriftState.Unknown);
        diffs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_server_version_is_read_once_per_cluster_not_once_per_deployment()
    {
        await SeedAsync(deployments: 4);

        await sut.GetTenantDriftAsync(tenantId, DateTime.UtcNow);

        // Four deployments, one cluster. The sweep walks every managed deployment in the tenant
        // and they share a handful of clusters, so resolving per deployment would multiply both
        // the version read and the client handshake behind it.
        versionReads.Should().Be(1);
        diffs.Should().HaveCount(4, "each deployment is still compared on its own");
    }

    /// <summary>
    /// The seam operation's own logic, asserted directly rather than through a mock of it — a
    /// mutant that turned exit 1 into Unknown passed everything above, because the tests reach the
    /// cluster through a mocked factory and the real mapping never ran.
    /// </summary>
    [Theory]
    [InlineData(0, DiffOutcome.Matches)]
    [InlineData(1, DiffOutcome.Differs)]
    [InlineData(2, DiffOutcome.Unknown)]
    [InlineData(127, DiffOutcome.Unknown)]
    public void Kubectl_diffs_exit_code_says_which_of_the_three_happened(int exitCode, DiffOutcome expected)
    {
        KubernetesClientFactory.InterpretDiffExit(exitCode, "- a\n+ b\n", "boom")
            .Outcome.Should().Be(expected);
    }

    [Fact]
    public void A_difference_carries_the_diff_and_an_unknown_carries_the_reason()
    {
        KubernetesClientFactory.InterpretDiffExit(1, "- replicas: 1\n", "").Text.Should().Contain("replicas");

        KubernetesClientFactory.InterpretDiffExit(2, "", "connection refused")
            .Error.Should().Be("connection refused");

        // kubectl can fail silently; a blank reason would reach the operator as an empty note.
        KubernetesClientFactory.InterpretDiffExit(2, "", "   ")
            .Error.Should().Be("kubectl diff failed.");
    }

    [Fact]
    public async Task Another_tenants_sweep_sees_nothing_of_ours()
    {
        await SeedAsync();

        DriftReport report = await sut.GetTenantDriftAsync(Guid.NewGuid(), DateTime.UtcNow);

        report.Results.Should().BeEmpty();
        diffs.Should().BeEmpty();
    }
}
