using EntKube.Web.Services.ClusterChanges;
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
        // Real factory over the intercepting test database: the vault-seeded kubeconfig resolves
        // as production resolves it, then reaches the same mock — the existing assertions hold.
        sut = new MongoService(
            testDb.Factory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8sFactory.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory),
            new EntKube.Web.Modules.Api.DeliveryApi(testDb.Factory),
            // A real gate, as production registers one. These tests mock the seam, so nothing
            // reaches it — but a service that opens an operation needs one to open.
            new EntKube.Web.Services.ClusterChanges.ClusterChangeGate(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<EntKube.Web.Services.ClusterChanges.ClusterChangeGate>.Instance));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
    }

    // ──────── Helpers ────────

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
            .ReturnsAsync(string.Empty);

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
            .ReturnsAsync(string.Empty);

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

    /// <summary>
    /// The operator check moved from a SQL predicate to <c>ICatalogApi</c> plus the same predicate
    /// in memory. This pins the part that a shorter conversion would have lost: <b>the aliases are
    /// not symmetrical across the three columns.</b> The two operator names are accepted as
    /// <c>Name</c> or <c>ReleaseName</c>, but the chart is only ever <c>community-operator</c> —
    /// so a contract method matching every key against every column would answer "installed" for
    /// a component this has always rejected, and MongoDB clusters would be created on a cluster
    /// with no operator to reconcile them.
    /// </summary>
    [Fact]
    public async Task CreateClusterAsync_DoesNotAcceptAnOperatorNameAsAChartName()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Chartish", Slug = "chartish" };
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

        // An installed component whose CHART is named like the operator, which the original
        // predicate never matched on: HelmChartName is only ever compared to "community-operator".
        db.ClusterComponents.Add(new ClusterComponent
        {
            Id = Guid.NewGuid(),
            ClusterId = cluster.Id,
            Name = "something-else",
            ComponentType = "helm",
            HelmChartName = "mongodb-operator",
            Status = ComponentStatus.Installed,
            Namespace = "elsewhere",
        });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "mongo", "default", 1, "5Gi", null, null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MongoDB Community Operator*not installed*");
    }

    /// <summary>
    /// The other half of the same asymmetry: the chart name the predicate does accept.
    /// </summary>
    [Fact]
    public async Task CreateClusterAsync_AcceptsTheOperatorByItsChartName()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "ByChart", Slug = "by-chart" };
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
            Name = "renamed-by-the-operator",
            ComponentType = "helm",
            HelmChartName = "community-operator",
            Status = ComponentStatus.Installed,
            Namespace = "mongodb-operator",
        });
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "mongo", "default", 1, "5Gi", null, null);

        // It fails at the NEXT step — resolving the cluster credential, which this test
        // deliberately does not seed — and that is the assertion: getting as far as the kubeconfig
        // means the operator check accepted a component installed under the chart name alone.
        // Asserting the later message rather than seeding a vault keeps the test about one thing.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("kubeconfig")
            .And.NotContain("Operator");
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

    /// <summary>
    /// ⚠️ <b>"AndKubernetes" was unasserted.</b> Both mocks were stubbed with all-
    /// <c>It.IsAny</c> matchers, neither was verified, and the one assertion was that the row had
    /// gone. <c>DeleteClusterAsync</c> wraps every cluster-side delete in
    /// <c>catch (Exception) { }</c> and removes the row regardless — deliberately, so an
    /// undeployed cluster stays deletable — so that assertion held with the Kubernetes leg
    /// removed entirely.
    ///
    /// <para>What leaks is the <c>MongoDBCommunity</c> CR, and with it the StatefulSet, its pods
    /// and its PVCs, running in the tenant's namespace after the UI has reported the cluster
    /// deleted. The vault copy of the admin password <em>is</em> removed, so the surviving
    /// database is also one nobody holds credentials for.</para>
    ///
    /// <para>⚠️ The stale <c>{name}-admin-password</c> Secret is NOT the hazard it looks like: a
    /// same-name recreate applies a fresh secret before the cluster manifest, and
    /// <c>kubectl apply</c> replaces the key, so it is silently overwritten. The CR delete is the
    /// one that matters. Checked rather than assumed, because the obvious reading is the wrong
    /// one.</para>
    ///
    /// <para>Verified on <see cref="IKubernetesClientFactory"/>, not <c>IClusterClient</c>: the
    /// product calls the latter, but this fixture mocks the factory the former forwards to.</para>
    /// </summary>
    [Fact]
    public async Task DeleteClusterAsync_RemovesFromDbAndKubernetes()
    {
        // Arrange — create a cluster first.

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

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

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "mongodbcommunity.mongodbcommunity.mongodb.com", "delete-me", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "without this the StatefulSet, its pods and its PVCs keep running after the row is "
            + "gone — a live database nobody can see and nobody holds the password for");

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "secret", "delete-me-admin-password", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);

        // No schedule was passed to CreateClusterAsync, so the CronJob branch is guarded off.
        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "cronjob", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// The conditional CronJob delete, which the test above cannot reach: its fixture passes no
    /// schedule, so that branch was unexecuted rather than weakly covered. A schedule requires a
    /// StorageLink alongside it — <c>CreateClusterAsync</c> refuses one without the other.
    /// </summary>
    [Fact]
    public async Task DeleteClusterAsync_WithABackupSchedule_AlsoDeletesTheCronJob()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        k8sFactory.Setup(f => f.DeleteManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        MongoCluster created = await sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "scheduled-me", "databases", 1, "5Gi", storageLink.Id, "0 2 * * *");

        await sut.DeleteClusterAsync(tenant.Id, created.Id);

        k8sFactory.Verify(f => f.DeleteManifestAsync(
            "cronjob", "scheduled-me-scheduled-backup", "databases",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once,
            "a CronJob outliving its cluster keeps firing mongodump against a StatefulSet that is "
            + "no longer there, and keeps failing silently");
    }

    // ──────── GetClustersAsync ────────

    [Fact]
    public async Task GetClustersAsync_ReturnsOnlyTenantClusters()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        await sut.CreateClusterAsync(tenant.Id, cluster.Id, "mongo-1", "ns1", 3, "10Gi", null, null);
        await sut.CreateClusterAsync(tenant.Id, cluster.Id, "mongo-2", "ns2", 1, "5Gi", null, null);

        // ⚠️ A SECOND TENANT'S CLUSTER, which is what makes this test about the word "Only".
        // Without a foreign row there is nothing for the predicate to exclude, so an UNFILTERED
        // query satisfies both assertions below and deleting
        // `.Where(c => c.TenantId == tenantId)` from MongoService.GetClustersAsync changes no
        // result. Measured before this row existed: it did not.
        (Tenant stranger, KubernetesCluster strangerCluster, _) =
            await SeedTenantWithClusterAsync("otherco");

        await sut.CreateClusterAsync(
            stranger.Id, strangerCluster.Id, "not-yours", "ns3", 1, "5Gi", null, null);

        // Act

        List<MongoCluster> result = await sut.GetClustersAsync(tenant.Id);

        // Assert

        result.Should().HaveCount(2);
        result.Select(c => c.Name).Should().Contain("mongo-1").And.Contain("mongo-2");

        result.Select(c => c.Name).Should().NotContain("not-yours",
            "another tenant's MongoDB cluster must not appear, which is the whole claim in this "
            + "test's name");
    }

    // ──────── CreateDatabaseAsync ────────

    [Fact]
    public async Task CreateDatabaseAsync_CreatesDbRecordAndExecutesMongo()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, _) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        // CreateDatabaseAsync runs a mongosh script and checks stdout for the
        // ENTK_SUCCESS sentinel before marking the database Ready.
        k8sFactory.Setup(f => f.ExecuteMongoWithOutputAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            It.IsAny<string>(), It.IsAny<ChangeIntent>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────── BackupAsync ────────

    [Fact]
    public async Task BackupAsync_CreatesBackupRecordAndAppliesManifest()
    {
        // Arrange

        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

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

    // ──────── Scheduled backups ────────

    [Fact]
    public async Task UpdateBackupSettingsAsync_RefusesAScheduleWithNowhereToUpload()
    {
        // A schedule with no bucket produced no CronJob, yet was stored and displayed — the
        // cluster showed a backup schedule that could never fire.
        (Tenant tenant, KubernetesCluster cluster, StorageLink _) = await SeedTenantWithClusterAsync();

        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "no-bucket",
            Namespace = "databases",
            Members = 3,
            StorageSize = "10Gi",
            MongoVersion = "8.0.4",
        };
        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        Func<Task> act = () => sut.UpdateBackupSettingsAsync(tenant.Id, mongo.Id, "0 2 * * *", 30, 20);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*backup storage*");

        MongoCluster reloaded = await db.MongoClusters.AsNoTracking().FirstAsync(c => c.Id == mongo.Id);
        reloaded.BackupSchedule.Should().BeNull("a schedule that cannot run must not be recorded");
    }

    [Fact]
    public async Task CreateClusterAsync_RefusesAScheduleWithoutBackupStorage()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink _) = await SeedTenantWithClusterAsync();

        Func<Task> act = () => sut.CreateClusterAsync(
            tenant.Id, cluster.Id, "no-bucket", "databases", 3, "10Gi",
            storageLinkId: null, backupSchedule: "0 2 * * *");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*backup storage*");
    }

    [Fact]
    public async Task UpdateBackupSettingsAsync_AppliesACronJobWithAFiveFieldSchedule()
    {
        // Kubernetes CronJobs reject the 6-field (seconds) form that CNPG accepts, so the
        // leading field is dropped — a schedule copied from a database backup still works.
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "sched-mongo",
            Namespace = "databases",
            Members = 3,
            StorageSize = "10Gi",
            MongoVersion = "8.0.4",
            StorageLinkId = storageLink.Id,
        };
        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        List<string> applied = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((manifest, _, _) => applied.Add(manifest))
            .ReturnsAsync(string.Empty);

        await sut.UpdateBackupSettingsAsync(tenant.Id, mongo.Id, "0 0 2 * * *", 30, 20);

        string cronJob = applied.First(m => m.Contains("kind: CronJob"));
        cronJob.Should().Contain("schedule: \"0 2 * * *\"");
        cronJob.Should().Contain("name: sched-mongo-scheduled-backup");
        // Finished Jobs are the only record of a run until someone opens the page, so they must
        // outlive a nightly schedule by more than a day.
        cronJob.Should().Contain("ttlSecondsAfterFinished: 604800");
        // The S3 credentials Secret must exist too, or every run fails to start its uploader.
        applied.Should().Contain(m => m.Contains("kind: Secret") && m.Contains("sched-mongo-s3-credentials"));
    }

    [Fact]
    public async Task GetScheduledBackupStatusAsync_ReportsTheCronJobItFinds()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "sched-mongo",
            Namespace = "databases",
            Members = 3,
            StorageSize = "10Gi",
            MongoVersion = "8.0.4",
            StorageLinkId = storageLink.Id,
            BackupSchedule = "0 2 * * *",
        };
        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.GetJsonAsync(
                "cronjob", "databases", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[{"metadata":{"name":"sched-mongo-scheduled-backup"},
                  "spec":{"schedule":"0 2 * * *"},
                  "status":{"lastScheduleTime":"2026-08-17T02:00:00Z",
                            "lastSuccessfulTime":"2026-08-17T02:04:00Z"}}]}
                """);

        MongoScheduledBackupStatus status = await sut.GetScheduledBackupStatusAsync(tenant.Id, mongo.Id);

        status.Exists.Should().BeTrue();
        status.AppliedSchedule.Should().Be("0 2 * * *");
        status.LastScheduleTime.Should().Be(new DateTime(2026, 8, 17, 2, 0, 0, DateTimeKind.Utc));
        status.LastSuccessfulTime.Should().NotBeNull();
        status.Problem.Should().BeNull();
        status.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task GetScheduledBackupStatusAsync_SaysSoWhenTheCronJobIsMissingOrSuspended()
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "sched-mongo",
            Namespace = "databases",
            Members = 3,
            StorageSize = "10Gi",
            MongoVersion = "8.0.4",
            StorageLinkId = storageLink.Id,
            BackupSchedule = "0 2 * * *",
        };
        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        k8sFactory.Setup(f => f.GetJsonAsync(
                "cronjob", "databases", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""{"items":[]}""");

        MongoScheduledBackupStatus missing = await sut.GetScheduledBackupStatusAsync(tenant.Id, mongo.Id);
        missing.Exists.Should().BeFalse();
        missing.Problem.Should().Contain("No CronJob");

        k8sFactory.Setup(f => f.GetJsonAsync(
                "cronjob", "databases", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("""
                {"items":[{"metadata":{"name":"sched-mongo-scheduled-backup"},
                  "spec":{"schedule":"0 2 * * *","suspend":true},"status":{}}]}
                """);

        MongoScheduledBackupStatus suspended = await sut.GetScheduledBackupStatusAsync(tenant.Id, mongo.Id);
        suspended.Exists.Should().BeTrue();
        suspended.Suspended.Should().BeTrue();
        suspended.Problem.Should().Contain("suspended");
        suspended.IsHealthy.Should().BeFalse();
    }

    // ──────── Live storage ────────
    //
    // Reported from production: volumes grown after a disk filled, while EntKube kept the size it
    // stored at create time. The StatefulSet was created with a 50Gi data-volume template, which
    // Kubernetes never lets change, and its three data claims were grown to 300Gi directly.

    private async Task<(Tenant Tenant, MongoCluster Mongo)> SeedRecordedAsync(string storedSize)
    {
        (Tenant tenant, KubernetesCluster cluster, StorageLink storageLink) = await SeedTenantWithClusterAsync();

        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            KubernetesClusterId = cluster.Id,
            Name = "my-mongo",
            Namespace = "databases",
            Members = 3,
            StorageSize = storedSize,
            MongoVersion = "7.0",
            StorageLinkId = storageLink.Id,
            Status = MongoClusterStatus.Running,
        };
        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        return (tenant, mongo);
    }

    /// <summary>
    /// A 50Gi template, three data claims at 300Gi, three small logs claims, and a neighbouring
    /// cluster whose claim names start the same way.
    /// </summary>
    private void LiveClaimsAre300GiOnA50GiTemplate(bool allowVolumeExpansion = true, bool reachable = true)
    {
        k8sFactory.Setup(f => f.GetJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string resource, string _, string _, string _, CancellationToken _) =>
            {
                if (!reachable)
                {
                    throw new InvalidOperationException("Unable to connect to the server");
                }

                static string Claim(string name, string size) => System.Text.Json.JsonSerializer.Serialize(new
                {
                    metadata = new { name },
                    spec = new
                    {
                        resources = new { requests = new { storage = size } },
                        volumeName = $"pv-{name}",
                        storageClassName = "csi-cinder",
                    },
                    status = new { capacity = new { storage = size } },
                });

                return resource switch
                {
                    "statefulset/my-mongo" => """
                        {"spec":{"volumeClaimTemplates":[
                          {"metadata":{"name":"data-volume"},
                           "spec":{"resources":{"requests":{"storage":"50Gi"}},"storageClassName":"csi-cinder"}},
                          {"metadata":{"name":"logs-volume"},
                           "spec":{"resources":{"requests":{"storage":"2G"}}}}]}}
                        """,
                    "persistentvolumeclaims" => "{\"items\":["
                        + string.Join(",",
                            Claim("data-volume-my-mongo-0", "300Gi"),
                            Claim("data-volume-my-mongo-1", "300Gi"),
                            Claim("data-volume-my-mongo-2", "300Gi"),
                            Claim("logs-volume-my-mongo-0", "2G"),
                            Claim("data-volume-my-mongo-archive-0", "1Ti"))
                        + "]}",
                    "storageclass/csi-cinder" => allowVolumeExpansion
                        ? """{"metadata":{"name":"csi-cinder"},"allowVolumeExpansion":true}"""
                        : """{"metadata":{"name":"csi-cinder"}}""",
                    _ when resource.StartsWith("persistentvolume/", StringComparison.Ordinal) =>
                        """{"spec":{"capacity":{"storage":"300Gi"}}}""",
                    _ => "{}",
                };
            });
    }

    private List<string> CaptureMongoApplies()
    {
        List<string> applied = [];
        k8sFactory.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => applied.Add(m))
            .ReturnsAsync(string.Empty);
        return applied;
    }

    private List<(string Resource, string Name, string Patch)> CaptureMongoPatches()
    {
        List<(string, string, string)> patched = [];
        k8sFactory.Setup(f => f.PatchJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, CancellationToken>(
                (resource, name, _, patch, _, _) => patched.Add((resource, name, patch)))
            .Returns(Task.CompletedTask);
        return patched;
    }

    private async Task<MongoCluster> StoredAsync(Guid id)
    {
        using ApplicationDbContext check = testDb.CreateContext();
        return await check.MongoClusters.AsNoTracking().FirstAsync(c => c.Id == id);
    }

    /// <summary>
    /// The StatefulSet's claim templates are immutable. A manifest carrying any other size breaks
    /// every later StatefulSet update. That holds when the stored size is stale (50Gi against
    /// 300Gi claims) and when an earlier claim resize raised it (300Gi against a 50Gi template).
    /// </summary>
    [Theory]
    [InlineData("50Gi")]
    [InlineData("300Gi")]
    public async Task UpgradeClusterAsync_KeepsTheStatefulSetsTemplateSize(string storedSize)
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync(storedSize);
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();

        await sut.UpgradeClusterAsync(tenant.Id, mongo.Id, "8.0");

        applied.Single(m => m.Contains("kind: MongoDBCommunity"))
            .Should().Contain("storage: 50Gi").And.NotContain("storage: 300Gi");
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("300Gi", "the record says what the data has");
    }

    [Fact]
    public async Task ResizeClusterAsync_CpuOnly_KeepsTheStatefulSetsTemplateSize()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("300Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();

        await sut.ResizeClusterAsync(tenant.Id, mongo.Id, null, "500m", null, null, null);

        applied.Should().ContainSingle().Which.Should().Contain("storage: 50Gi").And.NotContain("storage: 300Gi");
    }

    [Fact]
    public async Task ResizeClusterAsync_GrowsEveryLiveDataClaim_AndNothingElse()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        await sut.ResizeClusterAsync(tenant.Id, mongo.Id, "400Gi", null, null, null, null);

        patched.Select(p => p.Name).Should().BeEquivalentTo(
            ["data-volume-my-mongo-0", "data-volume-my-mongo-1", "data-volume-my-mongo-2"],
            "the logs volume and the neighbouring my-mongo-archive cluster are not this cluster's data");
        patched.Should().OnlyContain(p => p.Resource == "pvc"
            && p.Patch == """{"spec":{"resources":{"requests":{"storage":"400Gi"}}}}""");
        applied.Should().BeEmpty("growing storage re-applies nothing");
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("400Gi");
    }

    /// <summary>
    /// 200Gi is larger than the stale 50Gi record, so the old text comparison let it through and
    /// Kubernetes refused to shrink the claims mid-way. It is judged against the live 300Gi now,
    /// and refused before the CPU change that came with it is written or applied.
    /// </summary>
    [Fact]
    public async Task ResizeClusterAsync_RefusesToShrinkAgainstTheLiveSize_BeforeWritingAnything()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResizeClusterAsync(tenant.Id, mongo.Id, "200Gi", "500m", null, null, null));

        ex.Message.Should().Contain("can only grow").And.Contain("300Gi");
        applied.Should().BeEmpty();
        patched.Should().BeEmpty();
        (await StoredAsync(mongo.Id)).CpuRequest.Should().BeNull("the refusal came before the CPU change");
    }

    [Fact]
    public async Task ResizeClusterAsync_ToTheLiveSize_ChangesNothing()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        await sut.ResizeClusterAsync(tenant.Id, mongo.Id, "300Gi", null, null, null, null);

        applied.Should().BeEmpty();
        patched.Should().BeEmpty();
    }

    [Fact]
    public async Task ResizeClusterAsync_RefusesWhenTheStorageClassCannotExpand()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("300Gi");
        LiveClaimsAre300GiOnA50GiTemplate(allowVolumeExpansion: false);
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResizeClusterAsync(tenant.Id, mongo.Id, "400Gi", null, null, null, null));

        ex.Message.Should().Contain("does not allow volume expansion");
        patched.Should().BeEmpty();
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("300Gi");
    }

    [Fact]
    public async Task ResizeClusterAsync_RefusesToGrowBlind()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate(reachable: false);
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResizeClusterAsync(tenant.Id, mongo.Id, "100Gi", null, null, null, null));

        ex.Message.Should().Contain("Could not read the data volumes");
        patched.Should().BeEmpty();
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("50Gi", "an unread size is no reason to guess");
    }

    [Fact]
    public async Task ResizeClusterAsync_RefusesSomethingThatIsNotASize()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResizeClusterAsync(tenant.Id, mongo.Id, "400Gi\"}}", null, null, null, null));

        ex.Message.Should().Contain("is not a storage size");
        patched.Should().BeEmpty();
    }

    [Fact]
    public async Task GetClusterDetailAsync_ShowsAndRecordsTheLiveSize_WithoutChangingTheCluster()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();
        List<(string Resource, string Name, string Patch)> patched = CaptureMongoPatches();

        MongoClusterDetail? detail = await sut.GetClusterDetailAsync(tenant.Id, mongo.Id);

        detail!.Storage.Should().NotBeNull();
        detail.Storage!.EffectiveSize.Should().Be("300Gi");
        detail.Storage.TemplateSize.Should().Be("50Gi");
        detail.Storage.TemplateSource.Should().Be("StatefulSet");
        detail.Storage.AllowVolumeExpansion.Should().BeTrue();
        detail.Storage.DataVolumes.Should().HaveCount(3);
        detail.Storage.Volumes.Should().OnlyContain(v => v.VolumeCapacity == "300Gi");
        detail.Cluster.StorageSize.Should().Be("300Gi");
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("300Gi");
        applied.Where(m => m.Contains("MongoDBCommunity")).Should().BeEmpty("opening a panel is a read");
        patched.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreToNewClusterAsync_SizesTheCopyFromTheLiveSource()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate();
        List<string> applied = CaptureMongoApplies();

        MongoBackup backup = new()
        {
            Id = Guid.NewGuid(),
            MongoClusterId = mongo.Id,
            Name = "my-mongo-20261116-020000",
            Type = MongoBackupType.OnDemand,
            Status = MongoBackupStatus.Completed,
        };
        db.MongoBackups.Add(backup);
        await db.SaveChangesAsync();

        MongoCluster copy = await sut.RestoreToNewClusterAsync(tenant.Id, mongo.Id, backup.Id, "my-mongo-copy");

        copy.StorageSize.Should().Be("300Gi", "a copy on 50Gi volumes could not hold 300Gi of data");
        applied.Single(m => m.Contains("kind: MongoDBCommunity") && m.Contains("name: my-mongo-copy"))
            .Should().Contain("storage: 300Gi");
    }

    [Fact]
    public async Task AnUnreadableCluster_KeepsTheStoredSize()
    {
        (Tenant tenant, MongoCluster mongo) = await SeedRecordedAsync("50Gi");
        LiveClaimsAre300GiOnA50GiTemplate(reachable: false);
        CaptureMongoApplies();

        MongoClusterDetail? detail = await sut.GetClusterDetailAsync(tenant.Id, mongo.Id);

        detail!.Storage.Should().BeNull();
        (await StoredAsync(mongo.Id)).StorageSize.Should().Be("50Gi");
    }
}
