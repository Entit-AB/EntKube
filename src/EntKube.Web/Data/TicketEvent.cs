namespace EntKube.Web.Data;

/// <summary>What happened to a ticket. One value per thing the agreement cares about.</summary>
public enum TicketEventKind
{
    Created = 0,

    /// <summary>The customer proposed a priority at registration (§14.3).</summary>
    PriorityProposed = 1,

    /// <summary>We confirmed or changed it, in writing and with reasons (§14.3).</summary>
    PriorityConfirmed = 2,

    /// <summary>Reprioritised mid-flight — the new priority's targets run from here (§14.3).</summary>
    Reprioritised = 3,

    /// <summary>First assessment made and work begun — what response time is measured to.</summary>
    Responded = 4,

    /// <summary>A status update to the customer, at the §14.4 interval for the priority.</summary>
    StatusUpdate = 5,

    /// <summary>The resolution clock stopped because someone else is being waited on (§14.4).</summary>
    PauseStarted = 6,

    /// <summary>That party answered and the clock resumed.</summary>
    PauseEnded = 7,

    Assigned = 8,

    /// <summary>Escalated to level 2 or 3 of §14.5.</summary>
    Escalated = 9,

    Note = 10,

    /// <summary>Service restored, or a workaround the customer accepted (§14.4).</summary>
    Resolved = 11,

    Closed = 12,

    /// <summary>The fault lay outside the agreement (§14.4). Billable, not a deviation.</summary>
    Rejected = 13,

    /// <summary>The §14.6 written incident report for a P1 was delivered.</summary>
    IncidentReportDelivered = 14,

    /// <summary>
    /// Moved to another customer's application — possibly another tenant's. Recorded
    /// because the move changes which agreement the ticket is measured against, and §14.6
    /// makes that history the record between the parties.
    /// </summary>
    Moved = 15,

    /// <summary>Merged into another ticket, or another ticket merged into this one.</summary>
    Merged = 16,

    /// <summary>A link to another ticket was added or removed.</summary>
    Linked = 17,

    /// <summary>The title changed.</summary>
    Renamed = 18,

    /// <summary>A reminder or an automatic close was set, cleared or fired.</summary>
    Reminder = 19,

    /// <summary>A tag was added or removed.</summary>
    Tagged = 20,

    /// <summary>A new ticket was split off from an entry on this one, or this one was split off.</summary>
    Split = 21,
}

/// <summary>
/// One entry in a ticket's history — appended, never edited.
///
/// <para>§14.6 makes the portal's timestamps the record between the parties for SLA
/// measurement, and §28 makes what is reported binding unless disputed within thirty days.
/// That makes this table evidence rather than a convenience: every state change is
/// attributed and timed, and a correction is another entry rather than a rewrite.</para>
/// </summary>
public class TicketEvent
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public TicketEventKind Kind { get; set; }

    /// <summary>When it happened — which is not always when it was recorded.</summary>
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>Who did it: a user, or the name of the process that did it automatically.</summary>
    public string? Actor { get; set; }

    /// <summary>
    /// What was said or decided. For a priority change this carries the reason §14.3
    /// requires in writing.
    /// </summary>
    public string Detail { get; set; } = "";

    /// <summary>
    /// Whether the customer sees this in the portal. Internal working notes are not hidden
    /// from the SLA record — every entry counts as history — but they are not shown.
    /// </summary>
    public bool CustomerVisible { get; set; } = true;

    /// <summary>
    /// When the row was written, as opposed to <see cref="At"/>, which is when the thing it
    /// records happened and is sometimes earlier (a ticket is registered at its reporting
    /// time, a mail at its arrival). Subscriber notifications walk this, so that an event
    /// written late with an early <see cref="At"/> is not stepped over. Null on rows written
    /// before it existed.
    /// </summary>
    public DateTime? RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}
