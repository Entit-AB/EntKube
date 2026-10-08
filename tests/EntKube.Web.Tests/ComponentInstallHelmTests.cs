using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What installing a catalog component actually sends to a cluster.
///
/// <para><b>Why this is the one that mattered.</b> <c>ComponentLifecycleService</c> installs,
/// upgrades and removes every catalog component on every cluster — Harbor, Keycloak, Istio,
/// cert-manager, the telemetry stack — and it ran helm as its own process, which bypasses
/// <c>IKubernetesClientFactory</c> and therefore bypasses the acknowledgment gate entirely. Not
/// "ungated" in the sense of a dialog someone skipped: the gate was never invoked, so there was
/// nothing for <c>IClusterChangeRecorder</c> to record either (#139, #141).</para>
///
/// <para>One invocation moved, which undersells it: that line is every component install there
/// is. These tests pin what crosses with it.</para>
/// </summary>
public class ComponentInstallHelmTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ComponentLifecycleService sut;

    private readonly List<(HelmInvocation Invocation, string Kubeconfig)> helm = [];

    private Guid tenantId, clusterId, componentId;

    public ComponentInstallHelmTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.RunHelmAsync(It.IsAny<HelmInvocation>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((HelmInvocation i, string kc, CancellationToken _) => helm.Add((i, kc)))
            .ReturnsAsync(new HelmExecutionResult { Success = true, Output = "release deployed" });

        sut = TestServices.BuildLifecycle(testDb, k8s.Object);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(bool withKubeconfig = true)
    {
        tenantId = Guid.NewGuid();
        clusterId = Guid.NewGuid();
        componentId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = componentId, ClusterId = clusterId, Name = "harbor",
            ComponentType = "Helm", Namespace = "harbor", Status = Data.ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        if (withKubeconfig)
        {
            await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
        }
    }

    private static HelmCommand Install(string? version = "1.19.2", string? repoUrl = null) => new()
    {
        Operation = "upgrade --install",
        ReleaseName = "harbor",
        ChartReference = "harbor/harbor",
        Namespace = "harbor",
        RepoUrl = repoUrl,
        Version = version,
    };

    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Installing_a_component_goes_through_the_seam()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, Install());

        result.Success.Should().BeTrue(result.Output);

        (HelmInvocation invocation, string kubeconfig) = helm.Should().ContainSingle().Subject;

        invocation.Verb.Should().Be("upgrade --install");
        invocation.ReleaseName.Should().Be("harbor");
        invocation.Namespace.Should().Be("harbor");

        // The credential comes from inside the seam. Before this, the installer wrote it to a file
        // and passed --kubeconfig itself.
        kubeconfig.Should().Be(TestKubeconfig.Valid);
        string.Join(' ', invocation.Arguments).Should().NotContain("--kubeconfig");
    }

    [Fact]
    public async Task The_command_line_carries_the_chart_namespace_and_version()
    {
        await SeedAsync();

        await sut.ExecuteHelmAsync(componentId, Install());

        string argv = string.Join(' ', helm.Single().Invocation.Arguments);
        argv.Should().Contain("harbor")
            .And.Contain("harbor/harbor")
            .And.Contain("--namespace harbor")
            .And.Contain("--create-namespace")
            .And.Contain("--version 1.19.2");

        // The verb leads the built argument list and is split onto the invocation, so it must not
        // also be left in the arguments.
        argv.Should().NotStartWith("upgrade");
    }

    [Fact]
    public async Task An_operator_is_told_which_release_and_chart_is_changing()
    {
        await SeedAsync();

        await sut.ExecuteHelmAsync(componentId, Install());

        // The gate's own fallback for a Helm verb is the bare words "Helm manifest". An install
        // replaces whole workloads, so the chart and version are named.
        helm.Single().Invocation.Summary.Should()
            .Contain("harbor").And.Contain("harbor/harbor@1.19.2").And.Contain("in harbor");
    }

    [Fact]
    public async Task An_uninstall_says_that_it_removes_everything()
    {
        await SeedAsync();

        await sut.ExecuteHelmAsync(componentId, new HelmCommand
        {
            Operation = "uninstall", ReleaseName = "harbor", Namespace = "harbor",
        });

        HelmInvocation i = helm.Should().ContainSingle().Subject.Invocation;
        i.Verb.Should().Be("uninstall");
        i.Summary.Should().Contain("removes all release resources");

        // No --create-namespace on the way out.
        string.Join(' ', i.Arguments).Should().NotContain("--create-namespace");
    }

    [Fact]
    public async Task A_chart_with_no_version_pins_nothing()
    {
        await SeedAsync();

        await sut.ExecuteHelmAsync(componentId, Install(version: null));

        string.Join(' ', helm.Single().Invocation.Arguments).Should().NotContain("--version");
    }

    [Fact]
    public async Task Without_a_stored_kubeconfig_no_helm_runs()
    {
        await SeedAsync(withKubeconfig: false);

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, Install());

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("kubeconfig");
        helm.Should().BeEmpty();
    }

    [Fact]
    public async Task A_noop_operation_touches_nothing()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, new HelmCommand
        {
            Operation = "noop", ReleaseName = "harbor", Namespace = "harbor",
        });

        // CRD bundles are deliberately not uninstalled; the operation exists to say so rather than
        // to do nothing silently.
        result.Success.Should().BeTrue();
        helm.Should().BeEmpty();
    }
}
