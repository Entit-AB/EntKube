using EntKube.Web.Data;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Deleting a database's vault secrets honours the tenant it was asked for.
///
/// <para><b>What was wrong, stated accurately.</b> <c>CnpgService</c> and <c>MongoService</c>
/// deleted straight out of <c>db.VaultSecrets</c>, filtering on the owning id alone —
/// <c>Where(s =&gt; s.CnpgDatabaseId == databaseId)</c> — in a method that was handed a
/// <c>tenantId</c> and never used it. Mongo's two had no tenant in scope at all. Their sibling
/// reads on <c>VaultService</c> have always carried <c>s.Vault.TenantId == tenantId</c>, so the
/// read and the delete of the same rows disagreed about who owned them.</para>
///
/// <para>⚠️ <b>It was not an exploitable leak, and saying so matters.</b> I first described it as
/// one. <c>VaultSecret.CnpgDatabaseId</c> is a <em>foreign key</em> to a database row that belongs
/// to exactly one tenant (and cascades), so two tenants cannot share that id and the unscoped
/// predicate could not in practice match another tenant's rows. What the fix buys is a guarantee
/// that is explicit rather than incidental — it no longer depends on the FK happening to make the
/// wrong case unreachable — plus the removal of a cross-module write to Secrets' own table, which
/// is the structural point.</para>
///
/// <para>Found by giving DataServices its own module context (#229): the narrower context has no
/// <c>VaultSecrets</c>, so neither service compiled against it.</para>
/// </summary>
public class VaultSecretDeleteTenancyTests : IDisposable
{
    private static readonly byte[] TestRootKey = Convert.FromBase64String(TestServices.TestRootKeyBase64);

    private readonly InterceptingTestDb testDb;
    private readonly ApplicationDbContext db;
    private readonly VaultService vault;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid stranger = Guid.NewGuid();

    private Guid cnpgDatabaseId;
    private Guid mongoDatabaseId;
    private Guid mongoClusterId;

    public VaultSecretDeleteTenancyTests()
    {
        testDb = new InterceptingTestDb(TestRootKey);
        db = testDb.CreateContext();
        vault = testDb.CreateVaultService();

        Guid envId = Guid.NewGuid();
        Guid k8sId = Guid.NewGuid();

        foreach ((Guid tenantId, string label) in new[] { (ours, "ours"), (stranger, "stranger") })
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = label, Slug = $"{label}-{tenantId:N}" });
        }

        db.Set<Data.Environment>().Add(new Data.Environment { Id = envId, TenantId = ours, Name = "production" });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = k8sId, TenantId = ours, EnvironmentId = envId,
            Name = "primary", ApiServerUrl = "https://c:6443",
        });

        // The owning rows have to exist: VaultSecret's owner columns are foreign keys, which is
        // also why the unscoped predicate was not reachable across tenants.
        CnpgCluster cnpg = new()
        {
            Id = Guid.NewGuid(), TenantId = ours, KubernetesClusterId = k8sId,
            Name = "pg", Namespace = "data", PostgresVersion = "16", StorageSize = "10Gi",
        };
        MongoCluster mongo = new()
        {
            Id = Guid.NewGuid(), TenantId = ours, KubernetesClusterId = k8sId,
            Name = "mongo", Namespace = "data", MongoVersion = "7", StorageSize = "10Gi",
        };
        db.Set<CnpgCluster>().Add(cnpg);
        db.Set<MongoCluster>().Add(mongo);

        CnpgDatabase cnpgDb = new() { Id = Guid.NewGuid(), CnpgClusterId = cnpg.Id, Name = "billing", Owner = "app" };
        MongoDatabase mongoDb = new() { Id = Guid.NewGuid(), MongoClusterId = mongo.Id, Name = "billing", Owner = "app" };
        db.Set<CnpgDatabase>().Add(cnpgDb);
        db.Set<MongoDatabase>().Add(mongoDb);
        db.SaveChanges();

        cnpgDatabaseId = cnpgDb.Id;
        mongoDatabaseId = mongoDb.Id;
        mongoClusterId = mongo.Id;

        vault.InitializeVaultAsync(ours).GetAwaiter().GetResult();
        vault.InitializeVaultAsync(stranger).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        db.Dispose();
        testDb.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The property that was missing: the method honours its tenant argument. Asking as a tenant
    /// that does not own the database deletes nothing — which the previous version, filtering on
    /// the id alone, would have ignored.
    /// </summary>
    [Fact]
    public async Task Deleting_cnpg_secrets_as_a_tenant_that_does_not_own_them_removes_nothing()
    {
        await vault.SetCnpgDatabaseSecretAsync(ours, cnpgDatabaseId, "PASSWORD", "p", "k8s", "data");

        await vault.DeleteCnpgDatabaseSecretsAsync(stranger, cnpgDatabaseId);

        (await vault.GetCnpgDatabaseSecretsAsync(ours, cnpgDatabaseId)).Should().ContainSingle(
            "the delete is scoped to the asking tenant, not to the id alone");
    }

    [Fact]
    public async Task Deleting_mongo_database_secrets_as_a_stranger_removes_nothing()
    {
        await vault.SetMongoDatabaseSecretAsync(ours, mongoDatabaseId, "PASSWORD", "p", "k8s", "data");

        await vault.DeleteMongoDatabaseSecretsAsync(stranger, mongoDatabaseId);

        (await vault.GetMongoDatabaseSecretsAsync(ours, mongoDatabaseId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Deleting_mongo_cluster_secrets_as_a_stranger_leaves_the_admin_password()
    {
        await vault.SetMongoClusterSecretAsync(ours, mongoClusterId, "ADMIN_PASSWORD", "a", "k8s", "data");

        await vault.DeleteMongoClusterSecretsAsync(stranger, mongoClusterId);

        (await vault.GetMongoClusterSecretValueAsync(ours, mongoClusterId, "ADMIN_PASSWORD"))
            .Should().Be("a", "a managed cluster's admin password is the way back into the database");
    }

    /// <summary>
    /// And it still deletes what it should. A scoping fix that quietly stopped deleting would leave
    /// secrets behind for a database that no longer exists, which is its own problem.
    /// </summary>
    [Fact]
    public async Task Deleting_as_the_owner_removes_every_secret_of_that_database()
    {
        await vault.SetCnpgDatabaseSecretAsync(ours, cnpgDatabaseId, "PASSWORD", "p", "k8s", "data");
        await vault.SetCnpgDatabaseSecretAsync(ours, cnpgDatabaseId, "USERNAME", "u", "k8s", "data");

        (await vault.GetCnpgDatabaseSecretsAsync(ours, cnpgDatabaseId)).Should().HaveCount(2);

        await vault.DeleteCnpgDatabaseSecretsAsync(ours, cnpgDatabaseId);

        (await vault.GetCnpgDatabaseSecretsAsync(ours, cnpgDatabaseId)).Should().BeEmpty();
    }
}
