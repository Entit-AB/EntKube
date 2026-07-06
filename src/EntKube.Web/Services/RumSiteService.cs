using System.Collections.Concurrent;
using System.Security.Cryptography;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>The ingest-relevant projection of a <see cref="RumSite"/>, resolved from its public key.</summary>
public sealed record RumSiteInfo(
    Guid SiteId, Guid TenantId, Guid? ClusterId, IReadOnlyList<string> Origins, double SampleRate, bool IsEnabled);

/// <summary>
/// Resolves a RUM public key to its site (with a short in-memory TTL cache, including a negative cache for
/// unknown keys) so the public ingest endpoint doesn't hit the DB on every browser beacon, and provides the
/// admin CRUD for <see cref="RumSite"/>s. Singleton; cache is per management-plane instance.
/// </summary>
public sealed class RumSiteService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (RumSiteInfo? Info, DateTime Expiry)> _cache = new(StringComparer.Ordinal);

    /// <summary>Resolves a public key to its site info, or null if unknown. Cached (positive and negative).</summary>
    public async Task<RumSiteInfo?> ResolveAsync(string publicKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(publicKey)) return null;

        DateTime now = DateTime.UtcNow;
        if (_cache.TryGetValue(publicKey, out (RumSiteInfo? Info, DateTime Expiry) hit) && hit.Expiry > now)
            return hit.Info;

        using ApplicationDbContext db = dbFactory.CreateDbContext();
        RumSite? site = await db.RumSites.AsNoTracking().FirstOrDefaultAsync(s => s.PublicKey == publicKey, ct);
        RumSiteInfo? info = site is null
            ? null
            : new RumSiteInfo(site.Id, site.TenantId, site.ClusterId, site.Origins, site.SampleRate, site.IsEnabled);

        _cache[publicKey] = (info, now + CacheTtl);
        return info;
    }

    private void InvalidateCache() => _cache.Clear();

    public async Task<List<RumSite>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.RumSites.Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<RumSite> CreateAsync(
        Guid tenantId, string name, Guid? clusterId, string allowedOrigins, double sampleRate, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        RumSite site = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            Name = name.Trim(),
            PublicKey = NewPublicKey(),
            AllowedOrigins = allowedOrigins,
            SampleRate = Math.Clamp(sampleRate, 0, 1),
            IsEnabled = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.RumSites.Add(site);
        await db.SaveChangesAsync(ct);
        return site;
    }

    public async Task UpdateAsync(
        Guid tenantId, Guid id, string name, Guid? clusterId, string allowedOrigins, double sampleRate, bool isEnabled,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        RumSite? site = await db.RumSites.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (site is null) return;
        site.Name = name.Trim();
        site.ClusterId = clusterId;
        site.AllowedOrigins = allowedOrigins;
        site.SampleRate = Math.Clamp(sampleRate, 0, 1);
        site.IsEnabled = isEnabled;
        site.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateCache();
    }

    /// <summary>Rotates the public key (invalidating the old snippet embed).</summary>
    public async Task<string?> RotateKeyAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        RumSite? site = await db.RumSites.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (site is null) return null;
        site.PublicKey = NewPublicKey();
        site.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        InvalidateCache();
        return site.PublicKey;
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.RumSites.Where(s => s.Id == id && s.TenantId == tenantId).ExecuteDeleteAsync(ct);
        InvalidateCache();
    }

    private static string NewPublicKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
