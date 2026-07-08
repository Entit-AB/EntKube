namespace EntKube.Web.Services;

/// <summary>
/// One named series in a broken-down metric chart (e.g. one pod / service / namespace), with its time
/// points. Shared by the metrics explorer, the infrastructure metrics view, and the multi-series chart.
/// (Previously defined in PgMetricsService, which was removed when metrics moved to Prometheus.)
/// </summary>
public sealed record MetricSeriesGroup(string Name, List<TimeSeriesDataPoint> Points);
