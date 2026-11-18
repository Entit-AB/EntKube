namespace EntKube.Web.Data;

/// <summary>
/// A folder of help answers — Zammad's knowledge-base category. Nested by <see cref="ParentId"/>;
/// moving a category moves everything under it, and only an empty one can be deleted.
///
/// <para>Not the per-application runbook (<see cref="KnowledgeSection"/>), which is our own
/// operating knowledge and never leaves us. This is the answer library customers can read.</para>
/// </summary>
public class KbCategory
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid? ParentId { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Who may read an answer.</summary>
public enum KbVisibility
{
    /// <summary>Being written; only agents see it, marked as a draft.</summary>
    Draft = 0,

    /// <summary>For agents only — offered when writing to customers, never shown to them.</summary>
    Internal = 1,

    /// <summary>Customers see it in the portal, from <see cref="KbAnswer.PublishAt"/> when that is set.</summary>
    Public = 2,

    /// <summary>Retired: kept, shown to nobody but the knowledge base's own list.</summary>
    Archived = 3,
}

/// <summary>
/// One help answer (docs/helpdesk.md, phase 5). Written in one language of the author's choosing
/// (decision 2), carried in <see cref="Language"/>; there are no translation sets.
///
/// <para>The body is rich text, sanitised by <c>RichText</c> when saved and again when shown;
/// <see cref="BodyText"/> is the same content as text, for search.</para>
/// </summary>
public class KbAnswer
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CategoryId { get; set; }

    public required string Title { get; set; }

    public string BodyHtml { get; set; } = "";

    public string BodyText { get; set; } = "";

    /// <summary>A language tag such as <c>sv</c> or <c>en</c>.</summary>
    public string Language { get; set; } = "sv";

    public KbVisibility Visibility { get; set; }

    /// <summary>For a public answer: not shown to customers before this. Null is "now".</summary>
    public DateTime? PublishAt { get; set; }

    /// <summary>Shown only to this customer's portal, or to every customer of the tenant when null.</summary>
    public Guid? CustomerId { get; set; }

    /// <summary>Words it is found by besides its title and body.</summary>
    public string Keywords { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }

    // Navigation
    public KbCategory Category { get; set; } = null!;
}

/// <summary>
/// Two help answers that belong together (helpdesk phase 10) — Zammad's linked answers. Stored
/// once per pair, the lower id first, and shown on both. A customer sees a related answer only if
/// they could open it themselves.
/// </summary>
public class KbAnswerLink
{
    public Guid AnswerId { get; set; }

    public Guid RelatedId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// A help answer used on a ticket (helpdesk phase 10): recorded when an agent inserts it with
/// <c>??</c>, so an answer shows where it has helped and a ticket which answers it was given.
/// Agents only.
/// </summary>
public class KbAnswerTicket
{
    public Guid AnswerId { get; set; }

    public Guid TicketId { get; set; }

    public Guid TenantId { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;

    public string? LinkedBy { get; set; }
}
