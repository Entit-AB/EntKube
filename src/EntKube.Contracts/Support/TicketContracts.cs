namespace EntKube.Contracts.Support;

/// <summary>Which slice of a tenant's open tickets to list.</summary>
/// <remarks>
/// The person-relative overviews the screen has — mine, my reminders, following — are absent:
/// an API token belongs to a tenant, not to a person, so "mine" has nobody to mean.
/// </remarks>
public enum TicketQueueView
{
    /// <summary>Everything not yet closed, worst first.</summary>
    Open,

    /// <summary>Open, and nobody holds it.</summary>
    Unassigned,

    /// <summary>A reminder has come due.</summary>
    Reminders,

    /// <summary>A response, resolution or update deadline missed or due within ten minutes.</summary>
    Escalated,
}

/// <summary>One of a ticket's agreement clocks.</summary>
/// <param name="Deadline">When it runs out; null when this priority sets no target.</param>
/// <param name="MetAt">When it was met, if it was.</param>
/// <param name="Breached">Whether it was missed — met late, or still open past the deadline.</param>
/// <param name="Paused">Whether it is stopped while somebody else is being waited on.</param>
public sealed record TicketClock(DateTime? Deadline, DateTime? MetAt, bool Breached, bool Paused);

/// <summary>A ticket as a queue lists it.</summary>
/// <param name="Status">New, InProgress, Waiting, Resolved, Closed, Rejected or Merged.</param>
/// <param name="Priority">P1 to P4.</param>
/// <param name="PriorityConfirmed">False until somebody here has confirmed it (§14.3).</param>
/// <param name="PenaltyDeviation">Whether a missed response here carries §14.6's penalty.</param>
public sealed record TicketSummary(
    Guid Id,
    int Number,
    string Title,
    string Status,
    string Priority,
    bool PriorityConfirmed,
    Guid CustomerId,
    string Customer,
    string? Assignee,
    IReadOnlyList<string> Tags,
    DateTime ReportedAt,
    DateTime? ReminderAt,
    TicketClock Response,
    TicketClock Resolution,
    DateTime? UpdateDue,
    bool PenaltyDeviation);

/// <summary>One entry on a ticket's history.</summary>
/// <param name="CustomerVisible">Whether the customer sees it in the portal.</param>
/// <param name="FromMergedNumber">
/// Set when the entry belongs to a duplicate merged into this ticket: that duplicate's number.
/// </param>
public sealed record TicketEntry(
    Guid Id, DateTime At, string Kind, string? Actor, string Detail, bool CustomerVisible, int? FromMergedNumber);

/// <summary>A link to another ticket, seen from this one.</summary>
/// <param name="Role">What the other ticket is to this one: related, parent or child.</param>
public sealed record TicketLinkSummary(Guid LinkId, string Role, int Number, string Title, string Status);

/// <summary>One ticket in full.</summary>
/// <param name="MergedInto">For a merged duplicate, the number of the ticket it went into.</param>
public sealed record TicketView(
    TicketSummary Summary,
    string Description,
    string? ReminderNote,
    DateTime? AutoCloseAt,
    int? MergedInto,
    IReadOnlyList<TicketLinkSummary> Links,
    IReadOnlyList<TicketEntry> History);

/// <summary>What a search found.</summary>
/// <param name="Truncated">There were more than the limit; narrow the search.</param>
public sealed record TicketSearchOutcome(IReadOnlyList<TicketSummary> Tickets, bool Truncated);

/// <summary>What a merge did.</summary>
/// <param name="RecordedAsResponse">The duplicate had no response, and the merge became it.</param>
/// <param name="ResponseWasLate">That response, or an earlier one, missed its deadline.</param>
public sealed record TicketMergeOutcome(int Duplicate, int Original, bool RecordedAsResponse, bool ResponseWasLate);

/// <summary>
/// Thrown when a ticket action is refused for a reason the caller can act on — a merge across
/// customers, a tag with a comma, a reminder on a closed ticket. The message is written for the
/// person who asked.
/// </summary>
public sealed class TicketActionRefused(string message) : Exception(message);

/// <summary>
/// The support queue as the rest of the product — and the public API — may use it.
///
/// <para>Every method takes the tenant and finds the ticket by its <b>number within that
/// tenant</b>: the number is what customers quote and what an operator types, and a number
/// that belongs to another tenant's ticket is simply no ticket here. Every method that changes
/// something takes the actor, which is written to the ticket's history; the history is the
/// record between the parties (§14.6), so nothing here acts anonymously.</para>
///
/// <para>Methods return null (or false) for "no such ticket here", and throw
/// <see cref="TicketActionRefused"/> for "you may not do that, and here is why".</para>
/// </summary>
public interface ITicketApi
{
    Task<IReadOnlyList<TicketSummary>> GetQueueAsync(
        Guid tenantId, TicketQueueView view, CancellationToken ct = default);

    Task<TicketView?> GetTicketAsync(Guid tenantId, int number, CancellationToken ct = default);

    /// <summary>
    /// Searches the tenant's tickets, open and closed, newest first, with Zammad's syntax
    /// (<c>tag:vpn status:open</c>, <c>reported:&gt;now-7d</c>). A query that cannot be read throws
    /// <see cref="TicketActionRefused"/> saying why.
    /// </summary>
    Task<TicketSearchOutcome> SearchAsync(Guid tenantId, string query, CancellationToken ct = default);

    /// <returns>The ticket's tags afterwards, or null when there is no such ticket.</returns>
    Task<IReadOnlyList<string>?> AddTagAsync(
        Guid tenantId, int number, string tag, string actor, CancellationToken ct = default);

    /// <returns>The ticket's tags afterwards, or null when there is no such ticket.</returns>
    Task<IReadOnlyList<string>?> RemoveTagAsync(
        Guid tenantId, int number, string tag, string actor, CancellationToken ct = default);

    /// <param name="role">What <paramref name="otherNumber"/> is to this ticket: related, parent or child.</param>
    Task<TicketLinkSummary?> LinkAsync(
        Guid tenantId, int number, int otherNumber, string role, string actor, CancellationToken ct = default);

    Task<bool> UnlinkAsync(Guid tenantId, int number, Guid linkId, string actor, CancellationToken ct = default);

    /// <summary>
    /// Merges ticket <paramref name="number"/> — the duplicate — into <paramref name="into"/>.
    /// Never erases a missed response; excluding the duplicate from the SLA is the separate,
    /// reasoned <paramref name="excludeFromSla"/>.
    /// </summary>
    Task<TicketMergeOutcome?> MergeAsync(
        Guid tenantId, int number, int into, bool excludeFromSla, string? reason, string actor,
        CancellationToken ct = default);

    /// <param name="at">When, or null to clear the reminder.</param>
    Task<bool> SetReminderAsync(
        Guid tenantId, int number, DateTime? at, string? note, string actor, CancellationToken ct = default);

    Task<bool> AddNoteAsync(
        Guid tenantId, int number, string text, bool customerVisible, string actor, CancellationToken ct = default);
}
