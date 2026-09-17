using System.Text.Json;
using System.Text.RegularExpressions;

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
public static partial class OtlpLogsParser
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

                    string bodyText = lr.TryGetProperty("body", out JsonElement body)
                        ? OtlpJson.AnyValueToString(body)
                        : "";

                    string? traceId = lr.TryGetProperty("traceId", out JsonElement tid) ? OtlpJson.Str(tid) : null;
                    if (OtlpJson.IsAbsentId(traceId)) traceId = null;

                    // The record's own traceId is only ever set by a producer that knows the span context —
                    // an SDK log bridge, or a collector explicitly configured to parse one out. Neither
                    // applies to the overwhelming majority of what arrives here: logs are tailed from
                    // container stdout by the filelog receiver, which cannot know a trace id, so the field
                    // is empty and the app's own trace id sits unused in the line it wrote. Recovering it
                    // here is what makes trace/log correlation work without every app owner editing a
                    // collector config they share with every other app on the cluster.
                    traceId ??= TraceIdFromAttributes(recAttrs) ?? TraceIdFromText(bodyText);

                    string? attrsJson = recAttrs.Count > 0 ? JsonSerializer.Serialize(recAttrs) : null;

                    records.Add(new LogIngestRecord(
                        Timestamp: ReadTimestamp(lr),
                        Namespace: ns,
                        Pod: pod,
                        Container: container,
                        Severity: ReadSeverity(lr),
                        Body: bodyText,
                        TraceId: traceId,
                        AttributesJson: attrsJson));
                }
            }
        }

        return records;
    }

    /// <summary>
    /// Attribute keys a logging library is likely to carry a W3C trace id under. Matched ordinally and in
    /// the order listed, because <c>recAttrs</c> is an ordinal dictionary and the casing genuinely differs
    /// between ecosystems — Serilog and .NET emit <c>TraceId</c>, Logback's MDC and structlog
    /// <c>trace_id</c>, the OTel appenders <c>trace_id</c>, Datadog <c>dd.trace_id</c>.
    /// </summary>
    private static readonly string[] TraceIdAttributeKeys =
    [
        "trace_id", "traceId", "TraceId", "traceID", "traceid", "trace.id", "otelTraceID", "dd.trace_id",
    ];

    private static string? TraceIdFromAttributes(Dictionary<string, string> attrs)
    {
        foreach (string key in TraceIdAttributeKeys)
            if (attrs.TryGetValue(key, out string? value) && NormalizeTraceId(value) is string id)
                return id;
        return null;
    }

    /// <summary>
    /// Recovers a trace id written into the log line itself.
    ///
    /// <para>This is the case that actually happens. An app that logs JSON to stdout puts its trace id in
    /// that JSON, but the filelog receiver's <c>container</c> operator only unwraps the runtime envelope —
    /// it does not parse the payload — so the whole line arrives as body text with no attributes at all.
    /// The same pattern covers logfmt (<c>trace_id=…</c>) and the plain <c>key: value</c> shapes, so one
    /// expression serves every format worth serving without committing to a JSON parse per log line.</para>
    ///
    /// <para>Bounded deliberately: the substring pre-check rejects nearly every line before the regex runs,
    /// and the id must be exactly 32 hex digits immediately after a trace-id key, so a stack trace that
    /// merely says "trace" costs one scan and a bare hex blob elsewhere in the line cannot be mistaken for
    /// one. Span ids are 16 digits and so cannot match.</para>
    /// </summary>
    private static string? TraceIdFromText(string body)
    {
        if (body.Length < 32 || !body.Contains("trace", StringComparison.OrdinalIgnoreCase)) return null;
        Match m = TraceIdInText().Match(body);
        return m.Success ? NormalizeTraceId(m.Groups[1].Value) : null;
    }

    [GeneratedRegex("""trace[_.\-]?id["']?\s*[:=]\s*["']?([0-9a-fA-F]{32})""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdInText();

    /// <summary>
    /// Accepts only a real 32-hex-digit trace id, lower-cased.
    ///
    /// The casing matters: the indexed <c>trace_id</c> is a Lucene StringField matched by exact term, and
    /// the id a trace detail searches with comes from the span side, where OTLP/JSON renders it lower-case.
    /// An id recovered as <c>ABC…</c> from a log line would index as a term nothing ever looks up.
    /// </summary>
    private static string? NormalizeTraceId(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return null;
        ReadOnlySpan<char> s = hex.AsSpan().Trim();
        if (s.Length != 32) return null;
        foreach (char c in s)
            if (!char.IsAsciiHexDigit(c)) return null;
        return OtlpJson.IsAbsentId(hex.Trim()) ? null : new string(s).ToLowerInvariant();
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
