namespace EntKube.Web.Data;

/// <summary>
/// Catalog row for one sealed, immutable telemetry segment stored in object storage. The self-built
/// Lucene/S3 telemetry engine seals its active index into a time-bounded segment archive on S3 and
/// records it here; a query prunes to the segments whose <see cref="MinTs"/>/<see cref="MaxTs"/> overlap
/// its window before fetching anything. The catalog is deliberately tiny (one cold row per segment) and
/// lives in the control-plane database — never in the high-volume telemetry path — so it can never
/// contend with ingest.
///
/// Segments are tenant-scoped: each segment holds exactly one tenant's events (its own active index,
/// its own object storage), so different tenants' logs/traces/RUM never share a segment or a bucket.
/// Retention drops whole segments (object delete + row delete) once <see cref="MaxTs"/> ages past the window.
/// </summary>
public class TelemetrySegment
{
    public Guid Id { get; set; }

    /// <summary>The tenant this segment belongs to. Telemetry is tenant-scoped — a segment holds exactly
    /// one tenant's events, and queries only ever see the requesting tenant's segments.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The signal this segment holds: "logs", "spans", or "rum".</summary>
    public string Signal { get; set; } = "logs";

    /// <summary>Earliest event timestamp in the segment (UTC) — the lower pruning bound.</summary>
    public DateTime MinTs { get; set; }

    /// <summary>Latest event timestamp in the segment (UTC) — the upper pruning bound.</summary>
    public DateTime MaxTs { get; set; }

    /// <summary>Number of documents (log lines / spans) in the segment.</summary>
    public long DocCount { get; set; }

    /// <summary>Object-storage key of the sealed segment archive (a zipped Lucene index directory).</summary>
    public string ObjectKey { get; set; } = "";

    /// <summary>Compressed archive size in bytes (for cache accounting and diagnostics).</summary>
    public long SizeBytes { get; set; }

    /// <summary>When the segment was sealed and uploaded (UTC).</summary>
    public DateTime SealedAt { get; set; }
}
