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
                    JsonElement dataPoints = default;
                    if (metric.TryGetProperty("gauge", out JsonElement g) && g.TryGetProperty("dataPoints", out JsonElement gdp))
                        dataPoints = gdp;
                    else if (metric.TryGetProperty("sum", out JsonElement s) && s.TryGetProperty("dataPoints", out JsonElement sdp))
                        dataPoints = sdp;
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

                        records.Add(new MetricIngestRecord(ts, name, service, ns, pod, value.Value, labels));
                    }
                }
            }
        }

        return records;
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
