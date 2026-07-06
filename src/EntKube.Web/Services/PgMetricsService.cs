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
    /// Bucketed time series for a metric: avg(value) per date_bin bucket over [from, to], optionally
    /// service-scoped. avg suits gauges; counters are charted as their reported value (rate() is a later refinement).
    /// </summary>
    public async Task<KubernetesOperationResult<List<TimeSeriesDataPoint>>> GetSeriesAsync(
        Guid clusterId, string name, string? service, DateTime from, DateTime to, int buckets = 60, CancellationToken ct = default)
    {
        Guid? tenantId = await ResolveOrNull(clusterId, ct);
        if (tenantId is null) return Fail<List<TimeSeriesDataPoint>>();

        double bucketSecs = Math.Max(1, Math.Ceiling((to - from).TotalSeconds / Math.Max(1, buckets)));
        TimeSpan interval = TimeSpan.FromSeconds(bucketSecs);

        try
        {
            await using NpgsqlConnection conn = await Store.OpenConnectionAsync(ct);
            await using NpgsqlCommand cmd = conn.CreateCommand();
            string svcClause = string.IsNullOrEmpty(service) ? "" : " AND service = @service";
            cmd.CommandText =
                "SELECT date_bin(@interval, ts, @from) AS b, avg(value) AS v FROM metrics " +
                "WHERE tenant_id = @t AND cluster_id = @c AND name = @name AND ts >= @from AND ts < @to" + svcClause +
                " GROUP BY b ORDER BY b";
            cmd.Parameters.AddWithValue("t", tenantId.Value);
            cmd.Parameters.AddWithValue("c", clusterId);
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, from.ToUniversalTime());
            cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, to.ToUniversalTime());
            cmd.Parameters.AddWithValue("interval", interval);
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
}
