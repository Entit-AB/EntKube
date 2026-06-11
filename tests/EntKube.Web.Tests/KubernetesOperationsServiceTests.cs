using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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

        sut = new KubernetesOperationsService(dbFactory);
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
    //  GetPodsAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetPodsAsync_WithoutKubeconfig_ReturnsClusterNotConfiguredError()
    {
        // Arrange — cluster has no kubeconfig stored.
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        // Act — trying to get pods without a kubeconfig should return an error.
        KubernetesOperationResult<List<PodInfo>> result = await sut.GetPodsAsync(deployment.Id);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  RestartDeploymentAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RestartDeploymentAsync_WithoutKubeconfig_ReturnsError()
    {
        // Arrange
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult result = await sut.RestartDeploymentAsync(
            deployment.Id, "billing-api");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  DeletePodAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeletePodAsync_WithoutKubeconfig_ReturnsError()
    {
        // Arrange
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult result = await sut.DeletePodAsync(
            deployment.Id, "billing-api-abc123");

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
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult<string> result = await sut.GetPodLogsAsync(
            deployment.Id, "billing-api-abc123");

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }

    // ════════════════════════════════════════════════════════════════
    //  ScaleDeploymentAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ScaleDeploymentAsync_WithoutKubeconfig_ReturnsError()
    {
        // Arrange
        (AppDeployment deployment, _) = CreateDeploymentWithCluster();

        // Act
        KubernetesOperationResult result = await sut.ScaleDeploymentAsync(
            deployment.Id, "billing-api", 3);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("kubeconfig");
    }
}
