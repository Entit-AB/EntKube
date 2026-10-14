using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether running the migrations from nothing produces the schema the model expects, on the
/// provider a new install gets by default (<c>Program.cs</c>'s switch falls through to SQLite).
///
/// <para><b>What this found.</b> It did not. Five columns the model insists on were absent from a
/// freshly migrated database, so every query touching an app route or a deployment's git URL would
/// have failed on a column EF was certain existed. Two causes, and the second is the one worth
/// remembering:</para>
/// <list type="bullet">
///   <item>Two migrations were <b>invisible to EF</b>. A migration is found by its
///   <c>[Migration]</c> attribute, which normally lives in the Designer file — and these were
///   hand-written with neither. They were not failing; they were never running, which is quieter.
///   Four of them existed, across SQLite and SqlServer.</item>
///   <item>The weighted-release columns were created by <b>no migration on any provider</b>, while
///   the model, every Designer file and all three ModelSnapshots carried them. A snapshot that
///   already describes a column makes the next <c>migrations add</c> produce nothing, so the gap
///   is self-concealing — and on SqlServer it was worse than a missing column: a later
///   <em>visible</em> migration does <c>AlterColumn</c> on <c>GitUrl</c>, which the invisible one
///   was supposed to create, so a fresh SqlServer database could not be migrated at all.</item>
/// </list>
///
/// <para>⚠️ <b>Not asserted here, but true:</b> <c>Database.HasPendingModelChanges()</c> is still
/// <c>true</c>. That compares the model against the ModelSnapshot rather than against the database,
/// so it says some other model change was never scaffolded — a separate audit, and a larger one,
/// since EF will not say which. The schema comparison below is the property a new install actually
/// depends on, and it is the one that was broken.</para>
/// </summary>
public sealed class SqliteMigrationPathTests : IDisposable
{
    private readonly string migrated = TempDb();
    private readonly string fromModel = TempDb();

    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), $"entkube-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        foreach (string f in new[] { migrated, fromModel })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Tables and columns EF creates from the model itself, but that no migration produces, would
    /// each be a runtime failure on a new install. Comparing the two schemas is the only way to see
    /// them: the migrations all succeed either way.
    /// </summary>
    [Fact]
    public void Migrating_from_nothing_produces_the_schema_the_model_expects()
    {
        using (ApplicationDbContext db = Open(migrated)) db.Database.Migrate();
        using (ApplicationDbContext db = Open(fromModel)) db.Database.EnsureCreated();

        Dictionary<string, HashSet<string>> byMigration = Schema(migrated);
        Dictionary<string, HashSet<string>> byModel = Schema(fromModel);

        byMigration.Should().NotBeEmpty("if the migrations stopped running, everything below passes");

        List<string> missingTables = [.. byModel.Keys.Where(t => !byMigration.ContainsKey(t)).Order()];
        List<string> missingColumns =
        [
            .. byModel.Where(kv => byMigration.ContainsKey(kv.Key))
                .SelectMany(kv => kv.Value
                    .Where(c => !byMigration[kv.Key].Contains(c))
                    .Select(c => $"{kv.Key}.{c}"))
                .Order()
        ];

        string.Join(", ", missingTables).Should().BeEmpty(
            "a table only EnsureCreated makes is a table a migrated install does not have");

        string.Join(", ", missingColumns).Should().BeEmpty(
            "a column only EnsureCreated makes is one EF will select on a migrated install and the "
            + "database will refuse — which is what five of these were doing");
    }

    /// <summary>
    /// The other direction, allowed and bounded. EF's own lock table is not part of any model, and
    /// <c>AspNetUserPasskeys</c> is created by a migration while this context's model does not map
    /// it — harmless (the table is there), but worth pinning so the list cannot grow quietly.
    /// </summary>
    [Fact]
    public void Nothing_unexpected_is_created_only_by_the_migrations()
    {
        using (ApplicationDbContext db = Open(migrated)) db.Database.Migrate();
        using (ApplicationDbContext db = Open(fromModel)) db.Database.EnsureCreated();

        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "__EFMigrationsHistory", "__EFMigrationsLock", "AspNetUserPasskeys",
        };

        List<string> extra =
        [
            .. Schema(migrated).Keys
                .Where(t => !Schema(fromModel).ContainsKey(t) && !allowed.Contains(t))
                .Order()
        ];

        string.Join(", ", extra).Should().BeEmpty(
            "a table the migrations create and the model does not describe is either a leftover or "
            + "an entity someone forgot to map");
    }

    private static ApplicationDbContext Open(string file)
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"DataSource={file}")
            .Options);

    /// <summary>Tables to their columns, straight from SQLite rather than from EF's own view.</summary>
    private static Dictionary<string, HashSet<string>> Schema(string file)
    {
        Dictionary<string, HashSet<string>> schema = new(StringComparer.Ordinal);

        using SqliteConnection connection = new($"DataSource={file}");
        connection.Open();

        List<string> tables = [];
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        foreach (string table in tables)
        {
            HashSet<string> columns = new(StringComparer.Ordinal);
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info('{table}')";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            schema[table] = columns;
        }

        return schema;
    }
}
