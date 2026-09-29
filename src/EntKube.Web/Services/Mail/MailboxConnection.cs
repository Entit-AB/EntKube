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
    ///
    /// <para><b>Which address depends on how the server authenticates.</b> See
    /// <see cref="ReachedByPublicHostname"/>: a server whose directory is OIDC has to be reached on its
    /// public hostname even from inside its own cluster, and the in-cluster Service name is right for
    /// one that checks passwords itself.</para>
    /// </summary>
    public static MailboxConnection? Resolve(
        StalwartComponentConfig config, ClusterComponent component, StalwartMailAccount account,
        StalwartMailDomain domain)
    {
        if (!config.ImapEnabled)
        {
            return null;
        }

        bool publicHostname = ReachedByPublicHostname(config);

        string releaseName = component.ReleaseName ?? component.Name;
        string ns = component.Namespace ?? StalwartService.DefaultNamespace;

        return new MailboxConnection(
            Host: publicHostname
                ? config.Hostname.Trim()
                : $"{releaseName}.{ns}.svc.cluster.local",
            Port: MailPorts.Imaps,
            UseSsl: true,
            Username: $"{account.LocalPart.Trim().ToLowerInvariant()}@{domain.Name.Trim().ToLowerInvariant()}",
            // A password is unverifiable against an OIDC directory, whatever is stored on the account,
            // so the credential type follows the server's auth mode rather than being configured.
            UseOAuth: config.AuthMode == StalwartAuthMode.Oidc,
            // On the public hostname the certificate is for the name being used, so the ordinary check
            // applies. By Service name it cannot match, and insisting would fail a healthy handshake.
            ValidateCertificateName: publicHostname);
    }

    /// <summary>
    /// Whether to reach this server on its public hostname rather than its in-cluster Service.
    ///
    /// <para>The in-cluster Service is the better address when it works: no dependency on public DNS,
    /// the load balancer, or a certificate chain, for a connection that never leaves the cluster. But
    /// it does not work for a server whose directory is OIDC — that has to be reached on the public
    /// hostname even from inside its own cluster, which is an operational fact about how these
    /// deployments authenticate rather than something derivable from the component.</para>
    ///
    /// <para>The public hostname has two properties worth having in its own right, and both of them
    /// stop being coincidences once it is the address in use. The certificate is issued for that name,
    /// so the name check can be applied instead of waived. And it is the only path a PROXY header
    /// exists on: Stalwart demands one from every peer inside <c>proxyTrustedNetworks</c>, on every
    /// listener, with no way to exempt one — so a connection made to the Service name from a pod the
    /// server trusts as a proxy stalls waiting for a header an IMAP client never sends.</para>
    /// </summary>
    public static bool ReachedByPublicHostname(StalwartComponentConfig config) =>
        config.AuthMode == StalwartAuthMode.Oidc
        && !string.IsNullOrWhiteSpace(config.Hostname);

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
