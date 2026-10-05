using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>Where outbound mail goes and who it comes from.</summary>
/// <param name="Host">Null when nothing is configured, which is not an error — it means no mail.</param>
/// <param name="Port">587 unless told otherwise.</param>
/// <param name="From">The default sender, used when a caller has no better address.</param>
/// <param name="Username">Null for a relay that does not authenticate.</param>
/// <param name="Password">Null likewise.</param>
/// <param name="UseSsl">STARTTLS or implicit TLS as the port implies; never plaintext by choice.</param>
/// <param name="UseOAuth">
/// <paramref name="Password"/> is a bearer token rather than a password, because the server's
/// directory is OIDC and an OIDC directory validates nothing else. The same fact the fetching
/// side has always had to know — see <see cref="MailboxConnection"/>.
/// </param>
/// <param name="ValidateCertificateName">
/// False when the server is reached by a name its certificate cannot carry — its in-cluster
/// Service. Insisting there fails a healthy handshake against the server EntKube itself deployed
/// and issued the certificate to; the connection never leaves the cluster.
/// </param>
/// <param name="Source">
/// Where these settings came from, in words. Carried so a screen can say which of the three
/// possible answers is in force — the question "why did no mail go out" has three different
/// remedies and they are indistinguishable from the outside.
/// </param>
public readonly record struct SmtpSettings(
    string? Host,
    int Port,
    string From,
    string? Username,
    string? Password,
    bool UseSsl,
    bool UseOAuth = false,
    bool ValidateCertificateName = true,
    string Source = "nothing configured")
{
    /// <summary>Whether there is anywhere to send.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// Reads the SMTP configuration a given tenant sends through.
///
/// <para><b>Why this is its own class.</b> The same fifteen lines of precedence were about to
/// exist in two places, one for alert mail and one for support mail. Two copies of a credential
/// lookup is how one of them ends up reading a setting the other does not, and the symptom is
/// mail that works for alerts and silently does not for customers.</para>
///
/// <para><b>The tenant's own support mailbox comes first.</b> That was the shape the warning
/// above predicted, arriving from the other direction: everything about <em>fetching</em> support
/// mail is derived from the mail server an operator picked — address, port, TLS, login, credential
/// — and sending was left to a provider row nobody had filled in, because nothing ever said it
/// was needed. Support mail arrived, tickets opened, and not one receipt went out; every screen
/// looked healthy. Deriving it closes that, and it is also the only correct answer: the receipt is
/// sent <em>from the support address</em>, whose domain that server publishes SPF and DKIM for,
/// and the same message handed to an unrelated relay fails both.</para>
/// </summary>
public class SmtpSettingsResolver(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration,
    // Optional so that a caller with no mail server to derive from — the alert path, and the
    // tests — is not made to supply them. Without them the tenant's mailbox is simply not
    // consulted, which is exactly the behaviour this class had before.
    VaultService? vault = null,
    MailboxTokenProvider? tokens = null)
{
    /// <summary>The address used when nothing names a better one.</summary>
    public const string DefaultFrom = "alerts@entkube.io";

    /// <summary>
    /// Where outbound mail for this tenant goes.
    /// </summary>
    /// <param name="tenantId">
    /// Whose mail this is. Given, the tenant's own support mailbox is preferred, then that
    /// tenant's SMTP provider row. Omitted, only the installation <c>Smtp:</c> configuration
    /// is left — provider rows belong to a tenant, so there is nothing else to consult.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SmtpSettings> ResolveAsync(
        Guid? tenantId, CancellationToken ct = default)
    {
        if (tenantId is Guid tenant)
        {
            if (await FromSupportMailboxAsync(tenant, ct) is SmtpSettings derived)
            {
                return derived;
            }

            if (await FromProviderAsync(tenant, ct) is SmtpSettings configured)
            {
                return configured;
            }
        }

        return await ResolveAsync(ct);
    }

    /// <summary>
    /// The installation-wide answer, for a caller with no tenant to ask about.
    ///
    /// <para>Since provider rows became per-tenant there is nothing installation-wide left
    /// in the database, so this is the <c>Smtp:</c> configuration section or nothing. A
    /// caller that <em>has</em> a tenant must pass it — the overload above is the one that
    /// can still find a configured server.</para>
    /// </summary>
    public Task<SmtpSettings> ResolveAsync(CancellationToken ct = default)
    {
        string? configured = configuration["Smtp:Host"];

        return Task.FromResult(new SmtpSettings(
            configured,
            configuration.GetValue("Smtp:Port", 587),
            configuration["Smtp:FromAddress"] ?? DefaultFrom,
            configuration["Smtp:Username"],
            configuration["Smtp:Password"],
            true,
            Source: string.IsNullOrWhiteSpace(configured)
                ? "nothing configured"
                : "the Smtp: configuration section"));
    }

    /// <summary>
    /// The tenant's own SMTP notification provider row — the relay an operator typed in on the
    /// tenant's "Notification providers" tab.
    ///
    /// <para>Second in precedence, behind the tenant's own mail server: a server EntKube
    /// deployed publishes SPF and DKIM for the address it sends as, and a typed-in relay
    /// generally does not. Null when the tenant has configured none, or has switched it
    /// off, so the caller falls through to the installation configuration.</para>
    /// </summary>
    private async Task<SmtpSettings?> FromProviderAsync(Guid tenantId, CancellationToken ct)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        NotificationProviderConfig? provider = await db.NotificationProviderConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                c => c.TenantId == tenantId && c.ProviderType == NotificationProviderType.Smtp, ct);

        if (provider?.IsEnabled != true)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(provider.ConfigurationJson);
        JsonElement root = doc.RootElement;

        return new SmtpSettings(
            root.TryGetProperty("host", out JsonElement h) ? h.GetString() : null,
            root.TryGetProperty("port", out JsonElement p) ? p.GetInt32() : 587,
            root.TryGetProperty("from", out JsonElement f) ? f.GetString() ?? DefaultFrom : DefaultFrom,
            root.TryGetProperty("username", out JsonElement u) ? u.GetString() : null,
            root.TryGetProperty("password", out JsonElement pw) ? pw.GetString() : null,
            !root.TryGetProperty("enableSsl", out JsonElement ssl) || ssl.GetBoolean(),
            Source: "the tenant's SMTP notification provider");
    }

    /// <summary>
    /// Submission on the tenant's own support mail server, when that mailbox is one EntKube
    /// deployed. Null for a mailbox somebody typed in — that is an address to read, and nothing
    /// about it says we may send through it — and null when submission is switched off there.
    ///
    /// <para>Derived on every send, never stored, for the same reason the fetching side derives
    /// its connection: a server renamed, re-addressed or switched between auth modes is followed
    /// rather than remembered wrongly.</para>
    /// </summary>
    private async Task<SmtpSettings?> FromSupportMailboxAsync(Guid tenantId, CancellationToken ct)
    {
        if (vault is null)
        {
            return null;
        }

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportMailbox? mailbox = await db.SupportMailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId, ct);

        if (mailbox?.StalwartComponentId is not Guid componentId
            || mailbox.StalwartAccountId is not Guid accountId)
        {
            return null;
        }

        ClusterComponent? component = await db.ClusterComponents.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == componentId, ct);

        StalwartComponentConfig? config = await db.StalwartComponentConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClusterComponentId == componentId, ct);

        StalwartMailAccount? account = await db.StalwartMailAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        StalwartMailDomain? domain = account is null
            ? null
            : await db.StalwartMailDomains.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == account.DomainId, ct);

        if (component is null || config is null || account is null || domain is null)
        {
            return null;
        }

        MailboxConnection? where =
            MailboxConnectionResolver.ResolveSubmission(config, component, account, domain);

        if (where is null)
        {
            return null;
        }

        string? credential = where.UseOAuth
            ? await TokenAsync(mailbox, ct)
            : await vault.GetComponentSecretValueAsync(
                tenantId, componentId,
                StalwartManifestBuilder.AccountPasswordSecretName(account.Id), ct);

        // No credential is not the same as no server. Returning null here would fall through to
        // a relay that cannot send as this address anyway, and the error would name the relay.
        return string.IsNullOrEmpty(credential)
            ? null
            : new SmtpSettings(
                where.Host,
                where.Port,
                mailbox.Address ?? where.Username,
                where.Username,
                credential,
                UseSsl: where.UseSsl,
                UseOAuth: where.UseOAuth,
                ValidateCertificateName: where.ValidateCertificateName,
                Source: "the tenant's own support mail server");
    }

    /// <summary>
    /// A bearer token for the mailbox, for a server whose directory is OIDC. The same client
    /// credentials the poller authenticates with — one mailbox, one client, one cached token.
    /// </summary>
    private async Task<string?> TokenAsync(SupportMailbox mailbox, CancellationToken ct)
    {
        if (tokens is null
            || vault is null
            || string.IsNullOrWhiteSpace(mailbox.OAuthClientId)
            || string.IsNullOrWhiteSpace(mailbox.OAuthTokenEndpoint))
        {
            return null;
        }

        string? secret = await vault.GetSupportMailboxPasswordAsync(
            mailbox.TenantId, mailbox.Id, ct);

        return string.IsNullOrEmpty(secret)
            ? null
            : await tokens.GetAsync(
                mailbox.Id, mailbox.OAuthTokenEndpoint, mailbox.OAuthClientId, secret,
                mailbox.OAuthScopes ?? "openid email", ct);
    }
}
