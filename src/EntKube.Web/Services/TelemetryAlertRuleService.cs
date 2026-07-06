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

        // Keep fields within the AlertIncident column caps they flow into, so a firing rule can never
        // produce an incident that overflows and rolls back the evaluator's batch save.
        rule.Name = Cap(rule.Name, 200);
        rule.Severity = Cap(rule.Severity, 20);
        rule.RunbookUrl = rule.RunbookUrl is null ? null : Cap(rule.RunbookUrl, 500);

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
            existing.SiteId = rule.SiteId;
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

    private static string Cap(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];

    public async Task SetEnabledAsync(Guid tenantId, Guid id, bool enabled, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        TelemetryAlertRule? rule = await db.TelemetryAlertRules.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return;
        rule.IsEnabled = enabled;
        rule.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (!enabled) await ResolveOpenIncidentsAsync(db, id, ct);
    }

    public async Task DeleteAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        int deleted = await db.TelemetryAlertRules.Where(r => r.Id == id && r.TenantId == tenantId).ExecuteDeleteAsync(ct);
        if (deleted > 0) await ResolveOpenIncidentsAsync(db, id, ct);
    }

    /// <summary>
    /// Resolves any still-open incidents raised by a rule once it is disabled or deleted. The evaluator only
    /// touches incidents for currently-enabled rules, so without this an incident firing at the moment a rule
    /// is silenced would linger Active on the board forever with no all-clear.
    /// </summary>
    internal static async Task ResolveOpenIncidentsAsync(ApplicationDbContext db, Guid ruleId, CancellationToken ct)
    {
        string prefix = $"telemetry:{ruleId:N}:";
        DateTime now = DateTime.UtcNow;
        await db.AlertIncidents
            .Where(i => i.Fingerprint.StartsWith(prefix) && i.Status != IncidentStatus.Resolved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, IncidentStatus.Resolved)
                .SetProperty(i => i.ResolvedAt, now)
                .SetProperty(i => i.EndsAt, i => i.EndsAt ?? now)
                .SetProperty(i => i.UpdatedAt, now), ct);
    }
}
