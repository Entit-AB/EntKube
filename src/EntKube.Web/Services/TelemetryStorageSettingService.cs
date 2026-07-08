using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Reads/writes the per-tenant telemetry object-storage setting — which <see cref="StorageLink"/> a
/// tenant's sealed segments go to. Telemetry is tenant-scoped, so there is one row per tenant, edited from
/// the tenant's telemetry settings UI and read by the tenant's blob store on the (infrequent) seal/fetch
/// path. The current value is cached in memory per tenant and invalidated on save, so an admin change is
/// picked up without a restart. Registered as a singleton (its only dependency, the DbContext factory, is
/// a singleton).
/// </summary>
public sealed class TelemetryStorageSettingService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private readonly ConcurrentDictionary<Guid, Guid?> _cache = new();

    /// <summary>The tenant's selected storage link Id, or null when none is set (use the fallback storage).</summary>
    public async Task<Guid?> GetStorageLinkIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(tenantId, out Guid? cached)) return cached;

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        TelemetryStorageSetting? row = await db.TelemetryStorageSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        Guid? value = row?.StorageLinkId;
        _cache[tenantId] = value;
        return value;
    }

    /// <summary>Sets a tenant's telemetry storage link (null = fall back to per-tenant prefix on flat/local) and invalidates the cache.</summary>
    public async Task SetStorageLinkIdAsync(Guid tenantId, Guid? linkId, string? userId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        TelemetryStorageSetting? row = await db.TelemetryStorageSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (row is null)
        {
            row = new TelemetryStorageSetting { TenantId = tenantId };
            db.TelemetryStorageSettings.Add(row);
        }
        row.StorageLinkId = linkId;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);

        _cache[tenantId] = linkId;
    }
}
