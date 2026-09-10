using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// The reconciler runs unsupervised against a production database at startup, so every branch it
/// can take is pinned here — including the one where it refuses to act. The scenario throughout is
/// the one that produced it: the last migration's tables exist while its history row does not, so
/// EF re-runs it and the application never comes up.
/// </summary>
public class MigrationReconcilerTests : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;

    /// <summary>The migration under test: the most recent one, so re-applying it is what EF would do.</summary>
    private readonly string lastMigrationId;

    public MigrationReconcilerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);

        lastMigrationId = db.Database.GetMigrations().Last();
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private void Reconcile() => MigrationReconciler.Reconcile(db, NullLogger.Instance);

    private void Forget(string migrationId) =>
        db.Database.ExecuteSqlRaw(
            $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{migrationId}'");

    private bool TableExists(string name) =>
        db.Database.SqlQueryRaw<int>(
            $"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='{name}'")
          .AsEnumerable().Single() > 0;

    private bool IsRecorded(string migrationId) =>
        !db.Database.GetPendingMigrations().Contains(migrationId);

    /// <summary>A config row, so a table can be shown to be holding data worth keeping.</summary>
    private void SeedMailConfig()
    {
        Tenant tenant = new() { Id = Guid.NewGuid(), Name = "TestCo", Slug = "testco" };
        db.Tenants.Add(tenant);
        db.Set<StalwartComponentConfig>().Add(new StalwartComponentConfig
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Hostname = "mx.example.org"
        });
        db.SaveChanges();
    }

    [Fact]
    public void A_healthy_database_is_left_alone()
    {
        db.Database.Migrate();

        Reconcile();

        IsRecorded(lastMigrationId).Should().BeTrue();
        TableExists("StalwartComponentConfigs").Should().BeTrue();
    }

    [Fact]
    public void An_empty_database_is_left_alone()
    {
        // Nothing has ever been applied, so there is nothing to reconcile — and claiming otherwise
        // by creating a history table would make the first real migration look already-done.
        Reconcile();

        db.Database.Migrate();

        TableExists("StalwartComponentConfigs").Should().BeTrue();
        IsRecorded(lastMigrationId).Should().BeTrue();
    }

    [Fact]
    public void Empty_leftovers_are_dropped_so_the_migration_can_apply_in_full()
    {
        // The reported failure: the migration created its first table, never recorded itself, and
        // every start since died on "relation already exists".
        db.Database.Migrate();
        Forget(lastMigrationId);
        db.Database.ExecuteSqlRaw("DROP TABLE \"StalwartMailAccounts\"");
        db.Database.ExecuteSqlRaw("DROP TABLE \"StalwartMailDomains\"");

        Reconcile();

        // The leftover is gone rather than stamped over: its indexes and constraints were never
        // created either, so pretending it was complete would leave the schema quietly wrong.
        TableExists("StalwartComponentConfigs").Should().BeFalse();

        db.Database.Migrate();

        TableExists("StalwartComponentConfigs").Should().BeTrue();
        TableExists("StalwartMailDomains").Should().BeTrue();
        TableExists("StalwartMailAccounts").Should().BeTrue();
        IsRecorded(lastMigrationId).Should().BeTrue();
    }

    [Fact]
    public void A_complete_schema_holding_data_is_recorded_rather_than_re_run()
    {
        // Everything the migration creates is present and in use — a lost history row, not a lost
        // migration. Re-running it would fail; dropping the tables would destroy the data.
        db.Database.Migrate();
        SeedMailConfig();
        Forget(lastMigrationId);

        Reconcile();

        IsRecorded(lastMigrationId).Should().BeTrue();
        db.Set<StalwartComponentConfig>().Count().Should().Be(1, "nothing should have been dropped");

        db.Database.Migrate();
        IsRecorded(lastMigrationId).Should().BeTrue();
    }

    [Fact]
    public void A_half_applied_schema_holding_data_is_refused_rather_than_guessed_at()
    {
        // Some of the migration's tables are missing and one of the survivors holds rows. Dropping
        // loses data, stamping leaves the schema incomplete: there is no safe move to make here.
        db.Database.Migrate();
        SeedMailConfig();
        Forget(lastMigrationId);
        db.Database.ExecuteSqlRaw("DROP TABLE \"StalwartMailAccounts\"");

        Action reconcile = Reconcile;

        reconcile.Should().Throw<SchemaReconciliationException>()
            .Which.Message.Should().Contain("StalwartMailAccounts");

        db.Set<StalwartComponentConfig>().Count().Should().Be(1, "a refusal must not be destructive");
    }
}
