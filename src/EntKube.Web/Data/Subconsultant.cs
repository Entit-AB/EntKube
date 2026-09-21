namespace EntKube.Web.Data;

/// <summary>
/// Somebody outside our own payroll who works under the agreement — the §18 register.
///
/// <para>§18 lets us use subconsultants for on-call duty, incident handling, bugfixes,
/// deployments and development, and makes us answerable for their work as for our own. It
/// also attaches three conditions worth holding as data rather than as a folder of PDFs:
/// they must be bound by confidentiality and data-processing terms at least equal to §17
/// <em>before</em> they touch the customer's environments, they must be notified to the
/// customer for approval, and we must keep a current list available on request.</para>
///
/// <para>Approval is tacit: §18 gives the customer ten working days to object, after which
/// the subconsultant counts as approved. That deadline is a working-day calculation, which
/// is why the business calendar exists.</para>
/// </summary>
public class Subconsultant
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Company { get; set; }

    public string? OrganisationNumber { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>
    /// The customer this one has been notified to. §18's approval is per customer, since it
    /// is their environments and their data.
    /// </summary>
    public Guid? CustomerId { get; set; }

    /// <summary>When we notified the customer in writing. The ten working days run from here.</summary>
    public DateTime? NotifiedAt { get; set; }

    /// <summary>When the customer approved explicitly, if they did rather than letting it lapse.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>When the customer objected. §18 says approval may not be unreasonably withheld.</summary>
    public DateTime? ObjectedAt { get; set; }

    public string? ObjectionReason { get; set; }

    /// <summary>
    /// When they signed the individual confidentiality undertaking §24 requires before
    /// access is given.
    /// </summary>
    public DateTime? ConfidentialitySignedAt { get; set; }

    /// <summary>
    /// When they became bound by data-processing terms at least equal to §17 — the other
    /// condition §18 puts before access.
    /// </summary>
    public DateTime? DataProcessingBoundAt { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer? Customer { get; set; }
}
