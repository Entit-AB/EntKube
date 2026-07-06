using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace EntKube.Web.Services;

/// <summary>
/// Queries the native telemetry Postgres store (<see cref="TelemetryStore"/>) for logs and
/// returns the SAME <see cref="LokiLogStream"/>/<see cref="LokiLogEntry"/> DTOs as
/// <see cref="LokiService"/>, so the log viewers are backend-agnostic and a dispatcher can
/// route native-vs-Loki per cluster without any UI change.
///
/// Every query is scoped by both cluster_id AND the cluster's tenant_id (resolved from the
/// operational DB) — defence-in-depth, since the one telemetry database holds all tenants' logs.
/// The method surface intentionally mirrors LokiService one-for-one.
/// </summary>
public class PgLogService(
    TelemetryStore store,
    ClusterTenantResolver tenants,
    ILogger<PgLogService> logger)
    : TelemetryQueryServiceBase(store, tenants, logger)
{
    /// <summary>
    /// Does the store hold ANY log for this cluster (within retention)? Used by the dispatcher to
    /// route native-vs-Loki — a cluster still shipping to Loki has no native rows and transparently
    /// falls back. Unbounded in time so a quiet-but-populated native cluster isn't mis-routed to Loki.
    /// </summary>
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => HasAnyAsync("logs", clusterId, ct);

    public Task<KubernetesOperationResult<List<string>>> GetNamespacesAsync(
        Guid clusterId, CancellationToken ct = default)
        => LabelValuesAsync(clusterId, "namespace", null, ct);

    public Task<KubernetesOperationResult<List<string>>> GetPodsAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => LabelValuesAsync(clusterId, "pod", namespaceName, ct);

    public Task<KubernetesOperationResult<List<string>>> GetContainersAsync(
        Guid clusterId, string namespaceName, CancellationToken ct = default)
        => LabelValuesAsync(clusterId, "container", namespaceName, ct);

    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, CancellationToken ct = default)
    {
        if (!Store.IsEnabled)
            return KubernetesOperationResult<List<LokiLogStream>>.Failure("Native telemetry store is not configured.");
        if (filter.Namespaces.Count == 0)
            return KubernetesOperationResult<List<LokiLogStream>>.Success([]);

        Guid? tenantId = await ResolveTenantAsync(clusterId, ct);
        if (tenantId is null)
            return KubernetesOperationResult<List<LokiLogStream>>.Failure("Cluster not found.");

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            List<string> where = [];
            ApplyFilters(cmd, where, tenantId.Value, clusterId, filter, from, to);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.CommandText =
                "SELECT ts, namespace, pod, container, severity, body, trace_id FROM logs WHERE " +
                string.Join(" AND ", where) + " ORDER BY ts DESC LIMIT @limit";

            return KubernetesOperationResult<List<LokiLogStream>>.Success(await MapStreamsAsync(cmd, ct));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Telemetry range query failed (cluster {Cluster})", clusterId);
            return KubernetesOperationResult<List<LokiLogStream>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Native logs correlated to a trace: all log lines carrying the given trace_id, newest first.
    /// Powers the "logs for this trace" panel in the trace waterfall. Native-only (as traces are).
    /// </summary>
    public async Task<KubernetesOperationResult<List<LokiLogStream>>> QueryByTraceAsync(
        Guid clusterId, string traceId, int limit = 500, CancellationToken ct = default)
    {
        if (!Store.IsEnabled)
            return KubernetesOperationResult<List<LokiLogStream>>.Failure("Native telemetry store is not configured.");

        Guid? tenantId = await ResolveTenantAsync(clusterId, ct);
        if (tenantId is null)
            return KubernetesOperationResult<List<LokiLogStream>>.Failure("Cluster not found.");

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT ts, namespace, pod, container, severity, body, trace_id FROM logs " +
                "WHERE tenant_id = @t AND cluster_id = @c AND trace_id = @trace ORDER BY ts DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("trace", traceId);
            cmd.Parameters.AddWithValue("limit", limit);

            return KubernetesOperationResult<List<LokiLogStream>>.Success(await MapStreamsAsync(cmd, ct));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Telemetry trace-logs query failed (cluster {Cluster}, trace {Trace})", clusterId, traceId);
            return KubernetesOperationResult<List<LokiLogStream>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Log-volume histogram: row counts per time bucket (with the error+fatal subcount) over the
    /// same filters as the range query. Powers a Grafana-style volume strip above the log list —
    /// a capability Loki only approximates with metric queries. Buckets span [from, to] evenly.
    /// </summary>
    public async Task<KubernetesOperationResult<List<LogHistogramBucket>>> GetHistogramAsync(
        Guid clusterId, LogQueryFilter filter, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default)
    {
        if (!Store.IsEnabled)
            return KubernetesOperationResult<List<LogHistogramBucket>>.Failure("Native telemetry store is not configured.");
        if (filter.Namespaces.Count == 0)
            return KubernetesOperationResult<List<LogHistogramBucket>>.Success([]);

        Guid? tenantId = await ResolveTenantAsync(clusterId, ct);
        if (tenantId is null)
            return KubernetesOperationResult<List<LogHistogramBucket>>.Failure("Cluster not found.");

        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        TimeSpan interval = TimeSpan.FromSeconds(bucketSecs);

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            List<string> where = [];
            ApplyFilters(cmd, where, tenantId.Value, clusterId, filter, from, to);
            cmd.Parameters.AddWithValue("interval", interval);
            // date_bin aligns buckets to the @from origin; severity >= 4 is Error+Fatal.
            cmd.CommandText =
                "SELECT date_bin(@interval, ts, @from) AS b, count(*) AS total, " +
                "count(*) FILTER (WHERE severity >= 4) AS errors FROM logs WHERE " +
                string.Join(" AND ", where) + " GROUP BY b ORDER BY b";

            List<LogHistogramBucket> result = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                result.Add(new LogHistogramBucket(r.GetDateTime(0), r.GetInt64(1), r.GetInt64(2)));

            return KubernetesOperationResult<List<LogHistogramBucket>>.Success(result);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Telemetry histogram query failed (cluster {Cluster})", clusterId);
            return KubernetesOperationResult<List<LogHistogramBucket>>.Failure(ex.Message);
        }
    }

    // ──────── internal ────────

    // Builds the WHERE clauses shared by the range query and the histogram (so they filter over the
    // exact same set) and binds their parameters onto cmd. Adds the ts bounds; @from doubles as the
    // date_bin origin for the histogram.
    private static void ApplyFilters(
        NpgsqlCommand cmd, List<string> where, Guid tenantId, Guid clusterId,
        LogQueryFilter f, DateTime from, DateTime to)
    {
        where.Add("tenant_id = @t");
        where.Add("cluster_id = @c");
        where.Add("namespace = ANY(@ns)");
        where.Add("ts >= @from");
        where.Add("ts < @to");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", clusterId);
        cmd.Parameters.AddWithValue("ns", f.Namespaces.ToArray());
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());

        // pod may be a plain name (LogBrowser) OR a LogQL regex alternation like "(api|worker)"
        // (CustomerLogBrowser deployment scoping). Mirror Loki's anchored pod=~"<pat>.*" with an
        // anchored POSIX regex — a literal starts_with would never match the "(...)" alternation.
        if (!string.IsNullOrEmpty(f.Pod))
        {
            where.Add("pod ~ @pod");
            cmd.Parameters.AddWithValue("pod", "^(" + f.Pod + ")");
        }
        if (!string.IsNullOrEmpty(f.Container))
        {
            where.Add("container = @container");
            cmd.Parameters.AddWithValue("container", f.Container);
        }
        // Case-sensitive substring (matches Loki's |=), expressed as LIKE so a pg_trgm GIN index can
        // accelerate it when enabled; without the index it degrades to the same scan as before.
        if (!string.IsNullOrEmpty(f.Text))
        {
            where.Add("body LIKE @text");
            cmd.Parameters.AddWithValue("text", "%" + EscapeLike(f.Text) + "%");
        }
        // Push the level filter into SQL so "errors only" returns errors within the LIMIT rather than
        // a post-filtered slice of mixed lines. severity stores the LogLevel value; Error is 4.
        if (f.MinLevel > LogLevel.None)
        {
            where.Add("severity >= @minLevel");
            cmd.Parameters.AddWithValue("minLevel", (short)f.MinLevel);
        }
        // Structured-field filter on the JSONB attributes — a capability Loki lacks without a
        // purpose-built pipeline. key+value → containment (@>) and key-only → existence, both of
        // which a GIN index on attributes accelerates (enabled via Telemetry:EnableTextSearchIndex).
        if (!string.IsNullOrEmpty(f.AttrKey))
        {
            if (!string.IsNullOrEmpty(f.AttrValue))
            {
                where.Add("attributes @> @attrFilter");
                cmd.Parameters.AddWithValue("attrFilter", NpgsqlDbType.Jsonb,
                    JsonSerializer.Serialize(new Dictionary<string, string> { [f.AttrKey] = f.AttrValue }));
            }
            else
            {
                where.Add("jsonb_exists(attributes, @attrKey)");
                cmd.Parameters.AddWithValue("attrKey", f.AttrKey);
            }
        }
    }

    // Escapes LIKE wildcards so a user's filter text is matched literally (default '\' escape char).
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // Reads a (ts, namespace, pod, container, severity, body) result set and groups rows into streams
    // by label set — the shape LokiService returns and the viewers expect. Shared by the range and
    // by-trace queries.
    private static async Task<List<LokiLogStream>> MapStreamsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        Dictionary<(string, string, string), LokiLogStream> streams = [];
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            DateTime ts = r.GetDateTime(0);
            string ns = r.GetString(1);
            string pod = r.GetString(2);
            string container = r.GetString(3);
            short sev = r.GetInt16(4);
            string body = r.GetString(5);
            string? traceId = r.IsDBNull(6) ? null : r.GetString(6);

            (string, string, string) key = (ns, pod, container);
            if (!streams.TryGetValue(key, out LokiLogStream? stream))
            {
                stream = new LokiLogStream
                {
                    Labels = new() { ["namespace"] = ns, ["pod"] = pod, ["container"] = container }
                };
                streams[key] = stream;
            }

            stream.Entries.Add(new LokiLogEntry { Timestamp = ts, Line = body, DetectedLevel = (LogLevel)sev, TraceId = traceId });
        }
        return [.. streams.Values];
    }

    private async Task<KubernetesOperationResult<List<string>>> LabelValuesAsync(
        Guid clusterId, string column, string? namespaceName, CancellationToken ct)
    {
        if (!Store.IsEnabled)
            return KubernetesOperationResult<List<string>>.Failure("Native telemetry store is not configured.");

        Guid? tenantId = await ResolveTenantAsync(clusterId, ct);
        if (tenantId is null)
            return KubernetesOperationResult<List<string>>.Failure("Cluster not found.");

        // 'column' is a fixed internal literal (namespace/pod/container), never user input. Reads the
        // small log_streams table (distinct streams upserted on ingest) — not a DISTINCT scan of logs.
        bool filterNs = column != "namespace" && !string.IsNullOrEmpty(namespaceName);
        string sql = $"SELECT DISTINCT {column} AS v FROM log_streams WHERE tenant_id = @t AND cluster_id = @c"
                   + (filterNs ? " AND namespace = @ns" : "")
                   + $" AND {column} <> '' ORDER BY v";

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            if (filterNs) cmd.Parameters.AddWithValue("ns", namespaceName!);

            List<string> values = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) values.Add(r.GetString(0));

            return KubernetesOperationResult<List<string>>.Success(values);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Telemetry label query failed ({Column}, cluster {Cluster})", column, clusterId);
            return KubernetesOperationResult<List<string>>.Failure(ex.Message);
        }
    }

    private Task<Guid?> ResolveTenantAsync(Guid clusterId, CancellationToken ct)
        => ResolveOrNull(clusterId, ct);
}

/// <summary>One time bucket of the log-volume histogram: total rows and the error+fatal subcount.</summary>
public sealed record LogHistogramBucket(DateTime Start, long Total, long Errors);
