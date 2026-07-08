using EntKube.Web.Services;

namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// The telemetry ingest contract the OTLP/RUM endpoints write through, implemented by the Lucene/S3
/// <see cref="SegmentTelemetryStore"/>. The ingest endpoints and the parser front-halves
/// (<see cref="OtlpIngest"/>/<see cref="RumIngest"/>) depend only on this interface, so they are decoupled
/// from the storage engine. <see cref="IsEnabled"/> gates whether telemetry ingest/UI is active.
/// </summary>
public interface ITelemetryIngest
{
    /// <summary>True when telemetry ingest is configured and usable; false makes every write a no-op.</summary>
    bool IsEnabled { get; }

    Task<int> WriteLogsAsync(Guid tenantId, Guid clusterId, IReadOnlyList<LogIngestRecord> records, CancellationToken ct = default);
    Task<int> WriteSpansAsync(Guid tenantId, Guid clusterId, IReadOnlyList<SpanIngestRecord> records, CancellationToken ct = default);
    Task<int> WriteRumPageViewsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumPageViewRecord> records, CancellationToken ct = default);
    Task<int> WriteRumErrorsAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumErrorRecord> records, CancellationToken ct = default);
    Task<int> WriteRumResourcesAsync(Guid tenantId, Guid siteId, IReadOnlyList<RumResourceRecord> records, CancellationToken ct = default);
}
