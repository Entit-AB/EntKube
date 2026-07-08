namespace EntKube.Web.Data;

/// <summary>
/// Global (non-tenant) setting for the telemetry segment engine's object storage: which registered
/// <see cref="StorageLink"/> (AWS S3 / Cleura S3 / MinIO) sealed segments are written to. There is a
/// single row — one telemetry store serves all tenants — edited from the admin UI. A null
/// <see cref="StorageLinkId"/> means no link is selected, and the engine falls back to the flat
/// <c>Telemetry:ObjectStorage</c> config or local disk.
/// </summary>
public class TelemetryStorageSetting
{
    public Guid Id { get; set; }

    /// <summary>The chosen storage link's Id, or null to use the flat config / local-disk fallback.</summary>
    public Guid? StorageLinkId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedByUserId { get; set; }
}
