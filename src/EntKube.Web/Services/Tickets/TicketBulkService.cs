using EntKube.Web.Data;
using EntKube.Web.Services.Support;

namespace EntKube.Web.Services.Tickets;

/// <summary>One change applied to many tickets. Exactly one kind of change per request.</summary>
public abstract record BulkChange
{
    /// <summary>Hand them to a technician on the roster, or put them back with null.</summary>
    public sealed record Assign(SupportTechnician? To) : BulkChange;

    public sealed record AddTag(string Tag) : BulkChange;

    public sealed record RemoveTag(string Tag) : BulkChange;

    /// <summary>Set a reminder on each, or clear it with null.</summary>
    public sealed record Remind(DateTime? At, string? Note) : BulkChange;

    /// <summary>
    /// Confirm or change the priority, with the one written reason §14.3 requires — the same
    /// reason recorded on every ticket, since it was one decision.
    /// </summary>
    public sealed record Prioritise(TicketPriority Priority, string Reason) : BulkChange;

    /// <summary>
    /// Close the ones that are resolved. A ticket that is not resolved is skipped rather than
    /// closed: in the detail view only a resolved ticket offers Close, and selecting it in a
    /// list should not be a way round that.
    /// </summary>
    public sealed record Close : BulkChange;

    /// <summary>
    /// Run a macro on each — Zammad's drag-onto-a-macro. "Me" in the macro is the person running
    /// it, so who that is travels with the change. The macro's after-run behaviour does not apply
    /// in bulk (Zammad ignores it too): the operator stays on the list.
    /// </summary>
    public sealed record RunMacro(Guid MacroId, string RunnerName, string? RunnerUserId, string? RunnerEmail) : BulkChange;

    /// <summary>The same note on each — Zammad's bulk comment.</summary>
    public sealed record Comment(string Text, bool CustomerVisible) : BulkChange;
}

/// <summary>What happened to one ticket in a bulk change.</summary>
/// <param name="Skipped">Why it was left alone, or null when the change was made.</param>
public readonly record struct BulkOutcome(Guid TicketId, int? Number, string? Skipped);

/// <summary>
/// Applies one change to a selection of tickets, through the same service methods the detail
/// view uses, so a bulk change is exactly N single changes and records exactly what they would.
///
/// <para>Each ticket is changed on its own: one that cannot be changed is reported and the rest
/// go ahead, because "17 done, 1 skipped because it is not resolved" is more useful than an
/// all-or-nothing refusal over the one. Never asks for time (Zammad doesn't either).</para>
/// </summary>
public class TicketBulkService(TicketService tickets, TicketWorkService work, MacroService? macros = null)
{
    /// <summary>The most tickets one request may change.</summary>
    public const int MaxTickets = 200;

    public async Task<List<BulkOutcome>> ApplyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> ticketIds, BulkChange change, string actor, DateTime at,
        CancellationToken ct = default)
    {
        if (ticketIds.Count > MaxTickets)
        {
            throw new ArgumentException($"At most {MaxTickets} tickets at a time.", nameof(ticketIds));
        }

        if (change is BulkChange.Prioritise { Reason: var reason } && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("§14.3 requires a written reason for the priority.", nameof(change));
        }

        if (change is BulkChange.Comment { Text: var text } && string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("The note is empty.", nameof(change));
        }

        List<BulkOutcome> outcomes = [];

        foreach (Guid id in ticketIds.Distinct())
        {
            Ticket? ticket = await tickets.GetAsync(tenantId, id, ct);

            if (ticket is null)
            {
                outcomes.Add(new BulkOutcome(id, null, "No such ticket here."));
                continue;
            }

            string? skipped;

            try
            {
                skipped = await ApplyOneAsync(tenantId, ticket, change, actor, at, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                skipped = ex.Message;
            }

            outcomes.Add(new BulkOutcome(id, ticket.Number, skipped));
        }

        return outcomes;
    }

    private async Task<string?> ApplyOneAsync(
        Guid tenantId, Ticket ticket, BulkChange change, string actor, DateTime at, CancellationToken ct)
    {
        bool settled = TicketWorkService.IsSettled(ticket.Status);

        switch (change)
        {
            case BulkChange.Assign(var to):
                if (settled) return "Closed tickets are not reassigned.";
                await tickets.AssignAsync(tenantId, ticket.Id, to?.Name, actor, at, to?.UserId, to?.Email, ct: ct);
                return null;

            case BulkChange.AddTag(var tag):
                await work.AddTagAsync(tenantId, ticket.Id, tag, actor, at, ct);
                return null;

            case BulkChange.RemoveTag(var tag):
                await work.RemoveTagAsync(tenantId, ticket.Id, tag, actor, at, ct);
                return null;

            case BulkChange.Remind(var when, var note):
                if (settled) return "Closed tickets take no reminders.";
                await work.SetReminderAsync(tenantId, ticket.Id, when, note, actor, at, ct);
                return null;

            case BulkChange.Prioritise(var priority, var reason):
                if (settled) return "The priority of a closed ticket is part of its record.";
                await tickets.ConfirmPriorityAsync(tenantId, ticket.Id, priority, reason, actor, at, ct);
                return null;

            case BulkChange.Close:
                if (ticket.Status != TicketStatus.Resolved) return "Not resolved, so not closed.";
                await tickets.CloseAsync(tenantId, ticket.Id, actor, at, ct);
                return null;

            case BulkChange.RunMacro(var macroId, var name, var userId, var email):
                if (macros is null) throw new ArgumentException("Macros are not available here.");
                MacroRun? run = await macros.RunAsync(tenantId, ticket.Id, macroId, actor, (name, userId, email), at, ct);
                return run is null ? "No such macro here." : null;

            case BulkChange.Comment(var text, var visible):
                await tickets.AddEventAsync(tenantId, ticket.Id, TicketEventKind.Note, text.Trim(), actor, at, visible, ct);
                return null;

            default:
                throw new ArgumentException($"Unknown change {change.GetType().Name}.");
        }
    }
}
