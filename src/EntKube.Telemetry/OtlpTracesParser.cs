using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Parses an OTLP/JSON <c>ExportTraceServiceRequest</c> (what the OpenTelemetry Collector's
/// <c>otlphttp</c> exporter POSTs to <c>/v1/traces</c> with <c>encoding: json</c>) into flat
/// <see cref="SpanIngestRecord"/> rows. Shared OTLP/JSON reading lives in <see cref="OtlpJson"/>.
///
/// service.name and Kubernetes identity come from the *resource* attributes; per-span fields
/// (name, kind, times, status) come from the span. Trace/span ids are hex per the OTLP spec.
/// </summary>
public static class OtlpTracesParser
{
    public static List<SpanIngestRecord> Parse(JsonDocument doc)
    {
        List<SpanIngestRecord> records = [];
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("resourceSpans", out JsonElement resourceSpans) ||
            resourceSpans.ValueKind != JsonValueKind.Array)
            return records;

        foreach (JsonElement rs in resourceSpans.EnumerateArray())
        {
            Dictionary<string, string> resAttrs = new(StringComparer.Ordinal);
            if (rs.TryGetProperty("resource", out JsonElement resource) &&
                resource.TryGetProperty("attributes", out JsonElement rAttrs))
                OtlpJson.ReadAttributes(rAttrs, resAttrs);

            string service = OtlpJson.FirstOf(resAttrs, "service.name");
            if (string.IsNullOrEmpty(service)) service = "unknown";
            string ns = OtlpJson.FirstOf(resAttrs, "k8s.namespace.name", "namespace");
            string pod = OtlpJson.FirstOf(resAttrs, "k8s.pod.name", "pod");

            if (!rs.TryGetProperty("scopeSpans", out JsonElement scopeSpans) ||
                scopeSpans.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement ss in scopeSpans.EnumerateArray())
            {
                if (!ss.TryGetProperty("spans", out JsonElement spans) ||
                    spans.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement sp in spans.EnumerateArray())
                {
                    string? traceId = sp.TryGetProperty("traceId", out JsonElement tid) ? OtlpJson.Str(tid) : null;
                    string? spanId = sp.TryGetProperty("spanId", out JsonElement sid) ? OtlpJson.Str(sid) : null;
                    // A span with no trace/span id is unusable (can't be linked or fetched) — skip it.
                    if (OtlpJson.IsAbsentId(traceId) || OtlpJson.IsAbsentId(spanId))
                        continue;

                    string? parentSpanId = sp.TryGetProperty("parentSpanId", out JsonElement psid) ? OtlpJson.Str(psid) : null;
                    if (OtlpJson.IsAbsentId(parentSpanId)) parentSpanId = null;

                    long startNs = OtlpJson.ReadUnixNano(sp, "startTimeUnixNano");
                    long endNs = OtlpJson.ReadUnixNano(sp, "endTimeUnixNano");
                    // No start time → we can't place it in the waterfall; skip rather than stamp "now".
                    if (startNs == 0) continue;
                    double durationMs = endNs > startNs ? (endNs - startNs) / 1_000_000.0 : 0;

                    Dictionary<string, string> spanAttrs = new(StringComparer.Ordinal);
                    if (sp.TryGetProperty("attributes", out JsonElement sAttrs))
                        OtlpJson.ReadAttributes(sAttrs, spanAttrs);
                    string? attrsJson = spanAttrs.Count > 0 ? JsonSerializer.Serialize(spanAttrs) : null;

                    records.Add(new SpanIngestRecord(
                        Start: OtlpJson.UnixNanoToUtc(startNs),
                        TraceId: traceId!,
                        SpanId: spanId!,
                        ParentSpanId: parentSpanId,
                        Name: sp.TryGetProperty("name", out JsonElement nm) ? OtlpJson.Str(nm) ?? "" : "",
                        Service: service,
                        Kind: ReadKind(sp),
                        DurationMs: durationMs,
                        StatusCode: ReadStatusCode(sp),
                        Namespace: string.IsNullOrEmpty(ns) ? null : ns,
                        Pod: string.IsNullOrEmpty(pod) ? null : pod,
                        AttributesJson: attrsJson));
                }
            }
        }

        return records;
    }

    // OTLP SpanKind: 0 UNSPECIFIED, 1 INTERNAL, 2 SERVER, 3 CLIENT, 4 PRODUCER, 5 CONSUMER.
    // ProtoJSON may render it as an int or an enum name ("SPAN_KIND_SERVER").
    private static short ReadKind(JsonElement sp)
    {
        if (!sp.TryGetProperty("kind", out JsonElement k)) return 0;
        return k.ValueKind switch
        {
            JsonValueKind.Number => k.TryGetInt32(out int v) ? (short)v : (short)0,
            JsonValueKind.String => KindNameToNumber(k.GetString()),
            _ => 0
        };
    }

    private static short KindNameToNumber(string? name) => name switch
    {
        not null when name.EndsWith("SERVER", StringComparison.OrdinalIgnoreCase) => 2,
        not null when name.EndsWith("CLIENT", StringComparison.OrdinalIgnoreCase) => 3,
        not null when name.EndsWith("PRODUCER", StringComparison.OrdinalIgnoreCase) => 4,
        not null when name.EndsWith("CONSUMER", StringComparison.OrdinalIgnoreCase) => 5,
        not null when name.EndsWith("INTERNAL", StringComparison.OrdinalIgnoreCase) => 1,
        _ => 0
    };

    // OTLP status code: 0 UNSET, 1 OK, 2 ERROR. ProtoJSON may render int or "STATUS_CODE_ERROR".
    private static short ReadStatusCode(JsonElement sp)
    {
        if (!sp.TryGetProperty("status", out JsonElement status) ||
            !status.TryGetProperty("code", out JsonElement code))
            return 0;
        return code.ValueKind switch
        {
            JsonValueKind.Number => code.TryGetInt32(out int v) ? (short)v : (short)0,
            JsonValueKind.String => code.GetString() is string s && s.EndsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                ? (short)2
                : (code.GetString() is string ok && ok.EndsWith("OK", StringComparison.OrdinalIgnoreCase) ? (short)1 : (short)0),
            _ => 0
        };
    }
}
