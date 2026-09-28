using EntKube.Web.Data;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Where to connect, as whom, and with what kind of credential.
///
/// <para><paramref name="ValidateCertificateName"/> is the one that is not obvious. A mail server
/// EntKube deployed holds a certificate for the hostname its users reach it on, and the poller
/// reaches it by its in-cluster Service name instead — so the name will not match, and a client
/// that insists on one fails the handshake. That failure is not a security finding, it is the
/// wrong check: the connection never leaves the cluster, and the server at the other end is the
/// one EntKube configured and issued that certificate to.</para>
/// </summary>
/// <param name="UseOAuth">
/// The credential is a bearer token rather than a password, because the server's directory is OIDC
/// and an OIDC directory validates nothing else.
/// </param>
public sealed record MailboxConnection(
    string Host,
    int Port,
    bool UseSsl,
    string Username,
    bool UseOAuth,
    bool ValidateCertificateName);

/// <summary>
/// Turns a chosen Stalwart server and account into the connection to make.
///
/// <para>Derived on every connection rather than copied into the mailbox when it was chosen. A
/// server that is renamed, moved to another namespace, or switched from implicit TLS to STARTTLS
/// would otherwise leave a mailbox holding settings that were true once — and the failure would be
/// a connection error days later, with nothing pointing at the change that caused it.</para>
/// </summary>
public static class MailboxConnectionResolver
{
    /// <summary>
    /// The connection for a mailbox on a Stalwart server EntKube manages.
    ///
    /// <para>Always implicit TLS on 993. One toggle enables IMAP, and the plan then creates both
    /// listeners — 143 with STARTTLS and 993 with implicit TLS — so there is a choice to make and
    /// implicit is the one worth making: a support mailbox carries a customer's own account of their
    /// own systems, and STARTTLS can be stripped by anything on the path. Null when IMAP is off
    /// altogether, because then there is nothing to connect to.</para>
    /// </summary>
    public static MailboxConnection? Resolve(
        StalwartComponentConfig config, ClusterComponent component, StalwartMailAccount account,
        StalwartMailDomain domain)
    {
        string releaseName = component.ReleaseName ?? component.Name;
        string ns = component.Namespace ?? StalwartService.DefaultNamespace;

        // The internal Service, not the public hostname: the poller is in the same cluster, and going
        // out through the gateway and back would depend on DNS, the load balancer and a certificate
        // chain for a connection that needs none of them.
        string host = $"{releaseName}.{ns}.svc.cluster.local";

        if (!config.ImapEnabled)
        {
            return null;
        }

        return new MailboxConnection(
            Host: host,
            Port: MailPorts.Imaps,
            UseSsl: true,
            Username: $"{account.LocalPart.Trim().ToLowerInvariant()}@{domain.Name.Trim().ToLowerInvariant()}",
            // A password is unverifiable against an OIDC directory, whatever is stored on the account,
            // so the credential type follows the server's auth mode rather than being configured.
            UseOAuth: config.AuthMode == StalwartAuthMode.Oidc,
            // Reached by a Service name, presented a certificate for the mail hostname.
            ValidateCertificateName: false);
    }

    /// <summary>
    /// The connection for a mailbox somebody typed in — a customer's own server, or one EntKube did
    /// not deploy. Its certificate is expected to match the name it was reached by, because nothing
    /// here knows otherwise.
    /// </summary>
    public static MailboxConnection FromStoredSettings(SupportMailbox mailbox) => new(
        Host: mailbox.Host,
        Port: mailbox.Port,
        UseSsl: mailbox.UseSsl,
        Username: mailbox.Username,
        UseOAuth: false,
        ValidateCertificateName: true);
}

/// <summary>
/// The credential exists on EntKube's side and the mail server has not been told it yet.
///
/// <para>Its own type because it needs the opposite handling to a rejected credential. A wrong
/// password must count towards backing off — presenting one repeatedly is how an account gets locked
/// out. This is the reverse: nothing was presented, nothing can be locked, and the remedy is applying
/// the mail server. Counting it would stop fetching for good after five polls, so a mailbox would
/// stay dead after the operator did the very thing the message asked them to do.</para>
/// </summary>
public sealed class CredentialPendingApplyException(string message) : Exception(message);

/// <summary>
/// The mail server could not be reached at all — no TCP connection, no TLS, no greeting.
///
/// <para>Separate from a rejected credential because nothing was presented, and separate from the
/// generic socket failure because the message names the address that was tried. For a mailbox on a
/// managed server that address is derived rather than typed, so a bare <c>Resource temporarily
/// unavailable</c> tells an operator neither which name failed nor that a name was involved.</para>
/// </summary>
public sealed class MailboxUnreachableException(string message, Exception inner)
    : Exception(message, inner);
