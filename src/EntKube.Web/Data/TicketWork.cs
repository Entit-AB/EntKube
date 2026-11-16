namespace EntKube.Web.Data;

/// <summary>
/// A label on a ticket. One shared vocabulary per tenant, read off the tags already in use,
/// so there is no separate register of allowed tags to keep in step.
///
/// <para>Stored normalised (trimmed, lower-case, inner whitespace collapsed) — see
/// <c>TicketTags.Normalise</c> — so "VPN" and "vpn " are one tag, and the key
/// (<see cref="TicketId"/>, <see cref="Name"/>) makes adding one twice a no-op rather than a
/// second row.</para>
///
/// <para>Never shown to the customer. Tags are how we sort our own work.</para>
/// </summary>
public class TicketTag
{
    public Guid TicketId { get; set; }

    public required string Name { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string? AddedBy { get; set; }

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}

/// <summary>How two linked tickets relate.</summary>
public enum TicketLinkKind
{
    /// <summary>Related, with neither under the other — the same outage, the same delivery.</summary>
    Normal = 0,

    /// <summary>
    /// <see cref="TicketLink.FromTicketId"/> is the parent and <see cref="TicketLink.ToTicketId"/>
    /// the child: a piece of work and its subtasks, an original and the duplicate merged into
    /// it, a ticket and the one split off from it.
    /// </summary>
    ParentChild = 1,
}

/// <summary>
/// A relation between two tickets of the same tenant. One row per relation, read from both
/// ends: for <see cref="TicketLinkKind.ParentChild"/> the direction is the meaning, and for
/// <see cref="TicketLinkKind.Normal"/> it is only the order the two were linked in.
///
/// <para>Same tenant only, and enforced by the service rather than by the schema — a ticket
/// can be moved to another tenant afterwards, and the link then simply stops being shown on
/// the side that can no longer see the other ticket rather than leaking its title.</para>
/// </summary>
public class TicketLink
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid FromTicketId { get; set; }

    public Guid ToTicketId { get; set; }

    public TicketLinkKind Kind { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    // Navigation
    public Ticket FromTicket { get; set; } = null!;
    public Ticket ToTicket { get; set; } = null!;
}

/// <summary>Why somebody is following a ticket.</summary>
public enum TicketSubscriptionReason
{
    /// <summary>They asked to.</summary>
    Subscribed = 0,

    /// <summary>A colleague mentioned them in a note, which also subscribes them.</summary>
    Mentioned = 1,
}

/// <summary>
/// A tenant member following a ticket they do not necessarily own: they are mailed what
/// happens on it.
///
/// <para><b>Driven by the history, not by the code paths that write it.</b> A ticket changes
/// through a dozen methods, a mailbox poller and two bridges; a notification sent from each
/// would be forgotten at one of them. Instead a sweep reads the events recorded since
/// <see cref="NotifiedThrough"/> (by <see cref="TicketEvent.RecordedAt"/>) and sends one digest,
/// so anything that reaches the history reaches the subscriber — and advancing the cursor is a
/// conditional update, so with several instances one digest goes out, not several.</para>
/// </summary>
public class TicketSubscription
{
    public Guid TicketId { get; set; }

    /// <summary>The account. Max 450, matching the identity key.</summary>
    public required string UserId { get; set; }

    /// <summary>
    /// Their name as the history writes actors, so their own actions can be left out of what
    /// they are sent.
    /// </summary>
    public required string Name { get; set; }

    public required string Email { get; set; }

    public TicketSubscriptionReason Reason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    /// <summary>Events recorded up to here have been sent, or predate the subscription.</summary>
    public DateTime NotifiedThrough { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}
