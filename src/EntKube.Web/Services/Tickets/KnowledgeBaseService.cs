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
    /// </summary>
    public async Task<KbAnswer> SaveAnswerAsync(Guid tenantId, KbAnswer input, string actor, CancellationToken ct = default)
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

        if (input.CustomerId is Guid customerId && !await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That customer is not this tenant's.");
        }

        KbAnswer? row = input.Id == Guid.Empty
            ? null
            : await db.KbAnswers.FirstOrDefaultAsync(a => a.Id == input.Id && a.TenantId == tenantId, ct);

        if (row is null)
        {
            row = new KbAnswer { Id = Guid.NewGuid(), TenantId = tenantId, Title = "" };
            db.KbAnswers.Add(row);
        }

        row.Title = input.Title.Trim();
        row.CategoryId = input.CategoryId;
        row.BodyHtml = RichText.Sanitize(input.BodyHtml);
        row.BodyText = RichText.ToText(row.BodyHtml);
        row.Language = language;
        row.Visibility = input.Visibility;
        row.PublishAt = input.Visibility == KbVisibility.Public ? input.PublishAt : null;
        row.CustomerId = input.CustomerId;
        row.Keywords = (input.Keywords ?? "").Trim();
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> DeleteAnswerAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KbAnswers.Where(a => a.Id == id && a.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
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

        IQueryable<KbAnswer> q = db.KbAnswers.AsNoTracking()
            .Where(a => a.TenantId == tenantId
                        && (a.Visibility == KbVisibility.Internal
                            || (a.Visibility == KbVisibility.Public && (a.PublishAt == null || a.PublishAt <= now)))
                        && (a.CustomerId == null || a.CustomerId == customerId));

        return await Matching(q, query).OrderBy(a => a.Title).Take(20).ToListAsync(ct);
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
        db.KbAnswers.AsNoTracking().Where(a =>
            a.TenantId == tenantId
            && a.Visibility == KbVisibility.Public
            && (a.PublishAt == null || a.PublishAt <= now)
            && (a.CustomerId == null || a.CustomerId == customerId));

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
