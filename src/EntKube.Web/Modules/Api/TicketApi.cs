using EntKube.Contracts.Support;
using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using TicketClockContract = EntKube.Contracts.Support.TicketClock;

namespace EntKube.Web.Modules.Api;

/// <summary>
/// Support's implementation of <see cref="ITicketApi"/>: the ticket services, translated to the
/// contract's records and its two kinds of "no" — null for a ticket that is not this tenant's,
/// <see cref="TicketActionRefused"/> for an action the rules refuse.
///
/// <para>Numbers are resolved inside the tenant first (<see cref="TicketService.FindByNumberAsync"/>),
/// so every call below this point already holds an id the tenant owns, and each service method
/// checks the tenant again besides.</para>
/// </summary>
public sealed class TicketApi(
    TicketService tickets, TicketWorkService work, TicketOverviews overviews) : ITicketApi
{
    public async Task<IReadOnlyList<TicketSummary>> GetQueueAsync(
        Guid tenantId, TicketQueueView view, CancellationToken ct = default)
    {
        TicketOverview overview = view switch
        {
            TicketQueueView.Unassigned => TicketOverview.Unassigned,
            TicketQueueView.Reminders => TicketOverview.Reminders,
            TicketQueueView.Escalated => TicketOverview.Escalated,
            _ => TicketOverview.Open,
        };

        DateTime now = DateTime.UtcNow;

        // Nobody: the person-relative overviews are not offered through this contract.
        List<TicketSlaStatus> rows = await overviews.GetAsync(
            tenantId, overview, new OverviewViewer(null, null), now, ct);

        return [.. rows.Select(s => Summarise(s, [.. s.Ticket.Tags.Select(t => t.Name).Order(StringComparer.Ordinal)]))];
    }

    public async Task<TicketView?> GetTicketAsync(Guid tenantId, int number, CancellationToken ct = default)
    {
        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id
            || await tickets.GetAsync(tenantId, id, ct) is not Ticket ticket)
        {
            return null;
        }

        TicketSlaStatus status = TicketService.StatusOf(ticket, DateTime.UtcNow);

        int? mergedInto = null;

        if (ticket.MergedIntoTicketId is Guid targetId && await tickets.GetAsync(tenantId, targetId, ct) is Ticket target)
        {
            mergedInto = target.Number;
        }

        List<TicketLinkView> links = await work.GetLinksAsync(tenantId, id, ct);
        List<MergedEntry> merged = await work.GetMergedHistoryAsync(tenantId, id, ct);

        IEnumerable<TicketEntry> history = ticket.Events
            .Select(e => Entry(e, null))
            .Concat(merged.Select(m => Entry(m.Event, m.FromNumber)))
            .OrderBy(e => e.At);

        return new TicketView(
            Summarise(status, [.. ticket.Tags.Select(t => t.Name).Order(StringComparer.Ordinal)]),
            ticket.Description,
            ticket.ReminderNote,
            ticket.AutoCloseAt,
            mergedInto,
            [.. links.Select(l => new TicketLinkSummary(
                l.LinkId, l.Role.ToString().ToLowerInvariant(), l.OtherNumber, l.OtherTitle, l.OtherStatus.ToString()))],
            [.. history]);
    }

    public async Task<TicketSearchOutcome> SearchAsync(Guid tenantId, string query, CancellationToken ct = default)
    {
        try
        {
            TicketService.SearchResult result = await tickets.SearchAsync(tenantId, query, DateTime.UtcNow, ct);
            return new TicketSearchOutcome(
                [.. result.Tickets.Select(s => Summarise(s, [.. s.Ticket.Tags.Select(t => t.Name).Order(StringComparer.Ordinal)]))],
                result.Truncated);
        }
        catch (TicketSearchException ex)
        {
            throw new TicketActionRefused(ex.Message);
        }
    }

    public async Task<IReadOnlyList<string>?> AddTagAsync(
        Guid tenantId, int number, string tag, string actor, CancellationToken ct = default)
    {
        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id)
        {
            return null;
        }

        await Refusable(() => work.AddTagAsync(tenantId, id, tag, actor, DateTime.UtcNow, ct));
        return await work.GetTagsAsync(tenantId, id, ct);
    }

    public async Task<IReadOnlyList<string>?> RemoveTagAsync(
        Guid tenantId, int number, string tag, string actor, CancellationToken ct = default)
    {
        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id)
        {
            return null;
        }

        await work.RemoveTagAsync(tenantId, id, tag, actor, DateTime.UtcNow, ct);
        return await work.GetTagsAsync(tenantId, id, ct);
    }

    public async Task<TicketLinkSummary?> LinkAsync(
        Guid tenantId, int number, int otherNumber, string role, string actor, CancellationToken ct = default)
    {
        TicketLinkRole parsed = (role ?? "").Trim().ToLowerInvariant() switch
        {
            "related" => TicketLinkRole.Related,
            "parent" => TicketLinkRole.Parent,
            "child" => TicketLinkRole.Child,
            _ => throw new TicketActionRefused("A link's role is related, parent or child."),
        };

        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id)
        {
            return null;
        }

        TicketLinkView? link = await Refusable(() =>
            work.LinkAsync(tenantId, id, otherNumber, parsed, actor, DateTime.UtcNow, ct));

        return link is { } l
            ? new TicketLinkSummary(l.LinkId, l.Role.ToString().ToLowerInvariant(), l.OtherNumber, l.OtherTitle, l.OtherStatus.ToString())
            : null;
    }

    public async Task<bool> UnlinkAsync(
        Guid tenantId, int number, Guid linkId, string actor, CancellationToken ct = default)
    {
        return await tickets.FindByNumberAsync(tenantId, number, ct) is Guid id
            && await work.UnlinkAsync(tenantId, id, linkId, actor, DateTime.UtcNow, ct);
    }

    public async Task<TicketMergeOutcome?> MergeAsync(
        Guid tenantId, int number, int into, bool excludeFromSla, string? reason, string actor,
        CancellationToken ct = default)
    {
        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id)
        {
            return null;
        }

        TicketWorkService.MergeResult? result = await Refusable(() =>
            work.MergeAsync(tenantId, id, into, excludeFromSla, reason, actor, DateTime.UtcNow, ct));

        return result is { } r
            ? new TicketMergeOutcome(r.Source.Number, r.Target.Number, r.RecordedAsResponse, r.ResponseWasLate)
            : null;
    }

    public async Task<bool> SetReminderAsync(
        Guid tenantId, int number, DateTime? at, string? note, string actor, CancellationToken ct = default)
    {
        if (await tickets.FindByNumberAsync(tenantId, number, ct) is not Guid id)
        {
            return false;
        }

        // A time with no zone is ambiguous on the wire; read it as UTC, which is what the API
        // publishes everywhere, rather than as whatever zone the server happens to run in.
        DateTime? utc = at is DateTime t
            ? t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime()
            : null;

        await Refusable(() => work.SetReminderAsync(tenantId, id, utc, note, actor, DateTime.UtcNow, ct));
        return true;
    }

    public async Task<bool> AddNoteAsync(
        Guid tenantId, int number, string text, bool customerVisible, string actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TicketActionRefused("The note is empty.");
        }

        return await tickets.FindByNumberAsync(tenantId, number, ct) is Guid id
            && await tickets.AddEventAsync(
                tenantId, id, TicketEventKind.Note, text.Trim(), actor, DateTime.UtcNow, customerVisible, ct);
    }

    // ───────────────────────────── Mapping ─────────────────────────────

    private static TicketSummary Summarise(TicketSlaStatus s, IReadOnlyList<string> tags)
    {
        Ticket t = s.Ticket;

        return new TicketSummary(
            t.Id, t.Number, t.Title, t.Status.ToString(), t.Priority.ToString(), t.PriorityConfirmedAt is not null,
            t.CustomerId, t.Customer?.Name ?? "", t.Assignee, tags, t.ReportedAt, t.ReminderAt,
            Clock(s.Response), Clock(s.Resolution), s.UpdateDue, s.IsPenaltyDeviation);
    }

    private static TicketClockContract Clock(ClockStatus c) =>
        new(c.Budget.Exists ? c.Deadline : null, c.MetAt, c.Budget.Exists && c.Breached, c.Paused);

    private static TicketEntry Entry(TicketEvent e, int? fromMerged) =>
        new(e.Id, e.At, e.Kind.ToString(), e.Actor, e.Detail, e.CustomerVisible, fromMerged);

    /// <summary>The services' refusals, in the contract's terms.</summary>
    private static async Task<T> Refusable<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new TicketActionRefused(ex.Message);
        }
    }
}
