using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// The highlighter (docs/helpdesk.md, phase 6): passages agents mark in ticket entries. Kept
/// beside the entries, never in them — see <see cref="TicketHighlight"/> — and never shown to the
/// customer.
/// </summary>
public class TicketHighlightService(IDbContextFactory<SupportDbContext> dbFactory)
{
    public const int MaxLength = 1000;

    /// <summary>The highlights on these entries, oldest first — the entries a ticket shows, merged duplicates' included.</summary>
    public async Task<List<TicketHighlight>> ForEntriesAsync(Guid tenantId, IReadOnlyCollection<Guid> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0)
        {
            return [];
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketHighlights.AsNoTracking()
            .Where(h => h.TenantId == tenantId && entryIds.Contains(h.EventId))
            .OrderBy(h => h.At).ToListAsync(ct);
    }

    /// <summary>
    /// Marks a passage. The entry must be on one of this tenant's tickets, and the passage must
    /// actually be in it — compared without whitespace, because a selection across paragraphs
    /// carries line breaks the stored text spells differently.
    /// </summary>
    public async Task<TicketHighlight> AddAsync(
        Guid tenantId, Guid entryId, string text, HighlightColor color, string actor, CancellationToken ct = default)
    {
        string passage = (text ?? "").Trim();

        if (passage.Length == 0)
        {
            throw new ArgumentException("Select the words to highlight first.");
        }

        if (passage.Length > MaxLength)
        {
            throw new ArgumentException($"A highlight is at most {MaxLength} characters.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var entry = await db.TicketEvents.AsNoTracking()
            .Where(e => e.Id == entryId && e.Ticket.TenantId == tenantId)
            .Select(e => new { e.Detail, e.DetailHtml })
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("There is no such entry here.");

        string said = entry.DetailHtml is string html ? RichText.ToText(html) : entry.Detail;

        if (!Squeeze(said).Contains(Squeeze(passage), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That passage is not in this entry.");
        }

        TicketHighlight row = new()
        {
            Id = Guid.NewGuid(), TenantId = tenantId, EventId = entryId, Text = passage, Color = color, By = actor,
        };

        db.TicketHighlights.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> RemoveAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketHighlights.Where(h => h.Id == id && h.TenantId == tenantId).ExecuteDeleteAsync(ct) > 0;
    }

    private static string Squeeze(string s) => new([.. s.Where(c => !char.IsWhiteSpace(c))]);
}
