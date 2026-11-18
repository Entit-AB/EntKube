using System.Text.RegularExpressions;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Tickets;

/// <summary>A link as seen from one of its two tickets.</summary>
/// <param name="LinkId">The row, for removing it.</param>
/// <param name="Role">What the other ticket is to this one.</param>
/// <param name="OtherTicketId">The other ticket.</param>
/// <param name="OtherNumber">Its number.</param>
/// <param name="OtherTitle">Its title.</param>
/// <param name="OtherStatus">Its status, so a closed subtask reads as done.</param>
public readonly record struct TicketLinkView(
    Guid LinkId, TicketLinkRole Role, Guid OtherTicketId, int OtherNumber, string OtherTitle,
    TicketStatus OtherStatus);

/// <summary>What another ticket is to the one being looked at.</summary>
public enum TicketLinkRole
{
    /// <summary>Related, neither under the other.</summary>
    Related,

    /// <summary>The other ticket is this one's parent.</summary>
    Parent,

    /// <summary>The other ticket is this one's child.</summary>
    Child,
}

/// <summary>A tag in use in a tenant, and on how many tickets.</summary>
public readonly record struct TicketTagUse(string Name, int Tickets);

/// <summary>Who follows a ticket.</summary>
public readonly record struct TicketFollower(
    string UserId, string Name, string Email, TicketSubscriptionReason Reason, DateTime Since);

/// <summary>An entry from a duplicate merged into the ticket being looked at.</summary>
public readonly record struct MergedEntry(int FromNumber, TicketEvent Event);

