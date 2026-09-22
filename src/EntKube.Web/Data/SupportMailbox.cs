namespace EntKube.Web.Data;

/// <summary>What the poller does with a message once it has been taken in.</summary>
public enum MailboxDisposition
{
    /// <summary>
    /// Mark it read in the mailbox. The default: it makes visible progress, and someone
    /// looking at the mailbox itself can tell what has been picked up.
    /// </summary>
    MarkSeen = 0,

    /// <summary>
    /// Move it to another folder, leaving the inbox holding only what has not been taken
    /// in yet.
    /// </summary>
    MoveToFolder = 1,

    /// <summary>
    /// Change nothing. For a mailbox shared with a person who reads it themselves, or
    /// while trying the connection out. Progress is tracked by UID instead, so a message
    /// is still only fetched once.
    /// </summary>
    LeaveAlone = 2,
}

/// <summary>
/// The IMAP mailbox support mail arrives in, for one tenant.
///
/// <para><b>The password is not here.</b> It lives in the tenant's vault, encrypted, as a
/// secret owned by this row — the same arrangement as a cluster's kubeconfig or a git
/// credential. Everything else about the connection is ordinary configuration and is
/// stored plainly, because being able to read the host and user out of the database is
/// how a connection problem gets diagnosed.</para>
///
/// <para><b>Disabled until it is proven.</b> A new mailbox starts switched off. Polling an
/// address with a wrong password locks accounts, and a mailbox that has never had its
/// connection tested should not be the thing that discovers that at three in the
/// morning.</para>
/// </summary>
public class SupportMailbox
{
    public Guid Id { get; set; }

    /// <summary>One mailbox per tenant, enforced by a unique index.</summary>
    public Guid TenantId { get; set; }

    public required string Host { get; set; }

    public int Port { get; set; } = 993;

    /// <summary>Implicit TLS on connect — port 993's arrangement, and the usual one.</summary>
    public bool UseSsl { get; set; } = true;

    public required string Username { get; set; }

    /// <summary>The address support mail is sent to, for the drafted replies to come from.</summary>
    public string? Address { get; set; }

    public string Folder { get; set; } = "INBOX";

    /// <summary>
    /// Off until someone has tested the connection and switched it on, deliberately.
    /// </summary>
    public bool IsEnabled { get; set; }

    public int PollIntervalSeconds { get; set; } = 120;

    public MailboxDisposition Disposition { get; set; } = MailboxDisposition.MarkSeen;

    /// <summary>Where to move a handled message, for <see cref="MailboxDisposition.MoveToFolder"/>.</summary>
    public string? MoveToFolder { get; set; }

    // ---- What the poller has seen -------------------------------------------------------

    /// <summary>
    /// The highest UID taken in, so a poll asks the server only for what is new.
    ///
    /// <para>Meaningless on its own: IMAP UIDs are only unique within one
    /// <see cref="LastUidValidity"/>. When the server reports a different validity the
    /// mailbox has been rebuilt underneath us and the cursor is thrown away — dedupe on
    /// the message id then stops the re-read turning into duplicate tickets.</para>
    /// </summary>
    public uint? LastSeenUid { get; set; }

    public uint? LastUidValidity { get; set; }

    public DateTime? LastPolledAt { get; set; }

    /// <summary>When a message was last actually taken in — not merely when we looked.</summary>
    public DateTime? LastMessageAt { get; set; }

    /// <summary>
    /// What went wrong last time, cleared by a poll that worked. Kept because the useful
    /// question about a quiet support mailbox is whether it is quiet or broken, and those
    /// look identical from the queue.
    /// </summary>
    public string? LastError { get; set; }

    public int ConsecutiveFailures { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
