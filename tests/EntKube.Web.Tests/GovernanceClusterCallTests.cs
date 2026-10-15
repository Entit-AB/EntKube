using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Clusters;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// What <see cref="AppGovernanceService"/> sends to a cluster, and whose cluster it is willing to
/// send it to.
///
/// <para><b>Why there was none.</b> <c>GovernanceNamespaceTests</c> has four tests and none of them
/// constructs the service — they check name validation and a result shape. The three paths that
/// actually reach a cluster were untestable: one built its own <c>Kubernetes</c> client from the
/// kubeconfig, and two ran <c>kubectl apply</c> through a <c>private static</c> process call. Moving
/// them onto <see cref="IClusterClient"/> is what makes these writable, the same way it did for the
/// routing paths.</para>
///
/// <para>These assert on <em>what reached the cluster</em>. A governance apply that silently goes
/// nowhere leaves a quota unenforced and a NetworkPolicy unwritten, with the page reporting
/// success — and the three methods took an app, an environment and a cluster id with no tenant
/// anywhere, so the id was the whole authorization.</para>
/// </summary>
public class GovernanceClusterCallTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;
    private readonly Mock<IKubernetesClientFactory> k8s = new();
    private readonly AppGovernanceService sut;

    /// <summary>Every manifest applied, as (manifest, namespace).</summary>
    private readonly List<(string Manifest, string Namespace)> applied = [];

    /// <summary>Namespaces the service asked the cluster to ensure.</summary>
    private readonly List<string> ensured = [];

    private Guid tenantId, clusterId, appId, environmentId;

    public GovernanceClusterCallTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        k8s.Setup(f => f.ApplyManifestInNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, CancellationToken>(
                (manifest, ns, _, _, _) => applied.Add((manifest, ns)))
            .ReturnsAsync("applied");

        k8s.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((ns, _, _) => ensured.Add(ns))
            .Returns(Task.CompletedTask);

        sut = new AppGovernanceService(
            testDb.Factory,
            new ClusterClientFactory(testDb.Factory, k8s.Object),
            NullLogger<AppGovernanceService>.Instance);
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
        appId = Guid.NewGuid();
        environmentId = Guid.NewGuid();
        Guid customerId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "production",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-cluster", ApiServerUrl = "https://k8s.example.com",
        });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Contoso" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "billing" });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, Name = "billing-deploy", Type = DeploymentType.Manual,
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = "billing-ns",
        });
        await db.SaveChangesAsync();

        await vault.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vault, tenantId, clusterId, TestKubeconfig.Valid);
    }

    private KubernetesCluster Cluster() => db.KubernetesClusters.First(c => c.Id == clusterId);

    // ──────── Applying governance ────────

    [Fact]
    public async Task Governance_is_applied_into_the_deployments_namespace()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.ApplyToClusterAsync(tenantId, appId, environmentId, Cluster());

        ok.Should().BeTrue(output);
        applied.Should().ContainSingle()
            .Which.Namespace.Should().Be("billing-ns",
                "the namespace comes from the deployment targeting this cluster in this environment");
    }

    [Fact]
    public async Task Another_tenant_cannot_apply_governance_to_this_cluster()
    {
        await SeedAsync();

        (bool ok, string output) =
            await sut.ApplyToClusterAsync(Guid.NewGuid(), appId, environmentId, Cluster());

        ok.Should().BeFalse();
        output.Should().Contain("not this tenant's");
        applied.Should().BeEmpty("nothing should reach anyone's cluster");
    }

    // ──────── Creating a namespace ────────

    [Fact]
    public async Task Creating_a_namespace_asks_the_cluster_to_ensure_it()
    {
        await SeedAsync();

        (bool ok, string output) = await sut.CreateNamespaceOnClusterAsync(tenantId, clusterId, "team-a");

        ok.Should().BeTrue(output);
        ensured.Should().Equal(["team-a"]);
    }

    [Fact]
    public async Task Another_tenant_cannot_create_a_namespace_on_this_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.CreateNamespaceOnClusterAsync(Guid.NewGuid(), clusterId, "team-a");

        ok.Should().BeFalse();
        ensured.Should().BeEmpty();
    }

    /// <summary>
    /// An invalid name is refused before the tenant is even considered — the cluster is never
    /// reached either way, and a name SQLite would accept but Kubernetes would not is the caller's
    /// mistake rather than an authorization question.
    /// </summary>
    [Fact]
    public async Task An_invalid_namespace_name_never_reaches_the_cluster()
    {
        await SeedAsync();

        (bool ok, _) = await sut.CreateNamespaceOnClusterAsync(tenantId, clusterId, "Not Valid");

        ok.Should().BeFalse();
        ensured.Should().BeEmpty();
    }

    // ──────── Checking a namespace ────────

    [Fact]
    public async Task A_namespace_check_reports_a_cluster_it_may_not_reach_rather_than_failing()
    {
        await SeedAsync();

        NamespaceCheckResult result =
            await sut.CheckNamespaceAsync(Guid.NewGuid(), appId, environmentId, "billing-ns");

        result.Clusters.Should().ContainSingle()
            .Which.Error.Should().Contain("not this tenant's",
                "the check runs across a tenant's clusters, so one it cannot reach is a status on "
                + "that cluster and not a failure of the whole answer");
    }
}
