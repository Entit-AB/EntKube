namespace EntKube.Web.Data;

/// <summary>
/// Where a tenant's support attachments are kept: one of its own S3 storage links. The bucket
/// is the link's, and the credentials stay in the vault where the link already keeps them.
///
/// <para>No foreign keys — the tenant is a key column and the link belongs to another module —
/// so the tenant purge deletes the row itself, and the link is checked to be the tenant's every
/// time it is used rather than trusted from here.</para>
/// </summary>
public class SupportAttachmentSetting
{
    public Guid TenantId { get; set; }

    public Guid StorageLinkId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string? UpdatedBy { get; set; }
}

/// <summary>Where an attachment came from.</summary>
public enum AttachmentSource
{
    /// <summary>Attached to a mail the support mailbox took in.</summary>
    Mail = 0,

    /// <summary>Added by an agent to a note.</summary>
    Agent = 1,
}

/// <summary>
/// A file on a ticket, kept in the tenant's S3 bucket (docs/helpdesk.md, decision 4).
///
/// <para><b>A row exists even when the file could not be kept</b> — no storage configured, too
/// large, the upload failed. <see cref="Stored"/> is then false and <see cref="Problem"/> says
/// why, and the ticket shows it. An attachment the customer sent that silently never appeared
/// would be the worst outcome here: they would believe we had it.</para>
///
/// <para>The object key is tenant-prefixed and carries no part of the file name, so nothing the
/// sender chose ends up in a path. Downloads go through EntKube with a tenant check; nobody is
/// handed a link to the bucket.</para>
/// </summary>
public class TicketAttachment
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The ticket, for an attachment added to it directly. Null for one that came with a mail.</summary>
    public Guid? TicketId { get; set; }

    /// <summary>The history entry it belongs to, when there is one (a note it was attached to).</summary>
    public Guid? TicketEventId { get; set; }

    /// <summary>The mail it came with; it reaches a ticket when that mail is filed on one.</summary>
    public Guid? InboundMailMessageId { get; set; }

    public AttachmentSource Source { get; set; }

    public required string FileName { get; set; }

    public string ContentType { get; set; } = "application/octet-stream";

    public long Size { get; set; }

    public bool Stored { get; set; }

    /// <summary>Why it was not kept, when it was not.</summary>
    public string? Problem { get; set; }

    public Guid? StorageLinkId { get; set; }

    public string? ObjectKey { get; set; }

    /// <summary>Whether the customer sees it — true for what they sent, chosen for what we add.</summary>
    public bool CustomerVisible { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    // Navigation
    public Ticket? Ticket { get; set; }
}
