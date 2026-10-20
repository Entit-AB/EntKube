using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

        sut = new DeploymentService(dbFactory, new AuditService(dbFactory, new EntKube.Web.Modules.Api.DeliveryApi(dbFactory)),
            new DeploymentStatusNotifier(), NullLogger<DeploymentService>.Instance);
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
        Guid tenantId = Guid.NewGuid();
        Tenant tenant = new() { Id = tenantId, Name = "TestCo", Slug = $"testco-{tenantId:N}" };
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
            cluster.TenantId, app.Id, "billing-deploy", DeploymentType.Manual,
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
            cluster.TenantId, app.Id, "minio-deploy", DeploymentType.HelmChart,
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
            cluster.TenantId, app.Id, "billing-deploy", DeploymentType.Manual,
            env.Id, cluster.Id, "billing-ns");

        // Act & Assert
        Func<Task> act = () => sut.CreateDeploymentAsync(
            cluster.TenantId, app.Id, "billing-deploy", DeploymentType.Manual,
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

        await sut.CreateDeploymentAsync(cluster.TenantId, app.Id, "deploy-1", DeploymentType.Manual, env.Id, cluster.Id, "ns1");
        await sut.CreateDeploymentAsync(cluster.TenantId, app.Id, "deploy-2", DeploymentType.HelmChart, env.Id, cluster.Id, "ns2");

        // Act
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(cluster.TenantId, app.Id);

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

        await sut.CreateDeploymentAsync(cluster.TenantId, app.Id, "deploy-1", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(cluster.TenantId, app.Id);

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
            cluster.TenantId, app.Id, "to-delete", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act
        await sut.DeleteDeploymentAsync(cluster.TenantId, deployment.Id);

        // Assert
        List<AppDeployment> remaining = await sut.GetDeploymentsAsync(cluster.TenantId, app.Id);
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
            cluster.TenantId, app.Id, "yaml-deploy", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        string yaml = "apiVersion: v1\nkind: Service\nmetadata:\n  name: billing-svc";

        // Act — add a manifest to the deployment.
        DeploymentManifest manifest = await sut.AddManifestAsync(
            cluster.TenantId, deployment.Id, "Service", "billing-svc", yaml, sortOrder: 1);

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
            cluster.TenantId, app.Id, "ordered-deploy", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        await sut.AddManifestAsync(cluster.TenantId, deployment.Id, "Service", "svc", "svc-yaml", sortOrder: 2);
        await sut.AddManifestAsync(cluster.TenantId, deployment.Id, "Deployment", "dep", "dep-yaml", sortOrder: 1);
        await sut.AddManifestAsync(cluster.TenantId, deployment.Id, "PersistentVolumeClaim", "pvc", "pvc-yaml", sortOrder: 0);

        // Act
        List<DeploymentManifest> manifests = await sut.GetManifestsAsync(cluster.TenantId, deployment.Id);

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
            cluster.TenantId, app.Id, "update-test", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        DeploymentManifest manifest = await sut.AddManifestAsync(
            cluster.TenantId, deployment.Id, "Deployment", "billing", "old-yaml", sortOrder: 0);

        // Act
        await sut.UpdateManifestAsync(cluster.TenantId, manifest.Id, "new-yaml-content");

        // Assert
        List<DeploymentManifest> manifests = await sut.GetManifestsAsync(cluster.TenantId, deployment.Id);
        manifests[0].YamlContent.Should().Be("new-yaml-content");
    }

    [Fact]
    public async Task DeleteManifestAsync_RemovesManifest()
    {
        // Arrange
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        AppDeployment deployment = await sut.CreateDeploymentAsync(
            cluster.TenantId, app.Id, "del-manifest", DeploymentType.Yaml, env.Id, cluster.Id, "ns1");

        DeploymentManifest manifest = await sut.AddManifestAsync(
            cluster.TenantId, deployment.Id, "Service", "svc", "yaml", sortOrder: 0);

        // Act
        await sut.DeleteManifestAsync(cluster.TenantId, manifest.Id);

        // Assert
        List<DeploymentManifest> remaining = await sut.GetManifestsAsync(cluster.TenantId, deployment.Id);
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
            cluster.TenantId, app.Id, "helm-deploy", DeploymentType.HelmChart, env.Id, cluster.Id, "minio-ns",
            helmRepoUrl: "https://charts.min.io", helmChartName: "minio", helmChartVersion: "5.0.0");

        string values = "replicas: 3\npersistence:\n  size: 10Gi";

        // Act
        await sut.UpdateHelmValuesAsync(cluster.TenantId, deployment.Id, values);

        // Assert — reload from DB to verify persistence.
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(cluster.TenantId, app.Id);
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
            cluster.TenantId, app.Id, "res-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act — the cluster watcher reports a Deployment resource.
        DeploymentResource resource = await sut.UpsertResourceAsync(
            cluster.TenantId, deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
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
            cluster.TenantId, app.Id, "upsert-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        await sut.UpsertResourceAsync(
            cluster.TenantId, deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Progressing, "Rolling update");

        // Act — same resource, updated status.
        await sut.UpsertResourceAsync(
            cluster.TenantId, deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, "Running 3/3");

        // Assert — only one resource, with the updated health.
        List<DeploymentResource> resources = await sut.GetResourceTreeAsync(cluster.TenantId, deployment.Id);
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
            cluster.TenantId, app.Id, "tree-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        DeploymentResource parent = await sut.UpsertResourceAsync(
            cluster.TenantId, deployment.Id, "apps", "v1", "Deployment", "billing-api", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, null);

        await sut.UpsertResourceAsync(
            cluster.TenantId, deployment.Id, "apps", "v1", "ReplicaSet", "billing-api-abc123", "ns1",
            SyncStatus.Synced, HealthStatus.Healthy, null,
            parentResourceId: parent.Id);

        // Act — get the tree roots.
        List<DeploymentResource> roots = await sut.GetResourceTreeAsync(cluster.TenantId, deployment.Id);

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
            cluster.TenantId, app.Id, "status-test", DeploymentType.Manual, env.Id, cluster.Id, "ns1");

        // Act — simulate a sync completion.
        await sut.UpdateDeploymentStatusAsync(
            cluster.TenantId, deployment.Id, SyncStatus.Synced, HealthStatus.Healthy, "All resources healthy");

        // Assert
        List<AppDeployment> deployments = await sut.GetDeploymentsAsync(cluster.TenantId, app.Id);
        deployments[0].SyncStatus.Should().Be(SyncStatus.Synced);
        deployments[0].HealthStatus.Should().Be(HealthStatus.Healthy);
        deployments[0].StatusMessage.Should().Be("All resources healthy");
        deployments[0].LastSyncedAt.Should().NotBeNull();
    }

    // ════════════════════════════════════════════════════════════════
    //  Namespace guard
    // ════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateDeploymentAsync_RejectsABlankNamespace(string ns)
    {
        // A deployment stored without a namespace cannot be installed, and the edit form's own
        // required-namespace rule then refuses every later change to it — chart version included.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();

        Func<Task> act = () => sut.CreateDeploymentAsync(
            cluster.TenantId, app.Id, "no-ns", DeploymentType.HelmChart, env.Id, cluster.Id, ns);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*namespace is required*");
        (await sut.GetDeploymentsAsync(cluster.TenantId, app.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateDeploymentAsync_RejectsBlankingAnExistingNamespace()
    {
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        AppDeployment deployment = await sut.CreateDeploymentAsync(
            cluster.TenantId, app.Id, "billing", DeploymentType.HelmChart, env.Id, cluster.Id, "billing-ns");

        Func<Task> act = () => sut.UpdateDeploymentAsync(cluster.TenantId, deployment.Id, "billing", "");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*namespace is required*");
        (await sut.GetDeploymentsAsync(cluster.TenantId, app.Id))[0].Namespace.Should().Be("billing-ns");
    }

    [Fact]
    public async Task UpdateDeploymentAsync_ChangesTheChartVersionOfAnUninstalledHelmDeployment()
    {
        // The chart coordinates are plain stored fields — nothing about them depends on a
        // release existing on the cluster.
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        AppDeployment deployment = await sut.CreateDeploymentAsync(
            cluster.TenantId, app.Id, "billing", DeploymentType.GitHelm, env.Id, cluster.Id, "billing-ns",
            helmRepoUrl: "https://charts.example.com", helmChartName: "billing", helmChartVersion: "1.0.0");

        await sut.UpdateDeploymentAsync(
            cluster.TenantId, deployment.Id, "billing", "billing-ns",
            "https://charts.example.com", "billing", "2.3.4");

        AppDeployment updated = (await sut.GetDeploymentsAsync(cluster.TenantId, app.Id))[0];
        updated.HelmChartVersion.Should().Be("2.3.4");
        updated.SyncStatus.Should().Be(SyncStatus.Unknown);   // still never deployed
    }

    // ════════════════════════════════════════════════════════════════
    //  Tenancy
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creating a deployment names three things — an app, an environment and a cluster — and the
    /// cluster is the one that reaches a machine: a deployment is the row the sync loop acts on,
    /// so one pointed at somebody else's cluster is how manifests get applied there under our
    /// name. All three crossings are tried separately, because one guard clause per id means one
    /// clause that can be left out.
    /// </summary>
    [Fact]
    public async Task A_deployment_cannot_be_created_against_another_tenants_app_environment_or_cluster()
    {
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        (App theirApp, Data.Environment theirEnv, KubernetesCluster theirCluster) = CreateTestApp();

        Guid us = cluster.TenantId;

        Func<Task> theirAppOfOurs = () => sut.CreateDeploymentAsync(
            us, theirApp.Id, "planted", DeploymentType.Manual, env.Id, cluster.Id, "ns");
        Func<Task> theirEnvironment = () => sut.CreateDeploymentAsync(
            us, app.Id, "planted", DeploymentType.Manual, theirEnv.Id, cluster.Id, "ns");
        Func<Task> theirClusterTarget = () => sut.CreateDeploymentAsync(
            us, app.Id, "planted", DeploymentType.Manual, env.Id, theirCluster.Id, "ns");

        await theirAppOfOurs.Should().ThrowAsync<InvalidOperationException>();
        await theirEnvironment.Should().ThrowAsync<InvalidOperationException>();
        await theirClusterTarget.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetDeploymentsAsync(us, app.Id)).Should().BeEmpty();
        (await sut.GetDeploymentsAsync(theirCluster.TenantId, theirApp.Id)).Should().BeEmpty();

        // And our own, fully within one tenant, still goes through.
        AppDeployment ours = await sut.CreateDeploymentAsync(
            us, app.Id, "ours", DeploymentType.Manual, env.Id, cluster.Id, "ns");
        ours.ClusterId.Should().Be(cluster.Id);
    }

    /// <summary>
    /// The by-deployment-id readers and writers. Every one of them looked its row up with
    /// <c>FindAsync</c>, which takes a key and cannot carry a predicate — so a deployment id was
    /// the whole authorization, and a deployment id travels in page markup.
    ///
    /// <para>The writes return <c>void</c>, so the row has to say whether they landed. The git URL
    /// and the Helm values are the sharp ones: the first is what the sync loop pulls from, the
    /// second is arbitrary chart input — image, replicas, mounted secrets.</para>
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_read_or_change_another_tenants_deployment()
    {
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        (_, _, KubernetesCluster theirCluster) = CreateTestApp();

        Guid us = cluster.TenantId;
        Guid them = theirCluster.TenantId;

        AppDeployment ours = await sut.CreateDeploymentAsync(
            us, app.Id, "billing", DeploymentType.HelmChart, env.Id, cluster.Id, "billing-ns",
            helmRepoUrl: "https://charts.example.com", helmChartName: "billing",
            helmChartVersion: "1.0.0");

        // Reads.
        (await sut.GetDeploymentsAsync(them, app.Id)).Should().BeEmpty(
            "the deployment exists, so this is a refusal");
        (await sut.GetDeploymentsAsync(us, app.Id)).Should().ContainSingle("the owner still sees it");

        // Writes, each of which returns void.
        await sut.UpdateDeploymentAsync(them, ours.Id, "hijacked", "their-ns");
        await sut.UpdateGitSettingsAsync(them, ours.Id, "https://evil.example.com/repo.git", "/", "main", true);
        await sut.UpdateHelmValuesAsync(them, ours.Id, "image:\n  repository: evil/backdoor");
        await sut.UpdateDeploymentStatusAsync(them, ours.Id, SyncStatus.Synced, HealthStatus.Healthy, "all good");
        await sut.DeleteDeploymentAsync(them, ours.Id);

        AppDeployment after = (await sut.GetDeploymentsAsync(us, app.Id)).Should().ContainSingle().Subject;
        after.Name.Should().Be("billing");
        after.Namespace.Should().Be("billing-ns");
        after.GitUrl.Should().BeNull("the git URL is what the sync loop pulls from");
        after.HelmValues.Should().BeNull("Helm values are arbitrary chart input");
        after.SyncStatus.Should().Be(SyncStatus.Unknown, "a forged status hides a broken deployment");

        // And the owner can still do all of it.
        await sut.UpdateDeploymentAsync(us, ours.Id, "renamed", "billing-ns");
        (await sut.GetDeploymentsAsync(us, app.Id))[0].Name.Should().Be("renamed");
        await sut.DeleteDeploymentAsync(us, ours.Id);
        (await sut.GetDeploymentsAsync(us, app.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// A manifest is raw YAML that the next sync applies to the deployment's cluster, so adding
    /// one to a stranger's deployment is arbitrary cluster write access and editing one turns
    /// their workload into whatever was pasted. <c>DeploymentManifest</c> has no <c>TenantId</c>,
    /// so the scope runs manifest → deployment → app → customer.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_touch_another_tenants_manifests()
    {
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        (_, _, KubernetesCluster theirCluster) = CreateTestApp();

        Guid us = cluster.TenantId;
        Guid them = theirCluster.TenantId;

        AppDeployment ours = await sut.CreateDeploymentAsync(
            us, app.Id, "billing", DeploymentType.Yaml, env.Id, cluster.Id, "billing-ns");
        DeploymentManifest manifest = await sut.AddManifestAsync(
            us, ours.Id, "Deployment", "billing", "original-yaml", 0);

        Func<Task> plantOne = () => sut.AddManifestAsync(
            them, ours.Id, "Pod", "miner", "evil-yaml", 1);
        await plantOne.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetManifestsAsync(them, ours.Id)).Should().BeEmpty(
            "the manifest exists, so this is a refusal");

        await sut.UpdateManifestAsync(them, manifest.Id, "evil-yaml");
        await sut.DeleteManifestAsync(them, manifest.Id);

        List<DeploymentManifest> survived = await sut.GetManifestsAsync(us, ours.Id);
        survived.Should().ContainSingle();
        survived[0].YamlContent.Should().Be("original-yaml");

        // The owner's own edit and delete still work.
        await sut.UpdateManifestAsync(us, manifest.Id, "updated-yaml");
        (await sut.GetManifestsAsync(us, ours.Id))[0].YamlContent.Should().Be("updated-yaml");
        await sut.DeleteManifestAsync(us, manifest.Id);
        (await sut.GetManifestsAsync(us, ours.Id)).Should().BeEmpty();
    }

    /// <summary>
    /// The resource tree is what the UI shows as live cluster state, so an unscoped upsert lets
    /// one tenant's reconciler post rows under another's deployment and misreport their health —
    /// a healthy-looking tree over a broken workload.
    /// </summary>
    [Fact]
    public async Task A_stranger_cannot_post_or_read_another_tenants_resource_tree()
    {
        (App app, Data.Environment env, KubernetesCluster cluster) = CreateTestApp();
        (_, _, KubernetesCluster theirCluster) = CreateTestApp();

        Guid us = cluster.TenantId;
        Guid them = theirCluster.TenantId;

        AppDeployment ours = await sut.CreateDeploymentAsync(
            us, app.Id, "billing", DeploymentType.Yaml, env.Id, cluster.Id, "billing-ns");
        await sut.UpsertResourceAsync(
            us, ours.Id, "apps", "v1", "Deployment", "billing", "billing-ns",
            SyncStatus.OutOfSync, HealthStatus.Degraded, "CrashLoopBackOff");

        Func<Task> forge = () => sut.UpsertResourceAsync(
            them, ours.Id, "apps", "v1", "Deployment", "billing", "billing-ns",
            SyncStatus.Synced, HealthStatus.Healthy, null);
        await forge.Should().ThrowAsync<InvalidOperationException>();

        (await sut.GetResourceTreeAsync(them, ours.Id)).Should().BeEmpty(
            "the resource exists, so this is a refusal");

        DeploymentResource kept = (await sut.GetResourceTreeAsync(us, ours.Id)).Should().ContainSingle().Subject;
        kept.HealthStatus.Should().Be(HealthStatus.Degraded,
            "a forged Healthy row would hide a crash-looping workload from its owner");
    }
}
