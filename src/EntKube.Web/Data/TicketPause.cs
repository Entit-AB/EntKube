namespace EntKube.Web.Data;

/// <summary>
/// A period during which the resolution clock did not run, because the ticket was waiting
/// on somebody we do not control.
///
/// <para>§14.4 allows this for the customer, for the customer's own suppliers (hosting,
/// cloud, platform, integration partner) and for any other third party whose involvement is
/// needed — explicitly including the case where that party is only available during office
/// hours. It also requires the wait to be documented in the ticket with its time and the
/// party being waited for, which is exactly the shape of this row.</para>
///
/// <para>A pause with no <see cref="EndedAt"/> is still running, and while one is open the
/// ticket has no resolution deadline: it is not late, because the clock is not moving.</para>
/// </summary>
public class TicketPause
{
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    public DateTime StartedAt { get; set; }

    /// <summary>When the party answered. Null while still waiting.</summary>
    public DateTime? EndedAt { get; set; }

    public WaitingOn WaitingOn { get; set; }

    /// <summary>Who specifically — a named person, supplier or system.</summary>
    public string? Party { get; set; }

    /// <summary>What is being waited for. §14.4 requires the wait to be documented.</summary>
    public required string Reason { get; set; }

    public string? RecordedBy { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket Ticket { get; set; } = null!;
}
