using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>CRUD for tenant-scoped native telemetry alert rules (used by the rule-authoring UI).</summary>
public class TelemetryAlertRuleService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<TelemetryAlertRule>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.TelemetryAlertRules
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    /// <summary>Inserts a new rule (Id empty) or updates an existing one; enforces tenant ownership.</summary>
    public async Task UpsertAsync(TelemetryAlertRule rule, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        DateTime now = DateTime.UtcNow;

        if (rule.Id == Guid.Empty)
        {
            rule.Id = Guid.NewGuid();
            rule.CreatedAt = now;
            rule.UpdatedAt = now;
            db.TelemetryAlertRules.Add(rule);
        }
        else
        {
            TelemetryAlertRule? existing = await db.TelemetryAlertRules
                .FirstOrDefaultAsync(r => r.Id == rule.Id && r.TenantId == rule.TenantId, ct);
            if (existing is null) return;

            existing.Name = rule.Name;
            existing.ClusterId = rule.ClusterId;
            existing.Kind = rule.Kind;
            existing.Service = rule.Service;
            existing.Namespace = rule.Namespace;
            existing.MatchText = rule.MatchText;
            existing.Threshold = rule.Threshold;
            existing.WindowMinutes = rule.WindowMinutes;
            existing.Severity = rule.Severity;
            existing.RunbookUrl = rule.RunbookUrl;
            existing.IsEnabled = rule.IsEnabled;
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(Guid tenantId, Guid id, bool enabled, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        TelemetryAlertRule? rule = await db.TelemetryAlertRules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return;
        rule.IsEnabled = enabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        await db.TelemetryAlertRules.Where(r => r.Id == id && r.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }
}
