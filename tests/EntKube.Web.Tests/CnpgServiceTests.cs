using EntKube.Web.Services.ClusterChanges;
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
    private readonly InterceptingTestDb.InterceptingFactory dbFactory;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8sFactory;

    /// <summary>Kept so a test can register a sink and count the dialogs one action raises.</summary>
    private readonly EntKube.Web.Services.ClusterChanges.ClusterChangeGate gate;
    private readonly CnpgService sut;

    public CnpgServiceTests()
    {
        // Mirrors production: contexts resolve cluster.Kubeconfig from the vault via the interceptor.
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        dbFactory = testDb.Factory;

        vaultService = testDb.CreateVaultService();
        k8sFactory = new Mock<IKubernetesClientFactory>();
        gate = (EntKube.Web.Services.ClusterChanges.ClusterChangeGate)TestServices.NewGate();
        // The real factory, over the real interceptor: the kubeconfig these tests seed into the
        // vault is resolved the way production resolves it, and then handed to the mock. So the
        // existing k8sFactory assertions keep working and additionally prove the delegation.
        sut = new CnpgService(dbFactory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(dbFactory, k8sFactory.Object),
            new EntKube.Web.Modules.Api.CatalogApi(dbFactory),
            new EntKube.Web.Modules.Api.DeliveryApi(dbFactory), gate);
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    // ──────── Helpers ────────

    /// <param name="slug">
    /// ⚠️ Distinct per call. <c>Tenants.Slug</c> is UNIQUE, so seeding a second tenant with the
    /// default collides — which is how the first draft of the pairing tests below failed, on the
    /// fixture rather than on the thing under test.
    /// </param>
    private async Task<(Tenant tenant, KubernetesCluster cluster, StorageLink storageLink)> SeedTenantWithClusterAsync(
        string slug = "testco")
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = $"TestCo-{slug}", Slug = slug };
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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

        // Act — create without backup configuration.

        CnpgCluster result = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "standalone-pg", "default", 1, "5Gi",
            storageLinkId: null, backupSchedule: null);

        // Assert

        result.StorageLinkId.Should().BeNull();
        result.BackupSchedule.Should().BeNull();
    }

    /// <summary>
    /// A CloudNativePG operator that is still installing does not count as installed.
    ///
    /// <para>Added because a mutant proved it was not covered: dropping the <c>Installed</c> check
    /// from this gate passed all twenty-four tests here. The consequence is not cosmetic — the
    /// operator's CRDs may not be registered yet, so a <c>Cluster</c> object applied now is either
    /// rejected by the API server or accepted and never reconciled, and the user is told their
    /// database was created.</para>
    /// </summary>
    [Fact]
    public async Task CreateClusterAsync_OperatorStillInstalling_ThrowsInvalidOperation()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "MidInstall", Slug = "mid-install" };
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
            Kubeconfig = "fake",
        };
        db.KubernetesClusters.Add(cluster);

        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "cloudnative-pg",
            ComponentType = "helm",
            Status = ComponentStatus.Installing,
            Namespace = "cnpg-system",
        });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "pg", "default", 1, "5Gi", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*CloudNativePG operator*not installed*");
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
            .ReturnsAsync(string.Empty);

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

    /// <summary>
    /// ⚠️ <b>"AndDatabase" was the only half this asserted.</b> It stubbed
    /// <c>DeleteManifestAsync</c> with all-<c>It.IsAny</c> matchers, never verified it, and
    /// checked one thing: that the row was gone. <c>DeleteClusterAsync</c> wraps every
    /// cluster-side delete in <c>catch (Exception) { }</c> and removes the row regardless — by
    /// design, so a cluster that never deployed is still deletable — so that assertion is reached
    /// whatever the Kubernetes leg does, <em>including not running at all</em>.
    ///
    /// <para>The stub was not even load-bearing: delete it and the loose mock returns a null
    /// <c>Task</c>, the <c>NullReferenceException</c> is swallowed by the same catch, and the test
    /// still passes having asked Kubernetes for nothing. What leaks is a live CNPG
    /// <c>Cluster</c> with its pods and PVCs, plus a barmancloud <c>ObjectStore</c> still writing
    /// to the tenant's bucket, while the UI reports the cluster deleted.</para>
    ///
    /// <para>So the deletes are verified by exact kind and name, the idiom this file already uses
    /// on the major-upgrade path. Names matter more than counts here: <c>--ignore-not-found</c>
    /// means a wrong kind or name silently no-ops and orphans the real resource rather than
    /// failing.</para>
    /// </summary>
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

        // The two resources the method unconditionally deletes, by the names it builds them from.
        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "clusters.postgresql.cnpg.io", "to-delete", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "the row disappearing while the CNPG Cluster keeps running is the orphan this test is named for");

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "objectstores.barmancloud.cnpg.io", "to-delete-object-store", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "a left-behind ObjectStore keeps writing backups to the tenant's bucket, and is billed for");

        // No BackupSchedule was seeded, so the third leg is guarded off. Asserting Times.Never
        // keeps the two branches honest: without it, a change that deleted a ScheduledBackup
        // unconditionally would pass here and fail in a cluster that has none.
        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "scheduledbackups.postgresql.cnpg.io", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The conditional third delete, which the test above cannot reach — its fixture seeds no
    /// <c>BackupSchedule</c>, so that branch was not weakly covered but <b>unexecuted</b>.
    /// </summary>
    [Fact]
    public async Task DeleteClusterAsync_WithABackupSchedule_AlsoDeletesTheScheduledBackup()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "to-delete-scheduled",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            BackupSchedule = "0 0 * * *",
            Status = CnpgClusterStatus.Running
        };

        db.CnpgClusters.Add(cnpg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await sut.DeleteClusterAsync(tenant.Id, cnpg.Id);

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "scheduledbackups.postgresql.cnpg.io", "to-delete-scheduled-scheduled", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "a ScheduledBackup outliving its cluster keeps firing backup jobs against a Cluster "
            + "that no longer exists");
    }

    /// <summary>
    /// The catch block is deliberate — "so the user isn't stuck with an un-deletable entry" — but
    /// nothing proved it was deliberate rather than accidental. A row that survives a Kubernetes
    /// failure would be a cluster nobody can remove from the UI.
    /// </summary>
    [Fact]
    public async Task DeleteClusterAsync_WhenKubernetesRefuses_StillRemovesTheRow()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "unreachable",
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
            .ThrowsAsync(new InvalidOperationException("API server unreachable"));

        await sut.DeleteClusterAsync(tenant.Id, cnpg.Id);

        using ApplicationDbContext verifyDb = dbFactory.CreateDbContext();
        (await verifyDb.CnpgClusters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == cnpg.Id)).Should().BeNull(
            "the local record must go even when the cluster leg fails, or an undeployed cluster "
            + "becomes permanently undeletable");
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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

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
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<CancellationToken>()))
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

    /// <summary>
    /// ⚠️ <b>Nothing looked at the DROP statement.</b> The SQL was stubbed with all-
    /// <c>It.IsAny</c> matchers and never verified, so the two assertions — the row is gone, no
    /// vault secrets remain — were reached whether or not any SQL was sent, and whether or not it
    /// named the right database. <c>DeleteDatabaseAsync</c> builds
    /// <c>DROP DATABASE IF EXISTS "{database.Name}"; DROP ROLE IF EXISTS "{database.Owner}"</c>
    /// inside <c>catch (InvalidOperationException) { }</c>, so a left-behind production database
    /// and role would not have failed this test.
    ///
    /// <para>⚠️ The sharper risk is not a missing DROP but a <b>misaimed</b> one:
    /// <c>cnpg.Name</c>, <c>cnpg.Namespace</c> and the primary secret name are all in scope two
    /// lines from the interpolation, so an edit that reached for the wrong one would drop a
    /// <em>sibling</em> database of the same tenant. The row lookup is tenant-scoped, so the blast
    /// radius stops at one tenant — but inside it this is data loss.</para>
    ///
    /// <para>So the call is captured and the <b>seeded values</b> are asserted, read back off the
    /// returned entity rather than written as literals. Deliberately NOT asserted: the statement
    /// text, the <c>IF EXISTS</c> clause, or the ordering — those would pin the implementation and
    /// fail on a harmless rewording. What is asserted is where it was routed and what it aimed at.
    /// </para>
    /// </summary>
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

        // Every SQL call, in order, with where it was aimed. CreateDatabaseAsync below makes one
        // of its own, so the delete's call is identified by position rather than by being alone.
        List<(string Cluster, string Namespace, string Sql)> sqlCalls = [];
        k8sFactory.Setup(f => f.ExecuteSqlAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, ChangeIntent, CancellationToken>(
                (clusterName, ns, sql, _, _, _) => sqlCalls.Add((clusterName, ns, sql)))
            .Returns(Task.CompletedTask);

        CnpgDatabase database = await sut.CreateDatabaseAsync(
            tenant.Id, cnpg.Id, "to-remove");

        int callsAfterCreate = sqlCalls.Count;
        callsAfterCreate.Should().BeGreaterThan(0,
            "the create path must have run SQL, or the capture below is measuring nothing");

        // Act

        await sut.DeleteDatabaseAsync(tenant.Id, cnpg.Id, database.Id);

        // Assert

        CnpgDatabase? deleted = await db.CnpgDatabases.FindAsync(database.Id);
        deleted.Should().BeNull();

        List<VaultSecret> remainingSecrets = await db.VaultSecrets
            .Where(s => s.CnpgDatabaseId == database.Id)
            .ToListAsync();
        remainingSecrets.Should().BeEmpty();

        // The drop actually happened, and was aimed at the seeded database.
        sqlCalls.Should().HaveCount(callsAfterCreate + 1,
            "the delete must send exactly one statement — none means a database left running "
            + "after the UI said it was gone");

        (string Cluster, string Namespace, string Sql) drop = sqlCalls[^1];

        drop.Cluster.Should().Be(cnpg.Name);
        drop.Namespace.Should().Be(cnpg.Namespace,
            "routed to the cluster that owns the database, not to whatever was last in scope");

        drop.Sql.Should().Contain(database.Name,
            "this assertion is the only thing between a delete request and a DROP aimed at a "
            + "sibling database of the same tenant");
        drop.Sql.Should().Contain(database.Owner,
            "the role outlives the database otherwise, and a re-create of the same name then "
            + "collides with an existing role");

        drop.Sql.Should().NotContain($"\"{cnpg.Name}\"",
            "the cluster name is in scope two lines from the interpolation; dropping it instead "
            + "of the database is the specific confusion this guards");
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

        // ⚠️ A SECOND TENANT'S CLUSTER, which is what makes this test about the word "Only".
        // Without a foreign row there is nothing for the predicate to exclude, so an UNFILTERED
        // query satisfies every assertion below and deleting
        // `.Where(c => c.TenantId == tenantId)` from CnpgService.GetClustersAsync changes no
        // result. Measured before this row existed: it did not.
        (Tenant stranger, KubernetesCluster strangerCluster, _) =
            await SeedTenantWithClusterAsync("otherco");

        db.CnpgClusters.Add(new CnpgCluster
        {
            Id = Guid.NewGuid(),
            TenantId = stranger.Id,
            KubernetesClusterId = strangerCluster.Id,
            Name = "not-yours",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running
        });

        await db.SaveChangesAsync();

        // Act

        List<CnpgCluster> results = await sut.GetClustersAsync(tenant.Id);

        // Assert

        results.Should().HaveCount(2);
        results.Should().Contain(c => c.Name == "cluster-1");
        results.Should().Contain(c => c.Name == "cluster-2");

        results.Should().NotContain(c => c.Name == "not-yours",
            "another tenant's CNPG cluster must not appear, which is the whole claim in this "
            + "test's name");
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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

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
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<CancellationToken>()))
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
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        List<(string Name, string Namespace, IReadOnlyDictionary<string, string?> Keys)> merged = [];
        k8sFactory.Setup(f => f.MergeSecretKeysAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string?>>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string name, string ns, IReadOnlyDictionary<string, string?> keys,
                       IReadOnlyDictionary<string, string>? _, string _, string? _,
                       CancellationToken _) => merged.Add((name, ns, keys)))
            .ReturnsAsync("patched");

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        CnpgDatabase database = await sut.CreateDatabaseAsync(tenant.Id, cnpg.Id, "syncdb");

        // Act — push the credentials to Kubernetes.

        await sut.SyncDatabaseCredentialsToK8sAsync(tenant.Id, cnpg.Id, database.Id);

        // Assert — the credential fields go in as named keys of the Secret, not as a whole
        // manifest: a database binding's Secret may be the one holding the app's own vault
        // secrets, and an apply would delete those. See SharedSecretKeysTests.

        (string secretName, string ns, IReadOnlyDictionary<string, string?> keys) =
            merged.Should().ContainSingle().Subject;

        secretName.Should().Be("sync-cluster-syncdb-credentials");
        ns.Should().Be("databases");
        keys.Keys.Should().Contain(["HOST", "PORT", "DATABASE", "USERNAME", "PASSWORD"]);
    }

    // ════════════════════════════════════════════════════════════════
    //  Monitoring
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateClusterAsync_EnablesThePodMonitorSoPrometheusScrapesTheDatabase()
    {
        // CNPG instances expose metrics on :9187, but the operator only creates a PodMonitor
        // when asked to. Without this the database monitoring panel queries cnpg_* series that
        // no scrape ever produced, and shows an empty card for a perfectly healthy database.
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        List<string> appliedManifests = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifests.Add(manifest))
            .ReturnsAsync(string.Empty);

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "metrics-pg", "databases", 1, "5Gi", storageLink.Id, null);

        string clusterManifest = appliedManifests.First(m => m.Contains("kind: Cluster"));
        clusterManifest.Should().Contain("  monitoring:\n    enablePodMonitor: true");
    }

    [Fact]
    public async Task RestoreAsync_EnablesThePodMonitorOnTheRestoredCluster()
    {
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

        List<string> appliedManifests = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifests.Add(manifest))
            .ReturnsAsync(string.Empty);

        await sut.RestoreAsync(tenant.Id, source.Id, "restored-metrics-pg",
            new DateTime(2026, 5, 17, 10, 30, 0, DateTimeKind.Utc));

        string clusterManifest = appliedManifests.First(m => m.Contains("restored-metrics-pg") && m.Contains("kind: Cluster"));
        clusterManifest.Should().Contain("enablePodMonitor: true");
    }

    [Fact]
    public async Task ReapplyClusterSpecAsync_PushesTheClusterResourceWithMonitoringEnabled()
    {
        // The recovery path for databases created before the PodMonitor opt-in existed: rebuild
        // the manifest from the stored settings and apply it, changing nothing else.
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster existing = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "legacy-pg",
            Namespace = "databases",
            PostgresVersion = "18",
            Instances = 2,
            StorageSize = "10Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running
        };
        db.CnpgClusters.Add(existing);
        await db.SaveChangesAsync();

        List<string> appliedManifests = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => appliedManifests.Add(manifest))
            .ReturnsAsync(string.Empty);

        await sut.ReapplyClusterSpecAsync(tenant.Id, existing.Id);

        appliedManifests.Should().ContainSingle();
        appliedManifests[0].Should().Contain("kind: Cluster");
        appliedManifests[0].Should().Contain("name: legacy-pg");
        appliedManifests[0].Should().Contain("enablePodMonitor: true");
        // Rebuilt from the same stored settings — the diff the operator acknowledges should be
        // the monitoring block, not a resize or a version change.
        appliedManifests[0].Should().Contain("instances: 2");
        appliedManifests[0].Should().Contain("size: 10Gi");
        appliedManifests[0].Should().Contain("postgresql:18");
    }

    [Fact]
    public async Task ReapplyClusterSpecAsync_RejectsAnUnknownCluster()
    {
        (Tenant tenant, KubernetesCluster _, StorageLink _) = await SeedTenantWithClusterAsync();

        Func<Task> act = () => sut.ReapplyClusterSpecAsync(tenant.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }
    // ──────── Tenancy of the binding pairing ────────

    /// <summary>
    /// A database binding joins a deployment to a database, and it is what syncs the database
    /// credentials into that deployment's namespace. <c>AddCnpgDatabaseBindingAsync</c> wrote the
    /// row from two caller-supplied ids with <b>no validation at all</b> — not a tenant, not even
    /// that either id existed.
    ///
    /// <para>⚠️ Both directions are tested separately, because a pairing scoped on one end still
    /// crosses from the other: our deployment to their database, and their deployment to our
    /// database. A one-half fix passes a test that only tries one side.</para>
    ///
    /// <para>The assertion is on the stored rows: a method that refuses and still writes is the
    /// failure that matters.</para>
    /// </summary>
    [Fact]
    public async Task A_database_binding_cannot_be_written_across_a_tenant_boundary()
    {
        (Tenant ours, KubernetesCluster ourCluster, _) = await SeedTenantWithClusterAsync();
        (Tenant theirs, KubernetesCluster theirCluster, _) = await SeedTenantWithClusterAsync("stranger");

        Guid ourDatabase = SeedDatabase(ourCluster, ours.Id, "ours-db");
        Guid theirDatabase = SeedDatabase(theirCluster, theirs.Id, "theirs-db");
        Guid ourDeployment = SeedDeployment(ours.Id, ourCluster);
        Guid theirDeployment = SeedDeployment(theirs.Id, theirCluster);
        await db.SaveChangesAsync();

        // Our deployment, their database.
        Func<Task> theirDb = () => sut.AddCnpgDatabaseBindingAsync(
            ours.Id, ourDeployment, theirDatabase, "creds");
        await theirDb.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        // Their deployment, our database — the same pairing from the other side.
        Func<Task> theirDeploy = () => sut.AddCnpgDatabaseBindingAsync(
            ours.Id, theirDeployment, ourDatabase, "creds");
        await theirDeploy.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to this tenant*");

        using ApplicationDbContext check = testDb.CreateContext();
        check.Set<DatabaseBinding>().Should().BeEmpty("neither pairing may be written");

        DatabaseBinding ok = await sut.AddCnpgDatabaseBindingAsync(
            ours.Id, ourDeployment, ourDatabase, "creds");
        ok.Should().NotBeNull("the legitimate pairing still works");
    }

    [Fact]
    public async Task A_stranger_cannot_list_or_remove_another_tenants_bindings()
    {
        (Tenant ours, KubernetesCluster ourCluster, _) = await SeedTenantWithClusterAsync();
        (Tenant theirs, _, _) = await SeedTenantWithClusterAsync("stranger");

        Guid database = SeedDatabase(ourCluster, ours.Id, "ours-db");
        Guid deployment = SeedDeployment(ours.Id, ourCluster);
        await db.SaveChangesAsync();

        DatabaseBinding binding = await sut.AddCnpgDatabaseBindingAsync(
            ours.Id, deployment, database, "creds");

        (await sut.GetDatabaseBindingsAsync(ours.Id, deployment)).Should().ContainSingle();
        (await sut.GetDatabaseBindingsAsync(theirs.Id, deployment)).Should().BeEmpty(
            "a binding row names the Kubernetes Secret the credentials land in");

        Func<Task> remove = () => sut.RemoveDatabaseBindingAsync(theirs.Id, binding.Id);
        await remove.Should().ThrowAsync<InvalidOperationException>();

        using ApplicationDbContext check = testDb.CreateContext();
        check.Set<DatabaseBinding>().Should().ContainSingle(b => b.Id == binding.Id);

        await sut.RemoveDatabaseBindingAsync(ours.Id, binding.Id);
        using ApplicationDbContext after = testDb.CreateContext();
        after.Set<DatabaseBinding>().Should().BeEmpty("the owner can remove it");
    }

    /// <summary>A CNPG database on a cluster, returned by id.</summary>
    private Guid SeedDatabase(KubernetesCluster cluster, Guid tenantId, string name)
    {
        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = cluster.Id,
            Name = $"{name}-cluster",
            Namespace = "databases",
            PostgresVersion = "16",
            StorageSize = "10Gi",
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(cnpg);

        CnpgDatabase database = new()
        {
            Id = Guid.NewGuid(),
            CnpgClusterId = cnpg.Id,
            Name = name,
            Owner = "app",
        };
        db.Set<CnpgDatabase>().Add(database);
        return database.Id;
    }

    /// <summary>An app deployment for a tenant, returned by id.</summary>
    private Guid SeedDeployment(Guid tenantId, KubernetesCluster cluster)
    {
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = $"C{customerId:N}" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "App" });

        AppDeployment deployment = new()
        {
            Id = Guid.NewGuid(),
            AppId = appId,
            ClusterId = cluster.Id,
            EnvironmentId = cluster.EnvironmentId,
            Name = "prod",
            Namespace = "app",
        };
        db.AppDeployments.Add(deployment);
        return deployment.Id;
    }

    // ════════════════════════════════════════════════════════════════
    //  A read must not write
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opening the detail panel for a Postgres cluster must not change the cluster.
    ///
    /// <para><b>Reported from production:</b> expanding a Postgres cluster raised an
    /// acknowledgment dialog, and the diff in it was a CNPG <c>Cluster</c> apply whose only
    /// difference was the <c>last-applied-configuration</c> annotation. This read was firing
    /// <c>ReconcileClusterManifestsAsync</c> — two applies, fire-and-forget — so that a setting
    /// changed in a newer release reached the cluster next time somebody looked at it.</para>
    ///
    /// <para>The comment above it claimed "kubectl apply is idempotent — a cheap no-op GET with
    /// no write". The gate does not care whether an apply would change anything: it asks before
    /// every one. And it was not a no-op either, because kubectl rewrites that annotation.</para>
    ///
    /// <para>So: nothing is applied, and the drift is reported instead.</para>
    /// </summary>
    [Fact]
    public async Task GetClusterDetailAsync_AppliesNothing()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster pg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "postgres",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "50Gi",
            StorageLinkId = storageLink.Id,
            BackupSchedule = "0 0 2 * * *",
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(pg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        k8sFactory.Setup(f => f.DiffManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntKube.Web.Services.Clusters.ManifestDiff(
                EntKube.Web.Services.Clusters.DiffOutcome.Differs, "- old\n+ new"));

        CnpgClusterDetail? detail = await sut.GetClusterDetailAsync(tenant.Id, pg.Id);

        detail.Should().NotBeNull();

        // The whole point. Asserted with Verify rather than on a captured list, because the old
        // call was fire-and-forget: a list assertion could pass simply by running before the
        // write it was meant to catch.
        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        k8sFactory.Verify(f => f.ApplyManifestInNamespaceAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        detail!.ConfigurationDrift.Should().Be(EntKube.Web.Services.Clusters.DiffOutcome.Differs);
        detail.ConfigurationDriftText.Should().Be("- old\n+ new");
    }

    /// <summary>
    /// And "could not tell" is not reported as "no difference" — the three-state outcome is
    /// carried through, because a cluster that has stopped answering is exactly when an operator
    /// wants to know that EntKube cannot vouch for its configuration.
    /// </summary>
    [Fact]
    public async Task GetClusterDetailAsync_WhenTheDiffCannotBeTaken_SaysUnknownNotMatches()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster pg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "postgres",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "50Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(pg);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("{}");

        k8sFactory.Setup(f => f.DiffManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));

        CnpgClusterDetail? detail = await sut.GetClusterDetailAsync(tenant.Id, pg.Id);

        detail!.ConfigurationDrift.Should()
            .Be(EntKube.Web.Services.Clusters.DiffOutcome.Unknown);
    }

    /// <summary>
    /// Re-applying is the deliberate half: it does apply, both documents, and it is an action
    /// the operator chose after reading the diff.
    /// </summary>
    [Fact]
    public async Task ReapplyClusterManifestsAsync_AppliesTheClusterAndItsSchedule()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster pg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "postgres",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "50Gi",
            StorageLinkId = storageLink.Id,
            BackupSchedule = "0 0 2 * * *",
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(pg);
        await db.SaveChangesAsync();

        List<string> applied = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .ReturnsAsync(string.Empty);

        KubernetesOperationResult<string> result =
            await sut.ReapplyClusterManifestsAsync(tenant.Id, pg.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        applied.Should().Contain(m => m.Contains("kind: Cluster"));
        applied.Should().Contain(m => m.Contains("kind: ScheduledBackup"));
    }

    [Fact]
    public async Task ReapplyClusterManifestsAsync_ForAnotherTenant_AppliesNothing()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster pg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "postgres",
            Namespace = "databases",
            PostgresVersion = "18",
            StorageSize = "50Gi",
            StorageLinkId = storageLink.Id,
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(pg);
        await db.SaveChangesAsync();

        KubernetesOperationResult<string> result =
            await sut.ReapplyClusterManifestsAsync(Guid.NewGuid(), pg.Id);

        result.IsSuccess.Should().BeFalse();
        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ════════════════════════════════════════════════════════════════
    //  One action, one acknowledgment
    // ════════════════════════════════════════════════════════════════

    private sealed class CountingSink : EntKube.Web.Services.ClusterChanges.IClusterChangeAckSink
    {
        public int Calls { get; private set; }

        public Task<EntKube.Web.Services.ClusterChanges.ClusterChangeDecision> RequestAsync(
            EntKube.Web.Services.ClusterChanges.PlannedClusterChange change,
            EntKube.Web.Services.ClusterChanges.ClusterChangeDiff diff,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(
                EntKube.Web.Services.ClusterChanges.ClusterChangeDecision.Acknowledged);
        }
    }

    /// <summary>
    /// Makes the mocked factory gate its applies the way the real one does. The gate lives inside
    /// the real <see cref="KubernetesClientFactory"/>, so a mocked seam never reaches it and a
    /// count would sit at zero however the service behaved.
    /// </summary>
    /// <summary>
    /// How many gated changes the action made. Asserted alongside the dialog count, because
    /// "one dialog" is trivially true of an action that makes one change — see the RabbitMQ
    /// version of this test, which passed with its fix removed for exactly that reason.
    /// </summary>
    private int gatedChanges;

    private void GateEveryApply()
    {
        // EnsureNamespaceAsync as well as the applies: it is a separate method on the mocked
        // factory, though the real one reaches the gate through its own apply.
        k8sFactory.Setup(f => f.EnsureNamespaceAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string ns, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(
                    new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                    {
                        Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                        Manifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}\n",
                        Kubeconfig = kubeconfig,
                    }, ct);
            });

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string manifest, string kubeconfig, CancellationToken ct) =>
            {
                gatedChanges++;
                await gate.AcknowledgeAsync(
                    new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
                    {
                        Verb = EntKube.Web.Services.ClusterChanges.ChangeVerb.Apply,
                        Manifest = manifest,
                        Kubeconfig = kubeconfig,
                    }, ct);
                return string.Empty;
            });
    }

    /// <summary>
    /// Creating a Postgres cluster applies the S3 credentials Secret, the Cluster CR and — when a
    /// schedule is configured — the ScheduledBackup. One decision, one dialog.
    /// </summary>
    [Fact]
    public async Task CreatingAClusterAsksTheOperatorExactlyOnce()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryApply();

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "pg-ack", "databases", 1, "5Gi", storageLink.Id, null);

        gatedChanges.Should().BeGreaterThan(1, "the action must still make several gated changes");
        sink.Calls.Should().Be(1, "one action, one dialog");
    }

    /// <summary>
    /// And the window closes with the action, so a second cluster asks again.
    /// </summary>
    [Fact]
    public async Task CreatingASecondClusterAsksAgain()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CountingSink sink = new();
        using IDisposable registration = gate.RegisterSink(sink);
        GateEveryApply();

        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "pg-one", "databases", 1, "5Gi", storageLink.Id, null);
        await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "pg-two", "databases", 1, "5Gi", storageLink.Id, null);

        sink.Calls.Should().Be(2, "each action is its own decision");
    }

    // ════════════════════════════════════════════════════════════════
    //  The size is the cluster's, not the record's
    // ════════════════════════════════════════════════════════════════

    // Reported from production: a database filled its 50Gi volumes, they were grown to 300Gi
    // outside EntKube, and EntKube's stored 50Gi then made every manifest-building action ask
    // CNPG to shrink the cluster — which its webhook refuses.

    private async Task<(Tenant tenant, CnpgCluster pg)> SeedRecordedAt50GiAsync()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        CnpgCluster pg = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "postgres",
            Namespace = "databases",
            PostgresVersion = "18",
            Instances = 2,
            StorageSize = "50Gi",
            StorageLinkId = storageLink.Id,
            BackupSchedule = "0 0 2 * * *",
            Status = CnpgClusterStatus.Running,
        };
        db.CnpgClusters.Add(pg);
        await db.SaveChangesAsync();

        return (tenant, pg);
    }

    /// <summary>The cluster as it really is: 300Gi in the spec and on both claims.</summary>
    private void LiveClusterIs300Gi(bool allowVolumeExpansion = true, Func<bool>? clusterExists = null)
    {
        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string resource, string _, string _, string _, CancellationToken _) =>
            {
                if (resource == "cluster.postgresql.cnpg.io/postgres")
                {
                    return clusterExists is null || clusterExists()
                        ? """{"spec":{"storage":{"size":"300Gi","storageClass":"csi-cinder"}},"status":{}}"""
                        : throw new InvalidOperationException("NotFound");
                }

                // CNPG's claims are owned by the Cluster, so deleting it garbage-collects them.
                if (resource == "persistentvolumeclaims" && clusterExists is not null && !clusterExists())
                {
                    return """{"items":[]}""";
                }

                return resource switch
                {
                    "persistentvolumeclaims" => """
                        {"items":[
                          {"metadata":{"name":"postgres-1","labels":{"cnpg.io/pvcRole":"PG_DATA"}},
                           "spec":{"resources":{"requests":{"storage":"300Gi"}},"volumeName":"pv-1"},
                           "status":{"capacity":{"storage":"300Gi"}}},
                          {"metadata":{"name":"postgres-2","labels":{"cnpg.io/pvcRole":"PG_DATA"}},
                           "spec":{"resources":{"requests":{"storage":"300Gi"}},"volumeName":"pv-2"},
                           "status":{"capacity":{"storage":"300Gi"}}}]}
                        """,
                    "persistentvolume/pv-1" or "persistentvolume/pv-2" =>
                        """{"spec":{"capacity":{"storage":"300Gi"}}}""",
                    "storageclass/csi-cinder" => allowVolumeExpansion
                        ? """{"metadata":{"name":"csi-cinder"},"allowVolumeExpansion":true}"""
                        : """{"metadata":{"name":"csi-cinder"}}""",
                    _ => "{}",
                };
            });
    }

    private List<string> CaptureApplies()
    {
        List<string> applied = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .ReturnsAsync(string.Empty);
        return applied;
    }

    private async Task<string> StoredSizeAsync(Guid id)
    {
        using ApplicationDbContext check = testDb.CreateContext();
        return (await check.CnpgClusters.AsNoTracking().SingleAsync(c => c.Id == id)).StorageSize;
    }

    [Fact]
    public async Task ReapplyClusterManifestsAsync_CarriesTheLiveSize_NotTheStaleOne()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();
        List<string> applied = CaptureApplies();

        KubernetesOperationResult<string> result = await sut.ReapplyClusterManifestsAsync(tenant.Id, pg.Id);

        result.IsSuccess.Should().BeTrue(result.Error);
        string clusterManifest = applied.Single(m => m.Contains("kind: Cluster"));
        clusterManifest.Should().Contain("size: 300Gi").And.NotContain("size: 50Gi");
        (await StoredSizeAsync(pg.Id)).Should().Be("300Gi", "the correction is kept");
    }

    [Fact]
    public async Task BackupAsync_ReappliesAtTheLiveSize()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();
        List<string> applied = CaptureApplies();

        await sut.BackupAsync(tenant.Id, pg.Id);

        applied.Where(m => m.Contains("kind: Cluster"))
            .Should().ContainSingle().Which.Should().Contain("size: 300Gi");
    }

    [Fact]
    public async Task UpgradeClusterAsync_DoesNotAskToShrink()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();
        List<string> applied = CaptureApplies();

        await sut.UpgradeClusterAsync(tenant.Id, pg.Id, "18.1");

        applied.Single().Should().Contain("size: 300Gi").And.Contain("postgresql:18.1");
    }

    /// <summary>
    /// The page shows the live size and the drift check compares against it — otherwise the
    /// panel reported "differs" for a shrink nobody wanted.
    /// </summary>
    [Fact]
    public async Task GetClusterDetailAsync_ShowsAndRecordsTheLiveSize()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();

        string? diffed = null;
        k8sFactory.Setup(f => f.DiffManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((m, _, _, _) => diffed = m)
            .ReturnsAsync(new EntKube.Web.Services.Clusters.ManifestDiff(
                EntKube.Web.Services.Clusters.DiffOutcome.Matches, null));

        CnpgClusterDetail? detail = await sut.GetClusterDetailAsync(tenant.Id, pg.Id);

        detail!.Storage.Should().NotBeNull();
        detail.Storage!.EffectiveSize.Should().Be("300Gi");
        detail.Storage.Volumes.Should().HaveCount(2)
            .And.OnlyContain(v => v.VolumeCapacity == "300Gi");
        detail.Storage.AllowVolumeExpansion.Should().BeTrue();
        detail.Cluster.StorageSize.Should().Be("300Gi");
        diffed.Should().Contain("size: 300Gi");
        (await StoredSizeAsync(pg.Id)).Should().Be("300Gi");

        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "correcting EntKube's own record is not a change to the cluster");
    }

    /// <summary>
    /// The worst case: an in-place restore deletes the Cluster (and its claims) and recreates it
    /// from the stored size. The size must be read BEFORE the delete — afterwards there is
    /// nothing left to read, and 300GB does not restore onto 50Gi.
    /// </summary>
    [Fact]
    public async Task RestoreInPlaceAsync_ReadsTheSizeBeforeDeletingTheCluster()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();

        bool deleted = false;
        LiveClusterIs300Gi(clusterExists: () => !deleted);
        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => deleted = true)
            .Returns(Task.CompletedTask);
        List<string> applied = CaptureApplies();

        await sut.RestoreInPlaceAsync(tenant.Id, pg.Id, DateTime.UtcNow);

        deleted.Should().BeTrue();
        applied.Single(m => m.Contains("kind: Cluster")).Should().Contain("size: 300Gi");
    }

    [Fact]
    public async Task RestoreAsync_SizesTheNewClusterFromTheLiveSource()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();
        List<string> applied = CaptureApplies();

        CnpgCluster restored = await sut.RestoreAsync(tenant.Id, pg.Id, "postgres-restored");

        restored.StorageSize.Should().Be("300Gi");
        applied.Single(m => m.Contains("kind: Cluster")).Should().Contain("size: 300Gi");
    }

    /// <summary>When the cluster cannot be read, the stored value stands — no guessing.</summary>
    [Fact]
    public async Task AnUnreadableCluster_KeepsTheStoredSize()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));
        List<string> applied = CaptureApplies();

        await sut.ReapplyClusterManifestsAsync(tenant.Id, pg.Id);

        applied.Single(m => m.Contains("kind: Cluster")).Should().Contain("size: 50Gi");
        (await StoredSizeAsync(pg.Id)).Should().Be("50Gi");
    }

    // ──────── Expanding storage ────────

    [Fact]
    public async Task ExpandStorageAsync_PatchesOnlyTheSize_AndRecordsIt()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();

        string? patch = null;
        k8sFactory.Setup(f => f.PatchJsonAsync(
                "cluster.postgresql.cnpg.io", "postgres", "databases",
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, CancellationToken>((_, _, _, p, _, _) => patch = p)
            .Returns(Task.CompletedTask);

        await sut.ExpandStorageAsync(tenant.Id, pg.Id, "400Gi");

        patch.Should().Be("""{"spec":{"storage":{"size":"400Gi"}}}""");
        (await StoredSizeAsync(pg.Id)).Should().Be("400Gi");
        k8sFactory.Verify(f => f.ApplyManifestAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "a registered cluster must not have EntKube's whole manifest written over it to grow a disk");
    }

    /// <summary>
    /// "Larger" is judged against the LIVE size: 200Gi is larger than the stale 50Gi record but
    /// smaller than the 300Gi the cluster has, and CNPG would refuse it.
    /// </summary>
    [Theory]
    [InlineData("200Gi")]
    [InlineData("300Gi")]
    [InlineData("300G")]
    public async Task ExpandStorageAsync_RefusesAnythingNotLargerThanTheLiveSize(string size)
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();

        Func<Task> expand = () => sut.ExpandStorageAsync(tenant.Id, pg.Id, size);

        (await expand.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*can only grow*300Gi*");
        k8sFactory.Verify(f => f.PatchJsonAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpandStorageAsync_RefusesWhenTheStorageClassCannotExpand()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi(allowVolumeExpansion: false);

        Func<Task> expand = () => sut.ExpandStorageAsync(tenant.Id, pg.Id, "400Gi");

        (await expand.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*csi-cinder*does not allow volume expansion*");
        k8sFactory.Verify(f => f.PatchJsonAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExpandStorageAsync_RefusesToResizeBlind()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("apiserver unreachable"));

        Func<Task> expand = () => sut.ExpandStorageAsync(tenant.Id, pg.Id, "400Gi");

        (await expand.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*current size is unknown*");
        (await StoredSizeAsync(pg.Id)).Should().Be("50Gi");
    }

    [Fact]
    public async Task ExpandStorageAsync_RefusesSomethingThatIsNotASize()
    {
        (Tenant tenant, CnpgCluster pg) = await SeedRecordedAt50GiAsync();

        Func<Task> expand = () => sut.ExpandStorageAsync(tenant.Id, pg.Id, "400Gi\"}}");

        (await expand.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*is not a storage size*");
    }

    [Fact]
    public async Task ExpandStorageAsync_ForAnotherTenant_ChangesNothing()
    {
        (Tenant _, CnpgCluster pg) = await SeedRecordedAt50GiAsync();
        LiveClusterIs300Gi();

        Func<Task> expand = () => sut.ExpandStorageAsync(Guid.NewGuid(), pg.Id, "400Gi");

        (await expand.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*not found*");
        k8sFactory.Verify(f => f.PatchJsonAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
