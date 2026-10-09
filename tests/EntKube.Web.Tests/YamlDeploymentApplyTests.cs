using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Applying a YAML/Manual deployment — the most operator-visible apply in the product, and until
/// now untested: nothing in the suite called <c>ApplyYamlDeploymentAsync</c> at all, because the
/// kubectl invocation sat behind a <c>private static</c> no test could reach.
///
/// <para><b>What these pin.</b> Two things that look identical from the database row. The
/// namespace is passed as a <em>default</em> for documents that carry none of their own, so losing
/// it sends a deployment's resources to <c>default</c> while every row still reads correctly. And
/// the acknowledgment is now raised by the seam, so a second one raised here would ask the
/// operator twice for one apply — which is what this did for a moment while the call was being
/// moved.</para>
/// </summary>
public class YamlDeploymentApplyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly Mock<IClusterChangeGate> gate = new();
    private readonly KubernetesOperationsService sut;

    /// <summary>Every namespace-defaulted apply handed to the cluster, as (manifest, ns, summary).</summary>
    private readonly List<(string Manifest, string Namespace, string? Summary)> applied = [];

    public YamlDeploymentApplyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string m, string ns, string _, string? summary, CancellationToken _) =>
                applied.Add((m, ns, summary)))
            .ReturnsAsync("applied");

        sut = new KubernetesOperationsService(
            testDb.Factory,
            new AuditService(testDb.Factory),
            new KyvernoPolicyService(testDb.Factory, k8s.Object, gate.Object,
                NullLogger<KyvernoPolicyService>.Instance),
            gate.Object,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<AppDeployment> SeedDeploymentAsync(params string[] manifests)
    {
        Guid tenantId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid envId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = tenantId, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing-api" });

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            Name = "billing-deploy",
            Type = DeploymentType.Yaml,
            EnvironmentId = envId,
            ClusterId = clusterId,
            Namespace = "billing-ns",
            IsManaged = true,
        };
        db.AppDeployments.Add(deployment);

        int order = 0;
        foreach (string yaml in manifests)
        {
            db.DeploymentManifests.Add(new DeploymentManifest
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
                Kind = "ConfigMap",
                Name = $"doc-{order}",
                YamlContent = yaml,
                SortOrder = order++,
            });
        }

        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);

        return deployment;
    }

    private const string ConfigMapYaml = """
        apiVersion: v1
        kind: ConfigMap
        metadata:
          name: billing-config
        data:
          mode: live
        """;

    private const string ServiceYaml = """
        apiVersion: v1
        kind: Service
        metadata:
          name: billing
        spec:
          ports:
          - port: 80
        """;

    [Fact]
    public async Task The_deployment_namespace_is_the_default_for_documents_that_name_none()
    {
        AppDeployment deployment = await SeedDeploymentAsync(ConfigMapYaml, ServiceYaml);

        KubernetesOperationResult<string> result = await sut.ApplyYamlDeploymentAsync(deployment.Id);

        result.IsSuccess.Should().BeTrue(result.Error);

        applied.Should().ContainSingle("one apply carries the whole ordered set");

        // Neither document above names a namespace, which is the normal case for a manifest an
        // operator pastes in. Without the default they would land in `default`.
        applied[0].Namespace.Should().Be("billing-ns");
        applied[0].Manifest.Should().Contain("kind: ConfigMap").And.Contain("kind: Service");
    }

    /// <summary>
    /// The dialog the operator reads. The seam knows the manifest but not which deployment it came
    /// from, so the summary has to be passed down: without it the gate falls back to "Apply
    /// manifest" for every apply in the product.
    /// </summary>
    [Fact]
    public async Task The_acknowledgment_summary_names_the_deployment_and_counts_its_documents()
    {
        AppDeployment deployment = await SeedDeploymentAsync(ConfigMapYaml, ServiceYaml);

        await sut.ApplyYamlDeploymentAsync(deployment.Id);

        // Three manifests: the two above plus the Namespace document the composer prepends.
        applied[0].Summary.Should().Be("Apply 'billing-deploy' (2 manifest(s)) to billing-ns");
    }

    /// <summary>
    /// One apply, one question. The seam raises the acknowledgment now, so this service must not —
    /// it did both for a moment, and the symptom is an operator confirming the same apply twice.
    ///
    /// <para>The assertion works because the cluster is reached through a mocked
    /// <see cref="IKubernetesClientFactory"/>: the seam's own acknowledgment never fires here, so
    /// any acknowledgment this gate sees can only have been raised by the caller.</para>
    /// </summary>
    [Fact]
    public async Task The_apply_is_acknowledged_by_the_seam_and_not_a_second_time_here()
    {
        AppDeployment deployment = await SeedDeploymentAsync(ConfigMapYaml);

        await sut.ApplyYamlDeploymentAsync(deployment.Id);

        applied.Should().ContainSingle();
        gate.Verify(
            g => g.AcknowledgeAsync(
                It.Is<PlannedClusterChange>(c => c.Verb == ChangeVerb.Apply),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_failure_from_the_cluster_stays_a_failed_result()
    {
        AppDeployment deployment = await SeedDeploymentAsync(ConfigMapYaml);

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kubectl failed (exit 1): admission webhook denied"));

        KubernetesOperationResult<string> result = await sut.ApplyYamlDeploymentAsync(deployment.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("admission webhook denied");
    }

    /// <summary>
    /// An operator cancelling at the gate is not a failed apply. It threw out of here before the
    /// call moved onto the seam, and the pages calling this are written for that — swallowing it
    /// would put "cancelled by operator" in front of them as an error.
    /// </summary>
    [Fact]
    public async Task An_operator_cancelling_at_the_gate_is_not_reported_as_a_failure()
    {
        AppDeployment deployment = await SeedDeploymentAsync(ConfigMapYaml);

        k8s.Setup(x => x.ApplyManifestInNamespaceAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Cluster change cancelled by operator"));

        Func<Task> act = () => sut.ApplyYamlDeploymentAsync(deployment.Id);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
