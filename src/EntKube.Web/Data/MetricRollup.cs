namespace EntKube.Web.Data;

/// <summary>
/// One metric, for one scope, over one fixed-width time bucket: the central metrics store EntKube
/// keeps now that telemetry is held by the management plane again
/// (docs/telemetry-central-again.md).
///
/// <para><b>Why rollups rather than raw samples, when raw samples were what was asked for.</b>
/// <c>IMetricsQuery</c> — the surface every dashboard and the metrics explorer inject — never asks
/// for a sample. Each read is <c>GetSeriesAsync(name, service, from, to, buckets: 60)</c> or the
/// same question grouped by a dimension. A store of raw points would therefore be aggregated on
/// every read, which is the shape that produced the ">5s log/trace queries and <i>too many
/// clients</i> ingest failures" recorded against the row store deleted in <c>82ec230</c>.</para>
///
/// <para><b>And it still is raw samples, when you want them.</b> The bucket width is
/// <c>Telemetry:MetricsResolutionSeconds</c>. Set it to the scrape interval and every scrape lands
/// in a bucket of its own, which is a raw sample with a count of one; raise it and cardinality
/// falls proportionally. One store answers both, so the choice is a setting rather than a
/// migration — see §5 of the design note, which asked for exactly that escape hatch.</para>
///
/// <para><b>The scope columns are a closed set, not a simplification.</b>
/// <c>GetSeriesByAsync</c> takes a dimension name, and <c>PromMetricsQueryBuilder.BreakdownLabel</c>
/// maps it to one of three: pod, service, namespace — anything else falls back to pod. So a
/// fixed-column rollup can answer every breakdown the product is able to request, and an
/// arbitrary-label store would buy nothing a caller could use.</para>
/// </summary>
public class MetricRollup
{
    public Guid Id { get; set; }

    /// <summary>The owning tenant. Every query is scoped by it; nothing reads across tenants.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The cluster the metric came from. A soft association, as elsewhere in telemetry.</summary>
    public Guid ClusterId { get; set; }

    /// <summary>Metric name as the exporter emitted it, e.g. <c>container_cpu_usage_seconds_total</c>.</summary>
    public required string Name { get; set; }

    /// <summary>OTel <c>service.name</c>, when the point carried one.</summary>
    public string? Service { get; set; }

    /// <summary>Kubernetes namespace, when the point carried one.</summary>
    public string? Namespace { get; set; }

    /// <summary>Pod name, when the point carried one.</summary>
    public string? Pod { get; set; }

    /// <summary>
    /// Start of the bucket, UTC, floored to <see cref="ResolutionSeconds"/>. The bucket is
    /// [BucketStart, BucketStart + ResolutionSeconds).
    /// </summary>
    public DateTime BucketStart { get; set; }

    /// <summary>
    /// Width of this bucket in seconds, stored rather than assumed.
    ///
    /// <para>Changing <c>Telemetry:MetricsResolutionSeconds</c> must not silently reinterpret
    /// history: rows written at 30s and rows written at 300s describe different things, and a
    /// reader that averaged them together would be wrong without any error. Keeping the width on
    /// the row lets a query weight or exclude what it did not ask for.</para>
    /// </summary>
    public int ResolutionSeconds { get; set; }

    /// <summary>How many points landed in this bucket. With <see cref="Sum"/> it gives the mean.</summary>
    public long Count { get; set; }

    public double Sum { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }

    /// <summary>
    /// The last value in the bucket, by timestamp. A gauge's "current" reading is this, not the
    /// mean — memory in use at the end of the minute is a fact, the average over it is a different
    /// one, and the explorer plots both kinds of metric through the same call.
    /// </summary>
    public double Last { get; set; }
}
