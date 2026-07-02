using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for CnpgService — the orchestration layer for managed CloudNativePG clusters.
/// Covers cluster creation, deletion, upgrade, backup, restore, and database management.
///
/// The Kubernetes API interactions are mocked via IKubernetesClientFactory.
/// Database state management and vault secret creation are tested against
/// a real SQLite in-memory database to verify the full orchestration flow.
/// </summary>
public class CnpgServiceTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly IDbContextFactory<ApplicationDbContext> dbFactory;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8sFactory;
    private readonly CnpgService sut;

    public CnpgServiceTests()
    {
        // Mirrors production: contexts resolve cluster.Kubeconfig from the vault via the interceptor.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        dbFactory = testDb.Factory;

        vaultService = testDb.CreateVaultService();
        k8sFactory = new Mock<IKubernetesClientFactory>();
        sut = new CnpgService(dbFactory, vaultService, k8sFactory.Object);
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

        // Install the CNPG operator component on this cluster.

        ClusterComponent cnpgOperator = new()
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "cloudnative-pg",
            ComponentType = "helm",
            Status = ComponentStatus.Installed,
            Namespace = "cnpg-system"
        };

        db.ClusterComponents.Add(cnpgOperator);

        // Create a storage link for Barman backups.

        StorageLink storageLink = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Provider = StorageProvider.CleuraS3,
            Name = "Backup Bucket",
            Endpoint = "https://s3-kna1.citycloud.com",
            BucketName = "cnpg-backups",
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
        // Arrange — a tenant with a cluster and CNPG operator installed.

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — create a new managed CNPG cluster.

        CnpgCluster result = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "my-pg", "databases", 3, "10Gi",
            storageLink.Id, "0 0 2 * * *");

        // Assert — the cluster record should exist in the database.

        result.Should().NotBeNull();
        result.Name.Should().Be("my-pg");
        result.Namespace.Should().Be("databases");
        result.PostgresVersion.Should().Be("18");
        result.Instances.Should().Be(3);
        result.StorageSize.Should().Be("10Gi");
        result.StorageLinkId.Should().Be(storageLink.Id);
        result.BackupSchedule.Should().Be("0 0 2 * * *");
        result.Status.Should().Be(CnpgClusterStatus.Creating);

        CnpgCluster? persisted = await db.CnpgClusters.FindAsync(result.Id);
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

        CnpgCluster result = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "standalone-pg", "default", 1, "5Gi",
            storageLinkId: null, backupSchedule: null);

        // Assert

        result.StorageLinkId.Should().BeNull();
        result.BackupSchedule.Should().BeNull();
    }

    [Fact]
    public async Task CreateClusterAsync_NoCnpgOperator_ThrowsInvalidOperation()
    {
        // Arrange — tenant with a cluster but NO CNPG operator.

        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "NoCnpg", Slug = "no-cnpg" };
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
            tenant.Id, cluster.Id, "pg", "default", 1, "5Gi", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CloudNativePG operator*not installed*");
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
            tenant.Id, cluster.Id, "backup-pg", "databases", 3, "20Gi",
            storageLink.Id, "0 0 2 * * *");

        // Assert — verify the manifests contain expected CNPG configuration.

        string allManifests = string.Join("\n", appliedManifests);
        allManifests.Should().Contain("kind: Cluster");
        allManifests.Should().Contain("postgresql.cnpg.io/v1");
        allManifests.Should().Contain("name: backup-pg");
        allManifests.Should().Contain("namespace: databases");
        allManifests.Should().Contain("instances: 3");
        allManifests.Should().Contain("size: 20Gi");
        allManifests.Should().Contain("postgresql:18");
        allManifests.Should().Contain("barman-cloud.cloudnative-pg.io");
        allManifests.Should().Contain("kind: ObjectStore");
        allManifests.Should().Contain("barmanObjectName: backup-pg-object-store");
        allManifests.Should().Contain("cnpg-backups");
        appliedKubeconfig.Should().NotBeNullOrEmpty();
    }

    // ──────── DeleteClusterAsync ────────

    [Fact]
    public async Task DeleteClusterAsync_RemovesFromK8sAndDatabase()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "to-delete",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act

        await sut.DeleteClusterAsync(tenant.Id, cnpg.Id);

        // Assert — use a fresh context to avoid EF tracking cache.

        using ApplicationDbContext verifyDb = dbFactory.CreateDbContext();
        CnpgCluster? deleted = await verifyDb.CnpgClusters
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cnpg.Id);
        deleted.Should().BeNull();
    }

    // ──────── UpgradeClusterAsync ────────

    [Fact]
    public async Task UpgradeClusterAsync_MinorVersion_UpdatesVersionAndAppliesManifest()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "upgrade-me",
            Namespace = "databases",
            PostgresVersion = "18.1",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act — minor version bump within the same major

        await sut.UpgradeClusterAsync(tenant.Id, cnpg.Id, "18.2");

        // Assert

        using ApplicationDbContext verifyDb = dbFactory.CreateDbContext();
        CnpgCluster? upgraded = await verifyDb.CnpgClusters.FindAsync(cnpg.Id);
        upgraded!.PostgresVersion.Should().Be("18.2");
        upgraded.Status.Should().Be(CnpgClusterStatus.Upgrading);
    }

    [Fact]
    public async Task UpgradeClusterAsync_MajorVersion_ThrowsWithRestoreGuidance()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "upgrade-major",
            Namespace = "databases",
            PostgresVersion = "17",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        // Act & Assert — major version jump should be rejected

        Func<Task> act = () => sut.UpgradeClusterAsync(tenant.Id, cnpg.Id, "18");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Major version upgrades*restore*");
    }

    // ──────── MajorUpgradeAsync ────────

    [Fact]
    public async Task MajorUpgradeAsync_RestoresNewClusterAndRemovesOld()
    {
        // Arrange — a v17 cluster with a database and backup storage

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "prod-db",
            Namespace = "databases",
            PostgresVersion = "17",
            Instances = 3,
            StorageSize = "20Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running
        };

        CnpgDatabase database = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpg.Id,
            Name = "app_data",
            Owner = "app_data_owner",
            Status = CnpgDatabaseStatus.Ready
        };

        db.CnpgClusters.Add(cnpg);
        db.CnpgDatabases.Add(database);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act

        CnpgCluster result = await sut.MajorUpgradeAsync(tenant.Id, cnpg.Id, "18");

        // Assert — new cluster takes the original name and version

        result.Name.Should().Be("prod-db");
        result.PostgresVersion.Should().Be("18");
        result.Status.Should().Be(CnpgClusterStatus.Running);

        // Assert — old cluster record is removed

        using ApplicationDbContext verifyDb = dbFactory.CreateDbContext();
        bool oldExists = await verifyDb.CnpgClusters.AnyAsync(c => c.Id == cnpg.Id);
        oldExists.Should().BeFalse();

        // Assert — databases transferred to the new cluster

        CnpgDatabase? movedDb = await verifyDb.CnpgDatabases.FindAsync(database.Id);
        movedDb!.CnpgClusterId.Should().Be(result.Id);

        // Assert — K8s operations: backup applied, old deleted, temp deleted, final applied

        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.Is<string>(m => m.Contains("kind: Backup")),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "Cluster", "prod-db", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "Cluster", "prod-db-v18", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MajorUpgradeAsync_WithoutStorage_Throws()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "no-backup",
            Namespace = "databases",
            PostgresVersion = "17",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.MajorUpgradeAsync(tenant.Id, cnpg.Id, "18");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*backup storage*");
    }

    // ──────── BackupAsync ────────

    [Fact]
    public async Task BackupAsync_CreatesBackupRecordAndAppliesCR()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "backup-target",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act

        CnpgBackup backup = await sut.BackupAsync(tenant.Id, cnpg.Id);

        // Assert

        backup.Should().NotBeNull();
        backup.CnpgClusterId.Should().Be(cnpg.Id);
        backup.Status.Should().Be(CnpgBackupStatus.Running);
        backup.Type.Should().Be(CnpgBackupType.OnDemand);
        backup.Name.Should().StartWith("backup-target-");
    }

    [Fact]
    public async Task BackupAsync_NoStorageLink_ThrowsInvalidOperation()
    {
        // Arrange — cluster without backup storage configured.

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "no-backup-storage",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            StorageLinkId = null,
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        // Act & Assert

        Func<Task> act = () => sut.BackupAsync(tenant.Id, cnpg.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*backup storage*not configured*");
    }

    // ──────── RestoreAsync ────────

    [Fact]
    public async Task RestoreAsync_CreatesNewClusterWithRecoveryBootstrap()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster source = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "source-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(source);
        await db.SaveChangesAsync();

        string? appliedManifest = null;
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifest = manifest)
            .Returns(Task.CompletedTask);

        DateTime targetTime = new(2026, 5, 17, 10, 30, 0, DateTimeKind.Utc);

        // Act

        CnpgCluster restored = await sut.RestoreAsync(
            tenant.Id, source.Id, "restored-cluster", targetTime);

        // Assert — new cluster created with Restoring status.

        restored.Name.Should().Be("restored-cluster");
        restored.Status.Should().Be(CnpgClusterStatus.Restoring);
        restored.StorageLinkId.Should().Be(storageLink.Id);

        appliedManifest.Should().Contain("recovery");
        appliedManifest.Should().Contain("2026-05-17");
        appliedManifest.Should().Contain("restored-cluster");
    }

    // ──────── CreateDatabaseAsync ────────

    [Fact]
    public async Task CreateDatabaseAsync_CreatesRecordAndStoresSecrets()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "app-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act

        CnpgDatabase database = await sut.CreateDatabaseAsync(
            tenant.Id, cnpg.Id, "myapp");

        // Assert — database record created.

        database.Should().NotBeNull();
        database.Name.Should().Be("myapp");
        database.Owner.Should().Be("myapp_owner");
        database.Status.Should().Be(CnpgDatabaseStatus.Ready);

        CnpgDatabase? persisted = await db.CnpgDatabases.FindAsync(database.Id);
        persisted.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateDatabaseAsync_StoresVaultSecretsTaggedForK8sSync()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "secret-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act

        CnpgDatabase database = await sut.CreateDatabaseAsync(
            tenant.Id, cnpg.Id, "analytics");

        // Assert — vault secrets should be created with K8s sync tags.

        List<VaultSecret> secrets = await db.VaultSecrets
            .Where(s => s.CnpgDatabaseId == database.Id)
            .ToListAsync();

        secrets.Should().HaveCountGreaterThanOrEqualTo(5);
        secrets.Should().Contain(s => s.Name == "HOST");
        secrets.Should().Contain(s => s.Name == "PORT");
        secrets.Should().Contain(s => s.Name == "DATABASE");
        secrets.Should().Contain(s => s.Name == "USERNAME");
        secrets.Should().Contain(s => s.Name == "PASSWORD");

        // All secrets should be tagged for K8s sync.

        secrets.Should().OnlyContain(s => s.SyncToKubernetes == true);
        secrets.Should().OnlyContain(s => s.KubernetesSecretName == "secret-cluster-analytics-credentials");
        secrets.Should().OnlyContain(s => s.KubernetesNamespace == "databases");
    }

    // ──────── DeleteDatabaseAsync ────────

    [Fact]
    public async Task DeleteDatabaseAsync_RemovesDatabaseAndSecrets()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "db-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CnpgDatabase database = await sut.CreateDatabaseAsync(
            tenant.Id, cnpg.Id, "to-remove");

        // Act

        await sut.DeleteDatabaseAsync(tenant.Id, cnpg.Id, database.Id);

        // Assert

        CnpgDatabase? deleted = await db.CnpgDatabases.FindAsync(database.Id);
        deleted.Should().BeNull();

        List<VaultSecret> remainingSecrets = await db.VaultSecrets
            .Where(s => s.CnpgDatabaseId == database.Id)
            .ToListAsync();
        remainingSecrets.Should().BeEmpty();
    }

    // ──────── GetClustersAsync ────────

    [Fact]
    public async Task GetClustersAsync_ReturnsOnlyTenantClusters()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg1 = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "cluster-1",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        CnpgCluster cnpg2 = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "cluster-2",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "20Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.AddRange(cnpg1, cnpg2);
        await db.SaveChangesAsync();

        // Act

        List<CnpgCluster> results = await sut.GetClustersAsync(tenant.Id);

        // Assert

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.Name == "cluster-1");
        results.Should().Contain(c => c.Name == "cluster-2");
    }

    // ──────── Manifest Generation ────────

    [Fact]
    public async Task CreateClusterAsync_ManifestIncludesBackupStorageCredentials()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        List<string> appliedManifests = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifests.Add(manifest))
            .Returns(Task.CompletedTask);

        // Act

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "cred-test", "databases", 3, "10Gi",
            storageLink.Id, "0 0 2 * * *");

        // Assert — manifests should reference the K8s secret for S3 credentials.

        string allManifests = string.Join("\n", appliedManifests);
        allManifests.Should().Contain("s3Credentials");
        allManifests.Should().Contain("endpointURL");
        allManifests.Should().Contain("s3-kna1.citycloud.com");
        allManifests.Should().Contain("cnpg-backups");
    }

    [Fact]
    public async Task CreateClusterAsync_ManifestIncludesScheduledBackup()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        List<string> appliedManifests = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifests.Add(manifest))
            .Returns(Task.CompletedTask);

        // Act

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "sched-test", "databases", 3, "10Gi",
            storageLink.Id, "0 0 2 * * *");

        // Assert — ObjectStore and ScheduledBackup are applied in separate calls.

        string allManifests = string.Join("\n", appliedManifests);
        appliedManifests.Should().Contain(m => m.Contains("ObjectStore"));
        appliedManifests.Should().Contain(m => m.Contains("ScheduledBackup"));
        appliedManifests.Should().Contain(m => m.Contains("0 0 2 * * *"));
        appliedManifests.Should().Contain(m => m.Contains("immediate: true"));
    }

    // ──────── GetDatabaseCredentialsAsync ────────

    [Fact]
    public async Task GetDatabaseCredentialsAsync_ReturnsDecryptedCredentials()
    {
        // Arrange — create a cluster and database so the vault has credentials.

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "creds-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        CnpgDatabase database = await sut.CreateDatabaseAsync(tenant.Id, cnpg.Id, "webapp");

        // Act — retrieve the credentials from the vault.

        Dictionary<string, string> credentials = await sut.GetDatabaseCredentialsAsync(
            tenant.Id, database.Id);

        // Assert — all connection fields should be present with correct values.

        credentials.Should().ContainKey("HOST")
            .WhoseValue.Should().Be("creds-cluster-rw.databases.svc.cluster.local");
        credentials.Should().ContainKey("PORT").WhoseValue.Should().Be("5432");
        credentials.Should().ContainKey("DATABASE").WhoseValue.Should().Be("webapp");
        credentials.Should().ContainKey("USERNAME").WhoseValue.Should().Be("webapp_owner");
        credentials.Should().ContainKey("PASSWORD");
        credentials["PASSWORD"].Should().HaveLength(32);
    }

    // ──────── SyncDatabaseCredentialsToK8sAsync ────────

    [Fact]
    public async Task SyncDatabaseCredentialsToK8sAsync_AppliesK8sSecretManifest()
    {
        // Arrange — create a cluster and database with vault credentials.

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "sync-cluster",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        string? appliedManifest = null;
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifest = manifest)
            .Returns(Task.CompletedTask);

        CnpgDatabase database = await sut.CreateDatabaseAsync(tenant.Id, cnpg.Id, "syncdb");

        // Act — push the credentials to Kubernetes.

        await sut.SyncDatabaseCredentialsToK8sAsync(tenant.Id, cnpg.Id, database.Id);

        // Assert — a K8s Secret manifest with all credential fields should be applied.

        appliedManifest.Should().NotBeNull();
        appliedManifest.Should().Contain("kind: Secret");
        appliedManifest.Should().Contain("name: sync-cluster-syncdb-credentials");
        appliedManifest.Should().Contain("namespace: databases");
        appliedManifest.Should().Contain("HOST:");
        appliedManifest.Should().Contain("PORT:");
        appliedManifest.Should().Contain("DATABASE:");
        appliedManifest.Should().Contain("USERNAME:");
        appliedManifest.Should().Contain("PASSWORD:");
    }
}
