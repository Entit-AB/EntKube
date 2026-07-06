using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace EntKube.Web.Services;

/// <summary>
/// Owns the connection to the dedicated telemetry Postgres database — a separate
/// database on the same cnpg server as the app's operational DB (connection string
/// "TelemetryConnection"). Kept deliberately isolated from <c>ApplicationDbContext</c>
/// so that log volume — high-throughput, bursty, low-value-per-row — can never contend
/// with, lock, or bloat the control-plane database the management app depends on to run.
///
/// Uses raw Npgsql rather than EF Core because the logs table is natively RANGE-partitioned
/// by time (which EF cannot model) and is written via COPY for ingest throughput.
///
/// If no "TelemetryConnection" connection string is configured (local SQLite dev, or a
/// deployment that has not provisioned the telemetry DB yet) the store reports
/// <see cref="IsEnabled"/> = false and every operation is a no-op, so nothing else breaks.
/// </summary>
public sealed class TelemetryStore : IAsyncDisposable
{
    private readonly ILogger<TelemetryStore> _logger;
    private readonly NpgsqlDataSource? _dataSource;

    /// <summary>How many days ahead partitions are pre-created, so writes never race a missing partition.</summary>
    private const int PartitionLookaheadDays = 2;

    /// <summary>The RANGE-partitioned-by-time tables this store manages (partitions + retention).</summary>
    private static readonly string[] PartitionedTables = ["logs", "spans"];

    /// <summary>True when a telemetry database is configured and usable.</summary>
    public bool IsEnabled => _dataSource is not null;

    /// <summary>Days of logs to retain; daily partitions older than this are dropped. Default 14.</summary>
    public int RetentionDays { get; }

    /// <summary>
    /// When true, a pg_trgm GIN index is built on the body column so substring/LIKE text search is
    /// index-accelerated (fast full-text filtering — something Loki can only do by linear scan).
    /// Off by default because GIN maintenance adds write amplification on the ingest hot path; turn
    /// it on for search-heavy, moderate-ingest deployments.
    /// </summary>
    public bool EnableTextSearchIndex { get; }

    /// <summary>
    /// When true the logs/spans tables (and their partitions) are UNLOGGED — no WAL, dramatically
    /// higher ingest throughput and less write I/O. Trade-off: their contents are TRUNCATED on an
    /// unclean shutdown/crash and are NOT streamed to cnpg replicas. Acceptable for best-effort
    /// telemetry; opt-in, and must be set before the tables are first created (IF NOT EXISTS won't
    /// convert an existing logged table).
    /// </summary>
    public bool EnableUnloggedTables { get; }

    /// <summary>
    /// Optional per-tenant retention overrides (tenantId → days) shorter than the global window.
    /// Applied as targeted row deletes in maintenance (the global partition-drop keeps the long tail).
    /// </summary>
    private readonly IReadOnlyDictionary<Guid, int> _tenantRetentionDays;

    public TelemetryStore(IConfiguration config, ILogger<TelemetryStore> logger)
    {
        _logger = logger;
        RetentionDays = config.GetValue<int?>("Telemetry:RetentionDays") ?? 14;
        EnableTextSearchIndex = config.GetValue<bool>("Telemetry:EnableTextSearchIndex");
        EnableUnloggedTables = config.GetValue<bool>("Telemetry:UnloggedTables");
        _tenantRetentionDays = ReadTenantRetention(config);

        string? conn = config.GetConnectionString("TelemetryConnection");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogInformation(
                "Telemetry store disabled: no 'TelemetryConnection' connection string configured.");
            return;
        }

