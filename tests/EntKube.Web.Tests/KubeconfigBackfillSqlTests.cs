using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Locks in the SQL contract used by the startup kubeconfig backfill
/// (Program.EnsureClusterKubeconfigsMigratedAsync) against a real SQLite database that simulates the
/// pre-migration state (a legacy plaintext "Kubeconfig" column still present). Guards two subtle
/// cross-provider requirements the backfill depends on: scalar results must be CAST to BIGINT and
/// aliased "Value" for EF's SqlQueryRaw&lt;long&gt; to map them, and SQLite must support DROP COLUMN.
/// </summary>
public class KubeconfigBackfillSqlTests : IDisposable
{
    // Mirrors the strings in Program.cs (SQLite variants).
    private const string ExistsSql =
        "SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM pragma_table_info('KubernetesClusters') WHERE name = 'Kubeconfig'";
    private const string CountUnmigratedSql =
        "SELECT CAST(COUNT(*) AS BIGINT) AS \"Value\" FROM \"KubernetesClusters\" " +
        "WHERE \"Kubeconfig\" IS NOT NULL AND \"KubeconfigSecretId\" IS NULL";
    private const string DropSql = "ALTER TABLE \"KubernetesClusters\" DROP COLUMN \"Kubeconfig\"";

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;

    public KubeconfigBackfillSqlTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    [Fact]
    public async Task Backfill_Sql_ProbesCountsAndDropsLegacyColumn()
    {
        // The current model no longer maps Kubeconfig, so EnsureCreated omits it. Re-add the legacy
        // column to reproduce a database created before this change.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"KubernetesClusters\" ADD COLUMN \"Kubeconfig\" TEXT NULL");

        // The column now exists.
        (await db.Database.SqlQueryRaw<long>(ExistsSql).SingleAsync()).Should().Be(1);

        // Seed a cluster carrying a legacy plaintext kubeconfig and no vault secret yet.
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "Co", Slug = "co" };
        Data.Environment env = new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "prod" };
        KubernetesCluster cluster = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            EnvironmentId = env.Id,
            Name = "c1",
            ApiServerUrl = "https://k8s",
        };
        db.Tenants.Add(tenant);
        db.Set<Data.Environment>().Add(env);
        db.KubernetesClusters.Add(cluster);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"KubernetesClusters\" SET \"Kubeconfig\" = 'legacy-yaml' WHERE \"Id\" = {0}", cluster.Id);

        // One cluster is unmigrated (plaintext present, KubeconfigSecretId null).
        (await db.Database.SqlQueryRaw<long>(CountUnmigratedSql).SingleAsync()).Should().Be(1);

        // Once linked to a vault secret, it is no longer counted as unmigrated.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE \"KubernetesClusters\" SET \"KubeconfigSecretId\" = {0} WHERE \"Id\" = {1}",
            Guid.NewGuid(), cluster.Id);
        (await db.Database.SqlQueryRaw<long>(CountUnmigratedSql).SingleAsync()).Should().Be(0);

        // The legacy column can be dropped, and is then gone.
        await db.Database.ExecuteSqlRawAsync(DropSql);
        (await db.Database.SqlQueryRaw<long>(ExistsSql).SingleAsync()).Should().Be(0);
    }
}
