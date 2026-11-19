using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Tickets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EntKube.Web.Services.Knowledge;

/// <summary>What converting one tenant's not-yet-moved knowledge sections would give.</summary>
public sealed record KnowledgeMergeForecast(Guid TenantId, string TenantSlug, int Exact, int Mapped, int Fallback, int Detached)
{
    public int Total => Exact + Mapped + Fallback + Detached;
}

/// <summary>
/// Moving the per-application runbooks into the knowledge base (helpdesk phase 11).
///
/// <para><b>Release A runs only the dry run</b>: at startup it converts every section that has no
/// move-ledger row, IN MEMORY, and logs per tenant how many would convert exactly, with mappings,
/// or fall back to preformatted markdown — and how many belong to an application outside their
/// tenant. It writes nothing. The move itself ships in release B, once every instance runs A and
/// the logs have been read.</para>
///
/// <para>The tenant is an input: each tenant's sections are read under its own id, never taken
/// off a row.</para>
/// </summary>
public class KnowledgeMerge(IDbContextFactory<SupportDbContext> dbFactory, ILogger<KnowledgeMerge> logger)
{
    /// <summary>Who a moved section's extra revision is recorded as.</summary>
    public const string MoverName = "EntKube (moved to the knowledge base)";

    /// <summary>What one start of the mover did, per tenant. <c>LeftToMove</c> is -1 when the tenant stopped part-way and was not counted.</summary>
    public sealed record MoveReport(Guid TenantId, string TenantSlug, int Moved, int AlreadyMoved, int Failed, int Diverged, int LeftToMove);

    /// <summary>
    /// Release B: moves every section of every tenant that has no ledger row into the knowledge
    /// base, then carries across anything an older EntKube changed after a move. Safe to run on
    /// every start, and on several instances at once; never fails startup.
    /// </summary>
    public async Task<List<MoveReport>> MoveAsync(CancellationToken ct = default)
    {
        List<(Guid Id, string Slug)> tenants;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenants = [.. (await db.Tenants.Select(t => new { t.Id, t.Slug }).ToListAsync(ct)).Select(t => (t.Id, t.Slug))];
        }

        List<MoveReport> reports = [];

        foreach ((Guid tenantId, string slug) in tenants)
        {
            int moved = 0, already = 0, failed = 0, diverged = 0, left;

            // A tenant's failure is its own: every later tenant still moves on this start.
            try
            {
                foreach (Guid sectionId in await PendingAsync(tenantId, ct))
                {
                    switch (await MoveOneAsync(tenantId, sectionId, ct))
                    {
                        case MoveOutcome.Moved: moved++; break;
                        case MoveOutcome.AlreadyMoved: already++; break;
                        default: failed++; break;
                    }
                }

                (diverged, int convergeFailed) = await ConvergeAsync(tenantId, ct);
                failed += convergeFailed;
                left = (await PendingAsync(tenantId, ct)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Moving the knowledge of tenant {Tenant} stopped part-way; what is left is retried next start.", slug);
                failed++;
                left = -1;
            }

            if (moved + failed + diverged + Math.Max(left, 0) > 0)
            {
                reports.Add(new MoveReport(tenantId, slug, moved, already, failed, diverged, left));
            }
        }

        return reports;
    }

    private enum MoveOutcome { Moved, AlreadyMoved, Failed }

    private enum ConvergeOutcome { Unchanged, Diverged }

    /// <summary>
    /// An answer's title is at most 300 characters; a section's was unbounded. Cut to 299 and an
    /// ellipsis — but never between the halves of a surrogate pair: Npgsql's strict UTF-8 encoder
    /// refuses a lone surrogate, and that section would then fail to move on every start.
    /// </summary>
    public static string ClipTitle(string title)
    {
        if (title.Length <= 300)
        {
            return title;
        }

        int cut = char.IsHighSurrogate(title[298]) ? 298 : 299;
        return title[..cut] + "…";
    }

