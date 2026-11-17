namespace EntKube.Web.Data;

/// <summary>The issue trackers a ticket can link to (docs/helpdesk.md, phase 6).</summary>
public enum IssueTrackerKind
{
    /// <summary>GitHub.com or GitHub Enterprise Server.</summary>
    GitHub = 0,

    /// <summary>GitLab.com or a self-managed GitLab.</summary>
    GitLab = 1,
}

/// <summary>
/// A GitHub or GitLab the support team's tickets may link issues on — Zammad's GitHub and
/// GitLab integrations.
///
/// <para><b>Read-only, and only where configured.</b> EntKube reads an issue's title, state,
/// labels and assignees to show beside the ticket; it never writes to the tracker. And it only
/// ever calls a host somebody configured here: a pasted link to any other host is refused, so a
/// ticket can never be made to send the token, or a request, somewhere nobody chose.</para>
///
/// <para><b>The token is not here.</b> It lives in the tenant's vault as a secret owned by this
/// row, like the support mailbox's password, and goes with it when the row is deleted.</para>
///
/// <para>Tenant as a column, like the rest of the helpdesk tables: the tenant purge deletes these
/// itself, so no second cascade path to the vault's secrets is created.</para>
/// </summary>
public class IssueTracker
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public IssueTrackerKind Kind { get; set; }

    /// <summary>
    /// The host issue links are on — <c>github.com</c>, <c>gitlab.com</c>, <c>git.example.se</c>.
    /// Lower case, no scheme, no path. One tracker per host per tenant.
    /// </summary>
    public required string Host { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }
}

/// <summary>
/// An issue linked to a ticket. The link is what is stored; everything shown about the issue is
/// read live, so a closed issue shows as closed without anybody updating the ticket.
///
/// <para>Agents only. Never shown in the portal, never in mail to the customer, never in the
/// public API's ticket — an engineering issue can say things about a customer's system that the
/// customer was not meant to read.</para>
/// </summary>
public class TicketIssueLink
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TicketId { get; set; }

    public IssueTrackerKind Kind { get; set; }

    /// <summary>The issue's web address, as pasted and normalised.</summary>
    public required string Url { get; set; }

    /// <summary>What a person would quote: <c>acme/portal#12</c>.</summary>
    public required string Reference { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string? AddedBy { get; set; }

    public Ticket Ticket { get; set; } = null!;
}

/// <summary>The highlighter's colours (Zammad offers five).</summary>
public enum HighlightColor
{
    Yellow = 0,
    Green = 1,
    Blue = 2,
    Pink = 3,
    Purple = 4,
}

/// <summary>
/// A passage an agent marked in a ticket entry — Zammad's highlighter.
///
/// <para><b>For agents only, and outside the record.</b> A highlight is kept beside the entry,
/// never in it: §14.6 makes the history the record between the parties and entries are never
/// edited, so marking a passage must not change what the entry says. The customer never sees
/// highlights.</para>
///
/// <para>The passage is stored as its text, not as offsets into markup: the entry is shown
/// sanitised, and a sanitiser change must not move a highlight onto different words. The page
/// marks the first place the text occurs, without touching the page's markup.</para>
/// </summary>
public class TicketHighlight
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EventId { get; set; }

    /// <summary>The marked passage, as the agent selected it. At most 1000 characters.</summary>
    public required string Text { get; set; }

    public HighlightColor Color { get; set; }

    public string? By { get; set; }

    public DateTime At { get; set; } = DateTime.UtcNow;

    public TicketEvent Event { get; set; } = null!;
}
