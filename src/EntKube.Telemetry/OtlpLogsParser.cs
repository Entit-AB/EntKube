using System.Text.Json;

namespace EntKube.Web.Services;

/// <summary>
/// Parses an OTLP/JSON <c>ExportLogsServiceRequest</c> (what the OpenTelemetry Collector's
/// <c>otlphttp</c> exporter POSTs to <c>/v1/logs</c> when configured with <c>encoding: json</c>)
/// into flat <see cref="LogIngestRecord"/> rows. Shared OTLP/JSON reading lives in <see cref="OtlpJson"/>.
///
/// Kubernetes identity (namespace/pod/container) is read from the log record's *resource*
/// attributes, which the collector's k8sattributes processor populates as
/// <c>k8s.namespace.name</c> / <c>k8s.pod.name</c> / <c>k8s.container.name</c>. The collector also
/// copies short aliases (namespace/pod/container), so we fall back to those.
/// </summary>
public static class OtlpLogsParser
{
    public static List<LogIngestRecord> Parse(JsonDocument doc)
    {
        List<LogIngestRecord> records = [];
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("resourceLogs", out JsonElement resourceLogs) ||
            resourceLogs.ValueKind != JsonValueKind.Array)
            return records;

        foreach (JsonElement rl in resourceLogs.EnumerateArray())
        {
            Dictionary<string, string> resAttrs = new(StringComparer.Ordinal);
            if (rl.TryGetProperty("resource", out JsonElement resource) &&
                resource.TryGetProperty("attributes", out JsonElement rAttrs))
                OtlpJson.ReadAttributes(rAttrs, resAttrs);

            string ns = OtlpJson.FirstOf(resAttrs, "k8s.namespace.name", "namespace");
            string pod = OtlpJson.FirstOf(resAttrs, "k8s.pod.name", "pod");
            string containerFromResource = OtlpJson.FirstOf(resAttrs, "k8s.container.name", "container");

            if (!rl.TryGetProperty("scopeLogs", out JsonElement scopeLogs) ||
                scopeLogs.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement sl in scopeLogs.EnumerateArray())
            {
                if (!sl.TryGetProperty("logRecords", out JsonElement logRecords) ||
                    logRecords.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement lr in logRecords.EnumerateArray())
                {
                    Dictionary<string, string> recAttrs = new(StringComparer.Ordinal);
                    if (lr.TryGetProperty("attributes", out JsonElement lAttrs))
                        OtlpJson.ReadAttributes(lAttrs, recAttrs);

                    string container = !string.IsNullOrEmpty(containerFromResource)
                        ? containerFromResource
                        : OtlpJson.FirstOf(recAttrs, "k8s.container.name", "container");

                    string? traceId = lr.TryGetProperty("traceId", out JsonElement tid) ? OtlpJson.Str(tid) : null;
                    if (OtlpJson.IsAbsentId(traceId)) traceId = null;

                    string? attrsJson = recAttrs.Count > 0 ? JsonSerializer.Serialize(recAttrs) : null;

                    records.Add(new LogIngestRecord(
                        Timestamp: ReadTimestamp(lr),
                        Namespace: ns,
                        Pod: pod,
                        Container: container,
                        Severity: ReadSeverity(lr),
                        Body: lr.TryGetProperty("body", out JsonElement body) ? OtlpJson.AnyValueToString(body) : "",
                        TraceId: traceId,
                        AttributesJson: attrsJson));
                }
            }
        }

        return records;
    }

    // Prefer the event time; fall back to observed time; else now.
    private static DateTime ReadTimestamp(JsonElement lr)
    {
        long ns = OtlpJson.ReadUnixNano(lr, "timeUnixNano");
        if (ns == 0) ns = OtlpJson.ReadUnixNano(lr, "observedTimeUnixNano");
        return OtlpJson.UnixNanoToUtc(ns);
    }

    // Map OTLP SeverityNumber (1..24) to our LogLevel; fall back to severityText; else None.
    private static short ReadSeverity(JsonElement lr)
    {
        if (lr.TryGetProperty("severityNumber", out JsonElement sn))
        {
            int n = sn.ValueKind switch
            {
                JsonValueKind.Number => sn.TryGetInt32(out int v) ? v : 0,
                JsonValueKind.String => int.TryParse(sn.GetString(), out int v) ? v : SeverityNameToNumber(sn.GetString()),
                _ => 0
            };
            LogLevel byNumber = n switch
            {
                >= 21 => LogLevel.Fatal,
                >= 17 => LogLevel.Error,
                >= 13 => LogLevel.Warn,
                >= 9 => LogLevel.Info,
                >= 1 => LogLevel.Debug,
                _ => LogLevel.None
            };
            if (byNumber != LogLevel.None) return (short)byNumber;
        }

        if (lr.TryGetProperty("severityText", out JsonElement st))
            return (short)(LogLevelMap.FromText(st.GetString()) ?? LogLevel.None);

        return (short)LogLevel.None;
    }

    // ProtoJSON may render the enum by name (e.g. "SEVERITY_NUMBER_INFO").
    private static int SeverityNameToNumber(string? name) => name switch
    {
        not null when name.Contains("FATAL", StringComparison.OrdinalIgnoreCase) => 21,
        not null when name.Contains("ERROR", StringComparison.OrdinalIgnoreCase) => 17,
        not null when name.Contains("WARN", StringComparison.OrdinalIgnoreCase) => 13,
        not null when name.Contains("INFO", StringComparison.OrdinalIgnoreCase) => 9,
        not null when name.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("TRACE", StringComparison.OrdinalIgnoreCase) => 5,
        _ => 0
    };
}