    /// <summary>A tenant's sections with no ledger row. The ledger, not the answer, decides: an answer deleted after the move stays deleted.</summary>
    private async Task<List<Guid>> PendingAsync(Guid tenantId, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.KnowledgeSections.AsNoTracking()
            .Where(s => s.TenantId == tenantId && !db.KnowledgeSectionMoves.Any(m => m.SectionId == s.Id))
            .Select(s => s.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Moves one section: the answer, its history, the markdown as it stood and the ledger row,
    /// written by ONE SaveChanges — atomic on its own, so no transaction is opened around the
    /// reads. Holding one would gain nothing (a competitor is stopped by the keys, not by a lock)
    /// and would serialise every instance's startup behind the slowest.
    /// </summary>
    private async Task<MoveOutcome> MoveOneAsync(Guid tenantId, Guid sectionId, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        try
        {
            KnowledgeSection? section = await db.KnowledgeSections.AsNoTracking()
                .Include(s => s.Revisions)
                .FirstOrDefaultAsync(s => s.Id == sectionId && s.TenantId == tenantId, ct);

            if (section is null)
            {
                return MoveOutcome.AlreadyMoved;
            }

            var app = await db.Apps.Where(a => a.Id == section.AppId && a.Customer.TenantId == tenantId)
                .Select(a => new { a.Id, a.CustomerId }).FirstOrDefaultAsync(ct);

            KnowledgeConverted converted = KnowledgeConversion.Convert(section.Body);
            List<string> notes = [.. converted.Notes];
            MoveVerdict verdict = converted.Verdict;

            if (app is null)
            {
                verdict = MoveVerdict.Detached;
                notes.Add("Its application is not this tenant's, so it was kept as an archived answer for the whole tenant.");
            }

            Guid categoryId = KnowledgeBaseService.AppCategoryId(tenantId);

            if (!await db.KbCategories.AnyAsync(c => c.Id == categoryId, ct))
            {
                db.KbCategories.Add(new KbCategory { Id = categoryId, TenantId = tenantId, Name = "Applications", SortOrder = 1000 });
            }

            string title = section.Title.Trim();
            string body = converted.Html;

            if (title.Length > 300)
            {
                body = $"<p>{System.Net.WebUtility.HtmlEncode(title)}</p>{body}";
                title = ClipTitle(title);
                notes.Add("The title was longer than 300 characters; it is kept whole as the first paragraph.");
            }

            db.KbAnswers.Add(new KbAnswer
            {
                Id = section.Id,
                TenantId = tenantId,
                AppId = app?.Id,
                Section = app is null ? null : section.Kind,
                CustomerId = app?.CustomerId,
                CategoryId = categoryId,
                Title = title.Length == 0 ? section.Kind.ToString() : title,
                BodyHtml = body,
                BodyText = RichText.ToText(body),
                Language = "sv",
                Visibility = app is null ? KbVisibility.Archived : KbVisibility.Operations,
                CreatedAt = section.CreatedAt,
                UpdatedAt = section.UpdatedAt,
                UpdatedBy = section.UpdatedBy,
                ReviewedAt = section.ReviewedAt,
                ReviewedBy = section.ReviewedBy,
            });

            // The section's own history, verbatim, under its own ids.
            foreach (KnowledgeRevision revision in section.Revisions)
            {
                db.KbAnswerRevisions.Add(new KbAnswerRevision
                {
                    Id = revision.Id, AnswerId = section.Id, TenantId = tenantId, Body = revision.Body,
                    BodyFormat = KbBodyFormat.Markdown, SavedAt = revision.SavedAt, SavedBy = revision.SavedBy, Summary = revision.Summary,
                });
            }

            // And the markdown as it stood: whatever the conversion did, what was written is kept.
            db.KbAnswerRevisions.Add(new KbAnswerRevision
            {
                Id = Guid.NewGuid(), AnswerId = section.Id, TenantId = tenantId, Body = section.Body, BodyFormat = KbBodyFormat.Markdown,
                SavedAt = DateTime.UtcNow, SavedBy = MoverName,
                Summary = "Converted from markdown to rich text; this is the markdown as it stood."
                          + (notes.Count == 0 ? "" : " " + string.Join("; ", notes) + "."),
            });

            db.KnowledgeSectionMoves.Add(new KnowledgeSectionMove
            {
                SectionId = section.Id, TenantId = tenantId, AppId = section.AppId, AnswerId = section.Id,
                Verdict = verdict, Notes = notes.Count == 0 ? null : string.Join("; ", notes),
                LegacyUpdatedAt = section.UpdatedAt, LegacyReviewedAt = section.ReviewedAt, LegacyReviewedBy = section.ReviewedBy,
                LegacyRevisionCount = section.Revisions.Count,
            });

            await db.SaveChangesAsync(ct);
            return MoveOutcome.Moved;
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();

            // Another instance moving the same section loses on the key — or this failed for real.
            // Only the ledger can tell which.
            if (await db.KnowledgeSectionMoves.AnyAsync(m => m.SectionId == sectionId, ct))
            {
                return MoveOutcome.AlreadyMoved;
            }

            logger.LogError(ex, "Knowledge section {Section} of tenant {Tenant} could not be moved; it stays where it was and is retried next start.", sectionId, tenantId);
            return MoveOutcome.Failed;
        }
    }

    /// <summary>
    /// Sections changed on an older EntKube AFTER they moved (a rollback): the change is appended to
    /// the answer's history — never applied over what the answer says now — and flagged Diverged.
    /// An answer deleted since comes back archived, so nothing anyone wrote is lost and the deletion
    /// still holds everywhere it is shown. The ledger update is a compare-and-swap on the values it
    /// last saw, so two instances carry a change across once.
    /// </summary>
    private async Task<(int Diverged, int Failed)> ConvergeAsync(Guid tenantId, CancellationToken ct)
    {
        int diverged = 0;
        List<KnowledgeSectionMove> moves;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            moves = await db.KnowledgeSectionMoves.AsNoTracking().Where(m => m.TenantId == tenantId).ToListAsync(ct);
        }

        int failed = 0;

        foreach (KnowledgeSectionMove move in moves)
        {
            // One section's failure is its own: the rest of this tenant, and every later tenant,
            // still converge. Its ledger claim rolled back with it, so it is retried next start.
            try
            {
                if (await ConvergeOneAsync(tenantId, move, ct) == ConvergeOutcome.Diverged)
                {
                    diverged++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "A change made on an older EntKube to knowledge section {Section} of tenant {Tenant} could not be carried across; it is retried next start.", move.SectionId, tenantId);
                failed++;
            }
        }

        return (diverged, failed);
    }

    private async Task<ConvergeOutcome> ConvergeOneAsync(Guid tenantId, KnowledgeSectionMove move, CancellationToken ct)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        KnowledgeSection? section = await db.KnowledgeSections.AsNoTracking().Include(s => s.Revisions)
            .FirstOrDefaultAsync(s => s.Id == move.SectionId && s.TenantId == tenantId, ct);

        if (section is null
            || (section.UpdatedAt == move.LegacyUpdatedAt && section.ReviewedAt == move.LegacyReviewedAt
                && section.ReviewedBy == move.LegacyReviewedBy && section.Revisions.Count == move.LegacyRevisionCount))
        {
            return ConvergeOutcome.Unchanged;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        int claimed = await db.KnowledgeSectionMoves
            .Where(m => m.SectionId == move.SectionId && m.TenantId == tenantId
                        && m.LegacyUpdatedAt == move.LegacyUpdatedAt && m.LegacyRevisionCount == move.LegacyRevisionCount
                        && m.LegacyReviewedAt == move.LegacyReviewedAt && m.LegacyReviewedBy == move.LegacyReviewedBy)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.LegacyUpdatedAt, section.UpdatedAt)
                .SetProperty(m => m.LegacyReviewedAt, section.ReviewedAt)
                .SetProperty(m => m.LegacyReviewedBy, section.ReviewedBy)
                .SetProperty(m => m.LegacyRevisionCount, section.Revisions.Count)
                .SetProperty(m => m.Verdict, MoveVerdict.Diverged), ct);

