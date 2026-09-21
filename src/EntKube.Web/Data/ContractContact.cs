namespace EntKube.Web.Data;

/// <summary>
/// A named person the agreement requires on one side or the other.
///
/// <para>§14.1 requires the customer's designated contact to be notified of every incident,
/// §23 requires that contact to hold a mandate to prioritise and approve and to have a
/// named deputy, and §14.5 requires people at escalation levels 2 and 3 on both sides,
/// recorded in Bilaga B and kept current by each party. Until now a customer in EntKube was
/// a name and nothing else, so none of that could be honoured from the data.</para>
///
/// <para>Scoped to a customer, optionally narrowed to one application: Bilaga A.1 asks for
/// an "ansvarig teknisk kontakt" per application, which is often but not always the
/// portfolio-wide one.</para>
/// </summary>
public class ContractContact
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The application this contact is specific to, or null for the whole portfolio.</summary>
    public Guid? AppId { get; set; }

    public ContractParty Party { get; set; }

    public ContractContactRole Role { get; set; }

    public required string Name { get; set; }

    public string? Email { get; set; }

    /// <summary>
    /// A phone number matters more than it looks: at 03:00 a jourhavande needs to reach a
    /// person, and §14.3 requires the supplier to make contact over Teams for P1 and P2.
    /// </summary>
    public string? Phone { get; set; }

    public string? TeamsHandle { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
    public App? App { get; set; }
}
