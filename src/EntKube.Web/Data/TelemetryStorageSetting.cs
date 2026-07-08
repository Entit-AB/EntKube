namespace EntKube.Web.Data;

/// <summary>
/// Per-tenant setting for where that tenant's telemetry segments (logs / traces / RUM) are stored: which
/// registered <see cref="StorageLink"/> (AWS S3 / Cleura S3 / MinIO) sealed segments are written to.
/// One row per tenant, edited from the tenant's telemetry settings UI. A null <see cref="StorageLinkId"/>
/// means the tenant has no link selected, so its segments fall back to a per-tenant prefix on the flat
/// <c>Telemetry:ObjectStorage</c> config or local disk. Telemetry is tenant-scoped — each tenant's data
/// lives in its own storage and is never mixed with another tenant's.
/// </summary>
public class TelemetryStorageSetting
{
    public Guid Id { get; set; }

    /// <summary>The tenant this storage setting belongs to (one row per tenant).</summary>
    public Guid TenantId { get; set; }

    /// <summary>The chosen storage link's Id, or null to use the per-tenant-prefixed flat/local fallback.</summary>
    public Guid? StorageLinkId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
}
