using EntKube.Web.Services;

namespace EntKube.Telemetry;

/// <summary>
/// The RUM query surface, implemented by the Lucene/S3 segment engine (<see cref="SegmentRumService"/>).
/// The RUM dashboards inject this interface so they stay decoupled from the storage engine. Scoped by
/// tenant_id + site_id (RUM is site-scoped).
/// </summary>
public interface IRumQueryService
{
    Task<bool> HasDataAsync(Guid tenantId, Guid siteId, CancellationToken ct = default);

    Task<RumSiteOverview?> GetOverviewAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, CancellationToken ct = default);

    Task<List<RumTopPage>> GetTopPagesAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default);

    Task<List<RumTopError>> GetTopErrorsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default);

    /// <summary>
    /// As <see cref="GetTopErrorsAsync"/>, but null when the store could not be read. A dashboard can
    /// show a failure as an empty table; a sweep that moves a cursor past it would lose the window.
    /// </summary>
    Task<List<RumTopError>?> TryGetTopErrorsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 10, CancellationToken ct = default);

    Task<List<TimeSeriesDataPoint>> GetPageViewSeriesAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int buckets = 60, CancellationToken ct = default);

    Task<List<RumSessionSummary>> GetSessionsAsync(Guid tenantId, Guid siteId, DateTime from, DateTime to, int limit = 50, CancellationToken ct = default);

    Task<RumSessionDetail?> GetSessionDetailAsync(Guid tenantId, Guid siteId, string sessionId, DateTime from, DateTime to, CancellationToken ct = default);
}
