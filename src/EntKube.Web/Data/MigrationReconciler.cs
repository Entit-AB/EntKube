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
/// migration's objects are present but unrecorded — a container killed mid-migration, a restored
/// snapshot, a schema touched by hand — every subsequent start re-runs it, fails on
/// <c>relation already exists</c> or <c>column already exists</c>, and the application never comes
/// up. The remedy is a few lines of SQL, which is exactly what nobody has to hand on a production
/// box at the moment they need it. So the application does it on startup instead.</para>
///
/// <para><b>What it looks at.</b> Only the objects the failing migration itself creates: the tables
/// it creates, and the columns it adds to tables it does not. A pre-existing object is never a
/// candidate. From there:</para>
/// <list type="bullet">
/// <item>everything present, and something in it worth keeping — the migration really did run, so
/// its history row is written and nothing is altered;</item>
/// <item>only empty leftovers present — the remains of a run that did not finish, so they are
/// dropped and EF re-applies the migration properly, indexes and constraints included;</item>
/// <item>partly present, with something that cannot be given back — refused. That is a schema this
/// cannot complete without guessing, and guessing here destroys data.</item>
/// </list>
///
/// <para>Removability is the safety property throughout: an empty table can be dropped because
/// nothing is lost with it, and a column never can — the rows around it are real, and its values
/// would go with it. Nothing holding data is ever dropped.</para>
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

            List<CreateTableOperation> createdTables = migration.UpOperations
                .OfType<CreateTableOperation>()
                .ToList();

            // Columns added to tables this migration also creates are already accounted for by the
            // table itself, so judging them separately would only double-count.
            List<AddColumnOperation> addedColumns = migration.UpOperations
                .OfType<AddColumnOperation>()
                .Where(op => !createdTables.Any(t => t.Name == op.Table && t.Schema == op.Schema))
                .ToList();

            if (createdTables.Count == 0 && addedColumns.Count == 0)
            {
                // Nothing to compare against — an index-only or data-only migration cannot be judged
                // this way, so it is left to EF. The next pending migration still can be, so the scan
                // carries on rather than giving up here.
                continue;
            }

            List<SchemaObject> objects =
            [
                .. createdTables.Select(op => InspectTable(db, sqlHelper, op.Schema, op.Name, logger)),
                .. addedColumns.Select(op => InspectColumn(db, sqlHelper, op.Schema, op.Table, op.Name, logger)),
            ];

            List<SchemaObject> present = objects.Where(o => o.Exists).ToList();

            if (present.Count == 0)
            {
                // The ordinary case: this migration has not run. Neither has anything after it.
                return;
            }

            // An empty table can be handed back; a column cannot, because dropping it takes its
            // values with it and the rows around it are real.
            if (present.All(o => o.Removable))
            {
                DropLeftovers(db, sqlHelper, present, migrationId, logger);

                // EF applies the migration from clean ground on the Migrate() that follows. Anything
                // still wrong further along is picked up by the next pass rather than guessed at now.
                return;
            }

            if (present.Count == objects.Count)
            {
                StampAsApplied(db, history, migrationId, present, logger);
                continue;
            }

            throw new SchemaReconciliationException(
                $"Migration '{migrationId}' is half-applied and cannot be repaired automatically. "
                + $"Present and not safe to give back: {Describe(present.Where(o => !o.Removable))}. "
                + $"Missing: {Describe(objects.Where(o => !o.Exists))}. "
                + "Dropping these would lose data, so this needs a decision no process should make "
                + "on its own: either restore the missing objects, or move the data aside and drop "
                + "what this migration creates so it can re-apply.");
        }
    }

    /// <summary>
    /// One thing a migration brings into existence. <paramref name="Removable"/> says whether it can
    /// be dropped to put the database back where the migration found it — true only for a table this
    /// migration creates that holds no rows.
    /// </summary>
    private sealed record SchemaObject(
        string Description, bool Exists, bool Removable, string? DropSql);

    /// <summary>
    /// Asks the database whether a table is there and, if so, whether anything is in it. Existence is
    /// inferred from the query succeeding rather than from a catalog lookup, because the catalogs
    /// differ per provider and this has to hold for all three.
    /// </summary>
    private static SchemaObject InspectTable(
        DbContext db, ISqlGenerationHelper sqlHelper, string? schema, string name, ILogger logger)
    {
        string delimited = sqlHelper.DelimitIdentifier(name, schema);
        string label = schema is null ? name : $"{schema}.{name}";

        try
        {
            long rows = ExecuteScalarLong(db, $"SELECT COUNT(*) FROM {delimited}");
            return new SchemaObject(
                $"table {label} ({rows} rows)",
                Exists: true,
                Removable: rows == 0,
                DropSql: $"DROP TABLE {delimited}");
        }
        catch (DbException ex)
        {
            // Almost always "no such table", which is the answer we are after. It can also be a
            // permission problem, which would be misread as absence — so say what was seen.
            logger.LogDebug("Reconciler: table {Table} reads as absent ({Message})", delimited, ex.Message);
            return new SchemaObject($"table {label}", Exists: false, Removable: false, DropSql: null);
        }
    }

    /// <summary>
    /// Asks the database whether a column is there, by reading the shape of an empty result off the
    /// table. Naming the column in the query instead would be the obvious move and is wrong: SQLite
    /// falls back to reading a double-quoted identifier it cannot resolve as a string literal, so
    /// <c>SELECT COUNT("Missing") FROM "T"</c> succeeds and every absent column reads as present.
    /// The column list is the same answer on every provider and cannot be faked that way.
    /// A missing table reads as a missing column, which is right — it is not there either way.
    /// </summary>
    private static SchemaObject InspectColumn(
        DbContext db, ISqlGenerationHelper sqlHelper, string? schema, string table, string name, ILogger logger)
    {
        string delimitedTable = sqlHelper.DelimitIdentifier(table, schema);
        string label = schema is null ? $"{table}.{name}" : $"{schema}.{table}.{name}";

        List<string>? columns = ReadColumnNames(db, delimitedTable, logger);

        // Case-insensitively, because SQL Server and SQLite both fold it and only PostgreSQL would
        // ever disagree — and there a near-miss is a different bug, not a column to be judged here.
        bool exists = columns?.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)) == true;

        if (!exists)
        {
            logger.LogDebug("Reconciler: column {Column} on {Table} reads as absent", name, delimitedTable);
        }

        // Never removable: the table around it holds whatever it holds, and dropping the column
        // discards its values for every one of those rows.
        return new SchemaObject($"column {label}", exists, Removable: false, DropSql: null);
    }

    private static void DropLeftovers(
        DbContext db, ISqlGenerationHelper sqlHelper, List<SchemaObject> present, string migrationId, ILogger logger)
    {
        logger.LogWarning(
            "Migration '{MigrationId}' left {Count} empty object(s) behind without recording itself: {Objects}. "
            + "Dropping them so the migration can apply in full — they hold no rows, so nothing is lost.",
            migrationId, present.Count, Describe(present));

        // Reverse creation order, so a table is gone before the one it depends on. No CASCADE: if
        // something outside this migration references these, that is a surprise worth failing on.
        foreach (SchemaObject leftover in Enumerable.Reverse(present))
        {
            db.Database.ExecuteSqlRaw(leftover.DropSql!);
            logger.LogInformation("Reconciler: dropped leftover {Object}", leftover.Description);
        }
    }

    private static void StampAsApplied(
        DbContext db, IHistoryRepository history, string migrationId, List<SchemaObject> present, ILogger logger)
    {
        logger.LogWarning(
            "Migration '{MigrationId}' is not recorded in the history table, but everything it creates is "
            + "already present ({Objects}). Recording it as applied rather than re-running it.",
            migrationId, Describe(present));

        db.Database.ExecuteSqlRaw(
            history.GetInsertScript(new HistoryRow(migrationId, ProductInfo.GetVersion())));
    }

    private static string Describe(IEnumerable<SchemaObject> objects) =>
        string.Join(", ", objects.Select(o => o.Description));

    /// <summary>
    /// The columns of a table, or null if the table cannot be read at all — which for our purposes
    /// means it is not there.
    /// </summary>
    private static List<string>? ReadColumnNames(DbContext db, string delimitedTable, ILogger logger)
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
            command.CommandText = $"SELECT * FROM {delimitedTable} WHERE 1 = 0";
            using DbDataReader reader = command.ExecuteReader();

            return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        }
        catch (DbException ex)
        {
            logger.LogDebug("Reconciler: table {Table} reads as absent ({Message})", delimitedTable, ex.Message);
            return null;
        }
        finally
        {
            if (wasClosed)
            {
                connection.Close();
            }
        }
    }

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
