namespace EntKube.Web.Data;

/// <summary>
/// Bilaga A for one application — the terms on which it is under förvaltning.
///
/// <para>EntKube knows an application's deployments, routes and secrets. This is what it is
/// <em>owed</em>: which part of the agreement governs it, when the SLA started, whether a
/// guarantee period is still running, and what it is billed for.</para>
///
/// <para><b>Förvaltningsnivå and supportfönster are deliberately not columns here.</b> §4.1
/// lets the supplier demand reclassification, §16.2 reviews the level and the window every
/// quarter, and the kännedomsavgift follows both. A column that is overwritten cannot
/// explain a past invoice, so the classification lives in <see cref="ApplicationServiceLevel"/>
/// as a dated series and is resolved as of a date. Same reasoning as
/// <see cref="CostLedgerEntry"/>: a record of what was agreed, not a view that re-derives
/// the past from the present.</para>
/// </summary>
public class ApplicationContract
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The application these terms govern. One contract per application.</summary>
    public Guid AppId { get; set; }

    /// <summary>Del 2 or Del 3 — see <see cref="ContractOrigin"/>.</summary>
    public ContractOrigin Origin { get; set; }

    /// <summary>
    /// Who built it, for a Del 2 application. Recorded because §21.3 excludes the supplier
    /// from liability for faults that predate on-boarding, and that defence needs to say
    /// whose faults they were.
    /// </summary>
    public string? DevelopedBy { get; set; }

    /// <summary>
    /// When the application came under management: the signed startprotokoll for Del 2
    /// (fas 5), or delivery acceptance for Del 3.
    /// </summary>
    public DateTime? OnboardedAt { get; set; }

    /// <summary>
    /// End of the three-month guarantee period of §7. Del 3 only, and null once it lapses
    /// has no meaning — the date stays so an old ticket can be read in its own context.
    /// </summary>
    public DateTime? GuaranteeEndsAt { get; set; }

    /// <summary>
    /// The moderapplikation this is an instance of (§10.2.1). Set only at level
    /// <see cref="ManagementLevel.Instance"/>, and it is what makes the reduced fee from the
    /// twenty-first instance, the inherited support window, and "one incident across many
    /// instances is one ticket" all fall out of the data rather than out of a convention.
    /// </summary>
    public Guid? ParentAppId { get; set; }

    /// <summary>
    /// When SLA times begin to apply — the signed startprotokoll (§4.1, §8). Until this is
    /// set, §4.1 says tickets are handled på bästa förmåga with no guaranteed response, so
    /// nothing should be reported as a breach before it.
    /// </summary>
    public DateTime? SlaStartsAt { get; set; }

    /// <summary>
    /// When the application left förvaltning under §19. Kept rather than deleted: the
    /// months it was managed still have to be explicable.
    /// </summary>
    public DateTime? ManagementEndedAt { get; set; }

    /// <summary>
    /// The monthly ceiling on billable work the customer may set per application under §12.
    /// Modell B only; work above it needs written approval, except for P1.
    /// </summary>
    public decimal? MonthlyWorkCapHours { get; set; }

    /// <summary>The agreed on-boarding fee (§5.1, Bilaga C table C.5). Del 2 only.</summary>
    public decimal? OnboardingFee { get; set; }

    /// <summary>Kritikalitet 1–5 as recorded in Bilaga A.1.</summary>
    public int? Criticality { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
    public App? ParentApp { get; set; }
    public List<ApplicationServiceLevel> ServiceLevels { get; set; } = [];
}