/// <summary>
/// The day-to-day handling of a ticket that the agreement says nothing about: tags, links to
/// other tickets, merging a duplicate, splitting one report into two, reminders, closing a
/// resolved ticket automatically, and colleagues following along.
///
/// <para><b>None of it may move a §14 clock on its own say-so.</b> That is
/// <see cref="TicketService"/>'s business, and where something here touches a clock — a
/// merge stops the duplicate's — it does so by the same rules and leaves the same record.
/// See <c>docs/helpdesk.md</c> for the decisions behind each.</para>
///
/// <para>Every method takes the tenant as an input and finds the ticket through it, for the
/// reason every <see cref="TicketService"/> method does: a ticket carries customer
/// correspondence.</para>
/// </summary>
public partial class TicketWorkService(
    IDbContextFactory<SupportDbContext> dbFactory,
    TicketService tickets,
    TicketNotifier notifier,
    // Who counts as a colleague: the tenant's members, read through the roster rather than
    // from the membership table, which belongs to another module.
    Support.SupportDutyService duty)
{
    // ───────────────────────────── Title ─────────────────────────────

    /// <summary>
    /// Renames a ticket. Recorded, because the title is what the customer sees in the
    /// portal and in every subject line from here on.
    /// </summary>
    public async Task<Ticket?> RenameAsync(
        Guid tenantId, Guid ticketId, string title, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        string next = (title ?? "").Trim();

        if (next.Length == 0)
        {
            throw new ArgumentException("A ticket needs a title.", nameof(title));
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await FindAsync(db, tenantId, ticketId, ct);

        if (ticket is null || ticket.Title == next)
        {
            return ticket;
        }

        string previous = ticket.Title;
        ticket.Title = next;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Renamed, at, actor,
            $"Renamed from \u201c{previous}\u201d.", customerVisible: false));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    // ───────────────────────────── Tags ─────────────────────────────

    /// <summary>
    /// A tag as it is stored: trimmed, lower-case, runs of whitespace made one space. Null
    /// for something that cannot be a tag — empty, too long, or carrying a comma, which every
    /// list of tags anywhere would read as two.
    /// </summary>
    public static string? NormaliseTag(string? raw)
    {
        string tag = Whitespace().Replace((raw ?? "").Trim(), " ").ToLowerInvariant();

        return tag.Length == 0 || tag.Length > SupportModel.TicketTagNameMaxLength || tag.Contains(',')
            ? null
            : tag;
    }

    /// <summary>Adds a tag. Adding one the ticket already has changes nothing.</summary>
    /// <returns>The tag as stored, or null when the ticket is not this tenant's.</returns>
    /// <exception cref="ArgumentException">The text cannot be a tag.</exception>
    public async Task<string?> AddTagAsync(
        Guid tenantId, Guid ticketId, string tag, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        string name = NormaliseTag(tag) ?? throw new ArgumentException(
            $"A tag is up to {SupportModel.TicketTagNameMaxLength} characters, without commas.",
            nameof(tag));

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await FindAsync(db, tenantId, ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        if (await db.TicketTags.AnyAsync(t => t.TicketId == ticketId && t.Name == name, ct))
        {
            return name;
        }

        db.TicketTags.Add(new TicketTag { TicketId = ticketId, Name = name, AddedAt = at, AddedBy = actor });
        db.TicketEvents.Add(Event(ticketId, TicketEventKind.Tagged, at, actor,
            $"Tagged \u201c{name}\u201d.", customerVisible: false));

        await db.SaveChangesAsync(ct);
        return name;
    }

    /// <summary>Removes a tag. Removing one the ticket does not have changes nothing.</summary>
    /// <returns>Whether the ticket is this tenant's.</returns>
    public async Task<bool> RemoveTagAsync(
        Guid tenantId, Guid ticketId, string tag, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        string? name = NormaliseTag(tag);

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (await FindAsync(db, tenantId, ticketId, ct) is null)
        {
            return false;
        }

        TicketTag? row = name is null
            ? null
            : await db.TicketTags.FirstOrDefaultAsync(t => t.TicketId == ticketId && t.Name == name, ct);

        if (row is not null)
        {
            db.TicketTags.Remove(row);
            db.TicketEvents.Add(Event(ticketId, TicketEventKind.Tagged, at, actor,
                $"Tag \u201c{name}\u201d removed.", customerVisible: false));
            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>
    /// The tags in use across a tenant's tickets, most used first — what the tag box
    /// suggests. There is no separate list of allowed tags: the vocabulary is whatever is
    /// in use, as in Zammad with free tag creation on.
    /// </summary>
    public async Task<List<TicketTagUse>> GetTagVocabularyAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var counts = await db.TicketTags.AsNoTracking()
            .Where(t => t.Ticket.TenantId == tenantId)
            .GroupBy(t => t.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return [.. counts
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new TicketTagUse(c.Name, c.Count))];
    }

    /// <summary>The ticket's tags, alphabetically.</summary>
    public async Task<List<string>> GetTagsAsync(
        Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TicketTags.AsNoTracking()
            .Where(t => t.TicketId == ticketId && t.Ticket.TenantId == tenantId)
            .OrderBy(t => t.Name)
            .Select(t => t.Name)
            .ToListAsync(ct);
    }

    // ───────────────────────────── Links ─────────────────────────────

    /// <summary>
    /// Links a ticket to another of the same tenant, by the other's number.
    /// </summary>
    /// <param name="role">What <paramref name="otherNumber"/> is to this ticket.</param>
    /// <returns>The link, or null when this ticket is not the tenant's.</returns>
    /// <exception cref="InvalidOperationException">
    /// No such ticket in this tenant, the ticket itself, or the reverse relation already exists.
    /// </exception>
    public async Task<TicketLinkView?> LinkAsync(
        Guid tenantId, Guid ticketId, int otherNumber, TicketLinkRole role, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await FindAsync(db, tenantId, ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        // By number within the tenant. Numbers are unique across the installation, but a
        // number in another tenant is somebody else's ticket and must read as "no such".
        Ticket other = await db.Tickets
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Number == otherNumber, ct)
            ?? throw new InvalidOperationException($"There is no ticket #{otherNumber} here.");

        if (other.Id == ticket.Id)
        {
            throw new InvalidOperationException("A ticket cannot be linked to itself.");
        }

        TicketLink link = await AddLinkAsync(db, tenantId, ticket, other, role, actor, at, ct);
        await db.SaveChangesAsync(ct);

        return new TicketLinkView(link.Id, role, other.Id, other.Number, other.Title, other.Status);
    }

    /// <summary>
    /// Adds the row for a link, or returns the one already there. Shared with merge and split,
    /// which link the tickets they touch as parent and child.
    /// </summary>
    private static async Task<TicketLink> AddLinkAsync(
        SupportDbContext db, Guid tenantId, Ticket ticket, Ticket other, TicketLinkRole role,
        string? actor, DateTime at, CancellationToken ct)
    {
        (Guid from, Guid to, TicketLinkKind kind) = role switch
        {
            TicketLinkRole.Parent => (other.Id, ticket.Id, TicketLinkKind.ParentChild),
            TicketLinkRole.Child => (ticket.Id, other.Id, TicketLinkKind.ParentChild),
            _ => (ticket.Id, other.Id, TicketLinkKind.Normal),
        };

        List<TicketLink> between = await db.TicketLinks
            .Where(l => (l.FromTicketId == ticket.Id && l.ToTicketId == other.Id)
                        || (l.FromTicketId == other.Id && l.ToTicketId == ticket.Id))
            .ToListAsync(ct);

        TicketLink? same = between.FirstOrDefault(l =>
            l.Kind == kind && (kind == TicketLinkKind.Normal || (l.FromTicketId == from && l.ToTicketId == to)));

        if (same is not null)
        {
            return same;
        }

        // Each ticket the parent of the other is a contradiction, not two facts.
        if (kind == TicketLinkKind.ParentChild
            && between.Any(l => l.Kind == TicketLinkKind.ParentChild && l.FromTicketId == to))
        {
            throw new InvalidOperationException(
                $"#{other.Number} and #{ticket.Number} are already parent and child the other way round.");
        }

        TicketLink link = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FromTicketId = from,
            ToTicketId = to,
            Kind = kind,
            CreatedAt = at,
            CreatedBy = actor,
        };

        db.TicketLinks.Add(link);

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Linked, at, actor,
            $"Linked to #{other.Number} as its {Describe(role)}.", customerVisible: false));
        db.TicketEvents.Add(Event(other.Id, TicketEventKind.Linked, at, actor,
            $"Linked to #{ticket.Number} as its {Describe(Inverse(role))}.", customerVisible: false));

        return link;
    }

    /// <summary>Removes a link, from either of its tickets.</summary>
    /// <returns>Whether there was such a link on this tenant's ticket.</returns>
    public async Task<bool> UnlinkAsync(
        Guid tenantId, Guid ticketId, Guid linkId, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        TicketLink? link = await db.TicketLinks
            .Include(l => l.FromTicket)
            .Include(l => l.ToTicket)
            .FirstOrDefaultAsync(l => l.Id == linkId
                                      && l.TenantId == tenantId
                                      && (l.FromTicketId == ticketId || l.ToTicketId == ticketId), ct);

        if (link is null)
        {
            return false;
        }

        db.TicketLinks.Remove(link);

        db.TicketEvents.Add(Event(link.FromTicketId, TicketEventKind.Linked, at, actor,
            $"Link to #{link.ToTicket.Number} removed.", customerVisible: false));
        db.TicketEvents.Add(Event(link.ToTicketId, TicketEventKind.Linked, at, actor,
            $"Link to #{link.FromTicket.Number} removed.", customerVisible: false));

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The ticket's links, read from both ends. A link whose other ticket has since moved to
    /// another tenant is left out rather than shown: its title is another customer's business.
    /// </summary>
    public async Task<List<TicketLinkView>> GetLinksAsync(
        Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        List<TicketLink> rows = await db.TicketLinks.AsNoTracking()
            .Include(l => l.FromTicket)
            .Include(l => l.ToTicket)
            .Where(l => l.TenantId == tenantId && (l.FromTicketId == ticketId || l.ToTicketId == ticketId))
            .ToListAsync(ct);

        List<TicketLinkView> views = [];

        foreach (TicketLink l in rows)
        {
            bool outgoing = l.FromTicketId == ticketId;
            Ticket other = outgoing ? l.ToTicket : l.FromTicket;

            if (other.TenantId != tenantId)
            {
                continue;
            }

            TicketLinkRole role = l.Kind == TicketLinkKind.Normal
                ? TicketLinkRole.Related
                : outgoing ? TicketLinkRole.Child : TicketLinkRole.Parent;

            views.Add(new TicketLinkView(l.Id, role, other.Id, other.Number, other.Title, other.Status));
        }

        return [.. views.OrderBy(v => v.Role).ThenBy(v => v.OtherNumber)];
    }

    // ───────────────────────────── Merge ─────────────────────────────

    /// <summary>What merging one ticket into another did.</summary>
    /// <param name="Source">The duplicate, now <see cref="TicketStatus.Merged"/>.</param>
    /// <param name="Target">The ticket it was folded into.</param>
    /// <param name="RecordedAsResponse">
    /// Whether the merge is what discharged the duplicate's §14.4 response — it had none
    /// before, and assessing it as a duplicate is the assessment.
    /// </param>
    /// <param name="ResponseWasLate">Whether that response, or an earlier one, missed its target.</param>
    public readonly record struct MergeResult(
        Ticket Source, Ticket Target, bool RecordedAsResponse, bool ResponseWasLate);

    /// <summary>
    /// Folds a duplicate into the ticket it duplicates. Run from the duplicate.
    ///
    /// <para><b>What happens to the duplicate.</b> It becomes <see cref="TicketStatus.Merged"/>
    /// and points at the target, and its clocks stop here: an open pause ends, resolution is
    /// recorded at this instant, and — when it had no response yet — the merge is recorded as
    /// its first response, because looking at a report and finding it is already being worked
    /// as #N <em>is</em> the assessment §14.4 waits for. That is what keeps a merged duplicate
    /// from turning into a breach a week later for want of a response nobody will now give.
    /// It also means a merge made after the deadline is recorded as late: the merge neither
    /// manufactures nor erases a deviation.</para>
    ///
    /// <para><b>Excluding it from the SLA is a separate decision</b>, made in the same dialog
    /// but with its own reason, recorded under the same name — never a side effect. Two reports
    /// of one fault are one incident to the customer, and §14.6 should not count it twice, but
    /// that is a person's call about this pair of tickets.</para>
    ///
    /// <para><b>Its history stays where it is.</b> Zammad moves the articles; here the history
    /// is the §14.6 record of the duplicate and is never rewritten. The target shows the
    /// duplicate's entries alongside its own instead (<see cref="GetMergedHistoryAsync"/>), and
    /// a reply quoting the duplicate's number is filed on the target by the mailbox.</para>
    /// </summary>
    /// <param name="excludeFromSla">Also exclude the duplicate from SLA measurement.</param>
    /// <param name="exclusionReason">Why, which is required when excluding.</param>
    /// <exception cref="InvalidOperationException">The merge is refused, saying why.</exception>
    public async Task<MergeResult?> MergeAsync(
        Guid tenantId, Guid sourceId, int targetNumber, bool excludeFromSla, string? exclusionReason,
        string? actor, DateTime at, CancellationToken ct = default)
    {
        if (excludeFromSla && string.IsNullOrWhiteSpace(exclusionReason))
        {
            throw new ArgumentException(
                "Excluding a ticket from the SLA needs a reason (§14.6).", nameof(exclusionReason));
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? source = await db.Tickets
            .Include(t => t.Pauses)
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.Id == sourceId && t.TenantId == tenantId, ct);

        if (source is null)
        {
            return null;
        }

        Ticket target = await db.Tickets
            .Include(t => t.Tags)
            .FirstOrDefaultAsync(t => t.TenantId == tenantId && t.Number == targetNumber, ct)
            ?? throw new InvalidOperationException($"There is no ticket #{targetNumber} here.");

        RefuseMerge(source, target);

        // ── The duplicate ──
        foreach (TicketPause pause in source.Pauses.Where(p => p.EndedAt is null))
        {
            pause.EndedAt = at;
        }

        bool recordedAsResponse = source.FirstResponseAt is null;

        if (recordedAsResponse)
        {
            source.FirstResponseAt = at;
            db.TicketEvents.Add(Event(source.Id, TicketEventKind.Responded, at, actor,
                $"Assessed as a duplicate of #{target.Number}, which is being worked (§14.4)."));
        }

        bool late = TicketService.StatusOf(source, at).Response.Breached;

        source.Status = TicketStatus.Merged;
        source.MergedIntoTicketId = target.Id;
        source.ResolvedAt ??= at;
        source.ClosedAt = at;
        source.ReminderAt = null;
        source.ReminderNote = null;
        source.AutoCloseAt = null;
        source.UpdatedAt = at;

        db.TicketEvents.Add(Event(source.Id, TicketEventKind.Merged, at, actor,
            $"Merged into #{target.Number} \u2014 the same matter is followed there from now on."));

        if (excludeFromSla)
        {
            source.ExcludedFromSla = true;
            source.SlaExclusionReason = exclusionReason!.Trim();

            db.TicketEvents.Add(Event(source.Id, TicketEventKind.Note, at, actor,
                $"Excluded from SLA measurement: {source.SlaExclusionReason}", customerVisible: false));
        }

        // ── The ticket it went into ──
        target.UpdatedAt = at;

        db.TicketEvents.Add(Event(target.Id, TicketEventKind.Merged, at, actor,
            $"#{source.Number} (\u201c{source.Title}\u201d) was merged into this ticket."));

        foreach (TicketTag tag in source.Tags.Where(s => target.Tags.All(t => t.Name != s.Name)))
        {
            db.TicketTags.Add(new TicketTag { TicketId = target.Id, Name = tag.Name, AddedAt = at, AddedBy = actor });
        }

        // Whoever followed the duplicate follows the matter, which now lives on the target.
        List<TicketSubscription> followers = await db.TicketSubscriptions.AsNoTracking()
            .Where(s => s.TicketId == source.Id).ToListAsync(ct);

        HashSet<string> already = [.. await db.TicketSubscriptions
            .Where(s => s.TicketId == target.Id).Select(s => s.UserId).ToListAsync(ct)];

        foreach (TicketSubscription f in followers.Where(f => !already.Contains(f.UserId)))
        {
            db.TicketSubscriptions.Add(new TicketSubscription
            {
                TicketId = target.Id, UserId = f.UserId, Name = f.Name, Email = f.Email,
                Reason = f.Reason, CreatedAt = at, CreatedBy = actor, NotifiedThrough = DigestCursorStart(at),
            });
        }

        await AddLinkAsync(db, tenantId, source, target, TicketLinkRole.Parent, actor, at, ct);

        await db.SaveChangesAsync(ct);

        return new MergeResult(source, target, recordedAsResponse, late);
    }

    /// <summary>
    /// Why a merge cannot happen, if it cannot. Public so the dialog can say so before the
    /// agent has filled anything in.
    /// </summary>
    public static string? WhyNotMerge(Ticket source, Ticket target)
    {
        if (source.Id == target.Id)
        {
            return "A ticket cannot be merged into itself.";
        }

        if (source.Status is TicketStatus.Merged)
        {
            return $"#{source.Number} has already been merged.";
        }

        if (source.Status is TicketStatus.Closed or TicketStatus.Rejected)
        {
            return $"#{source.Number} is {source.Status.ToString().ToLowerInvariant()}; there is nothing left to fold in.";
        }

        if (target.Status is TicketStatus.Merged)
        {
            return $"#{target.Number} was itself merged into another ticket \u2014 merge into that one.";
        }

        if (target.Status is TicketStatus.Closed or TicketStatus.Rejected)
        {
            return $"#{target.Number} is {target.Status.ToString().ToLowerInvariant()}. Reopen it first if this is the same matter.";
        }

        // A merge across customers would put one customer's report on another's ticket, where
        // their portal shows it. Moving the ticket is the act that changes whose it is.
        if (source.CustomerId != target.CustomerId)
        {
            return $"#{target.Number} belongs to another customer. Move this ticket to them first if it is theirs.";
        }

        return null;
    }

    private static void RefuseMerge(Ticket source, Ticket target)
    {
        if (WhyNotMerge(source, target) is string why)
        {
            throw new InvalidOperationException(why);
        }
    }

    /// <summary>
    /// The entries of every duplicate merged into this ticket, so the target can show the
    /// whole matter in one place without the duplicates' records being rewritten.
    /// </summary>
    public async Task<List<MergedEntry>> GetMergedHistoryAsync(
        Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.TicketEvents.AsNoTracking()
            .Where(e => e.Ticket.MergedIntoTicketId == ticketId && e.Ticket.TenantId == tenantId)
            .Select(e => new { e.Ticket.Number, Event = e })
            .ToListAsync(ct);

        return [.. rows.Select(r => new MergedEntry(r.Number, r.Event)).OrderBy(r => r.Event.At)];
    }

    // ───────────────────────────── Split ─────────────────────────────

    /// <summary>
    /// Opens a new ticket from one entry of an existing one — a customer's reply that turns
    /// out to be about a second problem.
    ///
    /// <para><b>Reported at the entry's time</b>, not now. That is when the customer raised
    /// it, and §14.3 counts from when a fault was reported; starting the clock at the split
    /// would make us punctual by having been slow to notice. The dialog says so.</para>
    ///
    /// <para>The new ticket is registered the ordinary way, through
    /// <see cref="TicketService.CreateAsync"/>: it gets a receipt, it is assigned by the rota,
    /// its window comes from the same application. The two are linked parent and child.</para>
    /// </summary>
    /// <returns>The new ticket, or null when the ticket or the entry is not this tenant's.</returns>
    public async Task<Ticket?> SplitAsync(
        Guid tenantId, Guid ticketId, Guid eventId, string title, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("The new ticket needs a title.", nameof(title));
        }

        Ticket? source;
        TicketEvent? entry;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            source = await FindAsync(db, tenantId, ticketId, ct);
            entry = source is null
                ? null
                : await db.TicketEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == eventId && e.TicketId == ticketId, ct);
        }

        if (source is null || entry is null)
        {
            return null;
        }

        Ticket created = await tickets.CreateAsync(
            tenantId, source.CustomerId, source.AppId, title.Trim(), entry.Detail, source.Channel,
            proposedPriority: null, reportedAt: entry.At,
            requestedBy: source.RequestedBy, requestedByEmail: source.RequestedByEmail, ct: ct);

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            Ticket parent = (await FindAsync(db, tenantId, ticketId, ct))!;
            Ticket child = (await FindAsync(db, tenantId, created.Id, ct))!;

            db.TicketEvents.Add(Event(parent.Id, TicketEventKind.Split, at, actor,
                $"#{child.Number} opened from the entry of {BusinessCalendarText(entry.At)}.", customerVisible: false));
            db.TicketEvents.Add(Event(child.Id, TicketEventKind.Split, at, actor,
                $"Split off from #{parent.Number}.", customerVisible: false));

            await AddLinkAsync(db, tenantId, child, parent, TicketLinkRole.Parent, actor, at, ct);
            await db.SaveChangesAsync(ct);
        }

        return created;
    }

    // ───────────────────────────── Follow-up ─────────────────────────────

    /// <summary>
    /// Sets or clears a reminder. It mails whoever has the ticket when it comes due and puts
    /// the ticket in the reminder overviews; it never pauses a clock.
    /// </summary>
    /// <param name="remindAt">When, or null to clear it.</param>
    public async Task<Ticket?> SetReminderAsync(
        Guid tenantId, Guid ticketId, DateTime? remindAt, string? note, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await FindAsync(db, tenantId, ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        if (remindAt is not null && IsSettled(ticket.Status))
        {
            throw new InvalidOperationException("A closed ticket has nothing left to be reminded about.");
        }

        ticket.ReminderAt = remindAt;
        ticket.ReminderNote = remindAt is null || string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ticket.ReminderSentAt = null;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Reminder, at, actor,
            remindAt is DateTime when
                ? $"Reminder set for {BusinessCalendarText(when)}{(ticket.ReminderNote is null ? "." : $": {ticket.ReminderNote}")}"
                : "Reminder cleared.",
            customerVisible: false));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    /// <summary>
    /// For a resolved ticket: closes it by itself at <paramref name="closeAt"/> unless it is
    /// reopened first. Null clears it, and the ticket waits for a person as before.
    /// </summary>
    public async Task<Ticket?> SetAutoCloseAsync(
        Guid tenantId, Guid ticketId, DateTime? closeAt, string? actor, DateTime at,
        CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Ticket? ticket = await FindAsync(db, tenantId, ticketId, ct);

        if (ticket is null)
        {
            return null;
        }

        if (closeAt is not null && ticket.Status != TicketStatus.Resolved)
        {
            throw new InvalidOperationException("Only a resolved ticket can be closed automatically.");
        }

        ticket.AutoCloseAt = closeAt;
        ticket.UpdatedAt = at;

        db.TicketEvents.Add(Event(ticket.Id, TicketEventKind.Reminder, at, actor,
            closeAt is DateTime when
                ? $"Closes automatically on {BusinessCalendarText(when)} unless it is reopened first."
                : "No longer closes automatically.",
            customerVisible: false));

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    // ───────────────────────────── Followers ─────────────────────────────

    /// <summary>
    /// Starts following a ticket. Following one already followed changes nothing, except that
    /// a mention upgrades nothing and a plain subscribe after a mention keeps the mention.
    /// </summary>
    /// <returns>Whether the ticket is this tenant's.</returns>
    public async Task<bool> SubscribeAsync(
        Guid tenantId, Guid ticketId, string userId, string name, string email,
        TicketSubscriptionReason reason, string? actor, DateTime at, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            // Following a ticket means being mailed about it; without an address that is a
            // subscription that silently does nothing.
            throw new InvalidOperationException($"{name} has no e-mail address to be told at.");
        }

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (await FindAsync(db, tenantId, ticketId, ct) is null)
        {
            return false;
        }

        if ((await duty.GetRosterAsync(tenantId, ct)).All(m => m.UserId != userId))
        {
            throw new InvalidOperationException($"{name} is not a member of this tenant.");
        }

        if (!await db.TicketSubscriptions.AnyAsync(s => s.TicketId == ticketId && s.UserId == userId, ct))
        {
            db.TicketSubscriptions.Add(new TicketSubscription
            {
                TicketId = ticketId, UserId = userId, Name = name, Email = email.Trim(),
                Reason = reason, CreatedAt = at, CreatedBy = actor,

                // From now, by the clock the history's RecordedAt is written in: a mention's own
                // note was recorded a moment ago and has already been mailed to them.
                NotifiedThrough = DigestCursorStart(at),
            });

            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    /// <summary>Stops following a ticket.</summary>
    public async Task<bool> UnsubscribeAsync(
        Guid tenantId, Guid ticketId, string userId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        int removed = await db.TicketSubscriptions
            .Where(s => s.TicketId == ticketId && s.UserId == userId && s.Ticket.TenantId == tenantId)
            .ExecuteDeleteAsync(ct);

        return removed > 0;
    }

    /// <summary>Who follows the ticket.</summary>
    public async Task<List<TicketFollower>> GetFollowersAsync(
        Guid tenantId, Guid ticketId, CancellationToken ct = default)
    {
        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.TicketSubscriptions.AsNoTracking()
            .Where(s => s.TicketId == ticketId && s.Ticket.TenantId == tenantId)
            .OrderBy(s => s.Name)
            .Select(s => new TicketFollower(s.UserId, s.Name, s.Email, s.Reason, s.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>A colleague named in a note.</summary>
    public readonly record struct Mentioned(string UserId, string Name, string Email);

    /// <summary>
    /// Adds a note — written in the editor, so HTML — that names colleagues: each is subscribed
    /// and mailed the note's text at once (Zammad's <c>@@mention</c>).
    /// </summary>
    /// <returns>
    /// Who could actually be told — a colleague whose mail did not leave is still subscribed,
    /// and the caller says which ones were not reached. Null when the ticket is not the tenant's.
    /// </returns>
    public async Task<List<string>?> AddNoteMentioningAsync(
        Guid tenantId, Guid ticketId, string html, bool customerVisible, IReadOnlyList<Mentioned> mentioned,
        string? actor, string? actorUserId, DateTime at, CancellationToken ct = default)
    {
        if (RichText.IsEmpty(html))
        {
            throw new ArgumentException("The note is empty.", nameof(html));
        }

        string names = string.Join(", ", mentioned.Select(m => m.Name));
        string note = RichText.ToText(html);

        Guid? written = await tickets.AddNoteAsync(
            tenantId, ticketId, html, actor, actorUserId, at, customerVisible,
            mentioned.Count == 0 ? null : $"\u2192 {names}", ct);

        if (written is null)
        {
            return null;
        }

        List<string> reached = [];

        if (mentioned.Count == 0)
        {
            return reached;
        }

        Ticket ticket;

        using (SupportDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            ticket = (await FindAsync(db, tenantId, ticketId, ct))!;
        }

        foreach (Mentioned m in mentioned)
        {
            await SubscribeAsync(tenantId, ticketId, m.UserId, m.Name, m.Email,
                TicketSubscriptionReason.Mentioned, actor, at, ct);

            bool sent = await notifier.NotifyColleagueAsync(
                ticket, m.Email, $"{actor ?? "A colleague"} mentioned you: {ticket.Title}",
                $"{actor ?? "A colleague"} mentioned you on ticket #{ticket.Number}, \u201c{ticket.Title}\u201d:\n\n"
                + note.Trim()
                + "\n\nYou now follow this ticket and will be mailed what happens on it.",
                ct);

            if (sent)
            {
                reached.Add(m.Name);
            }
        }

        return reached;
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    /// <summary>
    /// Where a new follower's digest cursor starts. <see cref="TicketEvent.RecordedAt"/> is
    /// the wall clock at write time, which is always at or after an <c>at</c> the caller took
    /// before writing, so the later of the two is "everything so far has been seen".
    /// </summary>
    private static DateTime DigestCursorStart(DateTime at)
    {
        DateTime wall = DateTime.UtcNow;
        return wall > at ? wall : at;
    }

    /// <summary>Whether a status is one no more work happens in.</summary>
    public static bool IsSettled(TicketStatus status) =>
        status is TicketStatus.Closed or TicketStatus.Rejected or TicketStatus.Merged;

    private static Task<Ticket?> FindAsync(SupportDbContext db, Guid tenantId, Guid ticketId, CancellationToken ct) =>
        db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.TenantId == tenantId, ct);

    /// <summary>What this ticket is to the other, given what the other is to this one.</summary>
    private static string Describe(TicketLinkRole role) => role switch
    {
        TicketLinkRole.Parent => "child",
        TicketLinkRole.Child => "parent",
        _ => "related ticket",
    };

    private static TicketLinkRole Inverse(TicketLinkRole role) => role switch
    {
        TicketLinkRole.Parent => TicketLinkRole.Child,
        TicketLinkRole.Child => TicketLinkRole.Parent,
        _ => TicketLinkRole.Related,
    };

    /// <summary>A stored instant as a Swedish wall-clock time, the way the history reads.</summary>
    private static string BusinessCalendarText(DateTime instant) =>
        Support.BusinessCalendar.ToLocal(instant).ToString("yyyy-MM-dd HH:mm");

    private static TicketEvent Event(
        Guid ticketId, TicketEventKind kind, DateTime at, string? actor, string detail,
        bool customerVisible = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            TicketId = ticketId,
            Kind = kind,
            At = at,
            Actor = actor,
            Detail = detail,
            CustomerVisible = customerVisible,
        };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
