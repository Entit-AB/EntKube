namespace EntKube.Web.Data;

/// <summary>
/// Annex B — the choices that apply to a customer's whole portfolio rather than to one
/// application: the pricing model and, under Model A, the size of the monthly hour bank.
///
/// <para>Dated for the same reason the service level is. §2 lets the customer switch
/// pricing model at a calendar-quarter boundary and no sooner than three months after the
/// last switch; §11.1 lets the hour bank change size at a quarter boundary on one month's
/// notice. Both changes are prospective, so the agreement in force on a given date is the
/// latest one that had taken effect by then.</para>
///
/// <para>The window fee is deliberately absent: §10.1 derives it from the most extensive
/// window across the portfolio's applications, so storing it here would let the two
/// disagree. It is computed from the application service levels instead.</para>
/// </summary>
public class PortfolioAgreement
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    /// <summary>The date these terms took effect.</summary>
    public DateTime EffectiveFrom { get; set; }

    public PricingModel PricingModel { get; set; }

    /// <summary>
    /// Hours bought per calendar month under Model A. Null under Model B.
    ///
    /// <para>§11.1 makes the bank non-rolling: unused hours expire at month end and cannot
    /// be saved, transferred, offset or refunded. So this is the monthly entitlement, never
    /// a running balance — the balance is derived from the month's time entries.</para>
    /// </summary>
    public decimal? HourBankHoursPerMonth { get; set; }

    public string? Notes { get; set; }

    public string? RecordedBy { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer Customer { get; set; } = null!;
}
