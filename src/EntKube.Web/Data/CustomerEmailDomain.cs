namespace EntKube.Web.Data;

/// <summary>
/// A mail domain that belongs to a customer, so support mail from an address nobody has
/// registered individually can still be placed.
///
/// <para><b>Why this is a register and not a guess.</b> Matching a sender by taking the
/// part after the @ and looking for something similar is how one customer's mail ends up
/// on another's tickets. This is the opposite: somebody states that mail from this domain
/// is that customer's, and the statement is what the matcher reads. The §23 contact
/// register does the same job one address at a time; this is for the eighty other people
/// at the customer who write in once.</para>
///
/// <para>Placing the customer is what makes everything after it possible — the analyst can
/// only recognise an application name among <em>that customer's</em> applications, so an
/// unplaced message cannot be placed on an app either.</para>
/// </summary>
public class CustomerEmailDomain
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>
    /// The domain, lower-cased and without the @ — "capio.se".
    ///
    /// <para>Matches that domain exactly and any subdomain of it, so "capio.se" also
    /// places mail from "it.capio.se". The boundary is a dot, which is what stops
    /// "notcapio.se" matching.</para>
    /// </summary>
    public required string Domain { get; set; }

    /// <summary>Why it was added, or who confirmed it. Free text.</summary>
    public string? Notes { get; set; }

    public string? AddedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}
