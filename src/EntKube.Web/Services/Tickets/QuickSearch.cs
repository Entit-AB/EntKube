using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>A customer found by quick search, with how many of their tickets are open.</summary>
public sealed record CustomerHit(Guid Id, string Name, int OpenTickets);

/// <summary>What quick search found besides tickets.</summary>
public sealed record QuickResults(IReadOnlyList<CustomerHit> Customers, IReadOnlyList<KbAnswer> Answers)
{
    public static readonly QuickResults None = new([], []);

    public bool IsEmpty => Customers.Count == 0 && Answers.Count == 0;
}

/// <summary>
/// Zammad's quick search, the part beyond tickets (helpdesk phase 8): words typed into the queue's
/// search also find this tenant's customers by name and its knowledge-base answers.
///
/// <para>Only for plain words. A query with a field (<c>tag:vpn</c>), a quote, a range or an
/// operator is a ticket query and is left to the ticket search alone.</para>
///
/// <para>Answers are found as the knowledge base's own list finds them — drafts and internal ones
/// included, archived ones not — because the person searching is an agent who could open the
/// knowledge base and see them there.</para>
/// </summary>
public class SupportQuickSearch(IDbContextFactory<SupportDbContext> dbFactory)
{
    public const int Limit = 8;

    /// <summary>Whether the query is plain words: letters, digits, spaces and the odd dash or dot.</summary>
    public static bool IsPlain(string? query) =>
        !string.IsNullOrWhiteSpace(query)
        && query.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.' or '&' or '\'')
        && query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .All(w => w is not ("AND" or "OR" or "NOT"));

    public async Task<QuickResults> SearchAsync(Guid tenantId, string? query, CancellationToken ct = default)
    {
        if (!IsPlain(query))
        {
            return QuickResults.None;
        }

        string[] words = [.. query!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant())];

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<Customer> customers = db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId);
        IQueryable<KbAnswer> answers = db.KbAnswers.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Visibility != KbVisibility.Archived);

        foreach (string word in words)
        {
            string like = "%" + word.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            customers = customers.Where(c => EF.Functions.Like(c.Name.ToLower(), like, "\\"));
            answers = answers.Where(a => EF.Functions.Like(a.Title.ToLower(), like, "\\")
                                         || EF.Functions.Like(a.Keywords.ToLower(), like, "\\")
                                         || EF.Functions.Like(a.BodyText.ToLower(), like, "\\"));
        }

        List<CustomerHit> found = await customers
            .OrderBy(c => c.Name)
            .Take(Limit)
            .Select(c => new CustomerHit(c.Id, c.Name, db.Tickets.Count(t =>
                t.CustomerId == c.Id && t.TenantId == tenantId
                && t.Status != TicketStatus.Closed && t.Status != TicketStatus.Rejected && t.Status != TicketStatus.Merged)))
            .ToListAsync(ct);

        return new QuickResults(found, await answers.OrderBy(a => a.Title).Take(Limit).ToListAsync(ct));
    }
}
