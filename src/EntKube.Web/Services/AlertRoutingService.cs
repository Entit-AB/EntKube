using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;

namespace EntKube.Web.Services;

public class AlertRoutingService(IDbContextFactory<TelemetryDbContext> dbFactory)
{
    public async Task<List<AlertRoutingRule>> GetRulesAsync(Guid tenantId, CancellationToken ct = default)
    {
        using TelemetryDbContext db = dbFactory.CreateDbContext();
        return await db.AlertRoutingRules
            .Include(r => r.Channel)
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Whether a notification channel is this tenant's.
    ///
    /// <para>⚠️ The channel is where an incident is <b>delivered</b> — a Teams webhook, a
    /// mail recipient. So a rule may only ever name one of ours: pointing a rule at somebody
    /// else's channel sends our incidents to them, and pointing <i>their</i> rule at ours
    /// delivers theirs to us.</para>
    /// </summary>
    private static async Task<bool> ChannelIsTenantsAsync(
        TelemetryDbContext db, Guid tenantId, Guid? channelId, CancellationToken ct) =>
        channelId is not Guid id
        || await db.NotificationChannels.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct);

    public async Task<Guid> CreateRuleAsync(Guid tenantId, string name, int priority, Guid? channelId,
        string? matchAlertName, string? matchNamespace, string? matchSeverity,
        string? matchLabelKey, string? matchLabelValue,
        bool suppressIncident = false, Guid? matchClusterId = null,
        CancellationToken ct = default)
    {
        using TelemetryDbContext db = dbFactory.CreateDbContext();

        if (!await ChannelIsTenantsAsync(db, tenantId, channelId, ct))
        {
            throw new InvalidOperationException(
                "Notification channel not found, or it is not this tenant's.");
        }

        AlertRoutingRule rule = new()
        {
            TenantId = tenantId,
            Name = name,
            Priority = priority,
            ChannelId = channelId,
            MatchAlertName = string.IsNullOrWhiteSpace(matchAlertName) ? null : matchAlertName.Trim(),
            MatchNamespace = string.IsNullOrWhiteSpace(matchNamespace) ? null : matchNamespace.Trim(),
            MatchSeverity = string.IsNullOrWhiteSpace(matchSeverity) ? null : matchSeverity.Trim(),
            MatchLabelKey = string.IsNullOrWhiteSpace(matchLabelKey) ? null : matchLabelKey.Trim(),
            MatchLabelValue = string.IsNullOrWhiteSpace(matchLabelValue) ? null : matchLabelValue.Trim(),
            SuppressIncident = suppressIncident,
            MatchClusterId = matchClusterId,
        };
        db.AlertRoutingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return rule.Id;
    }

    /// <param name="tenantId">
    /// Whose rule. ⚠️ Two things, both off a bare id: the rule itself, and the channel it is
    /// repointed at. Taking somebody else's rule and giving it our channel delivers their
    /// incident notifications to us.
    /// </param>
    public async Task UpdateRuleAsync(
        Guid tenantId, Guid id, string name, int priority, Guid? channelId,
        string? matchAlertName, string? matchNamespace, string? matchSeverity,
        string? matchLabelKey, string? matchLabelValue, bool isEnabled,
        bool suppressIncident = false, Guid? matchClusterId = null,
        CancellationToken ct = default)
    {
        using TelemetryDbContext db = dbFactory.CreateDbContext();
        if (!await ChannelIsTenantsAsync(db, tenantId, channelId, ct))
        {
            throw new InvalidOperationException(
                "Notification channel not found, or it is not this tenant's.");
        }

        // FindAsync takes a key and cannot carry a predicate.
        AlertRoutingRule? rule = await db.AlertRoutingRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is null) return;

        rule.Name = name;
        rule.Priority = priority;
        rule.ChannelId = channelId;
        rule.MatchAlertName = string.IsNullOrWhiteSpace(matchAlertName) ? null : matchAlertName.Trim();
        rule.MatchNamespace = string.IsNullOrWhiteSpace(matchNamespace) ? null : matchNamespace.Trim();
        rule.MatchSeverity = string.IsNullOrWhiteSpace(matchSeverity) ? null : matchSeverity.Trim();
        rule.MatchLabelKey = string.IsNullOrWhiteSpace(matchLabelKey) ? null : matchLabelKey.Trim();
        rule.MatchLabelValue = string.IsNullOrWhiteSpace(matchLabelValue) ? null : matchLabelValue.Trim();
        rule.IsEnabled = isEnabled;
        rule.SuppressIncident = suppressIncident;
        rule.MatchClusterId = matchClusterId;
        await db.SaveChangesAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose rule. Removing one stops the alerts it routed being delivered at all.
    /// </param>
    public async Task DeleteRuleAsync(
        Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using TelemetryDbContext db = dbFactory.CreateDbContext();
        AlertRoutingRule? rule = await db.AlertRoutingRules
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (rule is not null) db.AlertRoutingRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns the first matching enabled rule for an alert, ordered by priority ascending.
    /// Includes suppression rules — callers should check <see cref="AlertRoutingRule.SuppressIncident"/>.
    /// Returns null if no rule matches (caller falls back to default channel behavior).
    /// </summary>
    public async Task<AlertRoutingRule?> MatchAsync(Guid tenantId, Guid? clusterId, string alertName, string severity,
        string? @namespace, Dictionary<string, string>? labels, CancellationToken ct = default)
    {
        List<AlertRoutingRule> rules = await GetRulesAsync(tenantId, ct);

        foreach (AlertRoutingRule rule in rules.Where(r => r.IsEnabled))
        {
            if (rule.MatchClusterId.HasValue && rule.MatchClusterId != clusterId)
                continue;

            if (!string.IsNullOrEmpty(rule.MatchAlertName)
                && !alertName.Contains(rule.MatchAlertName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchSeverity)
                && !string.Equals(severity, rule.MatchSeverity, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchNamespace)
                && !string.Equals(@namespace, rule.MatchNamespace, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(rule.MatchLabelKey) && labels is not null)
            {
                if (!labels.TryGetValue(rule.MatchLabelKey, out string? labelVal)) continue;
                if (!string.IsNullOrEmpty(rule.MatchLabelValue)
                    && !string.Equals(labelVal, rule.MatchLabelValue, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return rule;
        }
        return null;
    }
}
