namespace EntKube.Web.Data;

/// <summary>What a phrase in an inbound message is taken to signal.</summary>
public enum MailSignal
{
    /// <summary>
    /// A §14.2 priority band. The rule carries the priority it proposes and the criterion
    /// shown to whoever confirms it.
    /// </summary>
    Priority = 0,

    /// <summary>§15 new development rather than management.</summary>
    Development = 1,

    /// <summary>A third party whose involvement may let §14.4 pause the resolution clock.</summary>
    ThirdParty = 2,
}

/// <summary>
/// One phrase the support mailbox watches for, and what it is taken to mean.
///
/// <para><b>Configuration, not code.</b> The phrases are whatever the customers actually
/// write, which nobody can know in advance and which changes as a portfolio changes. They
/// are also necessarily in the customer's language — a Swedish customer reports that
/// something "fungerar inte", and a table of English phrases would recognise none of it.
/// Holding them as rows lets the people who read the mail every day tune them, without a
/// deployment and without anyone arguing about what language the codebase is in.</para>
///
/// <para>The <see cref="Criterion"/> is the other half and is always written for the
/// operator: it says which part of §14.2 the phrase is evidence of, so the person
/// confirming a priority under §14.3 can see the reasoning rather than a verdict.</para>
/// </summary>
public class MailTriageRule
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public MailSignal Signal { get; set; }

    /// <summary>
    /// The phrase to look for, matched case-insensitively anywhere in the subject or body.
    /// In the customer's language, whatever that is.
    /// </summary>
    public required string Phrase { get; set; }

    /// <summary>
    /// The priority this phrase proposes. Set only for <see cref="MailSignal.Priority"/>.
    /// </summary>
    public TicketPriority? Priority { get; set; }

    /// <summary>
    /// What the phrase is evidence of, in the operator's words — "login impossible for all
    /// users", not the phrase back again. Shown beside the proposal so it can be argued
    /// with.
    /// </summary>
    public string? Criterion { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Match order within a signal. The first match wins, so a narrow phrase has to be able
    /// to sit above a broad one.
    /// </summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
}
