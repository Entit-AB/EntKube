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
public sealed class RumSiteService(IDbContextFactory<ApplicationDbContext> dbFactory, IConfiguration config)
{
    // Lower Rum:SiteCacheTtlSeconds for faster propagation of a disable/rotate across instances (at the cost
    // of more resolve DB hits); the default trades ~30s of staleness for far fewer lookups.
    private readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(Math.Max(1, config.GetValue<int?>("Rum:SiteCacheTtlSeconds") ?? 30));
    private const int MaxCacheEntries = 10_000;   // bound the (incl. negative) cache against unknown-key sprays
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

        // Bound the dictionary: a spray of distinct unknown keys (each a negative-cache miss) can't grow it
        // without limit. Purge expired entries when it gets large; clear outright if still over the cap.
        if (_cache.Count >= MaxCacheEntries)
        {
            foreach (KeyValuePair<string, (RumSiteInfo? Info, DateTime Expiry)> kv in _cache)
                if (kv.Value.Expiry <= now) _cache.TryRemove(kv.Key, out _);
            if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        }

        _cache[publicKey] = (info, now + _cacheTtl);
        return info;
    }

    private void InvalidateCache() => _cache.Clear();

    public async Task<List<RumSite>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.RumSites.Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Sites owned by any of the given apps — the customer-portal scope. A customer only ever passes the
    /// ids of apps they can access, and sites with a null <see cref="RumSite.AppId"/> (admin-managed,
    /// unowned) are intentionally excluded so they never surface in the portal.
    /// </summary>
    public async Task<List<RumSite>> ListForAppsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> appIds, CancellationToken ct = default)
    {
        if (appIds.Count == 0) return [];
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.RumSites
            .Where(s => s.TenantId == tenantId && s.AppId != null && appIds.Contains(s.AppId.Value))
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<RumSite> CreateAsync(
        Guid tenantId, string name, Guid? clusterId, string allowedOrigins, double sampleRate,
        CancellationToken ct = default, Guid? appId = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;
        RumSite site = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            AppId = appId,
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
        CancellationToken ct = default, Guid? appId = null)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        RumSite? site = await db.RumSites.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
        if (site is null) return;
        site.Name = name.Trim();
        site.ClusterId = clusterId;
        site.AppId = appId;
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

        // Clean up RUM alert rules bound to this site (there's no FK cascade), resolving any incidents they
        // raised — otherwise a deleted site leaves orphaned rules evaluating against a gone site and, worse,
        // any incident open at deletion time would linger forever.
        List<Guid> ruleIds = await db.TelemetryAlertRules
            .Where(r => r.TenantId == tenantId && r.SiteId == id).Select(r => r.Id).ToListAsync(ct);
        foreach (Guid ruleId in ruleIds)
            await TelemetryAlertRuleService.ResolveOpenIncidentsAsync(db, ruleId, ct);
        if (ruleIds.Count > 0)
            await db.TelemetryAlertRules.Where(r => r.TenantId == tenantId && r.SiteId == id).ExecuteDeleteAsync(ct);

        await db.RumSites.Where(s => s.Id == id && s.TenantId == tenantId).ExecuteDeleteAsync(ct);
        InvalidateCache();
    }

    private static string NewPublicKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
