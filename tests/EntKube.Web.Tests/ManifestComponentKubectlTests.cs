using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Manifest-type catalog components — the ones installed by <c>kubectl apply</c> rather than helm.
///
/// <para><b>Why these are worth their own file.</b> This path spawned its own kubectl, so it held
/// the credential and never raised an acknowledgment; moving it onto the seam is what these pin.
/// Two of the three things that cross are invisible from any database row: the component's
/// namespace is a <em>default</em> for documents that name none of their own, and the operation
/// decides between apply and delete. Get the second wrong on an uninstall and the component is
/// re-applied instead of removed — or, on an install, removed instead of applied.</para>
/// </summary>
public class ManifestComponentKubectlTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly ComponentLifecycleService sut;

    private readonly List<(string Manifest, string Namespace, string? Summary)> applied = [];
    private readonly List<(string Manifest, string Namespace, string? Summary)> deleted = [];

    private Guid tenantId, clusterId, componentId;

    public ManifestComponentKubectlTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, string? s, CancellationToken _) => applied.Add((m, ns, s)))
            .ReturnsAsync("configmap/strongswan configured");

        k8s.Setup(x => x.DeleteManifestSetAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string _, string ns, string? s, CancellationToken _) => deleted.Add((m, ns, s)))
            .ReturnsAsync("configmap/strongswan deleted");

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
            Id = componentId, ClusterId = clusterId, Name = "strongswan",
            ComponentType = "Manifest", Namespace = "vpn", Status = Data.ComponentStatus.Installed,
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    /// <summary>A manifest that names no namespace of its own — the normal case here.</summary>
    private const string ManifestYaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: strongswan
        data:
          ipsec.conf: "conn %default"
        """;

    private static HelmCommand Command(string operation, string? yaml = ManifestYaml) => new()
    {
        Operation = operation,
        ReleaseName = "strongswan",
        Namespace = "vpn",
        HasValues = yaml is not null,
        ValuesYaml = yaml,
    };

    [Fact]
    public async Task Applying_a_manifest_component_defaults_its_documents_to_the_component_namespace()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, Command("kubectl-apply"));

        result.Success.Should().BeTrue(result.Output);
        result.Output.Should().Contain("configured");

        (string manifest, string ns, string? summary) = applied.Should().ContainSingle().Subject;

        // The document above carries no namespace, so without the default it lands in `default`.
        ns.Should().Be("vpn");
        manifest.Should().Contain("kind: ConfigMap");
        summary.Should().Be("Apply manifests for strongswan in vpn");

        deleted.Should().BeEmpty();
    }

    /// <summary>
    /// The branch. An uninstall that applies is the worst available outcome on this path: the
    /// component is reported as removed while its resources are rewritten into the cluster.
    /// </summary>
    [Fact]
    public async Task Removing_a_manifest_component_deletes_the_set_rather_than_applying_it()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, Command("kubectl-delete"));

        result.Success.Should().BeTrue(result.Output);

        applied.Should().BeEmpty("a delete must never reach the apply");

        (string manifest, string ns, string? summary) = deleted.Should().ContainSingle().Subject;
        ns.Should().Be("vpn");
        manifest.Should().Contain("kind: ConfigMap");
        summary.Should().Be("Delete manifests for strongswan in vpn");
    }

    [Fact]
    public async Task A_manifest_component_with_no_content_never_reaches_the_cluster()
    {
        await SeedAsync();

        HelmExecutionResult result = await sut.ExecuteHelmAsync(
            componentId, Command("kubectl-apply", yaml: null));

        result.Success.Should().BeFalse();
        applied.Should().BeEmpty();
        deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_from_the_cluster_stays_a_failed_result()
    {
        await SeedAsync();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): no matches for kind"));

        HelmExecutionResult result = await sut.ExecuteHelmAsync(componentId, Command("kubectl-apply"));

        result.Success.Should().BeFalse();
        result.Output.Should().Contain("no matches for kind");
    }
}
