namespace EntKube.Web.Data;

/// <summary>
/// The sections an application's knowledge base is kept in. A fixed set rather than free-form
/// pages, because §10.2 charges for particular things being current and a wiki with no
/// shape cannot say whether they are.
/// </summary>
public enum KnowledgeSectionKind
{
    /// <summary>How it is built and why. The narrative a new on-call engineer reads first.</summary>
    Architecture = 0,

    /// <summary>
    /// Runbook — start, stop, deploy, restore, and the things that break. Named as a
    /// deliverable of on-boarding fas 4 and again at off-boarding in §19.
    /// </summary>
    Runbook = 1,

    /// <summary>
    /// Why it looks like this. Cheap to write at the time and impossible to reconstruct two
    /// years later when the person who knew has left.
    /// </summary>
    Decisions = 2,

    /// <summary>
    /// Known defects and technical debt. §4.1 requires these in the fas-1 report, and
    /// §21.3 excludes us from liability for faults that predate on-boarding — but only the
    /// ones that were written down.
    /// </summary>
    KnownDefects = 3,

    /// <summary>How to get in: environments, accounts, what needs a JIT grant.</summary>
    Access = 4,

    /// <summary>Backup and restore: what is taken, how to test it, who owns it (§23).</summary>
    Recovery = 5,

    /// <summary>Anything that does not fit the others.</summary>
    Other = 6,
}

/// <summary>
/// One section of what we know about an application.
///
/// <para><b>This is what the knowledge fee buys.</b> §10.2 charges per application for
/// keeping monitoring, alarms and documentation current and the support team insatt in it.
/// EntKube knew an application's deployments, routes and secrets; it knew nothing about the
/// application. This is the other half, and it is the half that makes a on-call engineer who has
/// never seen it useful at 03:00 — which §10.1.1 requires before S3 or S4 can start.</para>
///
/// <para>Kept as markdown with a revision history, because a runbook that changed and
/// cannot say when or why is not evidence of anything at the next quarterly review.</para>
/// </summary>
public class KnowledgeSection
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AppId { get; set; }

    public KnowledgeSectionKind Kind { get; set; }

    public required string Title { get; set; }

    /// <summary>The content, as markdown.</summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// When someone last confirmed this is still true — which is not the same as when it
    /// was last edited. A fixed typo is not a review.
    /// </summary>
    public DateTime? ReviewedAt { get; set; }

    public string? ReviewedBy { get; set; }

    /// <summary>
    /// How long this section may go unreviewed before it counts as stale. Ninety days by
    /// default: a quarter, so a review falls naturally into the §16.2 quarterly meeting.
    /// </summary>
    public int ReviewIntervalDays { get; set; } = 90;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
    public List<KnowledgeRevision> Revisions { get; set; } = [];
}

/// <summary>
/// A previous version of a section, kept so a change can be seen rather than inferred.
/// </summary>
public class KnowledgeRevision
{
    public Guid Id { get; set; }

    public Guid SectionId { get; set; }

    /// <summary>The body as it stood before the edit that created this revision.</summary>
    public string Body { get; set; } = "";

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public string? SavedBy { get; set; }

    /// <summary>What changed, in a line.</summary>
    public string? Summary { get; set; }

    // Navigation
    public KnowledgeSection Section { get; set; } = null!;
}

/// <summary>How sensitive an application's data is, in GDPR terms.</summary>
public enum DataClassification
{
    /// <summary>No personal data at all.</summary>
    None = 0,

    /// <summary>Ordinary personal data under the GDPR.</summary>
    Personal = 1,

    /// <summary>Article 9 data — health among it. §17.2 makes the legal basis the customer's.</summary>
    SensitivePersonal = 2,
}

/// <summary>Who is responsible for backing an application's data up and testing restores.</summary>
public enum BackupResponsibility
{
    /// <summary>§23's default: the customer, unless Annex A says otherwise.</summary>
    Customer = 0,

    /// <summary>Agreed as ours in Annex A.</summary>
    Supplier = 1,

    /// <summary>A platform or hosting provider's, and named in the dependencies.</summary>
    ThirdParty = 2,
}

/// <summary>
/// The classification an application carries that is neither a contract term nor a
/// deployment fact: what data it holds, which rules apply to it, and who backs it up.
///
/// <para>§23 makes assessing regulatory scope — patientdatalagen, NIS2, MDR — the
/// customer's responsibility, and says we are not the tillverkare or regulatoriskt ansvarig
/// unless Annex A says so for a named application. Recording their assessment is how that
/// stays a documented position rather than an assumption nobody wrote down.</para>
/// </summary>
public class AppKnowledgeProfile
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid AppId { get; set; }

    public DataClassification DataClassification { get; set; }

    /// <summary>
    /// Whether it touches patient records. §24 limits access to such systems to what the
    /// assignment needs, personally and logged — so it has to be known which they are.
    /// </summary>
    public bool HandlesPatientData { get; set; }

    /// <summary>
    /// The regulatory regimes the customer has assessed as applying (§23). Free text: the
    /// list is theirs to write, and a fixed enum would quietly exclude whatever is next.
    /// </summary>
    public string? RegulatoryScope { get; set; }

    /// <summary>Who owns backup and restore testing (§23).</summary>
    public BackupResponsibility BackupResponsibility { get; set; } = BackupResponsibility.Customer;

    /// <summary>The business process this application serves, in a sentence.</summary>
    public string? BusinessPurpose { get; set; }

    /// <summary>What happens to the customer's operation when it is down.</summary>
    public string? ImpactWhenDown { get; set; }

    public string? Notes { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public App App { get; set; } = null!;
}
