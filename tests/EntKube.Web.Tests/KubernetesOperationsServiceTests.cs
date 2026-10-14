using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.ClusterChanges;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for KubernetesOperationsService — the service that performs live
/// cluster operations (pod logs, restart, redeploy) using stored kubeconfig.
///
/// These tests verify the service's data-layer behavior (looking up clusters,
/// building client configs). Actual K8s API calls are integration-level and
/// require a live cluster. We test the plumbing, not the wire.
/// </summary>
public class KubernetesOperationsServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly KubernetesOperationsService sut;

    public KubernetesOperationsServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        // A gate with no interactive sink registered passes straight through (background/test scope).
        ClusterChangeGate gate = new(new ConfigurationBuilder().Build(), NullLogger<ClusterChangeGate>.Instance);

        sut = new KubernetesOperationsService(dbFactory, new AuditService(dbFactory),
            new KyvernoPolicyService(dbFactory, new Mock<IKubernetesClientFactory>().Object,
                new EntKube.Web.Services.Clusters.ClusterClientFactory(dbFactory, new Mock<IKubernetesClientFactory>().Object), gate,
                new EntKube.Web.Modules.Api.CatalogApi(dbFactory), NullLogger<KyvernoPolicyService>.Instance),
            gate,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(
                dbFactory, new Mock<IKubernetesClientFactory>().Object),
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory),
            new EntKube.Web.Services.Rollouts.NoOpRolloutStarter(),
            NullLogger<KubernetesOperationsService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Helpers ──

    private KubernetesCluster CreateCluster(string? kubeconfig = null)
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "production" };
        db.Environments.Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com",
            Kubeconfig = kubeconfig
        };
        db.KubernetesClusters.Add(cluster);

        db.SaveChanges();
        return cluster;
    }

    private (AppDeployment deployment, KubernetesCluster cluster) CreateDeploymentWithCluster()
    {
        KubernetesCluster cluster = CreateCluster();

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = cluster.TenantId, Name = "Contoso" };
        db.Customers.Add(customer);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing-api" };
        db.Apps.Add(app);

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = app.Id,
            Name = "billing-deploy",
            Type = DeploymentType.Manual,
            EnvironmentId = cluster.EnvironmentId,
            ClusterId = cluster.Id,
            Namespace = "billing-ns"
        };
        db.AppDeployments.Add(deployment);

        db.SaveChanges();
        return (deployment, cluster);
    }

    // ════════════════════════════════════════════════════════════════
    //  GetDeploymentWithClusterAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetDeploymentWithClusterAsync_ReturnsDeploymentAndCluster()
    {
        // Arrange
        (AppDeployment deployment, KubernetesCluster cluster) = CreateDeploymentWithCluster();

        // Act — the service looks up the deployment with its cluster attached.
        AppDeployment? result = await sut.GetDeploymentWithClusterAsync(deployment.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("billing-deploy");
        result.Cluster.Should().NotBeNull();
        result.Cluster.Name.Should().Be("prod-cluster");
    }

    [Fact]
    public async Task GetDeploymentWithClusterAsync_ReturnsNullForMissing()
    {
        // Act
        AppDeployment? result = await sut.GetDeploymentWithClusterAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    // ════════════════════════════════════════════════════════════════
    //  RestartDeploymentAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RestartDeploymentAsync_WithoutKubeconfig_ReturnsError()
    {
        // Arrange
        (AppDeployment deployment, KubernetesCluster cluster) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult result = await sut.RestartDeploymentAsync(
            cluster.TenantId, deployment.Id, "billing-api");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  GetPodLogsAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPodLogsAsync_WithoutKubeconfig_ReturnsError()
    {
        // Arrange
        (AppDeployment deployment, KubernetesCluster cluster) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult<string> result = await sut.GetPodLogsAsync(
            cluster.TenantId, deployment.Id, "billing-api-abc123");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  The refusal the tenant parameter buys
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every action on the resource tree — scale, restart, delete, logs, events, containers,
    /// YAML, cronjob suspend — used to take a deployment id and nothing else, look up that
    /// deployment's cluster, and read the kubeconfig off it. The id was the authorization.
    ///
    /// <para>These assert the pairing is now checked. They are written against a cluster with no
    /// kubeconfig, so a converted method cannot reach a cluster either way — what they pin is
    /// that a <em>stranger</em> is refused at the seam, before the deployment's own namespace or
    /// cluster is ever consulted, and that the owner gets a different answer.</para>
    /// </summary>
    [Fact]
    public async Task ScaleWorkloadAsync_AnotherTenantsDeployment_IsRefused()
    {
        (AppDeployment deployment, KubernetesCluster cluster) = CreateDeploymentWithCluster();
        Guid stranger = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = stranger, Name = "Stranger", Slug = $"s-{stranger:N}" });
        db.SaveChanges();

        KubernetesOperationResult refused = await sut.ScaleWorkloadAsync(
            stranger, deployment.Id, "Deployment", "billing-api", 3);

        refused.IsSuccess.Should().BeFalse();
        refused.Error.Should().Contain("not this tenant's");

        // The owner reaches further: past the seam, and into the missing credential.
        KubernetesOperationResult owner = await sut.ScaleWorkloadAsync(
            cluster.TenantId, deployment.Id, "Deployment", "billing-api", 3);

        owner.IsSuccess.Should().BeFalse();
        owner.Error.Should().Contain("kubeconfig",
            "a different refusal from the tenant one — if both said the same thing, the "
            + "assertion above would prove nothing");
    }

    [Fact]
    public async Task DeleteResourceAsync_AnotherTenantsDeployment_IsRefused()
    {
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        KubernetesOperationResult result = await sut.DeleteResourceAsync(
            Guid.NewGuid(), deployment.Id, "Pod", "billing-api-abc123");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not this tenant's");
    }

    [Fact]
    public async Task GetResourceEventsAsync_AnotherTenantsDeployment_IsRefused()
    {
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        KubernetesOperationResult<List<string>> result = await sut.GetResourceEventsAsync(
            Guid.NewGuid(), deployment.Id, "Pod", "billing-api-abc123");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not this tenant's");
    }

    /// <summary>
    /// The resource tree's own data, and the cluster read behind the log viewer's namespace
    /// picker. Both took an id and nothing else; the tree read is the one that mattered most,
    /// because it is a full inventory of a namespace's workloads.
    /// </summary>
    [Fact]
    public async Task GetLiveResourcesAsync_AnotherTenantsDeployment_IsRefused()
    {
        (AppDeployment deployment, KubernetesCluster cluster) = CreateDeploymentWithCluster();

        KubernetesOperationResult<List<DeploymentResource>> refused =
            await sut.GetLiveResourcesAsync(Guid.NewGuid(), deployment.Id);

        refused.IsSuccess.Should().BeFalse();
        refused.Error.Should().Contain("not this tenant's");

        KubernetesOperationResult<List<DeploymentResource>> owner =
            await sut.GetLiveResourcesAsync(cluster.TenantId, deployment.Id);

        owner.IsSuccess.Should().BeFalse();
        owner.Error.Should().Contain("kubeconfig",
            "the owner gets past the seam and fails on the missing credential instead");
    }

    [Fact]
    public async Task ListNamespacesAsync_AnotherTenantsCluster_IsRefused()
    {
        KubernetesCluster cluster = CreateCluster();

        KubernetesOperationResult<List<string>> result =
            await sut.ListNamespacesAsync(Guid.NewGuid(), cluster.Id);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found for this tenant");
    }

    /// <summary>
    /// The public API's restart endpoint already checked that the APP belongs to the token's
    /// tenant. The seam now checks the CLUSTER's, which is a different row: a deployment pointing
    /// at another tenant's cluster is refused rather than restarted.
    /// </summary>
    [Fact]
    public async Task RestartDeploymentAsync_AnotherTenantsDeployment_IsRefused()
    {
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        KubernetesOperationResult result = await sut.RestartDeploymentAsync(
            Guid.NewGuid(), deployment.Id, "billing-api");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not this tenant's");
    }

    /// <summary>
    /// A deployment that does not exist is told so, rather than being refused as though it
    /// belonged to someone else — the two are different facts and the message should say which.
    /// </summary>
    [Fact]
    public async Task RestartWorkloadAsync_UnknownDeployment_SaysSo()
    {
        KubernetesOperationResult result = await sut.RestartWorkloadAsync(
            Guid.NewGuid(), Guid.NewGuid(), "Deployment", "billing-api");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Deployment not found");
    }

    // ════════════════════════════════════════════════════════════════
    //  ScaleDeploymentAsync
    // ════════════════════════════════════════════════════════════════
}
