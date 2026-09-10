using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace EntKube.Web.Data;

/// <summary>
/// Raised when the database is in a state this class will not repair on its own. Deliberately
/// distinct so the startup retry loop can stop immediately: a schema mismatch is not the database
/// being slow to come up, and retrying it ten times only delays the report by a minute.
/// </summary>
public sealed class SchemaReconciliationException(string message) : Exception(message);

/// <summary>
/// Repairs the one database state EF Core cannot get itself out of: a migration whose objects
/// exist while its <c>__EFMigrationsHistory</c> row does not.
///
/// <para><b>Why this exists.</b> EF decides what to apply purely from the history table. If a
/// migration's tables are present but unrecorded — a container killed mid-migration, a restored
/// snapshot, a schema touched by hand — every subsequent start re-runs it, fails on
/// <c>relation already exists</c>, and the application never comes up. The remedy is a few lines
/// of SQL, which is exactly what nobody has to hand on a production box at the moment they need
/// it. So the application does it on startup instead.</para>
///
/// <para><b>What it will and will not do.</b> It only ever touches tables that the failing
/// migration itself creates, so a pre-existing table is never a candidate. From there:</para>
/// <list type="bullet">
/// <item>tables all present and holding rows — the migration really did run, so its history row is
/// written and nothing is altered;</item>
/// <item>tables present but empty — leftovers from a run that did not finish, so they are dropped
/// and EF re-applies the migration properly, indexes and constraints included;</item>
/// <item>some present, some missing, and one of them holds rows — refused. That is a schema this
/// cannot complete without guessing, and guessing here destroys data.</item>
/// </list>
///
/// <para>Emptiness is the safety property throughout: nothing with a row in it is ever dropped.</para>
/// </summary>
public static class MigrationReconciler
{
    /// <summary>
    /// Brings the history table back into agreement with what the schema actually contains, so the
    /// <see cref="RelationalDatabaseFacadeExtensions.Migrate"/> that follows sees a coherent state.
    /// A no-op on a healthy database, including a brand-new empty one.
    /// </summary>
    public static void Reconcile(DbContext db, ILogger logger)
    {
        List<string> pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0)
        {
            return;
        }

        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        IHistoryRepository history = db.GetService<IHistoryRepository>();
        ISqlGenerationHelper sqlHelper = db.GetService<ISqlGenerationHelper>();
        string provider = db.GetService<IDatabaseProvider>().Name;

        // A missing history table means nothing has ever been applied here, so there is nothing to
        // reconcile — and creating one would claim otherwise.
        if (!history.Exists())
        {
            return;
        }

        foreach (string migrationId in pending)
        {
            if (!assembly.Migrations.TryGetValue(migrationId, out TypeInfo? migrationType))
            {
                continue;
            }

            Migration migration = assembly.CreateMigration(migrationType, provider);

            List<CreateTableOperation> created = migration.UpOperations
                .OfType<CreateTableOperation>()
                .ToList();

            if (created.Count == 0)
            {
                // Nothing to compare against. A migration that only alters existing objects cannot be
                // judged this way, so it is left to EF — which is the right answer, not a gap.
                return;
            }

            List<TableState> states = created
                .Select(op => Inspect(db, sqlHelper, op.Schema, op.Name, logger))
                .ToList();

            if (states.All(s => !s.Exists))
            {
                // The ordinary case: this migration has not run. Neither has anything after it.
                return;
            }

            List<TableState> present = states.Where(s => s.Exists).ToList();

            if (present.All(s => s.RowCount == 0))
            {
                DropLeftovers(db, sqlHelper, present, migrationId, logger);

                // EF applies the migration from clean ground on the Migrate() that follows. Anything
                // still wrong further along is picked up by the next pass rather than guessed at now.
                return;
            }

            if (states.All(s => s.Exists))
            {
                StampAsApplied(db, history, migrationId, present, logger);
                continue;
            }

            throw new SchemaReconciliationException(
                $"Migration '{migrationId}' is half-applied and cannot be repaired automatically. "
                + $"Present and holding data: {Describe(present.Where(s => s.RowCount > 0))}. "
                + $"Missing: {Describe(states.Where(s => !s.Exists))}. "
                + "Dropping a table with rows in it would lose them, so this needs a decision no "
                + "process should make on its own: either restore the missing objects, or move the "
                + "data aside and drop the tables this migration creates so it can re-apply.");
        }
    }

    private sealed record TableState(string? Schema, string Name, bool Exists, long RowCount);

    /// <summary>
    /// Asks the database whether a table is there and, if so, whether anything is in it. Existence is
    /// inferred from the query succeeding rather than from a catalog lookup, because the catalogs
    /// differ per provider and this has to hold for all three.
    /// </summary>
    private static TableState Inspect(
        DbContext db, ISqlGenerationHelper sqlHelper, string? schema, string name, ILogger logger)
    {
        string delimited = sqlHelper.DelimitIdentifier(name, schema);

        try
        {
            long rows = ExecuteScalarLong(db, $"SELECT COUNT(*) FROM {delimited}");
            return new TableState(schema, name, Exists: true, RowCount: rows);
        }
        catch (DbException ex)
        {
            // Almost always "no such table", which is the answer we are after. It can also be a
            // permission problem, which would be misread as absence — so say what was seen.
            logger.LogDebug("Reconciler: {Table} reads as absent ({Message})", delimited, ex.Message);
            return new TableState(schema, name, Exists: false, RowCount: 0);
        }
    }

    private static void DropLeftovers(
        DbContext db, ISqlGenerationHelper sqlHelper, List<TableState> present, string migrationId, ILogger logger)
    {
        logger.LogWarning(
            "Migration '{MigrationId}' left {Count} empty table(s) behind without recording itself: {Tables}. "
            + "Dropping them so the migration can apply in full — they hold no rows, so nothing is lost.",
            migrationId, present.Count, Describe(present));

        // Reverse creation order, so a table is gone before the one it depends on. No CASCADE: if
        // something outside this migration references these, that is a surprise worth failing on.
        foreach (TableState table in Enumerable.Reverse(present))
        {
            string delimited = sqlHelper.DelimitIdentifier(table.Name, table.Schema);
            db.Database.ExecuteSqlRaw($"DROP TABLE {delimited}");
            logger.LogInformation("Reconciler: dropped leftover table {Table}", delimited);
        }
    }

    private static void StampAsApplied(
        DbContext db, IHistoryRepository history, string migrationId, List<TableState> present, ILogger logger)
    {
        logger.LogWarning(
            "Migration '{MigrationId}' is not recorded in the history table, but everything it creates is "
            + "present and holds data ({Tables}). Recording it as applied rather than re-running it.",
            migrationId, Describe(present));

        db.Database.ExecuteSqlRaw(
            history.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion())));
    }

    private static string Describe(IEnumerable<TableState> tables) =>
        string.Join(", ", tables.Select(t =>
            t.Schema is null ? $"{t.Name} ({t.RowCount} rows)" : $"{t.Schema}.{t.Name} ({t.RowCount} rows)"));

    private static long ExecuteScalarLong(DbContext db, string sql)
    {
        DbConnection connection = db.Database.GetDbConnection();
        bool wasClosed = connection.State != System.Data.ConnectionState.Open;

        if (wasClosed)
        {
            connection.Open();
        }

        try
        {
            using DbCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }
}
