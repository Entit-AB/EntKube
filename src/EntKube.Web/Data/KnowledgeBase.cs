namespace EntKube.Web.Data;

/// <summary>
/// A folder of help answers — Zammad's knowledge-base category. Nested by <see cref="ParentId"/>;
/// moving a category moves everything under it, and only an empty one can be deleted.
///
/// <para>One knowledge base spans everything (helpdesk phase 11): help answers, and each
/// application's runbook as <see cref="KbVisibility.Operations"/> answers tied to it by
/// <see cref="KbAnswer.AppId"/>. The legacy per-application sections
/// (<see cref="KnowledgeSection"/>) are moved in at startup.</para>
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

    /// <summary>
    /// For agents — and offered by <c>??</c> when writing to customers, so its text may be inserted
    /// into a reply. Never shown in the portal. Not for operating knowledge: that is
    /// <see cref="Operations"/>.
    /// </summary>
    Internal = 1,

    /// <summary>Customers see it in the portal, from <see cref="KbAnswer.PublishAt"/> when that is set.</summary>
    Public = 2,

    /// <summary>Retired: kept, shown to nobody but the knowledge base's own list.</summary>
    Archived = 3,

    /// <summary>
    /// Our own operating knowledge (helpdesk phase 11) — an application's runbook, access notes,
    /// known defects. Agents read it in the knowledge base, quick search and an application's
    /// Knowledge button. <c>??</c> never offers it, in a reply or a note, and the portal never
    /// shows it. An application's answers can only be this or <see cref="Archived"/>, which a
    /// database CHECK constraint enforces. Appended last: stored as its number.
    /// </summary>
    Operations = 4,
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

    /// <summary>
    /// The application this answer is about — an answer moved from, or written on, the app's
    /// Knowledge button (helpdesk phase 11). Set only on creation; the stored value decides on
    /// every later save. An app's answer is agents-only (Operations or Archived), carries its
    /// app's customer, and never a publish date — a CHECK constraint holds all four.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>Which part of an application's knowledge this is. Set exactly when <see cref="AppId"/> is.</summary>
    public KnowledgeSectionKind? Section { get; set; }

    /// <summary>When somebody last confirmed it still holds — "Still true". Not moved by an edit.</summary>
    public DateTime? ReviewedAt { get; set; }

    public string? ReviewedBy { get; set; }

    // Navigation
    public KbCategory Category { get; set; } = null!;
}

/// <summary>How a stored body is written.</summary>
public enum KbBodyFormat
{
    /// <summary>Sanitised HTML, as every answer is now.</summary>
    Html = 0,

    /// <summary>Markdown, as an application's knowledge section was before it moved in. Kept verbatim.</summary>
    Markdown = 1,
}

/// <summary>
/// What an answer said before an edit (helpdesk phase 11) — the history the runbook sections had,
/// now for every answer. A section's own history moves in with its ids and its markdown intact.
/// </summary>
public class KbAnswerRevision
{
    public Guid Id { get; set; }

    public Guid AnswerId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The title before the edit; null on a moved section's revisions, which never recorded one.</summary>
    public string? Title { get; set; }

    /// <summary>The body BEFORE the edit.</summary>
    public string Body { get; set; } = "";

    public KbBodyFormat BodyFormat { get; set; }

    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public string? SavedBy { get; set; }

    public string? Summary { get; set; }
}

/// <summary>How a knowledge section's move into the knowledge base went.</summary>
public enum MoveVerdict
{
    /// <summary>Converted with nothing to note.</summary>
    Exact = 0,

    /// <summary>Converted; some constructs were mapped (listed in the notes).</summary>
    Mapped = 1,

    /// <summary>Could not be converted faithfully; the markdown is shown as preformatted text.</summary>
    Fallback = 2,

    /// <summary>The section was changed on an older EntKube after it moved; the change is in the answer's history.</summary>
    Diverged = 3,

    /// <summary>Its application is not its tenant's; kept as an archived tenant-wide answer.</summary>
    Detached = 4,
}

/// <summary>
/// The move ledger and its claim (helpdesk phase 11): one row per legacy knowledge section moved
/// into the knowledge base. A section is pending exactly when it has no row here — not when its
/// answer is missing, so an answer deleted after the move is never brought back. The Legacy*
/// values detect a section changed on an older EntKube after the move.
/// </summary>
public class KnowledgeSectionMove
{
    public Guid SectionId { get; set; }

    public Guid TenantId { get; set; }

    public Guid AppId { get; set; }

    public Guid AnswerId { get; set; }

    public DateTime MovedAt { get; set; } = DateTime.UtcNow;

    public MoveVerdict Verdict { get; set; }

    public string? Notes { get; set; }

    public DateTime LegacyUpdatedAt { get; set; }

    public DateTime? LegacyReviewedAt { get; set; }

    public string? LegacyReviewedBy { get; set; }

    public int LegacyRevisionCount { get; set; }
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
