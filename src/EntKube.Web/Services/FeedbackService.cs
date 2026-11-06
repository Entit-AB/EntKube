using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>A row of the inbox: the item and whether it still has a screenshot, never the image.</summary>
public sealed record FeedbackRow(FeedbackItem Item, bool HasScreenshot);

/// <summary>The figures above the inbox.</summary>
/// <param name="Unread">Items still <see cref="FeedbackStatus.New"/>.</param>
/// <param name="Rated">How many items in the window carry a rating.</param>
/// <param name="AverageRating">Their mean, or null when none do — no ratings is not a score of 0.</param>
public sealed record FeedbackSummary(int Unread, int Rated, double? AverageRating);

/// <summary>
/// The feedback inbox. Singleton, like <see cref="RumSiteService"/>, because the public endpoint
/// reaches it on every submission.
///
/// <para>Every read and write takes the tenant as an input and applies it to the query, so an id
/// from another tenant behaves exactly like an id that does not exist.</para>
/// </summary>
public sealed class FeedbackService(IDbContextFactory<TelemetryDbContext> dbFactory, IConfiguration config)
{
    /// <summary>The most a site may store in a day, whatever the rate limits allow.</summary>
    private int MaxPerSitePerDay => Math.Max(1, config.GetValue<int?>("Feedback:MaxPerSitePerDay") ?? 500);

    public async Task<bool> WithinDailyQuotaAsync(Guid siteId, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime since = DateTime.UtcNow.AddDays(-1);
        int used = await db.FeedbackItems.CountAsync(f => f.RumSiteId == siteId && f.CreatedAt >= since, ct);
        return used < MaxPerSitePerDay;
    }

    /// <summary>Stores a submission. Tenant, site and app come from the key, never from the body.</summary>
    public async Task<Guid> SubmitAsync(RumSiteInfo site, FeedbackSubmission s, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;

        FeedbackItem item = new()
        {
            Id = Guid.NewGuid(),
            TenantId = site.TenantId,
            RumSiteId = site.SiteId,
            SiteName = site.Name,
            AppId = site.AppId,
            Kind = s.Kind,
            Status = FeedbackStatus.New,
            Rating = s.Rating,
            Message = s.Message,
            ContactEmail = s.ContactEmail,
            PagePath = s.PagePath,
            Browser = s.Browser,
            Os = s.Os,
            Device = s.Device,
            ViewportWidth = s.ViewportWidth,
            ViewportHeight = s.ViewportHeight,
            RumSessionId = s.RumSessionId,
            RecentErrors = s.RecentErrors,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FeedbackItems.Add(item);

        if (s.Screenshot is not null && s.ScreenshotContentType is not null)
        {
            db.FeedbackScreenshots.Add(new FeedbackScreenshot
            {
                FeedbackItemId = item.Id,
                ContentType = s.ScreenshotContentType,
                Bytes = s.Screenshot,
            });
        }

        await db.SaveChangesAsync(ct);
        return item.Id;
    }

    /// <summary>
    /// Newest first. <paramref name="siteId"/> narrows to one site; the customer portal always
    /// passes one it has already confined to the customer's own apps.
    /// </summary>
    public async Task<List<FeedbackRow>> ListAsync(
        Guid tenantId, Guid? siteId = null, FeedbackStatus? status = null, int limit = 200,
        CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await Scoped(db, tenantId, siteId)
            .Where(f => status == null || f.Status == status)
            .OrderByDescending(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .Select(f => new FeedbackRow(f, db.FeedbackScreenshots.Any(s => s.FeedbackItemId == f.Id)))
            .ToListAsync(ct);
    }

    public async Task<FeedbackItem?> GetAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.FeedbackItems.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id && f.TenantId == tenantId, ct);
    }

    public async Task<FeedbackScreenshot?> GetScreenshotAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.FeedbackScreenshots.AsNoTracking()
            .Where(s => s.FeedbackItemId == id && db.FeedbackItems.Any(f => f.Id == id && f.TenantId == tenantId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The figures for a window. Unread is not windowed: old unread feedback is still unread.</summary>
    public async Task<FeedbackSummary> SummarizeAsync(
        Guid tenantId, Guid? siteId, DateTime since, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<FeedbackItem> items = Scoped(db, tenantId, siteId);

        int unread = await items.CountAsync(f => f.Status == FeedbackStatus.New, ct);
        List<int> ratings = await items
            .Where(f => f.CreatedAt >= since && f.Rating != null)
            .Select(f => f.Rating!.Value)
            .ToListAsync(ct);

        return new FeedbackSummary(unread, ratings.Count, ratings.Count == 0 ? null : ratings.Average());
    }

    /// <summary>Returns false when there is no such item in this tenant.</summary>
    public async Task<bool> SetStatusAsync(
        Guid tenantId, Guid id, FeedbackStatus status, string? actor, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;

        int updated = await db.FeedbackItems
            .Where(f => f.Id == id && f.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.Status, status)
                .SetProperty(f => f.TriagedBy, actor)
                .SetProperty(f => f.UpdatedAt, now), ct);

        return updated > 0;
    }

    /// <summary>Records the ticket an item became, and that somebody has therefore read it.</summary>
    public async Task LinkTicketAsync(
        Guid tenantId, Guid id, Guid ticketId, string? actor, CancellationToken ct = default)
    {
        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime now = DateTime.UtcNow;

        await db.FeedbackItems
            .Where(f => f.Id == id && f.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(f => f.TicketId, ticketId)
                .SetProperty(f => f.Status, FeedbackStatus.Triaged)
                .SetProperty(f => f.TriagedBy, actor)
                .SetProperty(f => f.UpdatedAt, now), ct);
    }

    /// <summary>
    /// Deletes screenshots, then items, past their retention. Both cutoffs are ages: this names no
    /// tenant and decides nothing about one. Returns how many of each went.
    /// </summary>
    public async Task<(int Screenshots, int Items)> PruneAsync(DateTime now, CancellationToken ct = default)
    {
        int screenshotDays = Math.Max(1, config.GetValue<int?>("Feedback:ScreenshotRetentionDays") ?? 90);
        int itemDays = Math.Max(screenshotDays, config.GetValue<int?>("Feedback:RetentionDays") ?? 365);

        DateTime screenshotCutoff = now.AddDays(-screenshotDays);
        DateTime itemCutoff = now.AddDays(-itemDays);

        await using TelemetryDbContext db = await dbFactory.CreateDbContextAsync(ct);

        int screenshots = await db.FeedbackScreenshots
            .Where(s => db.FeedbackItems.Any(f => f.Id == s.FeedbackItemId && f.CreatedAt < screenshotCutoff))
            .ExecuteDeleteAsync(ct);

        int items = await db.FeedbackItems.Where(f => f.CreatedAt < itemCutoff).ExecuteDeleteAsync(ct);

        return (screenshots, items);
    }

    private static IQueryable<FeedbackItem> Scoped(TelemetryDbContext db, Guid tenantId, Guid? siteId) =>
        db.FeedbackItems.AsNoTracking()
            .Where(f => f.TenantId == tenantId && (siteId == null || f.RumSiteId == siteId));
}
