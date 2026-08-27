using EntKube.Web.Data;

namespace EntKube.Telemetry;

/// <summary>
/// The segment catalog: the small, cold index of "which sealed segments exist and what time range does each
/// cover". A query prunes against it before fetching a single byte of index data, so it is on every read
/// path — but it holds one row per sealed segment, not per event, so it is tiny next to the data itself.
///
/// This is deliberately an interface rather than a direct <see cref="ApplicationDbContext"/> dependency.
/// The catalog is the engine's <b>only</b> tie to the management-plane database; behind it, the engine is
/// just Lucene indexes, local files and object storage. Abstracting it is what lets the same engine run
/// inside a managed cluster as the <c>entkube-telemetry-indexer</c> component, keeping its catalog in a
/// local SQLite file on its PersistentVolume, while the management plane keeps serving already-stored
/// segments from Postgres through <see cref="EfSegmentCatalog"/>. See docs/telemetry-in-cluster.md.
///
/// Implementations must be safe for concurrent use — ingest seals on a background timer while queries read.
/// </summary>
public interface ISegmentCatalog
{
    /// <summary>Records a newly sealed, uploaded segment so queries can start resolving it.</summary>
    Task AddAsync(TelemetrySegment segment, CancellationToken ct = default);

    /// <summary>
    /// The signal's sealed segments whose <c>[MinTs, MaxTs]</c> overlaps the requested window, ordered by
    /// <see cref="TelemetrySegment.MinTs"/>. A null bound widens that side to everything, which is what
    /// label and trace-id lookups need. This is the pruning step: segments not returned are never opened.
    /// </summary>
    Task<IReadOnlyList<TelemetrySegment>> ListOverlappingAsync(
        Guid tenantId, string signal, DateTime? from, DateTime? to, CancellationToken ct = default);

    /// <summary>
    /// Removes the signal's segments whose newest event predates <paramref name="cutoff"/> and returns
    /// exactly the rows removed, so the caller can then free their object storage and local cache.
    ///
    /// The catalog rows go first, on purpose: once a segment is uncataloged no new query can resolve it, so
    /// deleting its object afterwards can never pull the archive out from under a query that already
    /// planned around it.
    /// </summary>
    Task<IReadOnlyList<TelemetrySegment>> RemoveExpiredAsync(
        Guid tenantId, string signal, DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    /// Earliest event timestamp across the signal's sealed segments, or null when it has none. Bounds how
    /// far back an index can answer for — the trace list uses it to decide when a window predates the
    /// trace-summary index and must fall back to aggregating raw spans.
    /// </summary>
    Task<DateTime?> GetMinTsAsync(Guid tenantId, string signal, CancellationToken ct = default);
}
