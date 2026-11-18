using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// The help-answer library (docs/helpdesk.md, phase 5): categories, answers and who may read them.
///
/// <para><b>Three readers, three rules.</b> The knowledge base's own list shows everything. An
/// agent writing to a customer is offered what is published — public and internal — except
/// answers narrowed to a <em>different</em> customer, which would put one customer's material in
/// front of another. A customer in the portal sees only what is public, published, and theirs or
/// everybody's.</para>
/// </summary>
public partial class KnowledgeBaseService(IDbContextFactory<SupportDbContext> dbFactory)
{
    /// <summary>How an answer stands right now, as the list shows it.</summary>
    public static string StateOf(KbAnswer a, DateTime now) => a.Visibility switch
    {
        KbVisibility.Public when a.PublishAt is DateTime p && p > now => "scheduled",
        KbVisibility.Public => "public",
        KbVisibility.Internal => "internal",
        KbVisibility.Archived => "archived",
        KbVisibility.Operations => "operations",
        _ => "draft",
    };

    // ───────────────────────────── Categories ─────────────────────────────

    public async Task<List<KbCategory>> CategoriesAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbCategories.AsNoTracking().Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct);
    }

    /// <summary>
    /// Adds or renames a category, or moves it under another. A move takes everything under it
    /// along; a category cannot be moved under itself or anything beneath it.
    /// </summary>
    public async Task<KbCategory> SaveCategoryAsync(
        Guid tenantId, Guid? id, string name, Guid? parentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A category needs a name.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<KbCategory> all = await db.KbCategories.Where(c => c.TenantId == tenantId).ToListAsync(ct);

        if (parentId is Guid p)
        {
            if (all.All(c => c.Id != p))
            {
                throw new InvalidOperationException("There is no such parent category here.");
            }

            for (Guid? walk = p; walk is Guid w; walk = all.First(c => c.Id == w).ParentId)
            {
                if (w == id)
                {
                    throw new InvalidOperationException("A category cannot be moved under itself or anything beneath it.");
                }
            }
        }

        KbCategory? row = id is Guid existing ? all.FirstOrDefault(c => c.Id == existing) : null;

        if (id is not null && row is null)
        {
            throw new InvalidOperationException("There is no such category here.");
        }

        if (row is null)
        {
            row = new KbCategory { Id = Guid.NewGuid(), TenantId = tenantId, Name = "" };
            db.KbCategories.Add(row);
        }

        row.Name = name.Trim();
        row.ParentId = parentId;
        row.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>Deletes an empty category. One with answers or subcategories is refused, saying which.</summary>
    public async Task<bool> DeleteCategoryAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KbCategory? row = await db.KbCategories.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);

        if (row is null)
        {
            return false;
        }

        if (await db.KbCategories.AnyAsync(c => c.ParentId == id, ct))
        {
            throw new InvalidOperationException("This category has subcategories. Move or delete them first.");
        }

        if (await db.KbAnswers.AnyAsync(a => a.CategoryId == id, ct))
        {
            throw new InvalidOperationException("This category has answers. Move, archive or delete them first.");
        }

        db.KbCategories.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ───────────────────────────── Answers ─────────────────────────────

    /// <summary>Every answer in a category (or all of them), for the knowledge base's own list.</summary>
    public async Task<List<KbAnswer>> AnswersAsync(Guid tenantId, Guid? categoryId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswers.AsNoTracking()
            .Where(a => a.TenantId == tenantId && (categoryId == null || a.CategoryId == categoryId))
            .OrderBy(a => a.Title).ToListAsync(ct);
    }

    public async Task<KbAnswer?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Adds or changes an answer. The body is sanitised here, before it is stored; its text is
    /// kept beside it for search. The category and any customer must be this tenant's.
    ///
    /// <para><b>One rule set for every answer</b> (helpdesk phase 11):</para>
    /// <list type="bullet">
    /// <item>A non-empty id that is not this tenant's answer is refused — it used to create a new
    /// row from the form, so a deleted answer came back as a copy.</item>
    /// <item>An application's answer (<see cref="KbAnswer.AppId"/>) is agents-only: Operations or
    /// Archived, no publish date, its app's customer, a section. The application is taken from the
    /// STORED row on every update; only a new answer names one, and it must be this tenant's.</item>
    /// <item>A change to the title or body keeps what it said before as a revision, with the
    /// optional <paramref name="changeSummary"/>.</item>
    /// </list>
    /// </summary>
    public async Task<KbAnswer> SaveAnswerAsync(
        Guid tenantId, KbAnswer input, string actor, CancellationToken ct = default, string? changeSummary = null)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new ArgumentException("An answer needs a title.");
        }

        string language = (input.Language ?? "").Trim();

        if (!LanguageTag().IsMatch(language))
        {
            throw new ArgumentException("The language is a tag such as sv, en or en-GB.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.KbCategories.AnyAsync(c => c.Id == input.CategoryId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("Choose a category of this knowledge base.");
        }

        KbAnswer? row = null;

        if (input.Id != Guid.Empty)
        {
            row = await db.KbAnswers.FirstOrDefaultAsync(a => a.Id == input.Id && a.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException("This answer no longer exists; it may have been deleted while you edited it.");
        }

        // The application: the stored row's on an update, the input's only on creation.
        Guid? appId = row is null ? input.AppId : row.AppId;
        Guid? appCustomer = null;

        if (appId is Guid app)
        {
            appCustomer = await db.Apps.Where(a => a.Id == app && a.Customer.TenantId == tenantId)
                .Select(a => (Guid?)a.CustomerId).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Application not found, or it is not this tenant's.");

            if (input.Visibility is not (KbVisibility.Operations or KbVisibility.Archived))
            {
                throw new ArgumentException("An application's knowledge is for agents only: Operations or Archived.");
            }
        }
        else if (input.CustomerId is Guid customerId && !await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That customer is not this tenant's.");
        }

        string body = RichText.Sanitize(input.BodyHtml);
        string title = input.Title.Trim();
        DateTime now = DateTime.UtcNow;

        if (row is null)
        {
            row = new KbAnswer
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Title = "", AppId = appId, CreatedAt = now,
                ReviewedAt = now, ReviewedBy = actor,
            };
            db.KbAnswers.Add(row);
        }
        else if (row.Title != title || row.BodyHtml != body)
        {
            // What it said before — for every answer, as the runbook sections always had.
            db.KbAnswerRevisions.Add(new KbAnswerRevision
            {
                Id = Guid.NewGuid(), AnswerId = row.Id, TenantId = tenantId, Title = row.Title, Body = row.BodyHtml,
                BodyFormat = KbBodyFormat.Html, SavedAt = now, SavedBy = actor,
                Summary = string.IsNullOrWhiteSpace(changeSummary) ? null : changeSummary.Trim(),
            });
        }

        row.Title = title;
        row.CategoryId = input.CategoryId;
        row.BodyHtml = body;
        row.BodyText = RichText.ToText(body);
        row.Language = language;
        row.Visibility = input.Visibility;
        row.Keywords = (input.Keywords ?? "").Trim();
        row.UpdatedAt = now;
        row.UpdatedBy = actor;

        if (appId is not null)
        {
            row.PublishAt = null;
            row.CustomerId = appCustomer;
            row.Section = input.Section ?? row.Section ?? KnowledgeSectionKind.Other;
        }
        else
        {
            row.PublishAt = input.Visibility == KbVisibility.Public ? input.PublishAt : null;
            row.CustomerId = input.CustomerId;
            row.Section = null;
        }

        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// "Still true": somebody confirmed the answer holds. Not an edit — no revision, and the
    /// last-changed date stays (a fixed typo is not a review; a review changes no words).
    /// </summary>
    public async Task<bool> MarkReviewedAsync(Guid tenantId, Guid answerId, string reviewer, DateTime at, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswers.Where(a => a.Id == answerId && a.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.ReviewedAt, at).SetProperty(a => a.ReviewedBy, reviewer), ct) > 0;
    }

    /// <summary>What an answer said before each edit, newest first.</summary>
    public async Task<List<KbAnswerRevision>> RevisionsAsync(Guid tenantId, Guid answerId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswerRevisions.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.AnswerId == answerId)
            .OrderByDescending(r => r.SavedAt).ToListAsync(ct);
    }

    /// <summary>
    /// Puts an earlier version back. An ordinary edit: what it says now becomes a revision. A
    /// version kept as markdown (from before the move) is converted first.
    /// </summary>
    public async Task<KbAnswer?> RestoreAsync(Guid tenantId, Guid answerId, Guid revisionId, string actor, CancellationToken ct = default)
    {
        KbAnswer? answer = await GetAsync(tenantId, answerId, ct);
        KbAnswerRevision? revision;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            revision = await db.KbAnswerRevisions.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == revisionId && r.AnswerId == answerId && r.TenantId == tenantId, ct);
        }

        if (answer is null || revision is null)
        {
            return null;
        }

        answer.BodyHtml = revision.BodyFormat == KbBodyFormat.Markdown
            ? EntKube.Web.Services.Knowledge.KnowledgeConversion.Convert(revision.Body).Html
            : revision.Body;

        if (!string.IsNullOrWhiteSpace(revision.Title))
        {
            answer.Title = revision.Title;
        }

        return await SaveAnswerAsync(tenantId, answer, actor, ct, $"Restored the version of {revision.SavedAt:yyyy-MM-dd HH:mm}");
    }

    // ───────────────────────────── An application's knowledge (phase 11) ─────────────────────────────

    /// <summary>An application's answers, by section then title — what its Knowledge button shows.</summary>
    public async Task<List<KbAnswer>> AnswersForAppAsync(Guid tenantId, Guid appId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswers.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.AppId == appId)
            .OrderBy(a => a.Section).ThenBy(a => a.Title).ToListAsync(ct);
    }

    /// <summary>The names of this tenant's applications that have knowledge, for labelling lists.</summary>
    public async Task<Dictionary<Guid, string>> AppNamesAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Guid> ids = await db.KbAnswers.Where(a => a.TenantId == tenantId && a.AppId != null)
            .Select(a => a.AppId!.Value).Distinct().ToListAsync(ct);
        return await db.Apps.Where(a => ids.Contains(a.Id) && a.Customer.TenantId == tenantId)
            .ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    /// <summary>The fixed id of a tenant's "Applications" category: the same on every instance.</summary>
    public static Guid AppCategoryId(Guid tenantId)
    {
        byte[] bytes = System.Security.Cryptography.SHA256.HashData(
            [.. AppCategoryNamespace.ToByteArray(), .. tenantId.ToByteArray()]);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static readonly Guid AppCategoryNamespace = new("7d1c3e1a-5b2f-4f0e-9a41-3c0d2b6e8f11");

    /// <summary>
    /// The category applications' knowledge is filed under, created on first need. Scope is the
    /// answer's application, never its category — editors may rename this or move answers out.
    /// Two instances creating it at once: one insert loses on the key, and re-reads.
    /// </summary>
    public async Task<Guid> EnsureAppCategoryAsync(Guid tenantId, CancellationToken ct = default)
    {
        Guid id = AppCategoryId(tenantId);
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.KbCategories.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct))
        {
            return id;
        }

        db.KbCategories.Add(new KbCategory { Id = id, TenantId = tenantId, Name = "Applications", SortOrder = 1000 });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance made it first.
        }

        return id;
    }

    public async Task<bool> DeleteAnswerAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.KbAnswers.AnyAsync(a => a.Id == id && a.TenantId == tenantId, ct))
        {
            return false;
        }

        // Links on the side that does not cascade go first.
        await db.KbAnswerLinks.Where(l => l.TenantId == tenantId && (l.AnswerId == id || l.RelatedId == id)).ExecuteDeleteAsync(ct);
        return await db.KbAnswers.Where(a => a.Id == id && a.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    // ───────────────────────────── Related answers (phase 10) ─────────────────────────────

    /// <summary>Every answer linked to this one, either way round — for the knowledge base's own editor.</summary>
    public async Task<List<KbAnswer>> RelatedAsync(Guid tenantId, Guid answerId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Guid> ids = await RelatedIds(db, tenantId, answerId).ToListAsync(ct);
        return await db.KbAnswers.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).OrderBy(a => a.Title).ToListAsync(ct);
    }

    /// <summary>The related answers a customer may see: each must pass the same rules as opening it directly.</summary>
    public async Task<List<KbAnswer>> RelatedForCustomerAsync(
        Guid tenantId, Guid customerId, Guid answerId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Guid> ids = await RelatedIds(db, tenantId, answerId).ToListAsync(ct);
        return await Published(db, tenantId, customerId, now).Where(a => ids.Contains(a.Id)).OrderBy(a => a.Title).ToListAsync(ct);
    }

    private static IQueryable<Guid> RelatedIds(SupportDbContext db, Guid tenantId, Guid answerId) =>
        db.KbAnswerLinks.Where(l => l.TenantId == tenantId && l.AnswerId == answerId).Select(l => l.RelatedId)
            .Concat(db.KbAnswerLinks.Where(l => l.TenantId == tenantId && l.RelatedId == answerId).Select(l => l.AnswerId));

    /// <summary>
    /// Replaces the answers related to this one. All must be this tenant's, and an answer is not
    /// related to itself. Each pair is stored once, lower id first.
    /// </summary>
    public async Task SetRelatedAsync(Guid tenantId, Guid answerId, IReadOnlyCollection<Guid> relatedIds, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        HashSet<Guid> wanted = [.. relatedIds.Where(r => r != answerId)];
        List<Guid> known = await db.KbAnswers.Where(a => a.TenantId == tenantId && (a.Id == answerId || wanted.Contains(a.Id)))
            .Select(a => a.Id).ToListAsync(ct);

        if (!known.Contains(answerId) || wanted.Any(w => !known.Contains(w)))
        {
            throw new InvalidOperationException("Every related answer must be one of this knowledge base's.");
        }

        await db.KbAnswerLinks.Where(l => l.TenantId == tenantId && (l.AnswerId == answerId || l.RelatedId == answerId)).ExecuteDeleteAsync(ct);

        foreach (Guid other in wanted)
        {
            (Guid low, Guid high) = answerId.CompareTo(other) < 0 ? (answerId, other) : (other, answerId);
            db.KbAnswerLinks.Add(new KbAnswerLink { AnswerId = low, RelatedId = high, TenantId = tenantId });
        }

        await db.SaveChangesAsync(ct);
    }

    // ───────────────────────────── Answers used on tickets (phase 10) ─────────────────────────────

    /// <summary>Records that an answer was given on a ticket. Both must be this tenant's; twice is once.</summary>
    public async Task<bool> LinkTicketAsync(Guid tenantId, Guid answerId, Guid ticketId, string? actor, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Only an answer that can be given to a customer: a runbook is never recorded as given.
        if (!await db.KbAnswers.AnyAsync(a => a.Id == answerId && a.TenantId == tenantId && a.AppId == null
                                              && (a.Visibility == KbVisibility.Internal || a.Visibility == KbVisibility.Public), ct)
            || !await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            return false;
        }

        if (await db.KbAnswerTickets.AnyAsync(l => l.AnswerId == answerId && l.TicketId == ticketId, ct))
        {
            return true;
        }

        db.KbAnswerTickets.Add(new KbAnswerTicket { AnswerId = answerId, TicketId = ticketId, TenantId = tenantId, LinkedBy = actor });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two inserts of the same answer at once: the key already holds it.
        }

        return true;
    }

    /// <summary>The tickets an answer was given on, newest first.</summary>
    public async Task<List<(Guid Id, int Number, string Title)>> TicketsUsingAsync(Guid tenantId, Guid answerId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.KbAnswerTickets.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.AnswerId == answerId)
            .Join(db.Tickets.Where(t => t.TenantId == tenantId), l => l.TicketId, t => t.Id, (l, t) => new { t.Id, t.Number, t.Title, l.LinkedAt })
            .OrderByDescending(x => x.LinkedAt).Take(50).ToListAsync(ct);
        return [.. rows.Select(r => (r.Id, r.Number, r.Title))];
    }

    /// <summary>The answers given on a ticket.</summary>
    public async Task<List<KbAnswer>> AnswersOnTicketAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<Guid> ids = await db.KbAnswerTickets.Where(l => l.TenantId == tenantId && l.TicketId == ticketId).Select(l => l.AnswerId).ToListAsync(ct);
        return await db.KbAnswers.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.Id)).OrderBy(a => a.Title).ToListAsync(ct);
    }

    // ───────────────────────────── Reading ─────────────────────────────

    /// <summary>
    /// What an agent writing to <paramref name="customerId"/>'s ticket may insert (Zammad's
    /// <c>??</c>): published public and internal answers, never one narrowed to another customer.
    /// </summary>
    public async Task<List<KbAnswer>> ForAgentAsync(
        Guid tenantId, Guid? customerId, string? query, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<KbAnswer> q = db.KbAnswers.AsNoTracking().Where(KbAudience.OfferableInReplies(tenantId, customerId, now));

        return await Matching(q, query).OrderBy(a => a.Title).Take(20).ToListAsync(ct);
    }

    /// <summary>
    /// The answer to insert, asked for AGAIN under the same rules for this ticket's customer —
    /// the list item shown when <c>??</c> was typed may be minutes old, and an answer since moved
    /// to Operations or narrowed to another customer must not go out. Null when it may not.
    /// </summary>
    public async Task<KbAnswer?> ForInsertAsync(Guid tenantId, Guid ticketId, Guid answerId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var ticket = await db.Tickets.Where(t => t.Id == ticketId && t.TenantId == tenantId)
            .Select(t => new { t.CustomerId }).FirstOrDefaultAsync(ct);

        if (ticket is null)
        {
            return null;
        }

        return await db.KbAnswers.AsNoTracking()
            .Where(KbAudience.OfferableInReplies(tenantId, ticket.CustomerId, now))
            .FirstOrDefaultAsync(a => a.Id == answerId, ct);
    }

    /// <summary>What a customer sees in the portal: public, published, and theirs or everybody's.</summary>
    public async Task<List<KbAnswer>> ForCustomerAsync(
        Guid tenantId, Guid customerId, string? query, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await Matching(Published(db, tenantId, customerId, now), query).OrderBy(a => a.Title).Take(50).ToListAsync(ct);
    }

    /// <summary>One answer for a customer, or null when they may not see it.</summary>
    public async Task<KbAnswer?> GetForCustomerAsync(
        Guid tenantId, Guid customerId, Guid answerId, DateTime now, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await Published(db, tenantId, customerId, now).FirstOrDefaultAsync(a => a.Id == answerId, ct);
    }

    private static IQueryable<KbAnswer> Published(SupportDbContext db, Guid tenantId, Guid customerId, DateTime now) =>
        db.KbAnswers.AsNoTracking().Where(KbAudience.VisibleInPortal(tenantId, customerId, now));

    /// <summary>Every word must appear in the title, the text or the keywords. Case-insensitive, LIKE-escaped.</summary>
    private static IQueryable<KbAnswer> Matching(IQueryable<KbAnswer> q, string? query)
    {
        foreach (string word in (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string like = "%" + word.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(a => EF.Functions.Like(a.Title.ToLower(), like, "\\")
                             || EF.Functions.Like(a.BodyText.ToLower(), like, "\\")
                             || EF.Functions.Like(a.Keywords.ToLower(), like, "\\"));
        }

        return q;
    }

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Z]{2})?$")]
    private static partial Regex LanguageTag();
}
