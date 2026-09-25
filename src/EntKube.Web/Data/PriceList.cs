namespace EntKube.Web.Data;

/// <summary>
/// Annex C as it stood from a given date — every fee and hourly rate that applies.
///
/// <para><b>Versioned, never edited.</b> §19 indexes prices annually on 1 January against
/// SCB's arbetskostnadsindex with a floor of 2%, and §13 moves every time category and
/// hour bank price with the ordinary rate from the same date. Annex C says it may be
/// replaced by a new signed version without otherwise changing the agreement. A statement
/// covering March must price at March's list, so a new list is a new row and the old one
/// stays exactly as it was.</para>
///
/// <para>A tenant-wide list with <see cref="CustomerId"/> null is the standard price list;
/// a row for a customer overrides it for that customer.</para>
/// </summary>
public class PriceList
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The customer this list is negotiated for, or null for the standard list.</summary>
    public Guid? CustomerId { get; set; }

    public DateTime EffectiveFrom { get; set; }

    /// <summary>
    /// §20 invoices in SEK; a EUR invoice is converted at the Riksbank mid-rate on the
    /// invoice date. Recorded so a list in another currency cannot be mistaken for SEK.
    /// </summary>
    public string Currency { get; set; } = "SEK";

    /// <summary>What changed and why — usually the indexation notice this list implements.</summary>
    public string? Notes { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Customer? Customer { get; set; }
    public List<PriceListEntry> Entries { get; set; } = [];
}

/// <summary>
/// One amount in a price list.
///
/// <para><b>Rows rather than columns, on purpose.</b> Annex C's shape is not fixed: the
/// hour bank tiers are whatever was negotiated, the one-off fees grow as services are added,
/// and §10.2.1 already carries two knowledge fees for the same level depending on how
/// many instances there are. Twenty-odd nullable columns would model one version of one
/// annex and break on the next. <see cref="Key"/> is therefore a string, interpreted
/// according to <see cref="Kind"/> — the enum name for a window, level or time category,
/// and a short stable name for the one-off fees.</para>
/// </summary>
public class PriceListEntry
{
    public Guid Id { get; set; }

    public Guid PriceListId { get; set; }

    public PriceKind Kind { get; set; }

    /// <summary>
    /// What within the kind this amount is for. For <see cref="PriceKind.WindowFee"/> the
    /// <see cref="SupportWindow"/> name, for <see cref="PriceKind.KnowledgeFee"/> the
    /// <see cref="ManagementLevel"/> name (plus <c>InstanceReduced</c> for §10.2.1's
    /// twenty-first instance onwards), for <see cref="PriceKind.HourlyRate"/> the
    /// <see cref="SupportTimeCategory"/> name (plus <c>Development</c> for §15), for
    /// <see cref="PriceKind.HourBankTier"/> the tier's hours, and for
    /// <see cref="PriceKind.OneOffFee"/> a name such as <c>OnboardingComplex</c>.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>The amount, in the list's currency, excluding VAT.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// The hours a hour bank tier buys. Set for <see cref="PriceKind.HourBankTier"/> so the
    /// tier can be compared numerically rather than by parsing <see cref="Key"/>.
    /// </summary>
    public decimal? Hours { get; set; }

    public int SortOrder { get; set; }

    // Navigation
    public PriceList PriceList { get; set; } = null!;
}
