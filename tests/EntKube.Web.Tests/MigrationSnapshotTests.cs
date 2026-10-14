using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether EF's own baseline — the <c>ModelSnapshot</c> beside each provider's migrations — still
/// describes the model.
///
/// <para><b>What a stale snapshot costs, and who pays.</b> Not the databases: the two hand-written
/// migrations that moved ahead of it (<c>AddMailDnsRecords</c>,
/// <c>AddSupportRosterAndDeliveryReport</c>) create everything the model describes, and EF can see
/// both, so installations have the right schema. The cost falls on the next person to run
/// <c>dotnet ef migrations add</c>: that command diffs the model against the <em>snapshot</em>, so
/// with the snapshot behind it emitted a migration re-creating a table, five columns, a foreign key
/// and two indexes that already exist — one that fails on every database it reaches, and whose
/// failure looks like a bug in whatever feature was being added.</para>
///
/// <para>The inverse bites harder and is how the weighted-release columns went missing on all three
/// providers: a snapshot that <em>already</em> describes a column makes the next scaffold produce
/// nothing at all, so the column can be absent from every database while looking present to EF.</para>
///
/// <para><b>Why not <c>HasPendingModelChanges()</c>.</b> It is the obvious instrument and it does
/// not agree with the scaffolder from a hand-built context: it reports pending changes even when
/// <c>migrations add</c> produces an empty migration, because the model read back from a snapshot
/// is EF's indexer form (<c>Dictionary&lt;string, object&gt;</c> CLR types, "no field" properties)
/// and the comparison sees that shape difference. Checked both ways rather than assumed — the
/// scaffolder is the authority on what a scaffold will do. So this compares the thing a migration
/// is actually made of: the tables and columns on each side.</para>
/// </summary>
public class MigrationSnapshotTests
{
    [Fact]
    public void The_sqlite_snapshot_describes_the_same_tables_and_columns_as_the_model()
    {
        using ApplicationDbContext db = new(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite("DataSource=:memory:")
                .ConfigureWarnings(Quiet)
                .Options);

        AssertAgrees(db);
    }

    [Fact]
    public void The_postgres_snapshot_describes_the_same_tables_and_columns_as_the_model()
    {
        using PostgresApplicationDbContext db = new(
            new DbContextOptionsBuilder<PostgresApplicationDbContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .ConfigureWarnings(Quiet)
                .Options);

        AssertAgrees(db);
    }

    [Fact]
    public void The_sqlserver_snapshot_describes_the_same_tables_and_columns_as_the_model()
    {
        using SqlServerApplicationDbContext db = new(
            new DbContextOptionsBuilder<SqlServerApplicationDbContext>()
                .UseSqlServer("Server=localhost;Database=unused")
                .ConfigureWarnings(Quiet)
                .Options);

        AssertAgrees(db);
    }

    /// <summary>
    /// No connection is made: both models are built in memory, so a provider with no server here is
    /// still checkable — which matters, because two of the three cannot be run against a database
    /// from this repository.
    /// </summary>
    private static void AssertAgrees(DbContext db)
    {
        IModel? snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot?.Model;

        snapshot.Should().NotBeNull("without a snapshot EF has no baseline to diff a migration from");

        HashSet<string> live = Columns(db.GetService<IDesignTimeModel>().Model);
        HashSet<string> banked = Columns((IModel)snapshot!);

        banked.Should().NotBeEmpty("an empty snapshot would make every assertion below vacuous");

        List<string> missingFromSnapshot = [.. live.Except(banked).Order()];
        List<string> onlyInSnapshot =
        [
            .. banked.Except(live)
                // ASP.NET Core Identity adds the passkey entity to the model only at the schema
                // version the application configures, which a context built here with options alone
                // does not carry. So the snapshot (written by the design-time context, which runs
                // through the app's host) describes it and this model does not. Verified to be an
                // artifact rather than drift: `dotnet ef migrations add` against the real
                // design-time context produces an EMPTY migration, and the scaffolder is the
                // authority on what a scaffold will do.
                .Where(c => !c.StartsWith("AspNetUserPasskeys.", StringComparison.Ordinal))
                .Order()
        ];

        string.Join(", ", missingFromSnapshot).Should().BeEmpty(
            "the model has these and the snapshot does not, so the next scaffolded migration will "
            + "create them — correct only if no hand-written migration already did. Scaffold a "
            + "migration and empty its body if the objects exist (see ReBaselineSnapshot)");

        string.Join(", ", onlyInSnapshot).Should().BeEmpty(
            "the snapshot has these and the model does not. The next scaffold will DROP them, and "
            + "until someone runs it the gap is invisible — which is how a column no migration "
            + "creates can look present to EF");
    }

    /// <summary>
    /// The same warning configuration <c>Program.cs</c> applies. Without it these tests pass alone
    /// and fail in the full suite: EF escalates <c>ManyServiceProvidersCreatedWarning</c> once
    /// twenty internal providers exist in a process, and a 3,900-test run passes that long before
    /// reaching here. The warning is about the suite, not about the comparison — and a test that
    /// only fails in company is worse than no test, so it is configured rather than tolerated.
    /// </summary>
    private static void Quiet(Microsoft.EntityFrameworkCore.Diagnostics.WarningsConfigurationBuilder w)
        => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning);

    /// <summary>Every mapped column as <c>Table.Column</c>, which is what a migration is made of.</summary>
    private static HashSet<string> Columns(IModel model)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);

        foreach (IEntityType entity in model.GetEntityTypes())
        {
            string? table = entity.GetTableName();
            if (table is null) continue;

            StoreObjectIdentifier id = StoreObjectIdentifier.Table(table, entity.GetSchema());

            foreach (IProperty property in entity.GetProperties())
            {
                columns.Add($"{table}.{property.GetColumnName(id) ?? property.Name}");
            }
        }

        return columns;
    }
}
