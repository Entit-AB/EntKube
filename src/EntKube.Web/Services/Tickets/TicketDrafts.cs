using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>A note draft as the composer needs it.</summary>
public readonly record struct NoteDraft(string Text, bool CustomerVisible, string? By, DateTime At);

/// <summary>
/// Drafts: the note a person is typing (saved as they type, theirs alone), a note left on a
/// ticket for a colleague (one per ticket), and named half-filled new tickets (Zammad's shared
/// drafts). Tenant-scoped like everything else; a personal draft is also scoped to its owner.
/// </summary>
public class TicketDraftService(IDbContextFactory<SupportDbContext> dbFactory)
{
    // ───────────────────────────── Personal autosave ─────────────────────────────

    /// <summary>
    /// Saves what <paramref name="userId"/> is typing on a ticket. Empty text removes the draft,
    /// so clearing the box clears what would be restored.
    /// </summary>
    public async Task SavePersonalNoteAsync(
        Guid tenantId, Guid ticketId, string userId, string text, bool customerVisible, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            return;
        }

        TicketDraft? draft = await db.TicketDrafts.FirstOrDefaultAsync(d =>
            d.TicketId == ticketId && d.Kind == TicketDraftKind.PersonalNote && d.OwnerUserId == userId, ct);

        if (string.IsNullOrWhiteSpace(text))
        {
            if (draft is not null)
            {
                db.TicketDrafts.Remove(draft);
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (draft is null)
        {
            draft = new TicketDraft
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Kind = TicketDraftKind.PersonalNote,
                TicketId = ticketId, OwnerUserId = userId, CreatedBy = userId,
            };
            db.TicketDrafts.Add(draft);
        }

        draft.Text = text;
        draft.CustomerVisible = customerVisible;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.UpdatedBy = userId;

        await db.SaveChangesAsync(ct);
    }

    public async Task<NoteDraft?> GetPersonalNoteAsync(Guid tenantId, Guid ticketId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketDraft? d = await db.TicketDrafts.AsNoTracking().FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.TicketId == ticketId
            && x.Kind == TicketDraftKind.PersonalNote && x.OwnerUserId == userId, ct);

        return d is null ? null : new NoteDraft(d.Text, d.CustomerVisible, d.UpdatedBy, d.UpdatedAt);
    }

    // ───────────────────────────── Shared note ─────────────────────────────

    public async Task<NoteDraft?> GetSharedNoteAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketDraft? d = await db.TicketDrafts.AsNoTracking().FirstOrDefaultAsync(x =>
            x.TenantId == tenantId && x.TicketId == ticketId && x.Kind == TicketDraftKind.SharedNote, ct);

        return d is null ? null : new NoteDraft(d.Text, d.CustomerVisible, d.UpdatedBy, d.UpdatedAt);
    }

    /// <summary>
    /// Leaves a note on the ticket for a colleague. There is one per ticket: replacing somebody
    /// else's needs <paramref name="overwrite"/>, because overwriting is final and the screen asks
    /// first — as Zammad does.
    /// </summary>
    /// <exception cref="InvalidOperationException">A shared draft exists and overwriting was not asked for.</exception>
    public async Task<bool> ShareNoteAsync(
        Guid tenantId, Guid ticketId, string text, bool customerVisible, string actor, bool overwrite,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("There is nothing to share.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (!await db.Tickets.AnyAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct))
        {
            return false;
        }

        TicketDraft? draft = await db.TicketDrafts.FirstOrDefaultAsync(d =>
            d.TicketId == ticketId && d.Kind == TicketDraftKind.SharedNote, ct);

        if (draft is not null && !overwrite)
        {
            throw new InvalidOperationException($"{draft.UpdatedBy ?? "Somebody"} already left a draft on this ticket.");
        }

        if (draft is null)
        {
            draft = new TicketDraft
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Kind = TicketDraftKind.SharedNote, TicketId = ticketId, CreatedBy = actor,
            };
            db.TicketDrafts.Add(draft);
        }

        draft.Text = text;
        draft.CustomerVisible = customerVisible;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteSharedNoteAsync(Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketDrafts
            .Where(d => d.TenantId == tenantId && d.TicketId == ticketId && d.Kind == TicketDraftKind.SharedNote)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>
    /// A note has gone onto the ticket: the composer's own draft goes, and so does the shared one
    /// when that was what the note was made from (Zammad deletes a loaded shared draft on submit).
    /// </summary>
    public async Task NoteAddedAsync(Guid tenantId, Guid ticketId, string userId, bool fromSharedDraft, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        await db.TicketDrafts
            .Where(d => d.TenantId == tenantId && d.TicketId == ticketId
                        && ((d.Kind == TicketDraftKind.PersonalNote && d.OwnerUserId == userId)
                            || (fromSharedDraft && d.Kind == TicketDraftKind.SharedNote)))
            .ExecuteDeleteAsync(ct);
    }

    // ───────────────────────────── New-ticket drafts ─────────────────────────────

    public async Task<List<TicketDraft>> ListNewTicketDraftsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketDrafts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.Kind == TicketDraftKind.SharedNewTicket)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Saves a half-filled new ticket under a name. With <paramref name="id"/>, updates that draft;
    /// a new name with no id is a new draft (Zammad: saving a loaded draft under a new name copies it).
    /// </summary>
    public async Task<TicketDraft> SaveNewTicketDraftAsync(
        Guid tenantId, Guid? id, string name, NewTicketDraft form, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A shared draft needs a name.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (form.CustomerId is Guid customerId
            && !await db.Customers.AnyAsync(c => c.Id == customerId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException("That customer is not this tenant's.");
        }

        TicketDraft? draft = id is Guid existing
            ? await db.TicketDrafts.FirstOrDefaultAsync(d =>
                d.Id == existing && d.TenantId == tenantId && d.Kind == TicketDraftKind.SharedNewTicket, ct)
            : null;

        if (draft is null)
        {
            draft = new TicketDraft { Id = Guid.NewGuid(), TenantId = tenantId, Kind = TicketDraftKind.SharedNewTicket, CreatedBy = actor };
            db.TicketDrafts.Add(draft);
        }

        draft.Name = name.Trim();
        draft.Title = form.Title;
        draft.Text = form.Description;
        draft.CustomerId = form.CustomerId;
        draft.AppId = form.AppId;
        draft.Priority = form.Priority;
        draft.Tags = form.Tags;
        draft.UpdatedAt = DateTime.UtcNow;
        draft.UpdatedBy = actor;

        await db.SaveChangesAsync(ct);
        return draft;
    }

    public async Task<bool> DeleteNewTicketDraftAsync(Guid tenantId, Guid id, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.TicketDrafts
            .Where(d => d.Id == id && d.TenantId == tenantId && d.Kind == TicketDraftKind.SharedNewTicket)
            .ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Copies a new-ticket draft into the form, replacing what is there (loading is explicit).</summary>
    public static void Load(TicketDraft draft, NewTicketDraft form)
    {
        form.Title = draft.Title ?? "";
        form.Description = draft.Text;
        form.CustomerId = draft.CustomerId;
        form.AppId = draft.AppId;
        form.Priority = draft.Priority;
        form.Tags = draft.Tags;
    }
}
