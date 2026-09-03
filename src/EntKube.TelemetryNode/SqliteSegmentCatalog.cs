using EntKube.Telemetry;
using EntKube.Web.Data;
using Microsoft.Data.Sqlite;

namespace EntKube.TelemetryNode;

/// <summary>
/// The in-cluster <see cref="ISegmentCatalog"/>: a single SQLite file on the node's PersistentVolume,
/// alongside the index data it describes.
///
/// A cluster's telemetry node has no access to the management-plane database and should not need one —
/// coupling ingest to a remote database is exactly the property this whole move exists to remove. The
/// catalog is small (one row per sealed segment, not per event) and its access pattern is trivial: append
/// on seal, range-scan on query, delete on retention. SQLite in WAL mode serves that comfortably while the
/// indexer writes and the querier reads.
///
/// Timestamps are stored as epoch milliseconds rather than text, so the range scan compares integers and
/// the covering index is genuinely ordered. Losing this file loses only the *map*, never the data: every
/// segment archive is still in object storage under a key that encodes its signal and date, so the catalog
/// can be rebuilt by listing the bucket.
/// </summary>
public sealed class SqliteSegmentCatalog : ISegmentCatalog
{
    private readonly string _connectionString;

    public SqliteSegmentCatalog(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared cache + WAL so the seal path and the query path aren't serialised behind each other.
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        using SqliteConnection db = Open();
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Segments (
                Id        TEXT    NOT NULL PRIMARY KEY,
                TenantId  TEXT    NOT NULL,
                Signal    TEXT    NOT NULL,
                MinTs     INTEGER NOT NULL,
                MaxTs     INTEGER NOT NULL,
                DocCount  INTEGER NOT NULL,
                ObjectKey TEXT    NOT NULL,
                SizeBytes INTEGER NOT NULL,
                SealedAt  INTEGER NOT NULL
            );
            -- Mirrors the management plane's index: the pruning query filters tenant+signal then scans a
            -- time range, so this covers it without touching the table.
            CREATE INDEX IF NOT EXISTS IX_Segments_Tenant_Signal_MaxTs_MinTs
                ON Segments (TenantId, Signal, MaxTs, MinTs);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    public async Task AddAsync(TelemetrySegment segment, CancellationToken ct = default)
    {
        await using SqliteConnection db = Open();
        await using SqliteCommand cmd = db.CreateCommand();
        // A re-seal of the same id (a retried upload) should refresh the row, not fail the seal.
        cmd.CommandText = """
            INSERT INTO Segments (Id, TenantId, Signal, MinTs, MaxTs, DocCount, ObjectKey, SizeBytes, SealedAt)
            VALUES ($id, $tenant, $signal, $min, $max, $docs, $key, $size, $sealed)
            ON CONFLICT(Id) DO UPDATE SET
                MinTs = excluded.MinTs, MaxTs = excluded.MaxTs, DocCount = excluded.DocCount,
                ObjectKey = excluded.ObjectKey, SizeBytes = excluded.SizeBytes, SealedAt = excluded.SealedAt;
            """;
        cmd.Parameters.AddWithValue("$id", segment.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$tenant", segment.TenantId.ToString("N"));
        cmd.Parameters.AddWithValue("$signal", segment.Signal);
        cmd.Parameters.AddWithValue("$min", TelemetryTime.ToEpochMillis(segment.MinTs));
        cmd.Parameters.AddWithValue("$max", TelemetryTime.ToEpochMillis(segment.MaxTs));
        cmd.Parameters.AddWithValue("$docs", segment.DocCount);
        cmd.Parameters.AddWithValue("$key", segment.ObjectKey);
        cmd.Parameters.AddWithValue("$size", segment.SizeBytes);
        cmd.Parameters.AddWithValue("$sealed", TelemetryTime.ToEpochMillis(segment.SealedAt));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<TelemetrySegment>> ListOverlappingAsync(
        Guid tenantId, string signal, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        await using SqliteConnection db = Open();
        await using SqliteCommand cmd = db.CreateCommand();
        // "Overlaps [from, to)" — a segment is in scope unless it ends before the window or starts after it.
        cmd.CommandText = """
            SELECT Id, TenantId, Signal, MinTs, MaxTs, DocCount, ObjectKey, SizeBytes, SealedAt
            FROM Segments
            WHERE TenantId = $tenant AND Signal = $signal
              AND ($from IS NULL OR MaxTs >= $from)
              AND ($to   IS NULL OR MinTs <  $to)
            ORDER BY MinTs;
            """;
        cmd.Parameters.AddWithValue("$tenant", tenantId.ToString("N"));
        cmd.Parameters.AddWithValue("$signal", signal);
        cmd.Parameters.AddWithValue("$from", from is DateTime f ? TelemetryTime.ToEpochMillis(f) : DBNull.Value);
        cmd.Parameters.AddWithValue("$to", to is DateTime t ? TelemetryTime.ToEpochMillis(t) : DBNull.Value);

        var results = new List<TelemetrySegment>();
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(Read(reader));
        return results;
    }

    public async Task<IReadOnlyList<TelemetrySegment>> RemoveExpiredAsync(
        Guid tenantId, string signal, DateTime cutoff, CancellationToken ct = default)
    {
        await using SqliteConnection db = Open();
        await using SqliteTransaction tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);

        var expired = new List<TelemetrySegment>();
        await using (SqliteCommand select = db.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = """
                SELECT Id, TenantId, Signal, MinTs, MaxTs, DocCount, ObjectKey, SizeBytes, SealedAt
                FROM Segments
                WHERE TenantId = $tenant AND Signal = $signal AND MaxTs < $cutoff;
                """;
            select.Parameters.AddWithValue("$tenant", tenantId.ToString("N"));
            select.Parameters.AddWithValue("$signal", signal);
            select.Parameters.AddWithValue("$cutoff", TelemetryTime.ToEpochMillis(cutoff));
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) expired.Add(Read(reader));
        }

        if (expired.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return [];
        }

        // Rows go before their objects do, so no query can resolve a segment whose archive is being deleted.
        await using (SqliteCommand delete = db.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM Segments
                WHERE TenantId = $tenant AND Signal = $signal AND MaxTs < $cutoff;
                """;
            delete.Parameters.AddWithValue("$tenant", tenantId.ToString("N"));
            delete.Parameters.AddWithValue("$signal", signal);
            delete.Parameters.AddWithValue("$cutoff", TelemetryTime.ToEpochMillis(cutoff));
            await delete.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return expired;
    }

    public async Task<DateTime?> GetMinTsAsync(Guid tenantId, string signal, CancellationToken ct = default)
    {
        await using SqliteConnection db = Open();
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT MIN(MinTs) FROM Segments WHERE TenantId = $tenant AND Signal = $signal;";
        cmd.Parameters.AddWithValue("$tenant", tenantId.ToString("N"));
        cmd.Parameters.AddWithValue("$signal", signal);

        object? value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : TelemetryTime.FromEpochMillis(Convert.ToInt64(value));
    }

    private static TelemetrySegment Read(SqliteDataReader r) => new()
    {
        Id = Guid.ParseExact(r.GetString(0), "N"),
        TenantId = Guid.ParseExact(r.GetString(1), "N"),
        Signal = r.GetString(2),
        MinTs = TelemetryTime.FromEpochMillis(r.GetInt64(3)),
        MaxTs = TelemetryTime.FromEpochMillis(r.GetInt64(4)),
        DocCount = r.GetInt64(5),
        ObjectKey = r.GetString(6),
        SizeBytes = r.GetInt64(7),
        SealedAt = TelemetryTime.FromEpochMillis(r.GetInt64(8)),
    };
}
