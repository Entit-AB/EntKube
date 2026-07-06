using Npgsql;
using NpgsqlTypes;

namespace EntKube.Web.Services;

/// <summary>
/// Queries the native RUM tables (rum_page_views / rum_errors / rum_resources) for the RUM dashboards.
/// Scoped by tenant_id + site_id (a <see cref="Data.RumSite"/>) — RUM is site-scoped, not cluster-scoped,
/// so this does NOT go through the cluster→tenant resolver. Web Vitals are reported at p75 (the web standard).
/// </summary>
public class PgRumService(TelemetryStore store, ILogger<PgRumService> logger)
{
    public async Task<bool> HasDataAsync(Guid tenantId, Guid siteId, CancellationToken ct = default)
    {
        if (!store.IsEnabled) return false;
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM rum_page_views WHERE tenant_id = @t AND site_id = @s LIMIT 1";
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("s", siteId);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM HasData failed (site {Site})", siteId); return false; }
    }

    public async Task<RumSiteOverview?> GetOverviewAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            long errors = await ScalarLongAsync(conn,
                "SELECT count(*) FROM rum_errors WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to",
                tenantId, siteId, from, to, ct);

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT count(*), count(distinct session_id), " +
                "percentile_cont(0.75) WITHIN GROUP (ORDER BY lcp_ms), " +
                "percentile_cont(0.75) WITHIN GROUP (ORDER BY cls), " +
                "percentile_cont(0.75) WITHIN GROUP (ORDER BY inp_ms), " +
                "percentile_cont(0.75) WITHIN GROUP (ORDER BY fcp_ms), " +
                "avg(load_ms), avg(ttfb_ms) " +
                "FROM rum_page_views WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to";
            AddScope(cmd, tenantId, siteId, from, to);

            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return new RumSiteOverview(0, 0, errors, null, null, null, null, null, null);
            return new RumSiteOverview(
                r.GetInt64(0), r.GetInt64(1), errors,
                Dbl(r, 2), Dbl(r, 3), Dbl(r, 4), Dbl(r, 5), Dbl(r, 6), Dbl(r, 7));
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM overview failed (site {Site})", siteId); return null; }
    }

    public async Task<List<RumTopPage>> GetTopPagesAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default)
    {
        List<RumTopPage> rows = [];
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT path, count(*), percentile_cont(0.75) WITHIN GROUP (ORDER BY lcp_ms) " +
                "FROM rum_page_views WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to " +
                "GROUP BY path ORDER BY count(*) DESC LIMIT @lim";
            AddScope(cmd, tenantId, siteId, from, to);
            cmd.Parameters.AddWithValue("lim", limit);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new RumTopPage(r.GetString(0), r.GetInt64(1), Dbl(r, 2)));
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM top pages failed (site {Site})", siteId); }
        return rows;
    }

    public async Task<List<RumTopError>> GetTopErrorsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default)
    {
        List<RumTopError> rows = [];
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT message, count(*), max(ts) FROM rum_errors " +
                "WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to " +
                "GROUP BY message ORDER BY count(*) DESC LIMIT @lim";
            AddScope(cmd, tenantId, siteId, from, to);
            cmd.Parameters.AddWithValue("lim", limit);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add(new RumTopError(r.GetString(0), r.GetInt64(1), r.GetDateTime(2)));
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM top errors failed (site {Site})", siteId); }
        return rows;
    }

    public async Task<List<TimeSeriesDataPoint>> GetPageViewSeriesAsync(
        Guid tenantId, Guid siteId, DateTime from, DateTime to, int buckets = 60, CancellationToken ct = default)
    {
        List<TimeSeriesDataPoint> series = [];
        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT date_bin(@interval, ts, @from), count(*) FROM rum_page_views " +
                "WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to GROUP BY 1 ORDER BY 1";
            AddScope(cmd, tenantId, siteId, from, to);
            cmd.Parameters.AddWithValue("interval", TimeSpan.FromSeconds(bucketSecs));
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                series.Add(new TimeSeriesDataPoint { Timestamp = r.GetDateTime(0), Value = r.GetInt64(1) });
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM page-view series failed (site {Site})", siteId); }
        return series;
    }

    public async Task<List<RumSessionSummary>> GetSessionsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 50, CancellationToken ct = default)
    {
        List<RumSessionSummary> rows = [];
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT pv.session_id, min(pv.ts), max(pv.ts), count(*), (array_agg(pv.path ORDER BY pv.ts DESC))[1], coalesce(er.errs, 0) " +
                "FROM rum_page_views pv " +
                "LEFT JOIN (SELECT session_id, count(*) errs FROM rum_errors WHERE tenant_id = @t AND site_id = @s AND ts >= @from AND ts < @to GROUP BY session_id) er " +
                "  ON er.session_id = pv.session_id " +
                "WHERE pv.tenant_id = @t AND pv.site_id = @s AND pv.ts >= @from AND pv.ts < @to " +
                "GROUP BY pv.session_id, er.errs ORDER BY max(pv.ts) DESC LIMIT @lim";
            AddScope(cmd, tenantId, siteId, from, to);
            cmd.Parameters.AddWithValue("lim", limit);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new RumSessionSummary(r.GetString(0), r.GetDateTime(1), r.GetDateTime(2), r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5)));
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM sessions failed (site {Site})", siteId); }
        return rows;
    }

    public async Task<RumSessionDetail> GetSessionDetailAsync(Guid tenantId, Guid siteId, string sessionId, CancellationToken ct = default)
    {
        List<RumViewRow> views = [];
        List<RumErrorRow> errors = [];
        List<RumResourceRow> resources = [];
        try
        {
            await using NpgsqlConnection conn = await store.OpenConnectionAsync(ct);

            await using (NpgsqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ts, path, load_ms, lcp_ms, cls, inp_ms FROM rum_page_views WHERE tenant_id = @t AND site_id = @s AND session_id = @sess ORDER BY ts";
                AddSession(cmd, tenantId, siteId, sessionId);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    views.Add(new RumViewRow(r.GetDateTime(0), r.GetString(1), Dbl(r, 2), Dbl(r, 3), Dbl(r, 4), Dbl(r, 5)));
            }
            await using (NpgsqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ts, path, message, source FROM rum_errors WHERE tenant_id = @t AND site_id = @s AND session_id = @sess ORDER BY ts";
                AddSession(cmd, tenantId, siteId, sessionId);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    errors.Add(new RumErrorRow(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
            await using (NpgsqlCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ts, path, name, kind, duration_ms, status, trace_id FROM rum_resources WHERE tenant_id = @t AND site_id = @s AND session_id = @sess ORDER BY ts";
                AddSession(cmd, tenantId, siteId, sessionId);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    resources.Add(new RumResourceRow(r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3), Dbl(r, 4), r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6)));
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "RUM session detail failed (site {Site}, session {Session})", siteId, sessionId); }
        return new RumSessionDetail(views, errors, resources);
    }

    private static void AddScope(NpgsqlCommand cmd, Guid tenantId, Guid siteId, DateTime from, DateTime to)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("s", siteId);
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
    }

    private static void AddSession(NpgsqlCommand cmd, Guid tenantId, Guid siteId, string sessionId)
    {
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("s", siteId);
        cmd.Parameters.AddWithValue("sess", sessionId);
    }

    private async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql, Guid tenantId, Guid siteId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        AddScope(cmd, tenantId, siteId, from, to);
        return await cmd.ExecuteScalarAsync(ct) is long l ? l : 0;
    }

    private static double? Dbl(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
}

public sealed record RumSiteOverview(
    long PageViews, long Sessions, long Errors,
    double? LcpP75, double? ClsP75, double? InpP75, double? FcpP75, double? AvgLoadMs, double? AvgTtfbMs);

public sealed record RumTopPage(string Path, long Views, double? LcpP75);
public sealed record RumTopError(string Message, long Count, DateTime LastSeen);
public sealed record RumSessionSummary(string SessionId, DateTime Started, DateTime LastSeen, long Views, string? LastPath, long Errors);

public sealed record RumViewRow(DateTime Ts, string Path, double? LoadMs, double? LcpMs, double? Cls, double? InpMs);
public sealed record RumErrorRow(DateTime Ts, string? Path, string Message, string? Source);
public sealed record RumResourceRow(DateTime Ts, string? Path, string Name, string? Kind, double? DurationMs, int? Status, string? TraceId);
public sealed record RumSessionDetail(List<RumViewRow> Views, List<RumErrorRow> Errors, List<RumResourceRow> Resources);
