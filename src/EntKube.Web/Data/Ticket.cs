namespace EntKube.Web.Data;

/// <summary>
/// The §14.2 priorities. The number is the order: P1 is the most severe.
/// </summary>
public enum TicketPriority
{
    /// <summary>Critical — wholly unavailable, or a risk to patient safety, data or personal data.</summary>
    P1 = 1,

    /// <summary>High — a material function is unavailable or wrong for a larger group.</summary>
    P2 = 2,

    /// <summary>Medium — part of a function, or individual users. A workaround exists.</summary>
    P3 = 3,

    /// <summary>Low — cosmetic, documentation, questions, suggestions.</summary>
    P4 = 4,
}

/// <summary>Where a ticket came from. §14.3 lists the channels the agreement recognises.</summary>
public enum TicketChannel
{
    /// <summary>Raised by the customer in the ticket portal — the channel §14.3 requires.</summary>
    Portal = 0,

    /// <summary>Arrived as e-mail, accepted for P3–P4 under §14.3.</summary>
    Email = 1,

    /// <summary>Raised over Teams or by phone and registered afterwards.</summary>
    Conversation = 2,

    /// <summary>
    /// Opened by our own monitoring. §14.3: the reporting time is the time of the alarm, not
    /// the time somebody noticed it.
    /// </summary>
    Monitoring = 3,
}

/// <summary>Where a ticket stands.</summary>
public enum TicketStatus
{
    /// <summary>Registered, first assessment not yet made.</summary>
    New = 0,

    /// <summary>Acknowledged and being worked (§14.4's response time has been met).</summary>
    InProgress = 1,

    /// <summary>Waiting on the customer or a third party — the resolution clock is paused.</summary>
    Waiting = 2,

    /// <summary>Service restored, or a workaround the customer accepted (§14.4).</summary>
    Resolved = 3,

    /// <summary>Closed after resolution.</summary>
    Closed = 4,

    /// <summary>
    /// §14.4: the fault turned out to lie in a system outside the agreement. Time spent is
    /// still billable and it does not count as an SLA deviation.
    /// </summary>
    Rejected = 5,
}

/// <summary>Who a paused ticket is waiting for (§14.4).</summary>
public enum WaitingOn
{
    /// <summary>The customer — an answer, access, a decision, or an action.</summary>
    Customer = 0,

    /// <summary>One of the customer's own suppliers: hosting, cloud, platform, integration partner.</summary>
    CustomerVendor = 1,

    /// <summary>Another third party whose involvement is needed and whom we do not control.</summary>
    ThirdParty = 2,
}

