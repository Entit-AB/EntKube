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

    /// <summary>
    /// The Stalwart component this mailbox lives on, when it is one EntKube deployed.
    ///
    /// <para>Set, the connection stops being typed in: the host is the server's own in-cluster
    /// Service, the port and TLS follow the protocols it was configured with, the username is the
    /// chosen account's address, and the credential is a token minted for a service account created
    /// alongside it. Left null, this is an outside mailbox and <see cref="Host"/>,
    /// <see cref="Port"/>, <see cref="UseSsl"/> and <see cref="Username"/> are what they always
    /// were — the fields stay, because a customer's own IMAP server is still a valid thing to
    /// poll.</para>
    ///
    /// <para>A reference rather than copied settings, so that a server renamed, re-addressed or
    /// switched from implicit TLS to STARTTLS does not leave a mailbox pointing at what used to be
    /// true. The values are derived on every connection, not stored.</para>
    /// </summary>
    public Guid? StalwartComponentId { get; set; }

    /// <summary>
    /// The mailbox on that server, as a <see cref="StalwartMailAccount"/>. The account's local part
    /// and its domain are the login; EntKube created the account, so there is nothing to look up.
    /// </summary>
    public Guid? StalwartAccountId { get; set; }

    /// <summary>
    /// The Keycloak client EntKube created to authenticate as this mailbox, and where to mint from.
    ///
    /// <para>Only used when the mail server's own directory is OIDC, which is the case that makes a
    /// password impossible: upstream's rule is that a directory serves every protocol and both
    /// credential types, and an OIDC one validates only bearer tokens. So no password stored
    /// anywhere would be checked, and the poller authenticates with a token minted by client
    /// credentials instead. The secret lives in the vault, never here.</para>
    /// </summary>
    public string? OAuthClientId { get; set; }

    public string? OAuthTokenEndpoint { get; set; }

    /// <summary>
    /// Scopes to ask for, matching what the mail server's directory requires — its default is
    /// <c>openid email</c>. Requested explicitly because a client-credentials token carries only
    /// what its client grants, and the directory rejects a token missing what it asked for.
    /// </summary>
    public string? OAuthScopes { get; set; }

    public required string Host { get; set; }

    public int Port { get; set; } = 993;

    /// <summary>Implicit TLS on connect — port 993's arrangement, and the usual one.</summary>
    public bool UseSsl { get; set; } = true;

    public required string Username { get; set; }

    /// <summary>The address support mail is sent to, for the drafted replies to come from.</summary>
    public string? Address { get; set; }

    public string Folder { get; set; } = "INBOX";

    /// <summary>
    /// The <c>authserv-id</c> of the mail server whose <c>Authentication-Results</c> we
    /// believe — normally the hostname of the server this mailbox is on.
    ///
    /// <para><b>Nothing is checked without it.</b> That header is a header like any other:
    /// a sender is free to include one saying their own mail passed everything, so the only
    /// ones worth reading are those stamped by a server we actually trust, and only the
    /// tenant can say which that is. Left empty, every sender's authenticity is reported as
    /// unknown — which means a forged From is indistinguishable from a real one, and a
    /// stranger writing as a customer's named §23 contact is placed in their queue with no
    /// caveat at all.</para>
    /// </summary>
    public string? TrustedAuthenticationServer { get; set; }

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

    /// <summary>
    /// Whether the last failure was the server refusing the credential, as opposed to the attempt not
    /// reaching that point.
    ///
    /// <para>Stored rather than inferred from <see cref="LastError"/>, because the screen has to say why
    /// fetching stopped and the two reasons need opposite advice. It used to say a password was being
    /// rejected whatever had happened — including a connection that never completed and presented no
    /// password at all — which sent two debugging sessions after the credential while the error text
    /// beside it said the connection had failed.</para>
    /// </summary>
    public bool LastErrorWasRejection { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Tenant Tenant { get; set; } = null!;
}
