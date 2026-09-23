namespace EntKube.Web.Data;

/// <summary>
/// A support address of a customer's own — <c>customer-support@entit.se</c> — which routes
/// anything sent to it to that customer, and which their replies come back from.
///
/// <para><b>Why this beats knowing who sent it.</b> A domain says who somebody is; an
/// address says who they are writing about. Those differ exactly when it matters: a
/// consultant at a third company reporting a fault on a customer's system has a domain
/// that identifies nobody useful, and a supplier's engineer has one that identifies the
/// wrong customer entirely. The address they were given is a deliberate choice made per
/// message, and it is right more often than anything that can be inferred about them.</para>
///
/// <para><b>This is an alias, not a second mailbox.</b> The address has to deliver into the
/// mailbox the tenant already polls — nothing here opens another IMAP connection. What
/// makes it work is the envelope header the delivering server leaves behind, which is why
/// <see cref="InboundMailMessage.ToAddresses"/> keeps Delivered-To as well as To.</para>
/// </summary>
public class CustomerSupportAddress
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The address, lower-cased.</summary>
    public required string Address { get; set; }

    /// <summary>
    /// Whether replies to this customer are sent from it.
    ///
    /// <para>Normally yes, and that is the point: a customer who writes to their own
    /// address and is answered from the generic one learns to use the generic one. A
    /// customer with several addresses has at most one that replies come from.</para>
    /// </summary>
    public bool ReplyFromThis { get; set; } = true;

    public string? Notes { get; set; }

    public string? AddedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}
