using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The phrases a tenant's support mailbox watches for.
///
/// <para><b>Built-in until somebody takes them over.</b> A tenant with no rules of its own
/// uses <see cref="MailTriageRuleSet.BuiltIn"/>, so the mailbox works on day one without
/// anybody configuring anything. Choosing to edit them copies the built-in set into the
/// tenant's own rows, after which the built-ins are out of the picture entirely — a tenant
/// that deletes a phrase must not have it quietly reappear because it is still in the
/// code.</para>
/// </summary>
public class MailTriageRuleService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>The rules in force: the tenant's own if it has any, otherwise the built-ins.</summary>
    public async Task<MailTriageRuleSet> GetEffectiveAsync(Guid tenantId, CancellationToken ct = default)
    {
        List<MailTriageRule> configured = await ListAsync(tenantId, ct);

        return configured.Count == 0 ? MailTriageRuleSet.BuiltIn : new MailTriageRuleSet(configured);
    }

    /// <summary>Whether this tenant has taken the rules over, or is still on the built-ins.</summary>
    public async Task<bool> IsConfiguredAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.MailTriageRules.AnyAsync(r => r.TenantId == tenantId, ct);
    }

    public async Task<List<MailTriageRule>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.MailTriageRules.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.Signal)
            .ThenBy(r => r.Priority)
            .ThenBy(r => r.SortOrder)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Copies the built-in phrases into this tenant's own rules, so they can be edited.
    /// Refuses when the tenant already has rules — it is a starting point, not a reset.
    /// </summary>
    public async Task<int> AdoptBuiltInAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.MailTriageRules.AnyAsync(r => r.TenantId == tenantId, ct))
        {
            return 0;
        }

        List<MailTriageRule> copies = [.. MailTriageRuleSet.Defaults().Select(r => new MailTriageRule
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Signal = r.Signal,
            Phrase = r.Phrase,
            Priority = r.Priority,
            Criterion = r.Criterion,
            SortOrder = r.SortOrder,
        })];

        db.MailTriageRules.AddRange(copies);
        await db.SaveChangesAsync(ct);

        return copies.Count;
    }

    public async Task<MailTriageRule> SaveAsync(MailTriageRule rule, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        rule.Phrase = rule.Phrase.Trim();
        rule.UpdatedAt = DateTime.UtcNow;

        // Only a priority rule carries these, and leaving them on a development or
        // third-party rule would show an operator a criterion that decides nothing.
        if (rule.Signal != MailSignal.Priority)
        {
            rule.Priority = null;
            rule.Criterion = null;
        }

        if (rule.Id == Guid.Empty)
        {
            rule.Id = Guid.NewGuid();
            db.MailTriageRules.Add(rule);
        }
        else
        {
            db.MailTriageRules.Update(rule);
        }

        await db.SaveChangesAsync(ct);
        return rule;
    }

    public async Task DeleteAsync(Guid ruleId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        MailTriageRule? rule = await db.MailTriageRules.FindAsync([ruleId], ct);

        if (rule is not null)
        {
            db.MailTriageRules.Remove(rule);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// What a sample message would be triaged as, without ingesting it. The point of the
    /// rules being editable is being able to see the effect of an edit; the point of this
    /// is being able to see it before a real message arrives.
    /// </summary>
    public async Task<(TicketPriority Priority, string? Criterion, bool Development, bool ThirdParty)>
        PreviewAsync(Guid tenantId, string sampleText, CancellationToken ct = default)
    {
        MailTriageRuleSet rules = await GetEffectiveAsync(tenantId, ct);
        string text = (sampleText ?? "").ToLowerInvariant();

        (TicketPriority priority, string? criterion) = rules.ProposePriority(text);

        return (priority, criterion, rules.LooksLikeDevelopment(text), rules.MentionsThirdParty(text));
    }
}
