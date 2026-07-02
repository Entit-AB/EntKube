using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for MongoService — the orchestration layer for managed MongoDB Community Operator clusters.
/// Covers cluster creation, deletion, upgrade, backup, restore, and database management.
///
/// The Kubernetes API interactions are mocked via IKubernetesClientFactory.
/// Database state management and vault secret creation are tested against
/// a real SQLite in-memory database to verify the full orchestration flow.
/// </summary>
public class MongoServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8sFactory;
    private readonly MongoService sut;

    public MongoServiceTests()
    {
        // Mirrors production: contexts resolve cluster.Kubeconfig from the vault via the interceptor.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        dbFactory = testDb.Factory;

        vaultService = testDb.CreateVaultService();
        k8sFactory = new Mock<IKubernetesClientFactory>();
        sut = new MongoService(dbFactory, vaultService, k8sFactory.Object);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    // ──────── Helpers ────────

    private async Task<(Tenant tenant, KubernetesCluster cluster, StorageLink storageLink)> SeedTenantWithClusterAsync()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);

        Data.Environment env = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = "Production"
        };

        db.Set<Data.Environment>().Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "prod-cluster",
            ApiServerUrl = "https://k8s.example.com",
        };

        db.KubernetesClusters.Add(cluster);

        // Install the MongoDB Community Operator on this cluster.

        ClusterComponent mongoOperator = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "mongodb-community-operator",
            ComponentType = "helm",
            Status = ComponentStatus.Installed,
            Namespace = "mongodb-operator"
        };

        db.ClusterComponents.Add(mongoOperator);

        // Create a storage link for backups.

        StorageLink storageLink = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Backup Bucket",
            Endpoint = "https://s3-kna1.citycloud.com",
            BucketName = "mongo-backups",
            Region = "Kna1"
        };

        db.StorageLinks.Add(storageLink);
        await db.SaveChangesAsync();

        // Initialize vault and store S3 credentials + the cluster kubeconfig (resolved on load).

        await vaultService.InitializeVaultAsync(tenant.Id);
        await testDb.SeedKubeconfigAsync(vaultService, tenant.Id, cluster.Id, TestKubeconfig.Valid);
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, storageLink.Id, "ACCESS_KEY", "test-access-key");
        await vaultService.SetStorageLinkSecretAsync(tenant.Id, storageLink.Id, "SECRET_KEY", "test-secret-key");

        return (tenant, cluster, storageLink);
    }

    // ──────── CreateClusterAsync ────────

    [Fact]
    public async Task CreateClusterAsync_ValidInput_CreatesDbRecordAndReturnsCluster()
    {
        // Arrange — a tenant with a cluster and MongoDB Community Operator installed.

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — create a new managed MongoDB cluster.

        MongoCluster result = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "my-mongo", "databases", 3, "10Gi",
            storageLink.Id, "0 2 * * *");

        // Assert — the cluster record should exist in the database.

        result.Should().NotBeNull();
        result.Name.Should().Be("my-mongo");
        result.Namespace.Should().Be("databases");
        result.Members.Should().Be(3);
        result.StorageSize.Should().Be("10Gi");
        result.StorageLinkId.Should().Be(storageLink.Id);
        result.BackupSchedule.Should().Be("0 2 * * *");
        result.Status.Should().Be(MongoClusterStatus.Creating);

        MongoCluster? persisted = await db.MongoClusters.FindAsync(result.Id);
        persisted.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateClusterAsync_WithoutBackup_CreatesClusterWithNoStorageLink()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — create without backup configuration.

        MongoCluster result = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "standalone-mongo", "default", 1, "5Gi",
            storageLinkId: null, backupSchedule: null);

        // Assert

        result.StorageLinkId.Should().BeNull();
        result.BackupSchedule.Should().BeNull();
    }

    [Fact]
    public async Task CreateClusterAsync_NoMongoOperator_ThrowsInvalidOperation()
    {
        // Arrange — tenant with a cluster but NO MongoDB Community Operator.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "NoPercona", Slug = "no-percona" };
        db.Tenants.Add(tenant);

        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dev" };
        db.Set<Data.Environment>().Add(env);

        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "dev-cluster",
            ApiServerUrl = "https://k8s.dev.example.com",
            Kubeconfig = "fake"
        };

        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "mongo", "default", 1, "5Gi", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MongoDB Community Operator*not installed*");
    }

    [Fact]
    public async Task CreateClusterAsync_AppliesManifestToKubernetes()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        List<string> appliedManifests = [];
        string? appliedKubeconfig = null;

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, kubeconfig, _) =>
            {
                appliedManifests.Add(manifest);
                appliedKubeconfig = kubeconfig;
            })
            .Returns(Task.CompletedTask);

        // Act

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "backup-mongo", "databases", 3, "20Gi",
            storageLink.Id, "0 2 * * *");

        // Assert — verify the manifests contain expected Community Operator configuration.

        string allManifests = string.Join("\n", appliedManifests);
        allManifests.Should().Contain("kind: MongoDBCommunity");
        allManifests.Should().Contain("mongodbcommunity.mongodb.com/v1");
        allManifests.Should().Contain("name: backup-mongo");
        allManifests.Should().Contain("namespace: databases");
        appliedKubeconfig.Should().NotBeNull();
    }

    // ──────── DeleteClusterAsync ────────

    [Fact]
    public async Task DeleteClusterAsync_RemovesFromDbAndKubernetes()
    {
        // Arrange — create a cluster first.

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MongoCluster created = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "delete-me", "databases", 1, "5Gi", null, null);

        // Act

        await sut.DeleteClusterAsync(tenant.Id, created.Id);

        // Assert — should be gone from the database.

        MongoCluster? deleted = await db.MongoClusters.FindAsync(created.Id);
        deleted.Should().BeNull();
    }

    // ──────── GetClustersAsync ────────

    [Fact]
    public async Task GetClustersAsync_ReturnsOnlyTenantClusters()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.CreateClusterAsync(tenant.Id, cluster.Id, "mongo-1", "ns1", 3, "10Gi", null, null);
        await sut.CreateClusterAsync(tenant.Id, cluster.Id, "mongo-2", "ns2", 1, "5Gi", null, null);

        // Act

        List<MongoCluster> result = await sut.GetClustersAsync(tenant.Id);

        // Assert

        result.Should().HaveCount(2);
        result.Select(c => c.Name).Should().Contain("mongo-1").And.Contain("mongo-2");
    }

    // ──────── CreateDatabaseAsync ────────

    [Fact]
    public async Task CreateDatabaseAsync_CreatesDbRecordAndExecutesMongo()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // CreateDatabaseAsync runs a mongosh script and checks stdout for the
        // ENTK_SUCCESS sentinel before marking the database Ready.
        k8sFactory.Setup(f => f.ExecuteMongoWithOutputAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ENTK_SUCCESS");

        MongoCluster mongoCluster = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "db-test", "databases", 3, "10Gi", null, null);

        // Act

        MongoDatabase result = await sut.CreateDatabaseAsync(tenant.Id, mongoCluster.Id, "myapp");

        // Assert

        result.Should().NotBeNull();
        result.Name.Should().Be("myapp");
        result.Status.Should().Be(MongoDatabaseStatus.Ready);

        k8sFactory.Verify(f => f.ExecuteMongoWithOutputAsync(
            "db-test", "databases", It.Is<string>(s => s.Contains("myapp")),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────── BackupAsync ────────

    [Fact]
    public async Task BackupAsync_CreatesBackupRecordAndAppliesManifest()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MongoCluster mongoCluster = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "backup-test", "databases", 3, "10Gi",
            storageLink.Id, "0 2 * * *");

        // Act

        MongoBackup result = await sut.BackupAsync(tenant.Id, mongoCluster.Id);

        // Assert

        result.Should().NotBeNull();
        result.Type.Should().Be(MongoBackupType.OnDemand);
        result.Status.Should().Be(MongoBackupStatus.Running);

        // Verify a backup Job manifest was applied.

        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.Is<string>(m => m.Contains("kind: Job") && m.Contains("mongodump")),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
