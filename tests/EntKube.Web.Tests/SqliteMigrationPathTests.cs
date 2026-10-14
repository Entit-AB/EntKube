using EntKube.Web.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Whether running the migrations from nothing produces the schema the model expects, on the
/// provider a new install gets by default (<c>Program.cs</c>'s switch falls through to SQLite).
///
/// <para><b>What this found.</b> Three columns the model insists on — the weighted-release fields
/// on <c>AppDeploymentRoutes</c> — were created by <b>no migration on any provider</b>, while the
/// model, every Designer file and all three ModelSnapshots carried them. That gap is
/// self-concealing: a snapshot which already describes a column makes the next
/// <c>migrations add</c> produce nothing, so it cannot be found by scaffolding. Only comparing a
/// migrated schema against the model's own finds it. They are added by
/// <c>AddRouteCanaryColumns</c>, by hand, for all three providers.</para>
///
/// <para><b>What this does NOT treat as a defect, and why that is the interesting part.</b>
/// <c>AppDeployments.GitUrl</c> and <c>AppDeploymentRoutes.ClusterAppliedAt</c> are absent from a
/// migrated schema on purpose. Their migrations are deliberately invisible to EF — see
/// docs/migrations-defect.md — and <c>Program.cs</c> creates both at startup with idempotent
/// shims. So the boundary this test draws is the real one: some schema comes from migrations and
/// some is repaired at startup, and the allowance below is the first place that division has been
/// written down where it can fail.</para>
///
/// <para>⚠️ The repair must not be "make those migrations visible". An existing database already
/// has the columns, from the shim, while carrying no history row — so a visible migration would
/// <c>AddColumn</c> onto a column that exists. On SQLite that is a duplicate-column error raised
/// inside a migration, where the shim's own try/catch cannot help, and the application does not
/// start. I made exactly that change before reading the document that warned about it.</para>
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

        string.Join(", ", missingColumns.Except(RepairedAtStartup)).Should().BeEmpty(
            "a column only EnsureCreated makes is one EF will select on a migrated install and the "
            + "database will refuse. If it genuinely belongs to a startup shim instead, add it to "
            + "RepairedAtStartup with the shim's name — do not make its migration visible");

        RepairedAtStartup.Should().BeSubsetOf(missingColumns,
            "this column is in the schema the migrations build, so its startup shim is now dead "
            + "code and the allowance is hiding nothing");
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

    /// <summary>
    /// Columns a migrated database legitimately lacks because <c>Program.cs</c> adds them at
    /// startup: <c>EnsureDeploymentGitUrlAsync</c> and
    /// <c>EnsureDeploymentRouteClusterAppliedAtAsync</c>. Both are idempotent and both exist
    /// because their migrations are invisible by design.
    /// </summary>
    private static readonly string[] RepairedAtStartup =
    [
        "AppDeploymentRoutes.ClusterAppliedAt",
        "AppDeployments.GitUrl",
    ];

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
