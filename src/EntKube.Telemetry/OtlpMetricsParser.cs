using System.Text.Json;

namespace EntKube.Telemetry;

/// <summary>
/// One metric observation as it arrived, before anything has been aggregated. Shared with
/// <see cref="MetricRollupFold"/>, which folds these into buckets — one concept, one type.
/// </summary>
/// <param name="Name">Metric name as the exporter emitted it.</param>
/// <param name="Service">OTel <c>service.name</c>, when the resource carried one.</param>
/// <param name="Namespace">Kubernetes namespace, when the resource carried one.</param>
/// <param name="Pod">Pod name, when the resource carried one.</param>
/// <param name="Timestamp">When the point was observed, UTC.</param>
/// <param name="Value">The observation.</param>
public readonly record struct MetricPoint(
    string Name, string? Service, string? Namespace, string? Pod, DateTime Timestamp, double Value);

/// <summary>
/// Parses an OTLP/JSON metrics payload into flat points, the same envelope walk
/// <see cref="OtlpLogsParser"/> does: resourceMetrics → scopeMetrics → metrics, with
/// <c>service.name</c> / <c>k8s.namespace.name</c> / <c>k8s.pod.name</c> read off the resource.
///
/// <para><b>What it keeps and what it drops.</b> Gauges and sums become one point per data point —
/// that is every metric the explorer plots. Histograms contribute their <c>sum</c> and
/// <c>count</c> as two derived series (<c>name_sum</c>, <c>name_count</c>) rather than their
/// buckets: the query surface has no way to ask for a quantile, so storing bucket boundaries would
/// be bytes nothing could read. Exponential histograms and summaries are skipped outright for the
/// same reason, and skipping is visible as a missing metric rather than as wrong numbers.</para>
///
/// <para><b>Lenient about shape, strict about numbers.</b> A ProtoJSON producer may send a value as
/// <c>asDouble</c> or <c>asInt</c>, and an integer may arrive as a JSON string; all three are
/// accepted. A point whose value is absent, unparseable, NaN or infinite is dropped here rather
/// than carried to the fold, because one of those poisons every aggregate in its bucket.</para>
/// </summary>
public static class OtlpMetricsParser
{
    public static List<MetricPoint> Parse(JsonDocument doc)
    {
        List<MetricPoint> points = [];
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("resourceMetrics", out JsonElement resourceMetrics)
            || resourceMetrics.ValueKind != JsonValueKind.Array)
        {
            return points;
        }

        foreach (JsonElement rm in resourceMetrics.EnumerateArray())
        {
            Dictionary<string, string> resAttrs = new(StringComparer.Ordinal);
            if (rm.TryGetProperty("resource", out JsonElement resource)
                && resource.TryGetProperty("attributes", out JsonElement rAttrs))
            {
                OtlpJson.ReadAttributes(rAttrs, resAttrs);
            }

            string service = OtlpJson.FirstOf(resAttrs, "service.name", "service");
            string ns = OtlpJson.FirstOf(resAttrs, "k8s.namespace.name", "namespace");
            string pod = OtlpJson.FirstOf(resAttrs, "k8s.pod.name", "pod");

            if (!rm.TryGetProperty("scopeMetrics", out JsonElement scopeMetrics)
                || scopeMetrics.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement sm in scopeMetrics.EnumerateArray())
            {
                if (!sm.TryGetProperty("metrics", out JsonElement metrics)
                    || metrics.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement m in metrics.EnumerateArray())
                {
                    string? name = m.TryGetProperty("name", out JsonElement n) ? OtlpJson.Str(n) : null;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    ReadMetric(m, name, Blank(service), Blank(ns), Blank(pod), points);
                }
            }
        }

        return points;
    }

    private static void ReadMetric(
        JsonElement metric, string name, string? service, string? ns, string? pod,
        List<MetricPoint> into)
    {
        // Gauge and sum: one point per data point, which is what the explorer plots.
        foreach (string kind in (string[])["gauge", "sum"])
        {
            if (!metric.TryGetProperty(kind, out JsonElement body)) continue;
            foreach (JsonElement dp in DataPoints(body))
            {
                if (ReadValue(dp, "asDouble", "asInt") is double v)
                {
                    into.Add(new MetricPoint(name, service, ns, pod, TimeOf(dp), v));
                }
            }
        }

        // Histogram: the two fields a bucketed query can use. Named with the conventional
        // suffixes so they read like the Prometheus series an operator already knows.
        if (metric.TryGetProperty("histogram", out JsonElement hist))
        {
            foreach (JsonElement dp in DataPoints(hist))
            {
                DateTime at = TimeOf(dp);

                if (ReadValue(dp, "sum") is double sum)
                {
                    into.Add(new MetricPoint($"{name}_sum", service, ns, pod, at, sum));
                }

                if (ReadValue(dp, "count") is double count)
                {
                    into.Add(new MetricPoint($"{name}_count", service, ns, pod, at, count));
                }
            }
        }

        // exponentialHistogram and summary are deliberately not read: nothing in IMetricsQuery can
        // ask for a quantile, so their payload would be stored and never read.
    }

    private static IEnumerable<JsonElement> DataPoints(JsonElement body) =>
        body.TryGetProperty("dataPoints", out JsonElement dps) && dps.ValueKind == JsonValueKind.Array
            ? dps.EnumerateArray()
            : [];

    /// <summary>
    /// The first of <paramref name="props"/> that holds a usable number, or null.
    ///
    /// <para>A ProtoJSON integer may arrive as a JSON string, so a string that parses is accepted —
    /// and <c>GetDouble()</c> would throw on it rather than return anything.</para>
    /// </summary>
    private static double? ReadValue(JsonElement dp, params string[] props)
    {
        foreach (string prop in props)
        {
            if (!dp.TryGetProperty(prop, out JsonElement v)) continue;

            double? parsed = v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDouble(out double d) => d,
                JsonValueKind.String when double.TryParse(
                    v.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double s) => s,
                _ => null,
            };

            // NaN and the infinities are dropped here as well as in the fold: a payload is the
            // first place one can arrive, and the fold is the last place it could do damage.
            if (parsed is double value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The data point's own timestamp, falling back to the start of its window.
    ///
    /// <para><c>timeUnixNano</c> is when the observation was taken; <c>startTimeUnixNano</c> is
    /// when the series began, which for a cumulative counter can be days earlier. Preferring the
    /// former keeps a point in the bucket it belongs to — using the start time would pile a whole
    /// counter's history into one ancient bucket.</para>
    /// </summary>
    private static DateTime TimeOf(JsonElement dp)
    {
        long ns = OtlpJson.ReadUnixNano(dp, "timeUnixNano");
        return OtlpJson.UnixNanoToUtc(ns != 0 ? ns : OtlpJson.ReadUnixNano(dp, "startTimeUnixNano"));
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
