namespace EntKube.Web.Data;

/// <summary>What sort of thing an application depends on.</summary>
public enum DependencyKind
{
    /// <summary>Another system, usually the customer's own.</summary>
    System = 0,

    /// <summary>A supplier providing a service: hosting, a cloud, a platform.</summary>
    Supplier = 1,

    /// <summary>An integration with a third party's API.</summary>
    Integration = 2,

    /// <summary>Infrastructure: a network, an identity provider, a certificate authority.</summary>
    Infrastructure = 3,
}

/// <summary>
/// Something outside the application that it needs in order to work, with the hours that
/// party can actually be reached.
///
/// <para><b>Not the same thing as <see cref="ExternalDependency"/>.</b> That one models
/// egress — FQDN and port — so a deny-all NetworkPolicy does not silently break an outbound
/// call. This is the contractual version Annex A.1 asks for: system, supplier,
/// support hours and contact route.</para>
///
/// <para><b>Why the hours matter.</b> §14.4 lets the resolution clock pause while we wait
/// on one of these parties, explicitly including when they are only reachable during office
/// hours — and §23 warns that choosing S2–S4 while a dependency is office-hours-only means
/// no guaranteed resolution time. Recording the hours is what turns both of those from an
/// argument into a fact that was written down before it mattered.</para>
/// </summary>
public class AppServiceDependency
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AppId { get; set; }

    public required string Name { get; set; }

    public DependencyKind Kind { get; set; }

    /// <summary>Who runs it.</summary>
    public string? Supplier { get; set; }

    /// <summary>When they can be reached, as agreed with them — free text, e.g. "weekdays 08–17".</summary>
    public string? SupportHours { get; set; }

    /// <summary>
    /// Whether that is office hours only. The flag rather than parsing the text, because
    /// this is what §23's warning turns on and a warning that depends on a regex is a
    /// warning that will be wrong.
    /// </summary>
    public bool OfficeHoursOnly { get; set; }

    /// <summary>How to reach them: a portal, a number, a mailbox.</summary>
    public string? ContactRoute { get; set; }

    /// <summary>
    /// Whether the application stops working without it. A dependency that only degrades
    /// something is worth recording but does not hold up a P1.
    /// </summary>
    public bool CriticalPath { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
}

/// <summary>
/// A component that has reached, or will reach, the end of its supplier's support, and
/// what happened after we said so.
///
/// <para>§14.7: once we have given written notice with a proposed upgrade, the customer has
/// three months to order it. If they do not, we may exempt the application from the §14.4
/// SLA times and from liability for security faults traceable to that component — while the
/// base fee carries on unchanged. All of which depends on being able to show the notice
/// and its date, which is what this row is.</para>
/// </summary>
public class EndOfLifeNotice
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AppId { get; set; }

    /// <summary>The framework, runtime, database or library in question.</summary>
    public required string Component { get; set; }

    public string? CurrentVersion { get; set; }

    /// <summary>When its supplier's support ends, if that is known.</summary>
    public DateTime? EndOfLifeOn { get; set; }

    /// <summary>What we proposed instead.</summary>
    public string? ProposedUpgrade { get; set; }

    /// <summary>
    /// When the written notice §14.7 requires was given. The three months run from here.
    /// </summary>
    public DateTime? NoticeGivenAt { get; set; }

    public string? NoticeGivenBy { get; set; }

    /// <summary>When the customer ordered the upgrade, if they have.</summary>
    public DateTime? UpgradeOrderedAt { get; set; }

    /// <summary>When it was actually done, which ends the exemption.</summary>
    public DateTime? UpgradedAt { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
}
