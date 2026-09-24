namespace EntKube.Web.Data;

/// <summary>The ticketing systems an inbound bridge knows how to read.</summary>
public enum TicketSystem
{
    /// <summary>Atlassian Jira — Cloud or Data Center.</summary>
    Jira = 0,

    /// <summary>ServiceNow, reading incidents.</summary>
    ServiceNow = 1,

    /// <summary>Ivanti Neurons / Service Manager.</summary>
    Ivanti = 2,

    /// <summary>
    /// Anything else that can send us JSON. Read with the generic adapter, which takes the
    /// field names from the connection rather than knowing them.
    /// </summary>
    Other = 99,
}

/// <summary>
/// A customer's own ticketing system, and permission for it to raise tickets here.
///
/// <para><b>Per customer, not per tenant.</b> Every customer runs their own instance —
/// their own ServiceNow, their own Jira project — and the whole point of the connection is
/// to say which customer the tickets arriving on it belong to. That is also what makes the
/// endpoint safe: a request cannot name a customer, only present a secret that is already
/// bound to one.</para>
///
/// <para><b>Inbound only, and deliberately.</b> Nothing here holds credentials into the
/// customer's system: they push to us. That keeps us out of their production ITSM
/// altogether, which is a better answer to §17 than any amount of careful credential
/// handling — and a poller can be added behind the same seam later for a customer who
/// would rather be polled.</para>
/// </summary>
public class TicketBridgeConnection
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Whose tickets arrive on this connection. Not negotiable by the caller.</summary>
    public Guid CustomerId { get; set; }

    public TicketSystem System { get; set; }

    /// <summary>
    /// What to call it on screen — usually the instance, like <c>acme.service-now.com</c>.
    /// Recorded on every link so a ticket can say where it came from years later.
    /// </summary>
    public required string Instance { get; set; }

    /// <summary>
    /// The shared secret the sending system presents, salted and hashed.
    ///
    /// <para>Not in the vault, and not recoverable: nothing ever needs to read it back, only
    /// to compare it, so keeping a reversible copy would be a leak waiting to happen with no
    /// use to set against it. See <see cref="EntKube.Web.Services.Tickets.Bridge.BridgeSecret"/>.</para>
    /// </summary>
    public string? SecretHash { get; set; }

    /// <summary>
    /// Off until somebody switches it on. A connection that has never been tried should not
    /// be the thing that discovers a broken payload mapping during an incident.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// The application tickets land on when the payload does not say which one it is.
    ///
    /// <para>Optional, and worth leaving empty for a customer with several applications: a
    /// ticket with no application has no support window and no SLA
    /// (<see cref="Ticket.AppId"/>), which is visible and fixable, whereas a ticket on the
    /// wrong application is measured against the wrong window and looks fine.</para>
    /// </summary>
    public Guid? DefaultAppId { get; set; }

    /// <summary>
    /// How the sending system's priorities map onto §14.2's, as
    /// <c>their value=P1</c> lines — one per line, case-insensitive.
    ///
    /// <para>Configured rather than guessed. Jira's priorities are whatever a project admin
    /// last typed, and ServiceNow derives one from impact × urgency; §14.2's four are
    /// contractual definitions. Anything unmapped arrives without a proposal at all, which
    /// §14.3 already has an answer for.</para>
    /// </summary>
    public string? PriorityMap { get; set; }

    // ---- What has arrived ----------------------------------------------------------------

    public DateTime? LastDeliveryAt { get; set; }

    /// <summary>
    /// Why the last delivery was refused, cleared by one that worked. Kept for the same
    /// reason the mailbox keeps it: a quiet connection and a broken one look identical from
    /// the queue.
    /// </summary>
    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public App? DefaultApp { get; set; }
}

/// <summary>
/// The ticket in the customer's system that this one mirrors.
///
/// <para><b>What stops the same incident becoming five tickets.</b> A sending system
/// retries, resends on every field change, and will happily deliver the same incident twice
/// during a failover. The external id is the identity to dedupe on, exactly as
/// <see cref="InboundMailMessage.MessageId"/> is for mail, and the unique index is what
/// actually enforces it rather than a check somebody remembered to write.</para>
/// </summary>
public class ExternalTicketLink
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public Guid ConnectionId { get; set; }

    public TicketSystem System { get; set; }

    /// <summary>The instance it came from, copied at the time. See <see cref="TicketBridgeConnection.Instance"/>.</summary>
    public required string Instance { get; set; }

    /// <summary>
    /// The sending system's own identifier — a Jira issue id, a ServiceNow sys_id. Opaque,
    /// and the thing uniqueness is enforced on.
    /// </summary>
    public required string ExternalId { get; set; }

    /// <summary>
    /// What a person there would quote: <c>INC0012345</c>, <c>SUP-481</c>. Not unique, not
    /// dependable, and the only thing anybody will actually search for.
    /// </summary>
    public string? ExternalKey { get; set; }

    /// <summary>A link back, when the payload gave one.</summary>
    public string? Url { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The last delivery that mentioned this ticket.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Ticket Ticket { get; set; } = null!;
    public TicketBridgeConnection Connection { get; set; } = null!;
}
