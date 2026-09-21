namespace EntKube.Web.Data;

/// <summary>
/// What kind of work an hour was, which decides which rate it is priced at and whether it
/// touches the timbank at all.
/// </summary>
public enum WorkKind
{
    /// <summary>
    /// Förvaltning — the ordinary rate of §13, whatever the role. Draws on the timbank
    /// under Modell A.
    /// </summary>
    Management = 0,

    /// <summary>
    /// An utvecklingsuppdrag under §15, at the development rate. §11.1 is explicit that it
    /// does not draw on the timbank.
    /// </summary>
    Development = 1,

    /// <summary>
    /// Travel to and from site, billed at 50% of the ordinary rate (§20). Only when the
    /// customer asked for on-site work and approved it in advance.
    /// </summary>
    Travel = 2,

    /// <summary>
    /// The first assessment of an incident. §10.3 includes up to thirty minutes of it per
    /// incident in the grundavgift; the rest is ordinary förvaltning.
    /// </summary>
    IncidentAssessment = 3,

    /// <summary>
    /// On-boarding an application under §5. Billed at a fixed price from Bilaga C, so the
    /// hours are recorded for our own sake and never billed by the hour or drawn from the
    /// bank.
    /// </summary>
    Onboarding = 4,
}

/// <summary>
/// One stretch of worked time.
///
/// <para><b>Actual times, not rounded ones.</b> §13 rounds per påbörjad timme, but it also
/// says contiguous work on one ärende counts as a single arbetspass and that short bursts
/// inside it are not rounded separately. Rounding at the point of entry would therefore
/// overbill: three ten-minute touches on one ticket in an afternoon are one hour, not
/// three. So the row keeps what actually happened and the arithmetic happens on the way
/// out, where the rule can be applied to the pass as a whole.</para>
///
/// <para>The same reason the cost ledger keeps hours rather than a monthly figure: a
/// number you can re-derive is a number you can explain.</para>
/// </summary>
public class TimeEntry
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The application the work was on, when it was on one.</summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// The ticket the work belongs to. What makes a work pass a work pass: §13 groups
    /// contiguous work "i ett och samma ärende".
    /// </summary>
    public Guid? TicketId { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime EndedAt { get; set; }

    public WorkKind Kind { get; set; } = WorkKind.Management;

    /// <summary>What was done. Appears on the specification §20 requires.</summary>
    public required string Description { get; set; }

    public string? PerformedBy { get; set; }

    /// <summary>
    /// Set when the customer approved this work beyond the timbank (§11.1) or above a
    /// monthly cap (§12). P1 and P2 proceed without it, and say so in the reason.
    /// </summary>
    public Guid? AuthorisationId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public App? App { get; set; }
    public Ticket? Ticket { get; set; }
    public WorkAuthorisation? Authorisation { get; set; }
}

/// <summary>
/// The customer's go-ahead for work they would otherwise have to approve first.
///
/// <para>§11.1: once the timbank is spent, further work starts after the customer approves
/// it in the ärendeportal — except P1 and P2, which proceed without delay and are billed
/// without separate approval. §12 has the mirror rule for a monthly cap under Modell B.
/// Both are the same act, so both are this row.</para>
/// </summary>
public class WorkAuthorisation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The ticket it covers, or null for a general authorisation for the month.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>The application it covers, for a per-application cap under §12.</summary>
    public Guid? AppId { get; set; }

    /// <summary>The month it applies to, as the first instant of that month in UTC.</summary>
    public DateTime Month { get; set; }

    /// <summary>How many hours were authorised. Null means no explicit ceiling.</summary>
    public decimal? Hours { get; set; }

    public required string ApprovedBy { get; set; }

    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;

    public string? Note { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public Ticket? Ticket { get; set; }
    public App? App { get; set; }
    public List<TimeEntry> Entries { get; set; } = [];
}
