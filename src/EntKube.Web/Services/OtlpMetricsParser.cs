using System.Globalization;
using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Parses an OTLP/JSON <c>ExportMetricsServiceRequest</c> (collector <c>otlphttp</c> exporter,
/// <c>encoding: json</c>, POSTed to <c>/v1/metrics</c>) into flat numeric <see cref="MetricIngestRecord"/>
/// data points. Handles gauge and sum metrics (one row per data point). Histogram / summary /
/// exponential-histogram are skipped in v1 (their bucket structure needs a richer model).
/// </summary>
public static class OtlpMetricsParser
{
    public static List<MetricIngestRecord> Parse(JsonDocument doc)
    {
        List<MetricIngestRecord> records = [];
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("resourceMetrics", out JsonElement resourceMetrics) ||
            resourceMetrics.ValueKind != JsonValueKind.Array)
            return records;

        foreach (JsonElement rm in resourceMetrics.EnumerateArray())
        {
            Dictionary<string, string> resAttrs = new(StringComparer.Ordinal);
            if (rm.TryGetProperty("resource", out JsonElement resource) &&
                resource.TryGetProperty("attributes", out JsonElement rAttrs))
                OtlpJson.ReadAttributes(rAttrs, resAttrs);

            string? service = NullIfEmpty(OtlpJson.FirstOf(resAttrs, "service.name"));
            string? ns = NullIfEmpty(OtlpJson.FirstOf(resAttrs, "k8s.namespace.name", "namespace"));
            string? pod = NullIfEmpty(OtlpJson.FirstOf(resAttrs, "k8s.pod.name", "pod"));

            if (!rm.TryGetProperty("scopeMetrics", out JsonElement scopeMetrics) ||
                scopeMetrics.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement sm in scopeMetrics.EnumerateArray())
            {
                if (!sm.TryGetProperty("metrics", out JsonElement metrics) || metrics.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement metric in metrics.EnumerateArray())
                {
                    string name = metric.TryGetProperty("name", out JsonElement nm) ? OtlpJson.Str(nm) ?? "" : "";
                    if (string.IsNullOrEmpty(name)) continue;

                    // gauge / sum carry a dataPoints array; other types are skipped in v1.
                    JsonElement dataPoints;
                    MetricKind kind;
                    if (metric.TryGetProperty("gauge", out JsonElement g) && g.TryGetProperty("dataPoints", out JsonElement gdp))
                    {
                        dataPoints = gdp;
                        kind = MetricKind.Gauge;
                    }
                    else if (metric.TryGetProperty("sum", out JsonElement s) && s.TryGetProperty("dataPoints", out JsonElement sdp))
                    {
                        dataPoints = sdp;
                        kind = ClassifySum(s);
                    }
                    else
                        continue;

                    if (dataPoints.ValueKind != JsonValueKind.Array) continue;

                    foreach (JsonElement dp in dataPoints.EnumerateArray())
                    {
                        double? value = ReadValue(dp);
                        if (value is null) continue;   // no numeric value (or NoRecordedValue flag)

                        // Skip points without a real observation time — UnixNanoToUtc(0) would stamp them at
                        // ingest wall-clock, collapsing late/backfilled data onto "now" as a false right-edge spike.
                        long tsNano = OtlpJson.ReadUnixNano(dp, "timeUnixNano");
                        if (tsNano == 0) continue;
                        DateTime ts = OtlpJson.UnixNanoToUtc(tsNano);

                        Dictionary<string, string> dpAttrs = new(StringComparer.Ordinal);
                        if (dp.TryGetProperty("attributes", out JsonElement dpa)) OtlpJson.ReadAttributes(dpa, dpAttrs);
                        string? labels = dpAttrs.Count > 0 ? JsonSerializer.Serialize(dpAttrs) : null;

                        records.Add(new MetricIngestRecord(ts, name, service, ns, pod, value.Value, kind, labels));
                    }
                }
            }
        }

        return records;
    }

    // Classify an OTLP sum by temporality + monotonicity so the query layer can chart it correctly:
    // delta sums carry the increment directly; monotonic cumulative sums are counters (need rate());
    // a non-monotonic cumulative sum (up/down counter) is treated as a gauge.
    private static MetricKind ClassifySum(JsonElement sum)
    {
        if (ReadTemporality(sum) == DeltaTemporality) return MetricKind.DeltaSum;
        return ReadBool(sum, "isMonotonic") ? MetricKind.Counter : MetricKind.Gauge;
    }

    private const int DeltaTemporality = 1;   // AGGREGATION_TEMPORALITY_DELTA

    // aggregationTemporality is a proto enum; OTLP/JSON emits it as a number (1/2) or its name string.
    private static int ReadTemporality(JsonElement sum)
    {
        if (!sum.TryGetProperty("aggregationTemporality", out JsonElement t)) return 0;
        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out int n)) return n;
        if (t.ValueKind == JsonValueKind.String)
        {
            string v = t.GetString() ?? "";
            if (v.EndsWith("DELTA", StringComparison.Ordinal)) return DeltaTemporality;
            if (v.EndsWith("CUMULATIVE", StringComparison.Ordinal)) return 2;
        }
        return 0;
    }

    private static bool ReadBool(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out JsonElement b)) return false;
        return b.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(b.GetString(), out bool bv) && bv,
            _ => false
        };
    }

    // A NumberDataPoint is a one-of asDouble (number) / asInt (int64 as string). Be lenient about types.
    private static double? ReadValue(JsonElement dp)
    {
        if (dp.TryGetProperty("asDouble", out JsonElement d))
        {
            if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out double dv)) return dv;
            if (d.ValueKind == JsonValueKind.String && double.TryParse(d.GetString(), CultureInfo.InvariantCulture, out double sv)) return sv;
        }
        if (dp.TryGetProperty("asInt", out JsonElement i))
        {
            if (i.ValueKind == JsonValueKind.String && long.TryParse(i.GetString(), out long lv)) return lv;
            if (i.ValueKind == JsonValueKind.Number && i.TryGetInt64(out long lv2)) return lv2;
        }
        return null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
}
