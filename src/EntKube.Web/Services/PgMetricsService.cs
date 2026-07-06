using Npgsql;
using NpgsqlTypes;

namespace EntKube.Web.Services;

/// <summary>
/// Queries the native telemetry <c>metrics</c> table (app-emitted OTLP metrics) for the native metrics
/// explorer: the metric-name list, distinct services, and a bucketed time series for charting. Native
/// app metrics only — Prometheus still covers infra/scrape metrics. Scoped by tenant_id + cluster_id.
/// </summary>
public class PgMetricsService(TelemetryStore store, ClusterTenantResolver tenants, ILogger<PgMetricsService> logger)
    : TelemetryQueryServiceBase(store, tenants, logger)
{
    public Task<bool> HasDataAsync(Guid clusterId, CancellationToken ct = default)
        => HasAnyAsync("metrics", clusterId, ct);

    public async Task<KubernetesOperationResult<List<string>>> GetMetricNamesAsync(Guid clusterId, CancellationToken ct = default)
        => await DistinctAsync(clusterId, "name", ct);

    public async Task<KubernetesOperationResult<List<string>>> GetServicesAsync(Guid clusterId, CancellationToken ct = default)
        => await DistinctAsync(clusterId, "service", ct);

    private async Task<KubernetesOperationResult<List<string>>> DistinctAsync(Guid clusterId, string column, CancellationToken ct)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<string>>();

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            // column is an internal literal (name/service), never user input.
            cmd.CommandText = $"SELECT DISTINCT {column} AS v FROM metrics WHERE tenant_id = @t AND cluster_id = @c AND {column} IS NOT NULL ORDER BY v";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);

            List<string> values = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (!r.IsDBNull(0)) values.Add(r.GetString(0));
            return KubernetesOperationResult<List<string>>.Success(values);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Metric {Column} list failed (cluster {Cluster})", column, clusterId);
            return Fail<List<string>>(ex.Message);
        }
    }

    /// <summary>
    /// Bucketed time series for a metric over [from, to], optionally service-scoped, charted per its
    /// stored <see cref="MetricKind"/>: a gauge collapses each series (service/namespace/pod/labels) to its
    /// per-bucket average then sums across series (a metric from N pods reports the total, not a per-pod
    /// average); a cumulative counter is differenced into a reset-aware per-second rate summed across
    /// series; a delta sum's increments are summed into a per-second rate.
    /// </summary>
    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid clusterId, string name, string? service, DateTime from, DateTime to, int buckets = 60, CancellationToken ct = default)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<TimeSeriesDataPoint>>();

        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        TimeSpan interval = TimeSpan.FromSeconds(bucketSecs);
        string svcClause = string.IsNullOrEmpty(service) ? "" : " AND service = @service";

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            MetricKind kind = await GetKindAsync(conn, tenantId.Value, clusterId, name, svcClause, service, from, to, ct);

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = kind switch
            {
                // Cumulative counter → reset-aware per-second rate: difference consecutive samples per series
                // (a drop means the counter reset, so count the post-reset value as the increase), then sum.
                MetricKind.Counter =>
                    "SELECT b, sum(increase) / @bucketsecs AS v FROM (" +
                    "  SELECT date_bin(@interval, ts, @from) AS b," +
                    "    CASE WHEN prev IS NULL THEN 0 WHEN value < prev THEN value ELSE value - prev END AS increase " +
                    "  FROM (" +
                    "    SELECT ts, value, lag(value) OVER (PARTITION BY service, namespace, pod, labels ORDER BY ts) AS prev " +
                    "    FROM metrics " +
                    "    WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    "  ) d" +
                    ") e GROUP BY b ORDER BY b",
                // Delta sum → each point is already an increment; sum them into a per-second rate.
                MetricKind.DeltaSum =>
                    "SELECT date_bin(@interval, ts, @from) AS b, sum(value) / @bucketsecs AS v FROM metrics " +
                    "WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    " GROUP BY b ORDER BY b",
                // Gauge → per-series average per bucket, summed across series.
                _ =>
                    "SELECT b, sum(sv) AS v FROM (" +
                    "  SELECT date_bin(@interval, ts, @from) AS b, service, namespace, pod, labels, avg(value) AS sv " +
                    "  FROM metrics " +
                    "  WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    "  GROUP BY b, service, namespace, pod, labels" +
                    ") s GROUP BY b ORDER BY b"
            };
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
            cmd.Parameters.AddWithValue("interval", interval);
            cmd.Parameters.AddWithValue("bucketsecs", bucketSecs);
            if (!string.IsNullOrEmpty(service)) cmd.Parameters.AddWithValue("service", service);

            List<TimeSeriesDataPoint> series = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                series.Add(new TimeSeriesDataPoint { Timestamp = r.GetDateTime(0), Value = r.IsDBNull(1) ? 0 : r.GetDouble(1) });
            return KubernetesOperationResult<List<TimeSeriesDataPoint>>.Success(series);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Metric series failed (cluster {Cluster}, metric {Name})", clusterId, name);
            return Fail<List<TimeSeriesDataPoint>>(ex.Message);
        }
    }

    /// <summary>
    /// Like <see cref="GetSeriesAsync"/> but broken out into one line per distinct value of a grouping
    /// dimension (pod / service / namespace) — the "by-label" view. Same kind-aware aggregation, with the
    /// group value carried through. Capped at <paramref name="maxSeries"/> distinct series to stay legible.
    /// </summary>
    public async Task<KubernetesOperationResult<List<MetricSeriesGroup>>> GetSeriesByAsync(
        Guid clusterId, string name, string? service, string dimension, DateTime from, DateTime to,
        int buckets = 60, int maxSeries = 24, CancellationToken ct = default)
    {
        if (!GroupDimensions.Contains(dimension)) return Fail<List<MetricSeriesGroup>>($"Unsupported breakdown '{dimension}'.");
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<MetricSeriesGroup>>();

        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        TimeSpan interval = TimeSpan.FromSeconds(bucketSecs);
        string svcClause = string.IsNullOrEmpty(service) ? "" : " AND service = @service";
        string g = $"coalesce({dimension}, '(none)')";   // dimension is allowlisted (GroupDimensions), safe to inline

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            MetricKind kind = await GetKindAsync(conn, tenantId.Value, clusterId, name, svcClause, service, from, to, ct);

            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = kind switch
            {
                MetricKind.Counter =>
                    "SELECT g, b, sum(increase) / @bucketsecs AS v FROM (" +
                    "  SELECT g, date_bin(@interval, ts, @from) AS b," +
                    "    CASE WHEN prev IS NULL THEN 0 WHEN value < prev THEN value ELSE value - prev END AS increase " +
                    "  FROM (" +
                    "    SELECT " + g + " AS g, ts, value, lag(value) OVER (PARTITION BY service, namespace, pod, labels ORDER BY ts) AS prev " +
                    "    FROM metrics " +
                    "    WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    "  ) d" +
                    ") e GROUP BY g, b ORDER BY g, b",
                MetricKind.DeltaSum =>
                    "SELECT " + g + " AS g, date_bin(@interval, ts, @from) AS b, sum(value) / @bucketsecs AS v FROM metrics " +
                    "WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    " GROUP BY g, b ORDER BY g, b",
                _ =>
                    "SELECT g, b, sum(sv) AS v FROM (" +
                    "  SELECT " + g + " AS g, date_bin(@interval, ts, @from) AS b, service, namespace, pod, labels, avg(value) AS sv " +
                    "  FROM metrics " +
                    "  WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                    "  GROUP BY g, b, service, namespace, pod, labels" +
                    ") s GROUP BY g, b ORDER BY g, b"
            };
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
            cmd.Parameters.AddWithValue("interval", interval);
            cmd.Parameters.AddWithValue("bucketsecs", bucketSecs);
            if (!string.IsNullOrEmpty(service)) cmd.Parameters.AddWithValue("service", service);

            // Rows arrive ordered by group then bucket; accumulate into per-group series, capping distinct groups.
            Dictionary<string, MetricSeriesGroup> byName = new(StringComparer.Ordinal);
            List<MetricSeriesGroup> order = [];
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string gname = r.IsDBNull(0) ? "(none)" : r.GetString(0);
                if (!byName.TryGetValue(gname, out MetricSeriesGroup? grp))
                {
                    if (order.Count >= maxSeries) continue;   // ordered by group, so the rest are new groups too
                    grp = new MetricSeriesGroup(gname, []);
                    byName[gname] = grp;
                    order.Add(grp);
                }
                grp.Points.Add(new TimeSeriesDataPoint { Timestamp = r.GetDateTime(1), Value = r.IsDBNull(2) ? 0 : r.GetDouble(2) });
            }
            return KubernetesOperationResult<List<MetricSeriesGroup>>.Success(order);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Metric breakdown failed (cluster {Cluster}, metric {Name}, by {Dim})", clusterId, name, dimension);
            return Fail<List<MetricSeriesGroup>>(ex.Message);
        }
    }

    private static readonly HashSet<string> GroupDimensions = new(StringComparer.Ordinal) { "pod", "service", "namespace" };

    // The metric's aggregation kind (from the most recent sample in range) decides how the series is charted.
    private static async Task<MetricKind> GetKindAsync(
        NpgsqlConnection conn, Guid tenantId, Guid clusterId, string name, string svcClause, string? service,
        DateTime from, DateTime to, CancellationToken ct)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT kind FROM metrics WHERE tenant_id = @t AND cluster_id = @c AND name = @name " +
            "AND ts >= @from AND ts < @to" + svcClause + " ORDER BY ts DESC LIMIT 1";
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", clusterId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
        if (!string.IsNullOrEmpty(service)) cmd.Parameters.AddWithValue("service", service);

        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is short s && Enum.IsDefined((MetricKind)s) ? (MetricKind)s : MetricKind.Gauge;
    }
}

/// <summary>One named line in a metric breakdown chart (e.g. one pod / service / namespace).</summary>
public sealed record MetricSeriesGroup(string Name, List<TimeSeriesDataPoint> Points);
