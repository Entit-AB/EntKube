using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the DeploymentService which manages app deployments — creating
/// deployments (Manual, Yaml, Helm), managing manifests, and tracking status.
/// Uses SQLite in-memory for fast, isolated database tests.
/// </summary>
public class DeploymentServiceTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory dbFactory;
    private readonly DeploymentService sut;

    public DeploymentServiceTests()
    {
        // Stand up a fresh in-memory SQLite database for each test.
        // This gives us real EF Core behavior without touching disk.

        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        db = new ApplicationDbContext(options);
        dbFactory = new TestDbContextFactory(connection);
        db.Database.EnsureCreated();

        sut = new DeploymentService(dbFactory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    // ── Helpers ──

    /// <summary>
    /// Sets up a tenant with an environment, cluster, customer, and app — the
    /// minimum scaffolding needed to create a deployment.
    /// </summary>
    private (App app, Data.Environment env, KubernetesCluster cluster) CreateTestApp()
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
            ApiServerUrl = "https://k8s.example.com"
        };
        db.KubernetesClusters.Add(cluster);

        Customer customer = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Contoso" };
        db.Customers.Add(customer);

        App app = new() { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "billing-api" };
        db.Apps.Add(app);

        db.SaveChanges();
        return (app, env, cluster);
    }

    // ════════════════════════════════════════════════════════════════
    //  CreateDeploymentAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateDeploymentAsync_ManualType_CreatesWithUnknownStatus()
    {
        // Arrange — we need an app, environment, and cluster to target.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        // Act — create a manual deployment targeting the prod cluster.
        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "billing-deploy", DeploymentType.Manual,
            env.Id, cluster.Id, "billing-ns");

        // Assert — the deployment exists with the right defaults.
        deployment.Id.Should().NotBeEmpty();
        deployment.Name.Should().Be("billing-deploy");
        deployment.Type.Should().Be(DeploymentType.Manual);
        deployment.Namespace.Should().Be("billing-ns");
        deployment.SyncStatus.Should().Be(SyncStatus.Unknown);
        deployment.HealthStatus.Should().Be(HealthStatus.Unknown);
    }

    [Fact]
    public async Task CreateDeploymentAsync_HelmType_StoresChartInfo()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        // Act — create a Helm deployment with chart details.
        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "minio-deploy", DeploymentType.HelmChart,
            env.Id, cluster.Id, "minio-ns",
            helmRepoUrl: "https://charts.min.io",
            helmChartName: "minio",
            helmChartVersion: "5.0.0");

        // Assert
        deployment.Type.Should().Be(DeploymentType.HelmChart);
        deployment.HelmRepoUrl.Should().Be("https://charts.min.io");
        deployment.HelmChartName.Should().Be("minio");
        deployment.HelmChartVersion.Should().Be("5.0.0");
    }

    [Fact]
    public async Task CreateDeploymentAsync_DuplicateName_Throws()
    {
        // Arrange — create a deployment, then try to create another with the same name.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        await sut.CreateDeploymentAsync(
            app.Id, "billing-deploy", DeploymentType.Manual,
            env.Id, cluster.Id, "billing-ns");

        // Act & Assert
        Func<Task> act = () => sut.CreateDeploymentAsync(
            app.Id, "billing-deploy", DeploymentType.Manual,
            env.Id, cluster.Id, "other-ns");

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    // ════════════════════════════════════════════════════════════════
    //  GetDeploymentsAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetDeploymentsAsync_ReturnsDeploymentsForApp()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        await sut.CreateDeploymentAsync(app.Id, "deploy-1", DeploymentType.Manual, env.Id, cluster.Id, "ns1");
        await sut.CreateDeploymentAsync(app.Id, "deploy-2", DeploymentType.HelmChart, env.Id, cluster.Id, "ns2");

        // Act
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(app.Id);

        // Assert — both deployments returned with related data loaded.
        deployments.Should().HaveCount(2);
        deployments.Should().Contain(d => d.Name == "deploy-1");
        deployments.Should().Contain(d => d.Name == "deploy-2");
    }

    [Fact]
    public async Task GetDeploymentsAsync_IncludesEnvironmentAndCluster()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        await sut.CreateDeploymentAsync(app.Id, "deploy-1", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(app.Id);

        // Assert — navigation properties should be loaded.
        deployments[0].Environment.Name.Should().Be("production");
        deployments[0].Cluster.Name.Should().Be("prod-cluster");
    }

    // ════════════════════════════════════════════════════════════════
    //  DeleteDeploymentAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task DeleteDeploymentAsync_RemovesDeployment()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "to-delete", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act
        await sut.DeleteDeploymentAsync(deployment.Id);

        // Assert
        List<AppDeployment> remaining = await sut.GetDeploymentsAsync(app.Id);
        remaining.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  Manifest CRUD
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AddManifestAsync_CreatesManifest()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "yaml-deploy", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        string yaml = "apiVersion: v1\nkind: Service\nmetadata:\n  name: billing-svc";

        // Act — add a manifest to the deployment.
        DeploymentManifest manifest = await sut.AddManifestAsync(
            deployment.Id, "Service", "billing-svc", yaml, sortOrder: 1);

        // Assert
        manifest.Id.Should().NotBeEmpty();
        manifest.Kind.Should().Be("Service");
        manifest.Name.Should().Be("billing-svc");
        manifest.YamlContent.Should().Be(yaml);
    }

    [Fact]
    public async Task GetManifestsAsync_ReturnsOrderedManifests()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "ordered-deploy", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        await sut.AddManifestAsync(deployment.Id, "Service", "svc", "svc-yaml", sortOrder: 2);
        await sut.AddManifestAsync(deployment.Id, "Deployment", "dep", "dep-yaml", sortOrder: 1);
        await sut.AddManifestAsync(deployment.Id, "PersistentVolumeClaim", "pvc", "pvc-yaml", sortOrder: 0);

        // Act
        List<DeploymentManifest> manifests = await sut.GetManifestsAsync(deployment.Id);

        // Assert — returned in SortOrder (PVC → Deployment → Service).
        manifests.Should().HaveCount(3);
        manifests[0].Kind.Should().Be("PersistentVolumeClaim");
        manifests[1].Kind.Should().Be("Deployment");
        manifests[2].Kind.Should().Be("Service");
    }

    [Fact]
    public async Task UpdateManifestAsync_UpdatesYamlContent()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "update-test", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        DeploymentManifest manifest = await sut.AddManifestAsync(
            deployment.Id, "Deployment", "billing", "old-yaml", sortOrder: 0);

        // Act
        await sut.UpdateManifestAsync(manifest.Id, "new-yaml-content");

        // Assert
        List<DeploymentManifest> manifests = await sut.GetManifestsAsync(deployment.Id);
        manifests[0].YamlContent.Should().Be("new-yaml-content");
    }

    [Fact]
    public async Task DeleteManifestAsync_RemovesManifest()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "del-manifest", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        DeploymentManifest manifest = await sut.AddManifestAsync(
            deployment.Id, "Service", "svc", "yaml", sortOrder: 0);

        // Act
        await sut.DeleteManifestAsync(manifest.Id);

        // Assert
        List<DeploymentManifest> remaining = await sut.GetManifestsAsync(deployment.Id);
        remaining.Should().BeEmpty();
    }

    // ════════════════════════════════════════════════════════════════
    //  Helm Values
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateHelmValuesAsync_StoresYaml()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "helm-deploy", DeploymentType.HelmChart, env.Id, cluster.Id, "minio-ns",
            helmRepoUrl: "https://charts.min.io", helmChartName: "minio", helmChartVersion: "5.0.0");

        string values = "replicas: 3\npersistence:\n  size: 10Gi";

        // Act
        await sut.UpdateHelmValuesAsync(deployment.Id, values);

        // Assert — reload from DB to verify persistence.
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(app.Id);
        deployments[0].HelmValues.Should().Be(values);
    }

    // ════════════════════════════════════════════════════════════════
    //  Deployment Resources (ArgoCD-style tree)
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpsertResourceAsync_CreatesNewResource()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "res-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act — the cluster watcher reports a Deployment resource.
        DeploymentResource resource = await sut.UpsertResourceAsync(
            deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, "Running 3/3 replicas");

        // Assert
        resource.Id.Should().NotBeEmpty();
        resource.Kind.Should().Be("Deployment");
        resource.SyncStatus.Should().Be(SyncStatus.Synced);
        resource.HealthStatus.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task UpsertResourceAsync_UpdatesExistingResource()
    {
        // Arrange — create a resource, then update its health.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "upsert-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        await sut.UpsertResourceAsync(
            deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Progressing, "Rolling update");

        // Act — same resource, updated status.
        await sut.UpsertResourceAsync(
            deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, "Running 3/3");

        // Assert — only one resource, with the updated health.
        List<DeploymentResource> resources = await sut.GetResourceTreeAsync(deployment.Id);
        resources.Should().HaveCount(1);
        resources[0].HealthStatus.Should().Be(HealthStatus.Healthy);
        resources[0].StatusMessage.Should().Be("Running 3/3");
    }

    [Fact]
    public async Task GetResourceTreeAsync_ReturnsOnlyRootResources()
    {
        // Arrange — build a two-level tree: Deployment → ReplicaSet.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "tree-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        DeploymentResource parent = await sut.UpsertResourceAsync(
            deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, null);

        await sut.UpsertResourceAsync(
            deployment.Id, "apps", "v1", "ReplicaSet", "billing-api-abc123", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, null,
            parentResourceId: parent.Id);

        // Act — get the tree roots.
        List<DeploymentResource> roots = await sut.GetResourceTreeAsync(deployment.Id);

        // Assert — only the Deployment root, with the ReplicaSet as a child.
        roots.Should().HaveCount(1);
        roots[0].Kind.Should().Be("Deployment");
        roots[0].ChildResources.Should().HaveCount(1);
        roots[0].ChildResources.First().Kind.Should().Be("ReplicaSet");
    }

    // ════════════════════════════════════════════════════════════════
    //  UpdateDeploymentStatusAsync
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateDeploymentStatusAsync_UpdatesStatusFields()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            app.Id, "status-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act — simulate a sync completion.
        await sut.UpdateDeploymentStatusAsync(
            deployment.Id, SyncStatus.Synced, HealthStatus.Healthy, "All resources healthy");

        // Assert
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(app.Id);
        deployments[0].SyncStatus.Should().Be(SyncStatus.Synced);
        deployments[0].HealthStatus.Should().Be(HealthStatus.Healthy);
        deployments[0].StatusMessage.Should().Be("All resources healthy");
        deployments[0].LastSyncedAt.Should().NotBeNull();
    }
}
