using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Reads/writes the global telemetry object-storage setting — which <see cref="StorageLink"/> the segment
/// engine seals to. A single row; edited from the admin UI (<c>/admin/telemetry</c>) and read by the
/// telemetry blob store on the (infrequent) seal/fetch path. The current value is cached in memory and
/// invalidated on save, so the blob store can consult it cheaply and pick up an admin change without a
/// restart. Registered as a singleton (its only dependency, the DbContext factory, is a singleton).
/// </summary>
public sealed class TelemetryStorageSettingService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TelemetryStorageSetting? _cached;

    /// <summary>The currently selected storage link Id, or null when none is set (use fallback storage).</summary>
    public async Task<Guid?> GetStorageLinkIdAsync(CancellationToken ct = default)
        => (await GetAsync(ct)).StorageLinkId;

    public async Task<TelemetryStorageSetting> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null) return _cached;
            await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            _cached = await db.TelemetryStorageSettings.AsNoTracking().FirstOrDefaultAsync(ct)
                      ?? new TelemetryStorageSetting();
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sets the telemetry storage link (null = fall back to flat config / local disk) and invalidates the cache.</summary>
    public async Task SetStorageLinkIdAsync(Guid? linkId, string? userId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        TelemetryStorageSetting? row = await db.TelemetryStorageSettings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new TelemetryStorageSetting();
            db.TelemetryStorageSettings.Add(row);
        }
        row.StorageLinkId = linkId;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);

        _cached = null; // next read reloads
    }
}