        if (claimed != 1)
        {
            await tx.RollbackAsync(ct);
            return ConvergeOutcome.Unchanged;
        }

        KbAnswer? answer = await db.KbAnswers.FirstOrDefaultAsync(a => a.Id == move.AnswerId && a.TenantId == tenantId, ct);

        if (answer is null)
        {
            // Deleted after the move, then edited on an older build: back, archived, so the edit is not lost.
            // Everything the section carries comes back: its review too, and a long title whole.
            KnowledgeConverted c = KnowledgeConversion.Convert(section.Body);
            string revivedTitle = section.Title.Trim();
            string revivedBody = revivedTitle.Length > 300 ? $"<p>{System.Net.WebUtility.HtmlEncode(revivedTitle)}</p>{c.Html}" : c.Html;
            answer = new KbAnswer
            {
                Id = move.AnswerId, TenantId = tenantId, CategoryId = KnowledgeBaseService.AppCategoryId(tenantId),
                Title = revivedTitle.Length == 0 ? section.Kind.ToString() : ClipTitle(revivedTitle),
                BodyHtml = revivedBody, BodyText = RichText.ToText(revivedBody), Language = "sv",
                Visibility = KbVisibility.Archived, CreatedAt = section.CreatedAt, UpdatedAt = section.UpdatedAt, UpdatedBy = section.UpdatedBy,
                ReviewedAt = section.ReviewedAt, ReviewedBy = section.ReviewedBy,
            };

            if (await db.Apps.AnyAsync(a => a.Id == section.AppId && a.Customer.TenantId == tenantId, ct))
            {
                answer.AppId = section.AppId;
                answer.Section = section.Kind;
                answer.CustomerId = await db.Apps.Where(a => a.Id == section.AppId).Select(a => a.CustomerId).FirstAsync(ct);
            }

            if (!await db.KbCategories.AnyAsync(k => k.Id == answer.CategoryId, ct))
            {
                db.KbCategories.Add(new KbCategory { Id = answer.CategoryId, TenantId = tenantId, Name = "Applications", SortOrder = 1000 });
            }

            db.KbAnswers.Add(answer);
        }
        else if (section.ReviewedAt is DateTime reviewed && (answer.ReviewedAt is null || reviewed > answer.ReviewedAt))
        {
            answer.ReviewedAt = reviewed;
            answer.ReviewedBy = section.ReviewedBy;
        }

