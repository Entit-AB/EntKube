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

    /// <summary>First assessment made and work begun — what responstid is measured to.</summary>
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

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}
