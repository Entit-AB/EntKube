using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The Lucene/S3 segment-engine implementation of <see cref="ITelemetryIngest"/>. Each batch is routed to
/// the writing tenant's own segment manager (telemetry is tenant-scoped — no tenant's data ever shares a
/// segment or a bucket with another's). There is no per-request database connection, so the Postgres
/// "too many clients" exhaustion that motivated this rewrite cannot occur. Metrics are not stored here —
/// they are served by Prometheus.
/// </summary>
public sealed class SegmentTelemetryStore(
    SegmentManagerRegistry<LogSegmentManager> logs,
    SegmentManagerRegistry<SpanSegmentManager> spans,
    SegmentManagerRegistry<RumSegmentManager> rum) : ITelemetryIngest
{
    public bool IsEnabled => true;

    public Task<int> WriteLogsAsync(Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        logs.For(tenantId).WriteLogs(tenantId, clusterId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteSpansAsync(Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        spans.For(tenantId).WriteSpans(tenantId, clusterId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumPageViewsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumPageViewRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.For(tenantId).WritePageViews(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumErrorsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumErrorRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.For(tenantId).WriteErrors(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }

    public Task<int> WriteRumResourcesAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumResourceRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return Task.FromResult(0);
        rum.For(tenantId).WriteResources(tenantId, siteId, records);
        return Task.FromResult(records.Count);
    }
}
