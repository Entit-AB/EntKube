using System.ComponentModel.DataAnnotations.Schema;

namespace EntKube.Web.Data;

/// <summary>What the person reporting says this is.</summary>
public enum FeedbackKind
{
    Bug = 0,
    Idea = 1,
    Question = 2,
    Praise = 3,
}

/// <summary>Where an item has got to in triage.</summary>
public enum FeedbackStatus
{
    /// <summary>Nobody has looked at it.</summary>
    New = 0,

    /// <summary>Read and kept: it became a ticket, or is being acted on some other way.</summary>
    Triaged = 1,

    /// <summary>Dealt with.</summary>
    Resolved = 2,

    /// <summary>Noise, spam or a duplicate.</summary>
    Dismissed = 3,
}

/// <summary>
/// One submission from the in-app feedback widget.
///
/// <para><b>Why this is not a ticket.</b> The widget posts to a public endpoint whose key ships in
/// client code, and a ticket starts contractual §14 clocks the moment it exists. So the widget
/// writes here, into a triage inbox, and a person promotes what deserves it — see
/// <c>FeedbackTicketBridge</c>.</para>
///
/// <para><b>Soft references.</b> <see cref="RumSiteId"/>, <see cref="AppId"/> and
/// <see cref="TicketId"/> carry no foreign key. Deleting a RUM site is a one-click action that
/// loses no RUM data, and it must not lose what customers told us either, so the site's name and
/// app are stamped on the row at submission. Tenant purge deletes these by <see cref="TenantId"/>.</para>
///
/// <para>⚠️ Everything except <see cref="TenantId"/>, <see cref="RumSiteId"/> and
/// <see cref="AppId"/> is written by an anonymous browser. Treat it as untrusted, including
/// <see cref="ContactEmail"/>, which proves nothing about who submitted it.</para>
/// </summary>
public class FeedbackItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid RumSiteId { get; set; }

    /// <summary>The site's name when this arrived, so the row outlives the site.</summary>
    public string SiteName { get; set; } = "";

    /// <summary>The site's owning application when this arrived. Null = no customer to bill it to.</summary>
    public Guid? AppId { get; set; }

    public FeedbackKind Kind { get; set; }
    public FeedbackStatus Status { get; set; } = FeedbackStatus.New;

    /// <summary>1–5, or null when the person did not rate anything.</summary>
    public int? Rating { get; set; }

    public string Message { get; set; } = "";

    /// <summary>An address the person typed. Unverified: never send mail to it automatically.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>The URL path only. Query strings and fragments are where tokens live.</summary>
    public string PagePath { get; set; } = "";

    public string? Browser { get; set; }
    public string? Os { get; set; }
    public string? Device { get; set; }
    public int? ViewportWidth { get; set; }
    public int? ViewportHeight { get; set; }

    /// <summary>The page's RUM session when the RUM snippet runs beside the widget.</summary>
    public string? RumSessionId { get; set; }

    /// <summary>The last few uncaught JS errors on the page, newline-separated.</summary>
    public string? RecentErrors { get; set; }

    /// <summary>The ticket this was promoted to, if any.</summary>
    public Guid? TicketId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Who last changed <see cref="Status"/>.</summary>
    public string? TriagedBy { get; set; }

    /// <summary>The errors as a list; empty when there were none.</summary>
    [NotMapped]
    public IReadOnlyList<string> Errors =>
        (RecentErrors ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The screenshot for a <see cref="FeedbackItem"/>, in its own table so that listing the inbox
/// never reads an image. Kept for a shorter time than the item, because a screenshot shows
/// whatever was on the person's screen.
/// </summary>
public class FeedbackScreenshot
{
    public Guid FeedbackItemId { get; set; }

    /// <summary>image/png or image/jpeg, decided from the bytes and never from the client's claim.</summary>
    public string ContentType { get; set; } = "";

    public byte[] Bytes { get; set; } = [];
}
