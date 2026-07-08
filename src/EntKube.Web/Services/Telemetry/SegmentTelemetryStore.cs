using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The Lucene/S3 segment-engine implementation of <see cref="ITelemetryIngest"/> — the ingest
/// counterpart to <see cref="SegmentLogService"/>. Log batches are appended to the active Lucene index
/// (<see cref="ActiveLogIndex"/>); there is no per-request database connection, so the Postgres
/// "too many clients" exhaustion that motivated this rewrite cannot occur.
///
/// Phase status: logs are fully implemented. Spans and RUM are appended by later phases (trace engine =
/// Phase 3, RUM = Phase 5); until then they are accepted-and-dropped rather than 500'd, so an OTLP
/// collector configured for the segment engine does not retry-storm. Metrics are intentionally NOT
/// stored here at all — they move to Prometheus (Phase 4).
/// </summary>
public sealed class SegmentTelemetryStore(
    LogSegmentManager logs, SpanSegmentManager spans, RumSegmentManager rum,
    ILogger<SegmentTelemetryStore> logger) : ITelemetryIngest
{
    public bool IsEnabled => true;

    public Task<int> WriteLogsAsync(Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        logs.WriteLogs(tenantId, clusterId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteSpansAsync(Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        spans.WriteSpans(tenantId, clusterId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumPageViewsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumPageViewRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.WritePageViews(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumErrorsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumErrorRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.WriteErrors(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumResourcesAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumResourceRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.WriteResources(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }
}