        HashSet<Guid> present = [.. await db.KbAnswerRevisions.Where(r => r.AnswerId == move.AnswerId).Select(r => r.Id).ToListAsync(ct)];

        foreach (KnowledgeRevision revision in section.Revisions.Where(r => !present.Contains(r.Id)))
        {
            db.KbAnswerRevisions.Add(new KbAnswerRevision
            {
                Id = revision.Id, AnswerId = move.AnswerId, TenantId = tenantId, Body = revision.Body, BodyFormat = KbBodyFormat.Markdown,
                SavedAt = revision.SavedAt, SavedBy = revision.SavedBy, Summary = revision.Summary,
            });
        }

        // The whole of what the older build holds — title and section too, which it could change
        // without touching the body (and then wrote no revision of its own). Restore applies the
        // title; the section is named so it can be set by hand.
        db.KbAnswerRevisions.Add(new KbAnswerRevision
        {
            Id = Guid.NewGuid(), AnswerId = move.AnswerId, TenantId = tenantId,
            Title = section.Title.Trim().Length == 0 ? null : ClipTitle(section.Title.Trim()),
            Body = section.Body, BodyFormat = KbBodyFormat.Markdown,
            SavedAt = DateTime.UtcNow, SavedBy = section.UpdatedBy ?? MoverName,
            Summary = $"Edited in the application runbook on an older EntKube after the move (section: {KnowledgeService.SectionName(section.Kind)}); "
                      + "not applied — restore it from History if it should be current.",
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ConvergeOutcome.Diverged;
    }

    public async Task<List<KnowledgeMergeForecast>> ForecastAsync(CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<KnowledgeMergeForecast> forecasts = [];

        foreach (var tenant in await db.Tenants.Select(t => new { t.Id, t.Slug }).ToListAsync(ct))
        {
            List<Guid> moved = await db.KnowledgeSectionMoves.Where(m => m.TenantId == tenant.Id).Select(m => m.SectionId).ToListAsync(ct);

            var pending = await db.KnowledgeSections.AsNoTracking()
                .Where(s => s.TenantId == tenant.Id && !moved.Contains(s.Id))
                .Select(s => new { s.AppId, s.Body })
                .ToListAsync(ct);

            if (pending.Count == 0)
            {
                continue;
            }

            HashSet<Guid> ownApps = [.. await db.Apps.Where(a => a.Customer.TenantId == tenant.Id).Select(a => a.Id).ToListAsync(ct)];
            int exact = 0, mapped = 0, fallback = 0, detached = 0;

            foreach (var section in pending)
            {
                if (!ownApps.Contains(section.AppId))
                {
                    detached++;
                    continue;
                }

                switch (KnowledgeConversion.Convert(section.Body).Verdict)
                {
                    case MoveVerdict.Exact: exact++; break;
                    case MoveVerdict.Mapped: mapped++; break;
                    default: fallback++; break;
                }
            }

            forecasts.Add(new KnowledgeMergeForecast(tenant.Id, tenant.Slug, exact, mapped, fallback, detached));
        }

        return forecasts;
    }
}
