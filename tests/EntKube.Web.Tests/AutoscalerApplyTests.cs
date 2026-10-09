using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What applying autoscalers sends to a cluster.
///
/// <para><b>Why the namespace is the thing to pin.</b> This path is the reason the seam needed a
/// namespace-defaulting apply at all. A rendered ScaledObject and a user's own Custom YAML both
/// deliberately omit <c>metadata.namespace</c> so that one <c>-n</c> puts them in the app's — so
/// losing the default does not fail, it silently autoscales workloads in <c>default</c> while the
/// page reports success. <see cref="HpaAutoscalerTests"/> covers the rendering; nothing covered
/// the apply.</para>
/// </summary>
public class AutoscalerApplyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly KedaScalerService sut;

    private readonly List<(string Manifest, string Namespace, string? Summary)> applied = [];

    public AutoscalerApplyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, string? s, CancellationToken _) => applied.Add((m, ns, s)))
            .ReturnsAsync("scaledobject.keda.sh/billing-api configured\n");

        sut = new KedaScalerService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            NullLogger<KedaScalerService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<KubernetesCluster> SeedClusterAsync(bool withKubeconfig = true)
    {
        Guid tenantId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
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

    private static List<KedaScaler> OneHpa() =>
    [
        new()
        {
            Name = "billing-api",
            Kind = KedaScalerKind.Hpa,
            ScaleTargetKind = "Deployment",
            ScaleTargetName = "billing-api",
            MinReplicaCount = 2,
            MaxReplicaCount = 8,
            TargetCpuUtilization = 70,
        },
    ];

    [Fact]
    public async Task The_autoscalers_land_in_the_apps_namespace()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        (bool success, string output) = await sut.ApplyToNamespaceAsync(OneHpa(), cluster, "billing-prod");

        success.Should().BeTrue(output);
        output.Should().Be("scaledobject.keda.sh/billing-api configured");

        (string manifest, string ns, string? summary) = applied.Should().ContainSingle().Subject;

        ns.Should().Be("billing-prod");
        summary.Should().Be("Apply autoscalers to billing-prod");

        // The rendered document carries no namespace of its own — that is what the default is for.
        manifest.Should().Contain("kind: HorizontalPodAutoscaler");
        manifest.Should().NotContain("namespace:");
    }

    [Fact]
    public async Task A_cluster_with_no_stored_kubeconfig_is_refused_without_a_cluster_call()
    {
        KubernetesCluster cluster = await SeedClusterAsync(withKubeconfig: false);

        (bool success, string output) = await sut.ApplyToNamespaceAsync(OneHpa(), cluster, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("no kubeconfig");
        applied.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_from_the_cluster_stays_a_failed_result()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): no matches for kind \"ScaledObject\""));

        (bool success, string output) = await sut.ApplyToNamespaceAsync(OneHpa(), cluster, "billing-prod");

        success.Should().BeFalse();
        output.Should().Contain("no matches for kind");
    }

    /// <summary>
    /// An operator cancelling at the gate threw out of here before the call moved onto the seam,
    /// and the page is written for that rather than for a tuple saying the apply failed.
    /// </summary>
    [Fact]
    public async Task An_operator_cancelling_at_the_gate_is_not_reported_as_a_failure()
    {
        KubernetesCluster cluster = await SeedClusterAsync();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Cluster change cancelled by operator"));

        Func<Task> act = () => sut.ApplyToNamespaceAsync(OneHpa(), cluster, "billing-prod");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
