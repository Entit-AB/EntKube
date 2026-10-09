using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace EntKube.Web.Tests;

/// <summary>
/// The MongoService methods that change a running cluster.
///
/// <para><b>Why these, and why now.</b> MongoService is 3,464 lines that create clusters, patch
/// them, resize them, rotate credentials and delete databases, and thirteen tests covered seven
/// of its thirty public methods. Twenty-seven of its forty-seven cluster-credential call sites sat
/// in methods nothing exercised. The credential-custody work (docs/decomposition.md §4.0.1) is
/// about to rewrite every one of them, and the three services converted before this were each
/// safe because 24, 202 and 59 existing tests caught mistakes as they were made. Thirteen is not
/// enough to lean on, so the coverage comes first.</para>
///
/// <para>These assert on <em>what is sent to the cluster</em> rather than on the database row,
/// because that is the part a refactor of the call sites can silently change: a manifest that is
/// no longer applied, or applied to the wrong cluster, leaves the row looking perfectly correct.</para>
/// </summary>
public class MongoServiceMutationTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(
        "dGhpcyBpcyBhIDMyIGJ5dGUga2V5ISEhMTIzNDU2Nzg=");

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vaultService;
    private readonly Mock<IKubernetesClientFactory> k8s;
    private readonly MongoService sut;

    public MongoServiceMutationTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vaultService = testDb.CreateVaultService();
        k8s = new Mock<IKubernetesClientFactory>();
        // Real factory over the intercepting test database: the vault-seeded kubeconfig resolves
        // as production resolves it, then reaches the same mock — the existing assertions hold.
        sut = new MongoService(
            testDb.Factory, vaultService,
            new EntKube.Web.Services.Clusters.ClusterClientFactory(testDb.Factory, k8s.Object),
            new EntKube.Web.Modules.Api.CatalogApi(testDb.Factory));
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    // ──────── Fixture ────────

    private async Task<(Guid TenantId, Guid ClusterId)> SeedClusterAsync(string label = "prod")
    {
        Guid tenantId = Guid.NewGuid(), envId = Guid.NewGuid(), clusterId = Guid.NewGuid();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        db.Set<Data.Environment>().Add(new Data.Environment
        {
            Id = envId, TenantId = tenantId, Name = label,
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = envId,
            Name = $"{label}-k8s", ApiServerUrl = $"https://{label}.example",
        });
        await db.SaveChangesAsync();

        await vaultService.InitializeVaultAsync(tenantId);
        await testDb.SeedKubeconfigAsync(vaultService, tenantId, clusterId, TestKubeconfig.Valid);

        return (tenantId, clusterId);
    }

    private async Task<MongoCluster> SeedMongoAsync(
        Guid tenantId, Guid clusterId, bool external = false, string? backupSchedule = null,
        Guid? storageLinkId = null)
    {
        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            KubernetesClusterId = clusterId,
            Name = "my-mongo",
            Namespace = "databases",
            Members = 3,
            StorageSize = "10Gi",
            MongoVersion = "7.0",
            IsExternal = external,
            BackupSchedule = backupSchedule,
            StorageLinkId = storageLinkId,
            Status = MongoClusterStatus.Running,
        };

        db.MongoClusters.Add(mongo);
        await db.SaveChangesAsync();

        return mongo;
    }

    /// <summary>The manifests and patches actually sent to a cluster during one act.</summary>
    private List<string> Applied { get; } = [];

    private List<(string Resource, string Patch)> Patched { get; } = [];

    private void RecordClusterCalls()
    {
        k8s.Setup(f => f.ApplyManifestAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((m, _, _) => Applied.Add(m))
            .ReturnsAsync(string.Empty);

        k8s.Setup(f => f.PatchJsonAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, string, CancellationToken>(
                (resource, _, _, patch, _, _) => Patched.Add((resource, patch)))
            .Returns(Task.CompletedTask);
    }

    // ──────── UpgradeClusterAsync ────────

    /// <summary>
    /// An external broker's spec carries users and configuration EntKube did not write, so the
    /// upgrade patches only the version. Re-applying a whole manifest would clobber the rest.
    /// </summary>
    [Fact]
    public async Task Upgrading_an_external_cluster_patches_only_the_version()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId, external: true);
        RecordClusterCalls();

        await sut.UpgradeClusterAsync(tenantId, mongo.Id, "8.0");

        Applied.Should().BeEmpty("a full manifest would overwrite spec.users on a broker we do not own");
        Patched.Should().ContainSingle();
        Patched[0].Resource.Should().Be("mongodbcommunity");
        Patched[0].Patch.Should().Contain("\"version\":\"8.0.0\"",
            "the operator wants three components, and a two-component version is silently ignored");
        Patched[0].Patch.Should().NotContain("users");
    }

    [Fact]
    public async Task Upgrading_an_external_cluster_leaves_an_already_full_version_alone()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId, external: true);
        RecordClusterCalls();

        await sut.UpgradeClusterAsync(tenantId, mongo.Id, "8.0.4");

        Patched[0].Patch.Should().Contain("\"version\":\"8.0.4\"");
    }

    [Fact]
    public async Task Upgrading_a_managed_cluster_reapplies_its_manifest()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId);
        RecordClusterCalls();

        await sut.UpgradeClusterAsync(tenantId, mongo.Id, "8.0");

        Patched.Should().BeEmpty();
        Applied.Should().ContainSingle().Which.Should().Contain("MongoDBCommunity");

        MongoCluster persisted = await db.MongoClusters.AsNoTracking()
            .FirstAsync(c => c.Id == mongo.Id);
        persisted.MongoVersion.Should().Be("8.0");
        persisted.Status.Should().Be(MongoClusterStatus.Upgrading);
    }

    /// <summary>
    /// The backup CronJob pins the mongodump image to the cluster's version, so an upgrade that
    /// did not re-apply it would leave backups running the old client against the new server.
    /// </summary>
    [Fact]
    public async Task Upgrading_a_scheduled_cluster_also_reapplies_the_backup_cronjob()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        StorageLink link = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId,
            EnvironmentId = (await db.Set<Data.Environment>().FirstAsync()).Id,
            Provider = StorageProvider.CleuraS3, Name = "Backups",
            Endpoint = "https://s3.example", BucketName = "b", Region = "r",
        };
        db.StorageLinks.Add(link);
        await db.SaveChangesAsync();

        MongoCluster mongo = await SeedMongoAsync(
            tenantId, clusterId, backupSchedule: "0 2 * * *", storageLinkId: link.Id);
        RecordClusterCalls();

        await sut.UpgradeClusterAsync(tenantId, mongo.Id, "8.0");

        Applied.Should().HaveCount(2);
        Applied.Should().Contain(m => m.Contains("CronJob"));
    }

    // ──────── RestartClusterAsync ────────

    [Fact]
    public async Task Restarting_bumps_the_pod_template_annotation()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId);
        RecordClusterCalls();

        await sut.RestartClusterAsync(tenantId, mongo.Id);

        Patched.Should().ContainSingle();
        Patched[0].Patch.Should().Contain("restartedAt",
            "the rolling restart is driven by a changed pod template, not by deleting pods");
        Patched[0].Patch.Should().NotContain("__TS__", "the placeholder must be substituted");
    }

    // ──────── ResizeClusterAsync ────────

    [Fact]
    public async Task Resizing_cpu_or_memory_reapplies_the_manifest()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId);
        RecordClusterCalls();

        await sut.ResizeClusterAsync(tenantId, mongo.Id, null, "500m", "2", "1Gi", "4Gi");

        Applied.Should().NotBeEmpty("the operator picks up new requests via the pod template");

        MongoCluster persisted = await db.MongoClusters.AsNoTracking()
            .FirstAsync(c => c.Id == mongo.Id);
        persisted.CpuRequest.Should().Be("500m");
        persisted.MemoryLimit.Should().Be("4Gi");
    }

    /// <summary>Asking for what is already set is not a reason to restart a database.</summary>
    [Fact]
    public async Task Resizing_to_the_values_already_set_changes_nothing()
    {
        (Guid tenantId, Guid clusterId) = await SeedClusterAsync();
        MongoCluster mongo = await SeedMongoAsync(tenantId, clusterId);
        RecordClusterCalls();

        await sut.ResizeClusterAsync(tenantId, mongo.Id, "10Gi", null, null, null, null);

        Applied.Should().BeEmpty();
        Patched.Should().BeEmpty();
    }

    // ──────── Tenancy ────────

    /// <summary>
    /// Every one of these takes a tenant and a cluster id, and the tenant is what stops one
    /// customer reaching another's database. Worth a test precisely because it is the kind of
    /// filter a refactor can drop without anything else noticing.
    /// </summary>
    [Fact]
    public async Task Another_tenants_cluster_is_not_found()
    {
        (Guid ourTenant, Guid ourCluster) = await SeedClusterAsync("ours");
        (Guid theirTenant, Guid theirCluster) = await SeedClusterAsync("theirs");

        MongoCluster theirs = await SeedMongoAsync(theirTenant, theirCluster);
        RecordClusterCalls();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.UpgradeClusterAsync(ourTenant, theirs.Id, "8.0"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RestartClusterAsync(ourTenant, theirs.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ResizeClusterAsync(ourTenant, theirs.Id, "20Gi", null, null, null, null));

        Applied.Should().BeEmpty("nothing should have been sent to anyone's cluster");
        Patched.Should().BeEmpty();
    }
}
