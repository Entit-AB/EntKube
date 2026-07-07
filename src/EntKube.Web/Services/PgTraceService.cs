using Npgsql;
using NpgsqlTypes;

namespace EntKube.Web.Services;

/// <summary>
/// Queries the native telemetry <c>spans</c> table for APM/trace views: trace search, full-trace
/// fetch (waterfall), the service list, and RED (rate/errors/duration) aggregates. Native-only —
/// there is no Loki fallback for traces — so the UI checks <see cref="HasDataAsync"/> to decide
/// whether to show trace features. Every query is scoped by tenant_id + cluster_id.
/// </summary>
public class PgTraceService(TelemetryStore store, ClusterTenantResolver tenants, ILogger<PgTraceService> logger)
    : TelemetryQueryServiceBase(store, tenants, logger)
{
    /// <summary>Whether the native store holds any span for this cluster (drives showing trace UI).</summary>
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => HasAnyAsync("spans", clusterId, ct);

    // Builds "AND namespace = ANY(@ns)" and binds @ns when a namespace scope is supplied.
    // The customer portal passes the namespaces of a customer's app deployments so spans in
    // other namespaces (other customers) are never returned; tenant-side callers pass null
    // for cluster-wide scope. Returns "" (no clause) when the scope is null/empty.
    // Call at most once per command — it binds @ns; reuse the returned string if the SQL
    // references the filter in more than one place.
    private static string NsScope(NpgsqlCommand cmd, IReadOnlyList<string>? namespaces, string column = "namespace")
    {
        if (namespaces is null || namespaces.Count == 0) return "";
        cmd.Parameters.AddWithValue("ns", namespaces.ToArray());
        return $" AND {column} = ANY(@ns)";
    }

    /// <summary>Distinct service names seen in the store, for the trace-search service dropdown.</summary>
    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(
        Guid clusterId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT service FROM spans WHERE tenant_id = @t AND cluster_id = @c"
                + NsScope(cmd, namespaces) + " ORDER BY service";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);

            List<string> services = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) services.Add(r.GetString(0));
            return KubernetesOperationResult<List<string>>.Success(services);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Trace service list failed (cluster {Cluster})", clusterId);
            return KubernetesOperationResult<List<string>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Trace search: one summary row per trace in the window, optionally filtered by involved service,
    /// minimum duration, and errors-only. The root span (parent_span_id IS NULL) supplies the service
    /// and operation name shown in the list.
    /// </summary>
    public async Task<KubernetesOperationResult<List<TraceSummary>>> ListTracesAsync(
        Guid clusterId, string? service, DateTime from, DateTime to,
        double minDurationMs = 0, bool errorsOnly = false, int limit = 50, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<TraceSummary>>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
            cmd.Parameters.AddWithValue("minDur", minDurationMs);
            cmd.Parameters.AddWithValue("limit", limit);

            // Namespace scope (customer isolation): applied to BOTH the service subquery and the
            // outer scan so only the customer's spans are considered. Built once (binds @ns once).
            string nsScope = NsScope(cmd, namespaces);

            // Restrict to traces that involve the chosen service (any span), then summarize each trace.
            string serviceFilter = "";
            if (!string.IsNullOrEmpty(service))
            {
                serviceFilter =
                    " AND trace_id IN (SELECT trace_id FROM spans WHERE tenant_id = @t AND cluster_id = @c " +
                    "AND ts >= @from AND ts < @to" + nsScope + " AND service = @service)";
                cmd.Parameters.AddWithValue("service", service);
            }
            string errorsHaving = errorsOnly ? " AND count(*) FILTER (WHERE status_code = 2) > 0" : "";

            // Trace duration = wall-clock extent (latest span end − earliest start), not max(single-span
            // duration) which would report a long child instead of the request. root_service/root_name
            // come from the root span (parent_span_id IS NULL sorts first via array_agg) when one is in
            // the window; otherwise they degrade to the earliest span (best effort).
            const string durationExpr =
                "EXTRACT(EPOCH FROM (max(ts + make_interval(secs => duration_ms / 1000.0)) - min(ts))) * 1000";
            cmd.CommandText =
                "SELECT trace_id, min(ts) AS start, " + durationExpr + " AS duration_ms, count(*) AS span_count, " +
                "count(*) FILTER (WHERE status_code = 2) AS error_count, " +
                "(array_agg(service ORDER BY (parent_span_id IS NULL) DESC, ts))[1] AS root_service, " +
                "(array_agg(name ORDER BY (parent_span_id IS NULL) DESC, ts))[1] AS root_name " +
                "FROM spans WHERE tenant_id = @t AND cluster_id = @c AND ts >= @from AND ts < @to" +
                nsScope + serviceFilter +
                " GROUP BY trace_id HAVING " + durationExpr + " >= @minDur" + errorsHaving +
                " ORDER BY start DESC LIMIT @limit";

            List<TraceSummary> traces = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                traces.Add(new TraceSummary(
                    TraceId: r.GetString(0),
                    Start: r.GetDateTime(1),
                    DurationMs: r.GetDouble(2),
                    SpanCount: (int)r.GetInt64(3),
                    ErrorCount: (int)r.GetInt64(4),
                    RootService: r.IsDBNull(5) ? "" : r.GetString(5),
                    RootName: r.IsDBNull(6) ? "" : r.GetString(6)));
            return KubernetesOperationResult<List<TraceSummary>>.Success(traces);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Trace list failed (cluster {Cluster})", clusterId);
            return KubernetesOperationResult<List<TraceSummary>>.Failure(ex.Message);
        }
    }

    /// <summary>All spans of a trace, ordered by start time — the input to the waterfall view.</summary>
    public async Task<KubernetesOperationResult<List<SpanRecord>>> GetTraceAsync(
        Guid clusterId, string traceId, CancellationToken ct = default, IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<SpanRecord>>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            // Namespace scope keeps a customer's waterfall to their own spans even if a trace
            // happens to cross a namespace boundary — no other customer's spans are returned.
            cmd.CommandText =
                "SELECT ts, span_id, parent_span_id, name, service, kind, duration_ms, status_code, attributes " +
                "FROM spans WHERE tenant_id = @t AND cluster_id = @c AND trace_id = @trace"
                + NsScope(cmd, namespaces) + " ORDER BY ts";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("trace", traceId);

            List<SpanRecord> spans = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                spans.Add(new SpanRecord(
                    Start: r.GetDateTime(0),
                    SpanId: r.GetString(1),
                    ParentSpanId: r.IsDBNull(2) ? null : r.GetString(2),
                    Name: r.GetString(3),
                    Service: r.GetString(4),
                    Kind: r.GetInt16(5),
                    DurationMs: r.GetDouble(6),
                    StatusCode: r.GetInt16(7),
                    AttributesJson: r.IsDBNull(8) ? null : r.GetString(8)));
            return KubernetesOperationResult<List<SpanRecord>>.Success(spans);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Trace fetch failed (cluster {Cluster}, trace {Trace})", clusterId, traceId);
            return KubernetesOperationResult<List<SpanRecord>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// RED time series for a service: request count, error count, and duration avg/p50/p95 per bucket,
    /// measured over inbound spans (SERVER=2, CONSUMER=5) — the requests the service actually handles.
    /// </summary>
    public async Task<KubernetesOperationResult<List<RedBucket>>> GetServiceRedAsync(
        Guid clusterId, string service, DateTime from, DateTime to, int buckets = 48, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<RedBucket>>();

        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        TimeSpan interval = TimeSpan.FromSeconds(bucketSecs);

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT date_bin(@interval, ts, @from) AS b, count(*) AS total, " +
                "count(*) FILTER (WHERE status_code = 2) AS errors, avg(duration_ms) AS avg_ms, " +
                "percentile_disc(0.5) WITHIN GROUP (ORDER BY duration_ms) AS p50, " +
                "percentile_disc(0.95) WITHIN GROUP (ORDER BY duration_ms) AS p95 " +
                // Inbound requests (SERVER/CONSUMER) OR trace-entry spans (no parent) — so services
                // instrumented without an explicit SpanKind still surface RED instead of a blank chart.
                "FROM spans WHERE tenant_id = @t AND cluster_id = @c AND service = @service " +
                "AND ts >= @from AND ts < @to AND (kind IN (2, 5) OR parent_span_id IS NULL)"
                + NsScope(cmd, namespaces) + " GROUP BY b ORDER BY b";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("service", service);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
            cmd.Parameters.AddWithValue("interval", interval);

            List<RedBucket> series = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                series.Add(new RedBucket(
                    Start: r.GetDateTime(0),
                    Count: r.GetInt64(1),
                    Errors: r.GetInt64(2),
                    AvgMs: r.IsDBNull(3) ? 0 : r.GetDouble(3),
                    P50Ms: r.IsDBNull(4) ? 0 : r.GetDouble(4),
                    P95Ms: r.IsDBNull(5) ? 0 : r.GetDouble(5)));
            return KubernetesOperationResult<List<RedBucket>>.Success(series);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Service RED query failed (cluster {Cluster}, service {Service})", clusterId, service);
            return KubernetesOperationResult<List<RedBucket>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Service-map edges: caller→callee service pairs derived from parent/child spans where the two
    /// services differ, with call count, error count, and average callee latency. Powers a service
    /// dependency overview.
    /// </summary>
    public async Task<KubernetesOperationResult<List<ServiceEdge>>> GetServiceMapAsync(
        Guid clusterId, DateTime from, DateTime to, CancellationToken ct = default,
        IReadOnlyList<string>? namespaces = null)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<ServiceEdge>>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            // Namespace scope: require BOTH ends of an edge to be in the customer's namespaces so
            // the map never reveals a service outside their apps. (Built once, binds @ns once.)
            string nsScope = namespaces is { Count: > 0 } ? " AND c.namespace = ANY(@ns) AND p.namespace = ANY(@ns)" : "";
            if (namespaces is { Count: > 0 }) cmd.Parameters.AddWithValue("ns", namespaces.ToArray());
            // Self-join child span → its parent (same trace) on span_id; keep cross-service edges.
            cmd.CommandText =
                "SELECT p.service AS from_service, c.service AS to_service, count(*) AS calls, " +
                "count(*) FILTER (WHERE c.status_code = 2) AS errors, avg(c.duration_ms) AS avg_ms " +
                "FROM spans c JOIN spans p ON p.tenant_id = c.tenant_id AND p.cluster_id = c.cluster_id " +
                "AND p.trace_id = c.trace_id AND p.span_id = c.parent_span_id " +
                "WHERE c.tenant_id = @t AND c.cluster_id = @c AND c.ts >= @from AND c.ts < @to " +
                "AND p.service <> c.service" + nsScope + " " +
                "GROUP BY p.service, c.service ORDER BY calls DESC";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());

            List<ServiceEdge> edges = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                edges.Add(new ServiceEdge(
                    From: r.GetString(0),
                    To: r.GetString(1),
                    Calls: r.GetInt64(2),
                    Errors: r.GetInt64(3),
                    AvgMs: r.IsDBNull(4) ? 0 : r.GetDouble(4)));
            return KubernetesOperationResult<List<ServiceEdge>>.Success(edges);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Service map query failed (cluster {Cluster})", clusterId);
            return KubernetesOperationResult<List<ServiceEdge>>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Aggregate stats for a service over a window (inbound spans only): total, error count, p95 latency
    /// in ms. Drives the trace-based alert rules (error-rate / p95 threshold).
    /// </summary>
    public async Task<KubernetesOperationResult<ServiceStats>> GetServiceStatsAsync(
        Guid clusterId, string service, DateTime from, DateTime to, CancellationToken ct = default)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<ServiceStats>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) AS total, count(*) FILTER (WHERE status_code = 2) AS errors, " +
                "coalesce(percentile_disc(0.95) WITHIN GROUP (ORDER BY duration_ms), 0) AS p95 " +
                "FROM spans WHERE tenant_id = @t AND cluster_id = @c AND service = @service " +
                "AND ts >= @from AND ts < @to AND (kind IN (2, 5) OR parent_span_id IS NULL)";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("service", service);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());

            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                return KubernetesOperationResult<ServiceStats>.Success(
                    new ServiceStats(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetDouble(2)));
            return KubernetesOperationResult<ServiceStats>.Success(new ServiceStats(0, 0, 0));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Service stats failed (cluster {Cluster}, service {Service})", clusterId, service);
            return Fail<ServiceStats>(ex.Message);
        }
    }
}

/// <summary>A row in the trace-search list: the trace and its root span's service/operation.</summary>
public sealed record TraceSummary(
    string TraceId, DateTime Start, double DurationMs, int SpanCount, int ErrorCount,
    string RootService, string RootName);

/// <summary>One span in a trace (for the waterfall). <see cref="StatusCode"/> 2 = error.</summary>
public sealed record SpanRecord(
    DateTime Start, string SpanId, string? ParentSpanId, string Name, string Service,
    short Kind, double DurationMs, short StatusCode, string? AttributesJson);

/// <summary>One RED time bucket for a service: request/error counts and duration avg/p50/p95 (ms).</summary>
public sealed record RedBucket(
    DateTime Start, long Count, long Errors, double AvgMs, double P50Ms, double P95Ms);

/// <summary>A caller→callee edge in the service map, with call/error counts and average callee latency.</summary>
public sealed record ServiceEdge(string From, string To, long Calls, long Errors, double AvgMs);

/// <summary>Aggregate service stats over a window (for alert evaluation): request count, errors, p95 ms.</summary>
public sealed record ServiceStats(long Count, long Errors, double P95Ms);
