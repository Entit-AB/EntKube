namespace EntKube.Web.Services;

// Telemetry DTOs — the parsed ingest records and the query-result shapes. These used to live inside
// TelemetryStore/PgLogService/PgTraceService/PgRumService; they were consolidated here when the Postgres
// telemetry path was decommissioned in favour of the self-built Lucene/S3 segment engine. Identity
// (tenant/cluster/site) is intentionally absent from the ingest records — it is stamped by the ingest
// endpoint from the verified token, never the payload.

// ──────── Ingest records ────────

/// <summary>One parsed log line ready to write. <c>Severity</c> is the numeric <see cref="LogLevel"/>; UTC timestamp.</summary>
public sealed record LogIngestRecord(
    DateTime Timestamp, string Namespace, string Pod, string Container,
    short Severity, string Body, string? TraceId, string? AttributesJson);

/// <summary>One parsed span. <c>Kind</c> = OTLP SpanKind (0..5); <c>StatusCode</c> = OTLP status (0 unset, 1 ok, 2 error).</summary>
public sealed record SpanIngestRecord(
    DateTime Start, string TraceId, string SpanId, string? ParentSpanId, string Name, string Service,
    short Kind, double DurationMs, short StatusCode, string? Namespace, string? Pod, string? AttributesJson);

/// <summary>One browser page view + its Core Web Vitals. UTC timestamp.</summary>
public sealed record RumPageViewRecord(
    DateTime Timestamp, string SessionId, string ViewId, string Path, string? Referrer,
    double? LoadMs, double? TtfbMs, double? LcpMs, double? Cls, double? InpMs, double? FcpMs,
    string? Browser, string? Os, string? Device);

/// <summary>One browser JS error (window.onerror / unhandledrejection). UTC timestamp.</summary>
public sealed record RumErrorRecord(
    DateTime Timestamp, string SessionId, string ViewId, string? Path, string Message, string? Stack, string? Source);

/// <summary>One browser resource / AJAX timing. <c>TraceId</c> links to the spans trace waterfall. UTC timestamp.</summary>
public sealed record RumResourceRecord(
    DateTime Timestamp, string SessionId, string ViewId, string? Path, string Name, string? Kind,
    double? DurationMs, int? Status, string? TraceId);

// ──────── Log query results ────────

/// <summary>One time bucket of the log-volume histogram: total rows and the error+fatal subcount.</summary>
public sealed record LogHistogramBucket(DateTime Start, long Total, long Errors);

// ──────── Trace query results ────────

/// <summary>A row in the trace-search list: the trace and its root span's service/operation.</summary>
public sealed record TraceSummary(
    string TraceId, DateTime Start, double DurationMs, int SpanCount, int ErrorCount,
    string RootService, string RootName);

/// <summary>One span in a trace (for the waterfall). <see cref="StatusCode"/> 2 = error.</summary>
public sealed record SpanRecord(
    DateTime Start, string SpanId, string? ParentSpanId, string Name, string Service,
    short Kind, double DurationMs, short StatusCode, string? AttributesJson);

/// <summary>One RED time bucket for a service: request/error counts and duration avg/p50/p95 (ms).</summary>
public sealed record RedBucket(
    DateTime Start, long Count, long Errors, double AvgMs, double P50Ms, double P95Ms);

/// <summary>A caller→callee edge in the service map, with call/error counts and average callee latency.</summary>
public sealed record ServiceEdge(string From, string To, long Calls, long Errors, double AvgMs);

/// <summary>Aggregate service stats over a window (for alert evaluation): request count, errors, p95 ms.</summary>
public sealed record ServiceStats(long Count, long Errors, double P95Ms);

// ──────── RUM query results ────────

public sealed record RumSiteOverview(
    long PageViews, long Sessions, long Errors,
    double? LcpP75, double? ClsP75, double? InpP75, double? FcpP75, double? AvgLoadMs, double? AvgTtfbMs);

public sealed record RumTopPage(string Path, long Views, double? LcpP75);
public sealed record RumTopError(string Message, long Count, DateTime LastSeen);
public sealed record RumSessionSummary(string SessionId, DateTime Started, DateTime LastSeen, long Views, string? LastPath, long Errors);

public sealed record RumViewRow(DateTime Ts, string Path, double? LoadMs, double? LcpMs, double? Cls, double? InpMs);
public sealed record RumErrorRow(DateTime Ts, string? Path, string Message, string? Source);
public sealed record RumResourceRow(DateTime Ts, string? Path, string Name, string? Kind, double? DurationMs, int? Status, string? TraceId);
public sealed record RumSessionDetail(List<RumViewRow> Views, List<RumErrorRow> Errors, List<RumResourceRow> Resources);