/// <summary>
/// A support ticket — the human counterpart to <see cref="AlertIncident"/>.
///
/// <para><b>Why this is not the same entity as an alert incident.</b> An alert incident is
/// machine-shaped: keyed by fingerprint and alert name, scoped to a cluster, its severity
/// decided by the rule that fired. A ticket is raised by a named person about an
/// application, carries a priority that is proposed and then confirmed in writing, and runs
/// clocks that only tick inside a support window and stop while somebody else is being
/// waited on. An alert incident <em>opens</em> a ticket, through
/// <see cref="AlertIncidentId"/>; it does not become one.</para>
///
/// <para><b>The timestamps are evidence.</b> §14.6 makes the ticket portal's timestamps the
/// record between the parties, and §28 makes anything in the monthly report binding unless
/// disputed within thirty days. So changes are appended to <see cref="TicketEvent"/> rather
/// than overwritten, and the fields here say what is true now while the events say how it
/// got that way.</para>
/// </summary>
public class Ticket
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>
    /// The application the ticket is about. Null only for something that does not belong to
    /// one; the SLA cannot be measured without it, since the support window comes from
    /// Annex A.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// The human reference, unique within a tenant — what people quote to each other. A
    /// number rather than the id, because nobody reads a GUID down a phone.
    /// </summary>
    public int Number { get; set; }

    public required string Title { get; set; }

    public string Description { get; set; } = "";

    public TicketChannel Channel { get; set; }

    public TicketStatus Status { get; set; } = TicketStatus.New;

    /// <summary>
    /// What the customer proposed at registration. §14.3 asks them to state one; we confirm
    /// or change it, in writing and with reasons.
    /// </summary>
    public TicketPriority? ProposedPriority { get; set; }

    /// <summary>
    /// The priority in force. Until the first assessment §14.3 gives the customer's view
    /// precedence for P1 and P2, so this starts as their proposal when they made one.
    /// </summary>
    public TicketPriority Priority { get; set; } = TicketPriority.P3;

    /// <summary>
    /// When the current priority took effect. §14.3: after a reprioritisation the targets
    /// are counted from that moment, not from registration.
    /// </summary>
    public DateTime PriorityEffectiveFrom { get; set; }

    /// <summary>The written reason for the priority as it stands, required by §14.3.</summary>
    public string? PriorityReason { get; set; }

    public string? PriorityConfirmedBy { get; set; }

    public DateTime? PriorityConfirmedAt { get; set; }

    /// <summary>
    /// When the ticket was registered. For a monitoring-detected ticket this is the time of
    /// the alarm (§14.3), not the time it was written down.
    /// </summary>
    public DateTime ReportedAt { get; set; }

    /// <summary>
    /// When the clocks start: the moment of registration if the window was open, otherwise
    /// the window's next opening (§9.1). Stored rather than recomputed, because the window
    /// can be renegotiated later and the past must not move.
    /// </summary>
    public DateTime ClockStartsAt { get; set; }

    /// <summary>
    /// The support window in force when the ticket was raised. A snapshot for the same
    /// reason: §2 lets the customer change window at a quarter boundary, and a ticket from
    /// before the change is still measured against what applied then.
    /// </summary>
    public SupportWindow SupportWindow { get; set; }

    /// <summary>
    /// True when this was a P1 worked outside the bought window — a call-out under §13,
    /// billed at the callout rate with a two-hour minimum.
    /// </summary>
    public bool IsCallout { get; set; }

    /// <summary>
    /// When we confirmed the ticket, made the first assessment and began work — what §14.4
    /// measures response time against.
    /// </summary>
    public DateTime? FirstResponseAt { get; set; }

    /// <summary>
    /// When service was restored or the customer accepted a workaround (§14.4). A remaining
    /// root cause becomes a new P3.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    public DateTime? ClosedAt { get; set; }

    public string? Assignee { get; set; }

    /// <summary>The person at the customer who raised it — who §14.1 says we keep informed.</summary>
    public string? RequestedBy { get; set; }

    public string? RequestedByEmail { get; set; }

    /// <summary>The alert incident that opened this ticket, when monitoring found it first.</summary>
    public Guid? AlertIncidentId { get; set; }

    public string? Resolution { get; set; }

    /// <summary>
    /// Set when §14.6's written incident report for a P1 has been delivered. The deadline is
    /// five working days after the ticket was closed.
    /// </summary>
    public DateTime? IncidentReportDeliveredAt { get; set; }

    /// <summary>
    /// Deviations are judged per ticket and reported monthly. Recording the judgement — with
    /// its reason — keeps §14.6's "waiting time, force majeure or a 14.7 exemption explains it"
    /// out of a spreadsheet.
    /// </summary>
    public bool ExcludedFromSla { get; set; }

    public string? SlaExclusionReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public App? App { get; set; }
    public List<TicketEvent> Events { get; set; } = [];
    public List<TicketPause> Pauses { get; set; } = [];
    public List<TicketAffectedApp> AffectedApps { get; set; } = [];
}

/// <summary>
/// One more application affected by the same ticket.
///
/// <para>§14.3: an issue hitting several instances of one parent application is handled as a
/// single ticket at the highest affected priority. Without this the choice would be between
/// opening twenty tickets for one fault or losing the record of which instances were
/// down.</para>
/// </summary>
public class TicketAffectedApp
{
    public Guid TicketId { get; set; }

    public Guid AppId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Ticket Ticket { get; set; } = null!;
    public App App { get; set; } = null!;
}