        _dataSource = new NpgsqlDataSourceBuilder(conn).Build();
    }

    // Reads Telemetry:TenantRetentionDays ({ "<tenantGuid>": <days> }) into a validated map.
    private static IReadOnlyDictionary<Guid, int> ReadTenantRetention(IConfiguration config)
    {
        Dictionary<Guid, int> result = [];
        Dictionary<string, int>? raw = config.GetSection("Telemetry:TenantRetentionDays").Get<Dictionary<string, int>>();
        if (raw is not null)
            foreach ((string key, int days) in raw)
                if (Guid.TryParse(key, out Guid tid) && days > 0)
                    result[tid] = days;
        return result;
    }

    /// <summary>
    /// Rents a pooled connection to the telemetry database. Throws when the store is disabled —
    /// callers that may run without telemetry configured should check <see cref="IsEnabled"/> first.
    /// </summary>
    public ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        if (_dataSource is null)
            throw new InvalidOperationException("Telemetry store is not configured (no TelemetryConnection).");
        return _dataSource.OpenConnectionAsync(ct);
    }

    // ──────── Schema ────────

    /// <summary>
    /// Creates the partitioned logs table and its indexes if absent, ensures the daily
    /// partitions around "now" exist, and drops partitions past the retention window.
    /// Idempotent — safe to run on every startup and on the maintenance cycle.
    /// </summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_dataSource is null) return;

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);

        await using (NpgsqlCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = EnableUnloggedTables
                ? CreateTableSql.Replace("CREATE TABLE IF NOT EXISTS", "CREATE UNLOGGED TABLE IF NOT EXISTS")
                : CreateTableSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        DateTime now = DateTime.UtcNow;
        foreach (string table in PartitionedTables)
        {
            await EnsurePartitionsAsync(conn, table, now, ct);
            await DropExpiredPartitionsAsync(conn, table, now, ct);
        }

        // Per-tenant retention shorter than the global window (targeted deletes; the partition-drop
        // above keeps the long tail). Then age out stale stream rows.
        if (_tenantRetentionDays.Count > 0)
            await DeleteExpiredTenantRowsAsync(conn, now, ct);

        await using (NpgsqlCommand sc = conn.CreateCommand())
        {
            sc.CommandText = "DELETE FROM log_streams WHERE last_seen < @cutoff";
            sc.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, now.Date.AddDays(-RetentionDays));
            await sc.ExecuteNonQueryAsync(ct);
        }

        if (EnableTextSearchIndex)
            await EnsureTextSearchIndexAsync(conn, ct);

        _logger.LogInformation("Telemetry schema ensured (retention {RetentionDays}d).", RetentionDays);
    }

    /// <summary>
    /// Best-effort creation of the pg_trgm extension + a GIN trigram index on body for fast
    /// substring/LIKE search. Requires privilege to CREATE EXTENSION; failures are logged and
    /// swallowed so a locked-down database simply falls back to sequential text scans.
    /// </summary>
    private async Task EnsureTextSearchIndexAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        try
        {
            await using (NpgsqlCommand ext = conn.CreateCommand())
            {
                ext.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm;";
                await ext.ExecuteNonQueryAsync(ct);
            }
            await using (NpgsqlCommand idx = conn.CreateCommand())
            {
                idx.CommandText =
                    "CREATE INDEX IF NOT EXISTS logs_body_trgm ON logs USING gin (body gin_trgm_ops);";
                await idx.ExecuteNonQueryAsync(ct);
            }
            // GIN on the JSONB attributes accelerates the structured-field filter (@> containment
            // and jsonb_exists key checks) on both logs and spans.
            await using (NpgsqlCommand la = conn.CreateCommand())
            {
                la.CommandText = "CREATE INDEX IF NOT EXISTS logs_attrs_gin ON logs USING gin (attributes);";
                await la.ExecuteNonQueryAsync(ct);
            }
            await using (NpgsqlCommand sa = conn.CreateCommand())
            {
                sa.CommandText = "CREATE INDEX IF NOT EXISTS spans_attrs_gin ON spans USING gin (attributes);";
                await sa.ExecuteNonQueryAsync(ct);
            }
            _logger.LogInformation("Telemetry search indexes (pg_trgm body + JSONB attributes) ensured.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not create the pg_trgm text-search index; text search falls back to sequential scan.");
        }
    }

    // Parent table is RANGE-partitioned by ts; rows land in per-day child partitions
    // (see EnsurePartitionsAsync). The BRIN index on ts is tiny and ideal for append-only,
    // time-ordered data. The btree on (tenant_id, cluster_id, namespace, pod) backs the
    // label-value dropdowns and the filtered range queries the log viewer issues — every
    // query is scoped by tenant_id + cluster_id, so those lead the index. Indexes declared
    // on the partitioned parent propagate to every partition automatically (PG 11+).
    private const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS logs (
            ts          timestamptz NOT NULL,
            tenant_id   uuid        NOT NULL,
            cluster_id  uuid        NOT NULL,
            namespace   text        NOT NULL,
            pod         text        NOT NULL,
            container   text        NOT NULL,
            severity    smallint    NOT NULL DEFAULT 0,
            body        text        NOT NULL,
            trace_id    text,
            attributes  jsonb
        ) PARTITION BY RANGE (ts);

        CREATE INDEX IF NOT EXISTS logs_ts_brin
            ON logs USING brin (ts) WITH (pages_per_range = 32);

        CREATE INDEX IF NOT EXISTS logs_scope_ns_pod
            ON logs (tenant_id, cluster_id, namespace, pod);

        CREATE INDEX IF NOT EXISTS logs_scope_ts
            ON logs (tenant_id, cluster_id, ts DESC);

        CREATE TABLE IF NOT EXISTS spans (
            ts             timestamptz NOT NULL,
            tenant_id      uuid        NOT NULL,
            cluster_id     uuid        NOT NULL,
            trace_id       text        NOT NULL,
            span_id        text        NOT NULL,
            parent_span_id text,
            name           text        NOT NULL,
            service        text        NOT NULL,
            kind           smallint    NOT NULL DEFAULT 0,
            duration_ms    double precision NOT NULL DEFAULT 0,
            status_code    smallint    NOT NULL DEFAULT 0,
            namespace      text,
            pod            text,
            attributes     jsonb
        ) PARTITION BY RANGE (ts);

        CREATE INDEX IF NOT EXISTS spans_ts_brin
            ON spans USING brin (ts) WITH (pages_per_range = 32);

        -- Fetch a whole trace by id (and correlate a log's trace_id → its spans).
        CREATE INDEX IF NOT EXISTS spans_scope_trace
            ON spans (tenant_id, cluster_id, trace_id);

        -- Service-scoped trace listing + RED aggregates (rate/errors/duration) over time.
        CREATE INDEX IF NOT EXISTS spans_scope_service_ts
            ON spans (tenant_id, cluster_id, service, ts DESC);

        -- Distinct (namespace, pod, container) seen per cluster, upserted on ingest, so the log-viewer
        -- dropdowns are a tiny index lookup instead of a DISTINCT scan over the huge partitioned logs.
        CREATE TABLE IF NOT EXISTS log_streams (
            tenant_id  uuid        NOT NULL,
            cluster_id uuid        NOT NULL,
            namespace  text        NOT NULL,
            pod        text        NOT NULL,
            container  text        NOT NULL,
            last_seen  timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (tenant_id, cluster_id, namespace, pod, container)
        );

        CREATE INDEX IF NOT EXISTS log_streams_scope ON log_streams (tenant_id, cluster_id);
        """;

    /// <summary>
    /// Ensures daily partitions exist across the whole retention window plus the lookahead:
    /// [now-RetentionDays, now+lookahead]. Covering the full window back means late-arriving
    /// logs (a buffered collector catching up after an outage) always have a partition, and the
    /// lookahead absorbs mild clock skew — so a write never lands in the (unpartitioned) parent.
    /// Bounds are pinned to UTC (<c>+00</c>) so they align with the timestamptz column regardless
    /// of the server/session TimeZone (bare date literals would otherwise anchor at local midnight).
    /// </summary>
    public async Task EnsurePartitionsAsync(
        NpgsqlConnection conn, string table, DateTime utcNow, CancellationToken ct = default)
    {
        for (int offset = -RetentionDays; offset <= PartitionLookaheadDays; offset++)
        {
            DateTime day = utcNow.Date.AddDays(offset);
            string suffix = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string from = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string to = day.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            await using NpgsqlCommand cmd = conn.CreateCommand();
            // table is an internal literal from PartitionedTables; suffix/from/to derive from a
            // DateTime — no injection surface. Partitions match the parent's logged/unlogged persistence.
            string unlogged = EnableUnloggedTables ? "UNLOGGED " : "";
            cmd.CommandText =
                $"CREATE {unlogged}TABLE IF NOT EXISTS {table}_{suffix} PARTITION OF {table} " +
                $"FOR VALUES FROM ('{from} 00:00:00+00') TO ('{to} 00:00:00+00');";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Drops daily partitions entirely older than the retention window. Dropping a partition
    /// is instant and reclaims disk immediately — the reason time-partitioning beats DELETE
    /// for retention on high-volume log data (no row-by-row delete, no vacuum bloat).
    /// </summary>
    public async Task DropExpiredPartitionsAsync(
        NpgsqlConnection conn, string table, DateTime utcNow, CancellationToken ct = default)
    {
        DateTime cutoff = utcNow.Date.AddDays(-RetentionDays);

        List<string> partitions = [];
        await using (NpgsqlCommand find = conn.CreateCommand())
        {
            // Only true child partitions of <table> whose name is exactly <table>_<8 digits>.
            find.CommandText = """
                SELECT c.relname
                FROM pg_inherits i
                JOIN pg_class c ON c.oid = i.inhrelid
                JOIN pg_class p ON p.oid = i.inhparent
                WHERE p.relname = @table AND c.relname ~ ('^' || @table || '_[0-9]{8}$');
                """;
            find.Parameters.AddWithValue("table", table);
            await using NpgsqlDataReader reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                partitions.Add(reader.GetString(0));
        }

        foreach (string part in partitions)
        {
            string datePart = part[(table.Length + 1)..];
            if (!DateTime.TryParseExact(datePart, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
                continue;
            if (day >= cutoff) continue;

            await using NpgsqlCommand drop = conn.CreateCommand();
            // Name validated by the SQL regex + exact date parse above → safe to interpolate.
            drop.CommandText = $"DROP TABLE IF EXISTS {part};";
            await drop.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Dropped expired telemetry partition {Partition}.", part);
        }
    }

    /// <summary>
    /// Applies per-tenant retention overrides shorter than the global window as targeted row deletes.
    /// (True per-partition drop can't be per-tenant since a partition holds all tenants; these deletes
    /// only run for the handful of tenants with an explicit shorter retention, so bloat is bounded.)
    /// </summary>
    private async Task DeleteExpiredTenantRowsAsync(NpgsqlConnection conn, DateTime utcNow, CancellationToken ct)
    {
        foreach ((Guid tenantId, int days) in _tenantRetentionDays)
        {
            if (days >= RetentionDays) continue;   // global partition-drop already covers these
            DateTime cutoff = utcNow.Date.AddDays(-days);

            foreach (string table in PartitionedTables)
            {
                await using NpgsqlCommand cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE tenant_id = @t AND ts < @cutoff";
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
                int deleted = await cmd.ExecuteNonQueryAsync(ct);
                if (deleted > 0)
                    _logger.LogInformation(
                        "Per-tenant retention: deleted {N} {Table} rows for tenant {Tenant} (>{Days}d).",
                        deleted, table, tenantId, days);
            }
        }
    }

    // ──────── Ingest ────────

    /// <summary>
    /// Bulk-writes a batch of log records via binary COPY — the fastest Postgres ingest path,
    /// an order of magnitude beyond row-by-row INSERT. The caller stamps tenant/cluster identity
    /// (resolved from the ingest token) so those columns are never taken from untrusted payload.
    /// Returns the number of rows written (0 when the store is disabled or the batch is empty).
    /// </summary>
    public async Task<int> WriteLogsAsync(
        Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records, CancellationToken ct = default)
    {
        if (_dataSource is null || records.Count == 0) return 0;

        DateTime now = DateTime.UtcNow;

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using (NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY logs (ts, tenant_id, cluster_id, namespace, pod, container, severity, body, trace_id, attributes) " +
            "FROM STDIN (FORMAT BINARY)", ct))
        {
        foreach (LogIngestRecord r in records)
        {
            DateTime ts = ClampTimestamp(r.Timestamp, now);

            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ts, NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(tenantId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(clusterId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(r.Namespace, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(r.Pod, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(r.Container, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(r.Severity, NpgsqlDbType.Smallint, ct);
            await writer.WriteAsync(r.Body, NpgsqlDbType.Text, ct);

            if (r.TraceId is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(r.TraceId, NpgsqlDbType.Text, ct);

            if (r.AttributesJson is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(r.AttributesJson, NpgsqlDbType.Jsonb, ct);
        }

            await writer.CompleteAsync(ct);
        }

        await UpsertStreamsAsync(conn, tenantId, clusterId, records, ct);
        return records.Count;
    }

    // Upserts the batch's distinct (namespace, pod, container) streams so label dropdowns read a tiny
    // table instead of DISTINCT-scanning logs. last_seen is bumped so streams age out via maintenance.
    private static async Task UpsertStreamsAsync(
        NpgsqlConnection conn, Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records, CancellationToken ct)
    {
        HashSet<(string Ns, string Pod, string Container)> streams = [];
        foreach (LogIngestRecord r in records) streams.Add((r.Namespace, r.Pod, r.Container));
        if (streams.Count == 0) return;

        StringBuilder sb = new("INSERT INTO log_streams (tenant_id, cluster_id, namespace, pod, container) VALUES ");
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", clusterId);
        int i = 0;
        foreach ((string ns, string pod, string container) in streams)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"(@t, @c, @ns{i}, @pod{i}, @con{i})");
            cmd.Parameters.AddWithValue($"ns{i}", ns);
            cmd.Parameters.AddWithValue($"pod{i}", pod);
            cmd.Parameters.AddWithValue($"con{i}", container);
            i++;
        }
        sb.Append(" ON CONFLICT (tenant_id, cluster_id, namespace, pod, container) DO UPDATE SET last_seen = now()");
        cmd.CommandText = sb.ToString();
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Bulk-writes a batch of spans via binary COPY. Same clamping/atomic-batch rationale as
    /// <see cref="WriteLogsAsync"/>; tenant/cluster identity is stamped from the ingest token.
    /// </summary>
    public async Task<int> WriteSpansAsync(
        Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records, CancellationToken ct = default)
    {
        if (_dataSource is null || records.Count == 0) return 0;

        DateTime now = DateTime.UtcNow;

        await using NpgsqlConnection conn = await _dataSource.OpenConnectionAsync(ct);
        await using NpgsqlBinaryImporter writer = await conn.BeginBinaryImportAsync(
            "COPY spans (ts, tenant_id, cluster_id, trace_id, span_id, parent_span_id, name, service, " +
            "kind, duration_ms, status_code, namespace, pod, attributes) FROM STDIN (FORMAT BINARY)", ct);

        foreach (SpanIngestRecord s in records)
        {
            DateTime ts = ClampTimestamp(s.Start, now);

            await writer.StartRowAsync(ct);
            await writer.WriteAsync(ts, NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync(tenantId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(clusterId, NpgsqlDbType.Uuid, ct);
            await writer.WriteAsync(s.TraceId, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(s.SpanId, NpgsqlDbType.Text, ct);

            if (s.ParentSpanId is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(s.ParentSpanId, NpgsqlDbType.Text, ct);

            await writer.WriteAsync(s.Name, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(s.Service, NpgsqlDbType.Text, ct);
            await writer.WriteAsync(s.Kind, NpgsqlDbType.Smallint, ct);
            await writer.WriteAsync(s.DurationMs, NpgsqlDbType.Double, ct);
            await writer.WriteAsync(s.StatusCode, NpgsqlDbType.Smallint, ct);

            if (s.Namespace is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(s.Namespace, NpgsqlDbType.Text, ct);

            if (s.Pod is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(s.Pod, NpgsqlDbType.Text, ct);

            if (s.AttributesJson is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(s.AttributesJson, NpgsqlDbType.Jsonb, ct);
        }

        await writer.CompleteAsync(ct);
        return records.Count;
    }

    // Clamps a timestamp into the partitioned range [now-(retention-1), now+lookahead] so a
    // pathological value (seconds-as-nanos giving 1970, far-future clock skew, stale backfill beyond
    // retention) can never land outside all partitions. Postgres binary COPY is atomic, so one such
    // row would otherwise fail the ENTIRE batch and — since the collector retries on 5xx — wedge the
    // pipeline permanently. Lossy only for already-pathological rows.
    private DateTime ClampTimestamp(DateTime ts, DateTime now)
    {
        DateTime minTs = now.Date.AddDays(-(RetentionDays - 1));
        DateTime maxTs = now.Date.AddDays(PartitionLookaheadDays + 1);
        return ts < minTs ? minTs : (ts >= maxTs ? now : ts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
            await _dataSource.DisposeAsync();
    }
}

/// <summary>
/// One parsed log line ready to write. Tenant/cluster identity is intentionally NOT here —
/// it is stamped by the ingest endpoint from the verified ingest token, never from the payload.
/// <paramref name="Severity"/> is the numeric <see cref="LogLevel"/> value. <paramref name="Timestamp"/>
/// must be UTC. <paramref name="AttributesJson"/> is a JSON object string (or null).
/// </summary>
public sealed record LogIngestRecord(
    DateTime Timestamp,
    string Namespace,
    string Pod,
    string Container,
    short Severity,
    string Body,
    string? TraceId,
    string? AttributesJson);

/// <summary>
/// One parsed span ready to write. Tenant/cluster identity is stamped by the ingest endpoint from
/// the verified token, not the payload. <paramref name="Start"/> must be UTC; <paramref name="Kind"/>
/// is the OTLP SpanKind (0..5); <paramref name="StatusCode"/> is the OTLP status (0 unset, 1 ok, 2 error).
/// </summary>
public sealed record SpanIngestRecord(
    DateTime Start,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Service,
    short Kind,
    double DurationMs,
    short StatusCode,
    string? Namespace,
    string? Pod,
    string? AttributesJson);
