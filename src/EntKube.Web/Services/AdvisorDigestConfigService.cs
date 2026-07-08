using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

/// <summary>
/// Reads and writes per-tenant Operations Advisor digest settings, and decides when a
/// digest is due. Tenants without a row fall back to the global <c>Advisor:Digest:*</c>
/// configuration, so existing behavior is preserved until someone customizes it.
/// </summary>
public class AdvisorDigestConfigService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration)
{
    private DigestFrequency DefaultFrequency =>
        configuration.GetValue("Advisor:Digest:Enabled", true) ? DigestFrequency.Daily : DigestFrequency.Off;
    private int DefaultHour => configuration.GetValue("Advisor:Digest:HourUtc", 7);

    private AdvisorDigestConfig Default(Guid tenantId) => new()
    {
        TenantId = tenantId,
        Frequency = DefaultFrequency,
        HourUtc = DefaultHour,
    };

    /// <summary>The tenant's saved config, or the global-default config (not persisted) if none.</summary>
    public async Task<AdvisorDigestConfig> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AdvisorDigestConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId, ct)
               ?? Default(tenantId);
    }

    /// <summary>All saved configs keyed by tenant (used by the scan; missing tenants use defaults).</summary>
    public async Task<Dictionary<Guid, AdvisorDigestConfig>> GetAllAsync(CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.AdvisorDigestConfigs.ToDictionaryAsync(c => c.TenantId, ct);
    }

    public AdvisorDigestConfig DefaultFor(Guid tenantId) => Default(tenantId);

    public async Task SaveAsync(
        Guid tenantId, DigestFrequency frequency, int hourUtc, DayOfWeek weeklyDay, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AdvisorDigestConfig? c = await db.AdvisorDigestConfigs.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (c is null)
        {
            c = new AdvisorDigestConfig { TenantId = tenantId };
            db.AdvisorDigestConfigs.Add(c);
        }
        c.Frequency = frequency;
        c.HourUtc = Math.Clamp(hourUtc, 0, 23);
        c.WeeklyDay = weeklyDay;
        c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task MarkSentAsync(Guid tenantId, DateTime when, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AdvisorDigestConfig? c = await db.AdvisorDigestConfigs.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (c is null)
        {
            c = Default(tenantId);
            db.AdvisorDigestConfigs.Add(c);
        }
        c.LastSentAt = when;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True when a digest is due now for this config (at/after the hour, not already sent this period).</summary>
    public static bool IsDue(AdvisorDigestConfig c, DateTime nowUtc)
    {
        if (c.Frequency == DigestFrequency.Off) return false;
        if (nowUtc.Hour < c.HourUtc) return false;

        return c.Frequency switch
        {
            DigestFrequency.Daily => c.LastSentAt is null || c.LastSentAt.Value.Date < nowUtc.Date,
            DigestFrequency.Weekly => nowUtc.DayOfWeek == c.WeeklyDay
                                      && (c.LastSentAt is null || (nowUtc.Date - c.LastSentAt.Value.Date).TotalDays >= 6),
            _ => false,
        };
    }
}
