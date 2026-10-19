using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Whose database backup a method acts on.
///
/// <para>⚠️ <b>The tenant was derived from the dump row, and the derivation is what made the
/// damage possible rather than what prevented it.</b> DeleteDumpAsync found the dump by bare
/// id, read the owning tenant back off it, and handed that tenant to the storage service —
/// which unseals the owner's own credentials with it. So a dump id alone deleted another
/// tenant's backup from S3, using their keys, with the failure swallowed by `catch { }`.</para>
///
/// <para>Every refusal here happens before storage is touched, which is why the service can be
/// built without one: the tests that would reach it are the ones that must not get that far.</para>
/// </summary>
public class RegisteredPostgresDumpTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly DataServicesDbContext db;
    private readonly VaultService vault;
    private readonly RegisteredPostgresService sut;

    private Guid ourInstanceId;
    private Guid ourDatabaseId;
    private Guid ourStorageLinkId;
    private Guid theirStorageLinkId;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid ourCnpgId = Guid.NewGuid();

    private readonly Guid otherTenantId = Guid.NewGuid();
    private readonly Guid theirDatabaseId = Guid.NewGuid();
    private readonly Guid theirDumpId = Guid.NewGuid();
    private readonly Guid theirCnpgId = Guid.NewGuid();

    public RegisteredPostgresDumpTenancyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = ((IDbContextFactory<DataServicesDbContext>)testDb.Factory).CreateDbContext();
        vault = testDb.CreateVaultService();

        Seed(tenantId, "entit", ourCnpgId, out _, out _);
        Seed(otherTenantId, "annan", theirCnpgId, out _, out _);

        // The admin password LoadInstanceAsync insists on, so CreateDumpAsync can reach the
        // two lookups this file is about rather than stopping at the vault.
        vault.SetRegisteredPostgresAdminPasswordAsync(tenantId, ourInstanceId, "pw")
            .GetAwaiter().GetResult();

        sut = new RegisteredPostgresService(
            testDb.Factory, vault, null!, null!,
            NullLogger<RegisteredPostgresService>.Instance, null!);
    }

    private void Seed(Guid tenant, string slug, Guid cnpgId, out Guid databaseId, out Guid dumpId)
    {
        Guid environmentId = Guid.NewGuid();
        Guid k8sId = Guid.NewGuid();
        Guid instanceId = Guid.NewGuid();
        Guid linkId = Guid.NewGuid();
        databaseId = tenant == otherTenantId ? theirDatabaseId : Guid.NewGuid();
        dumpId = tenant == otherTenantId ? theirDumpId : Guid.NewGuid();

        using (ApplicationDbContext seed = testDb.CreateContext())
        {
            seed.Tenants.Add(new Tenant { Id = tenant, Name = slug, Slug = $"{slug}-{tenant:N}" });
            seed.Set<Data.Environment>().Add(new Data.Environment
            {
                Id = environmentId, TenantId = tenant, Name = "production",
            });
            seed.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = k8sId, TenantId = tenant, EnvironmentId = environmentId,
                Name = $"{slug}-prod", ApiServerUrl = "https://k8s.example.com",
            });
            seed.SaveChanges();
        }

        db.Set<StorageLink>().Add(new StorageLink
        {
            Id = linkId, TenantId = tenant, EnvironmentId = environmentId, Name = $"{slug}-backups",
        });
        db.Set<RegisteredPostgresInstance>().Add(new RegisteredPostgresInstance
        {
            Id = instanceId, TenantId = tenant, KubernetesClusterId = k8sId,
            Name = $"{slug}-pg", Namespace = "db", ServiceName = "pg", AdminPodName = "pg-0",
        });
        db.Set<RegisteredPostgresDatabase>().Add(new RegisteredPostgresDatabase
        {
            Id = databaseId, RegisteredPostgresInstanceId = instanceId,
            Name = "journal", Owner = "journal_owner",
        });
        db.Set<RegisteredPostgresDump>().Add(new RegisteredPostgresDump
        {
            Id = dumpId, RegisteredPostgresDatabaseId = databaseId, StorageLinkId = linkId,
            S3Key = $"{slug}/journal.sql", SizeBytes = 4096,
        });
        db.Set<CnpgCluster>().Add(new CnpgCluster
        {
            Id = cnpgId, TenantId = tenant, KubernetesClusterId = k8sId,
            Name = $"{slug}-cnpg", Namespace = "db", PostgresVersion = "16", StorageSize = "10Gi",
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        if (tenant == tenantId)
        {
            ourInstanceId = instanceId;
            ourDatabaseId = databaseId;
            ourStorageLinkId = linkId;
        }
        else
        {
            theirStorageLinkId = linkId;
        }
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A dump row names the storage link and the S3 key the backup lives under.</summary>
    [Fact]
    public async Task Another_tenants_dumps_are_not_listable()
    {
        Guid ourDatabaseId = await db.Set<RegisteredPostgresDatabase>().AsNoTracking()
            .Where(d => d.RegisteredPostgresInstance.TenantId == tenantId)
            .Select(d => d.Id).FirstAsync();

        // A positive first, so the empty below is a refusal and not an empty table.
        (await sut.GetDumpsAsync(tenantId, ourDatabaseId)).Should().ContainSingle();

        (await sut.GetDumpsAsync(tenantId, theirDatabaseId)).Should().BeEmpty();
    }

    /// <summary>
    /// <b>The one that destroys something.</b> The delete reached S3 with the owner's own
    /// credentials, so by dump id alone another tenant's backup went, and the `catch { }`
    /// around the storage call meant a failure there was silent either way.
    /// </summary>
    [Fact]
    public async Task Another_tenants_backup_cannot_be_deleted()
    {
        Func<Task> act = () => sut.DeleteDumpAsync(tenantId, theirDumpId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Dump not found, or it is not this tenant's.");

        (await db.Set<RegisteredPostgresDump>().AsNoTracking().AnyAsync(d => d.Id == theirDumpId))
            .Should().BeTrue();
    }

    /// <summary>
    /// Restoring reads the backup out of storage. The refusal has to come before that, which
    /// is why the target cluster is now resolved before the download rather than after it.
    /// </summary>
    [Fact]
    public async Task Another_tenants_backup_cannot_be_restored_and_nor_can_ours_go_to_their_cluster()
    {
        Func<Task> theirDump = () => sut.RestoreDumpToCnpgAsync(
            tenantId, theirDumpId, ourCnpgId, "loot");

        await theirDump.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Dump not found, or it is not this tenant's.");

        Guid ourDumpId = await db.Set<RegisteredPostgresDump>().AsNoTracking()
            .Where(d => d.RegisteredPostgresDatabase.RegisteredPostgresInstance.TenantId == tenantId)
            .Select(d => d.Id).FirstAsync();

        // Our own backup, their cluster: refused on the target, and refused before the
        // download — a null storage service would throw if it were reached.
        Func<Task> theirCluster = () => sut.RestoreDumpToCnpgAsync(
            tenantId, ourDumpId, theirCnpgId, "ours");

        await theirCluster.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("CNPG cluster not found, or it is not this tenant's.");
    }
    /// <summary>
    /// <b>The storage link is where the dump is written.</b> CreateDumpAsync already took a
    /// tenant and used it for the instance — then took the database and the storage link as
    /// bare ids beside it. A foreign link means our backup is uploaded into their bucket,
    /// which no other guard here covers.
    /// </summary>
    [Fact]
    public async Task A_dump_cannot_be_written_to_another_tenants_storage()
    {
        Func<Task> act = () => sut.CreateDumpAsync(
            tenantId, ourInstanceId, ourDatabaseId, theirStorageLinkId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Storage link not found, or it is not this tenant's.");
    }

    /// <summary>
    /// And the database has to be on the instance that was loaded with the tenant, rather
    /// than any database the caller can name.
    /// </summary>
    [Fact]
    public async Task A_dump_cannot_be_taken_of_a_database_on_another_instance()
    {
        Func<Task> act = () => sut.CreateDumpAsync(
            tenantId, ourInstanceId, theirDatabaseId, ourStorageLinkId);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Database not found on this instance.");
    }
}
