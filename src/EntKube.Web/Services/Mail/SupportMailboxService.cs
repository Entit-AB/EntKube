using System.Collections.Concurrent;
using System.Security.Cryptography;
using EntKube.Web.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EntKube.Web.Services.Mail;

/// <summary>What a poll or a connection test came back with.</summary>
/// <param name="Ok">Whether it worked.</param>
/// <param name="Error">Why not, in words an operator can act on.</param>
/// <param name="Taken">How many messages were taken in.</param>
/// <param name="Seen">How many were looked at, including ones already known.</param>
/// <param name="AlreadyRunning">
/// Nothing was done because a fetch for this mailbox was already in progress. Not a
/// failure, and not "nothing new" either — saying the latter would tell somebody who
/// pressed the button that their mailbox was empty when it was merely busy.
/// </param>
/// <param name="FromJunk">
/// How many of <paramref name="Taken"/> were rescued from the Junk folder. Reported rather than
/// counted silently: a non-zero number here means the spam filter is misclassifying a customer's
/// mail, which is worth someone's attention even though no message was lost.
/// </param>
/// <param name="JunkUnavailable">
/// Customer domains are registered, so the Junk sweep was wanted, and the server offered no Junk
/// folder to sweep. Reported because the protection is then not in force while an operator has every
/// reason to believe it is: a mail server that does not advertise the folder's SPECIAL-USE attribute
/// cannot be found by one, and a customer's request filed as spam is simply never read.
/// </param>
public readonly record struct MailPollResult(
    bool Ok, string? Error, int Taken = 0, int Seen = 0, bool AlreadyRunning = false,
    int FromJunk = 0, bool JunkUnavailable = false);

/// <summary>
/// The tenant's support mailbox: its settings, and the fetch that fills the triage queue.
///
/// <para><b>Fetching is all this does.</b> What arrives goes to
/// <see cref="SupportMailService.IngestAsync"/>, which records it, has the analyst propose,
/// and — where <see cref="ArrivalPolicy"/> allows it — opens the ticket so the sender has a
/// number to quote. No priority is confirmed and nothing is resolved without a person. A
/// mail server being reachable does not change who decides anything.</para>
///
/// <para><b>A failed poll is a reported state, not an exception into a log.</b> The useful
/// question about a quiet support mailbox is whether it is quiet or broken, and those look
/// identical from the queue — so the last error, and how many polls in a row have failed,
/// are stored on the mailbox where the configuration screen shows them.</para>
/// </summary>
public class SupportMailboxService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    VaultService vault,
    SupportMailService mail,
    MailboxTokenProvider tokens,
    KeycloakService keycloak,
    ILogger<SupportMailboxService> logger)
{
    /// <summary>
    /// One fetch per mailbox at a time.
    ///
    /// <para>The background poller and the "Fetch now" button reach the same mailbox, and
    /// two fetches racing each other both read the same UIDs. Ingestion dedupes on the
    /// message id, so no duplicate ticket comes of it — but the loser then tries to mark
    /// or move messages the winner has already moved, the server refuses, and the failure
    /// counts towards the threshold that stops the mailbox. Somebody pressing a button
    /// should not be able to disable their own support mail.</para>
    ///
    /// <para>Static because the service is scoped: a new instance per request would
    /// otherwise have nothing to contend on. One entry per tenant, which is bounded by
    /// the number of tenants.</para>
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    /// <summary>
    /// A mailbox that has failed this many polls in a row is left alone until someone
    /// looks at it.
    ///
    /// <para>Repeatedly presenting a password a server is rejecting is how an account gets
    /// locked, and a locked service account takes the support mailbox down for everyone
    /// rather than for one poll. Backing off costs a delay in noticing mail; not backing
    /// off costs the mailbox.</para>
    /// </summary>
    public const int FailuresBeforeBackingOff = 5;

    /// <summary>
    /// How long any one IMAP operation may take. The sweep polls mailboxes one after
    /// another, so this bounds how long one unresponsive server delays everybody else.
    /// </summary>
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    public async Task<SupportMailbox?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.SupportMailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Saves the mailbox settings, and the password when one was typed.
    ///
    /// <para>An empty password leaves the stored one alone — the configuration screen
    /// cannot show it back, so treating a blank box as "clear it" would erase the
    /// credential every time someone corrected the port.</para>
    /// </summary>
    public async Task<SupportMailbox> SaveAsync(
        SupportMailbox settings, string? password, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportMailbox? existing = await db.SupportMailboxes
            .FirstOrDefaultAsync(m => m.TenantId == settings.TenantId, ct);

        if (existing is null)
        {
            settings.Id = settings.Id == Guid.Empty ? Guid.NewGuid() : settings.Id;
            settings.CreatedAt = DateTime.UtcNow;
            settings.UpdatedAt = DateTime.UtcNow;
            db.SupportMailboxes.Add(settings);
            existing = settings;
        }
        else
        {
            // A change of server or account invalidates the UID cursor: the new mailbox's
            // UIDs mean nothing in terms of the old one's, and carrying the number over
            // would skip every message below it.
            bool movedMailbox = existing.Host != settings.Host
                || existing.Username != settings.Username
                || existing.Folder != settings.Folder
                || existing.StalwartComponentId != settings.StalwartComponentId
                || existing.StalwartAccountId != settings.StalwartAccountId;

            existing.StalwartComponentId = settings.StalwartComponentId;
            existing.StalwartAccountId = settings.StalwartAccountId;
            existing.Host = settings.Host;
            existing.Port = settings.Port;
            existing.UseSsl = settings.UseSsl;
            existing.Username = settings.Username;
            existing.Address = settings.Address;
            existing.Folder = settings.Folder;
            existing.IsEnabled = settings.IsEnabled;
            existing.AcknowledgeOnArrival = settings.AcknowledgeOnArrival;
            existing.PollIntervalSeconds = settings.PollIntervalSeconds;
            existing.Disposition = settings.Disposition;
            existing.MoveToFolder = settings.MoveToFolder;
            existing.TrustedAuthenticationServer = settings.TrustedAuthenticationServer;
            existing.UpdatedAt = DateTime.UtcNow;

            if (movedMailbox)
            {
                existing.LastSeenUid = null;
                existing.LastUidValidity = null;
            }

            // A new attempt deserves a clean slate, or a mailbox that backed off after
            // five failures could never be repaired by fixing the password.
            existing.ConsecutiveFailures = 0;
            existing.LastError = null;
            existing.LastErrorWasRejection = false;
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(password))
        {
            await vault.SetSupportMailboxPasswordAsync(
                existing.TenantId, existing.Id, password, ct);
        }

        await EnsureReplyAddressAsync(db, existing, ct);
        await EnsureCredentialAsync(db, existing, ct);

        return existing;
    }

    /// <summary>
    /// Fills in the address replies come from, when a mailbox was chosen rather than typed.
    ///
    /// <para>Without this, picking a server and a mailbox leaves the reply address blank, and
    /// TicketNotifier falls back to the From in appsettings — so acknowledgements to customers go out
    /// from whatever that happens to be, addressed from somewhere nobody reads. It is the worst shape
    /// of wrong: the mailbox says it is configured, mail is fetched correctly, and only the replies
    /// are addressed from the wrong place.</para>
    ///
    /// <para>Only when blank. An operator who set an address meant it — replying from an alias rather
    /// than the mailbox the mail arrived in is a legitimate thing to want, and overwriting it on every
    /// save would make that impossible to express.</para>
    /// </summary>
    private async Task EnsureReplyAddressAsync(
        ApplicationDbContext db, SupportMailbox mailbox, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(mailbox.Address)
            || mailbox.StalwartAccountId is not Guid accountId)
        {
            return;
        }

        StalwartMailAccount? account = await db.StalwartMailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        StalwartMailDomain? domain = account is null
            ? null
            : await db.StalwartMailDomains.FirstOrDefaultAsync(d => d.Id == account.DomainId, ct);

        if (account is null || domain is null)
        {
            return;
        }

        mailbox.Address =
            $"{account.LocalPart.Trim().ToLowerInvariant()}@{domain.Name.Trim().ToLowerInvariant()}";

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Makes sure this mailbox has a credential its mail server will actually accept, creating one if
    /// it does not — so that choosing a server and an account is the whole of the configuration.
    ///
    /// <para>Which credential is not a choice, it is a consequence. A directory serves every protocol
    /// and both credential types, and an OIDC one validates only bearer tokens — so on a server whose
    /// directory is Keycloak, no password stored anywhere would be checked, and the poller has to
    /// present a token. A token needs an identity to be minted for, and there is no human here, so it
    /// is a service account: a confidential Keycloak client whose own mapper hardcodes the mailbox
    /// address into the claim the directory reads.</para>
    ///
    /// <para>Idempotent and quiet. It runs on every save, including saves that changed nothing, and it
    /// never throws into the caller: a mailbox that could not be given a credential is still worth
    /// saving, and the failure belongs on the mailbox where its other errors are reported rather than
    /// as an exception over a form the operator has just filled in.</para>
    /// </summary>
    private async Task EnsureCredentialAsync(
        ApplicationDbContext db, SupportMailbox mailbox, CancellationToken ct)
    {
        if (mailbox.StalwartComponentId is not Guid componentId
            || mailbox.StalwartAccountId is not Guid accountId)
        {
            return;
        }

        try
        {
            StalwartComponentConfig? config = await db.StalwartComponentConfigs
                .FirstOrDefaultAsync(c => c.ClusterComponentId == componentId, ct);

            if (config is null)
            {
                return;
            }

            if (config.AuthMode == StalwartAuthMode.Internal)
            {
                await EnsureAccountPasswordAsync(db, mailbox, componentId, accountId, ct);
                return;
            }

            if (config.AuthMode == StalwartAuthMode.Ldap)
            {
                // Nothing to mint and nothing to pretend about. Stalwart routes authentication to the
                // directory, so the credential has to exist as an LDAP entry's password — which EntKube
                // does not write. Minting one here would store a password nothing will ever check.
                mailbox.LastError =
                    "That mail server authenticates against LDAP, so its mailbox passwords live in the "
                    + "directory and EntKube cannot create one. Either set this mailbox up by entering "
                    + "the IMAP details and the directory password by hand, or switch the mail server "
                    + "to an OIDC directory, where a service account can be created for it.";
                await db.SaveChangesAsync(ct);
                return;
            }

            // The Stalwart config keeps the issuer URL rather than a realm id — the realm selector
            // resolves to one at configure time — so the realm is found back out of it. The last path
            // segment of ".../realms/<name>" is the realm, which is how the issuer is built.
            string issuer = (config.OidcIssuerUrl ?? "").Trim().TrimEnd('/');
            string realmName = issuer.Contains("/realms/", StringComparison.OrdinalIgnoreCase)
                ? issuer[(issuer.LastIndexOf('/') + 1)..]
                : "";

            KeycloakRealm? realm = realmName.Length == 0
                ? null
                : await db.KeycloakRealms.FirstOrDefaultAsync(
                    r => r.TenantId == mailbox.TenantId && r.RealmName == realmName, ct);

            if (realm is null)
            {
                mailbox.LastError =
                    $"That mail server authenticates against OIDC at {issuer}, but no Keycloak realm "
                    + "EntKube manages matches it — so no service account can be created for this "
                    + "mailbox. Pick a realm on the mail server's component settings rather than "
                    + "typing an issuer URL.";
                await db.SaveChangesAsync(ct);
                return;
            }

            Guid realmId = realm.Id;

            StalwartMailAccount? account = await db.StalwartMailAccounts
                .FirstOrDefaultAsync(a => a.Id == accountId, ct);
            StalwartMailDomain? domain = account is null
                ? null
                : await db.StalwartMailDomains.FirstOrDefaultAsync(d => d.Id == account.DomainId, ct);

            if (account is null || domain is null)
            {
                return;
            }

            string username =
                $"{account.LocalPart.Trim().ToLowerInvariant()}@{domain.Name.Trim().ToLowerInvariant()}";

            // One client per mailbox, named after what it is for rather than randomly, so somebody
            // looking at the realm's client list can tell what created it and why. Half the mailbox id
            // is plenty to keep it unique and keeps the whole thing short enough to read in a list.
            string clientId = $"entkube-support-mailbox-{mailbox.Id:N}"[..IdentifierLength];

            (string id, string secret, string tokenEndpoint) =
                await keycloak.EnsureServiceAccountClientAsync(
                    mailbox.TenantId, realmId, clientId, username,
                    // The directory validates aud, and its default is "stalwart" — so the audience the
                    // mapper adds has to be whatever that server actually requires, not a guess.
                    string.IsNullOrWhiteSpace(config.OidcRequireAudience)
                        ? "stalwart"
                        : config.OidcRequireAudience!.Trim(),
                    ct);

            await vault.SetSupportMailboxOAuthSecretAsync(mailbox.TenantId, mailbox.Id, secret, ct);

            mailbox.OAuthClientId = id;
            mailbox.OAuthTokenEndpoint = tokenEndpoint;
            // The scopes the directory requires, so the token carries what it will be checked for.
            mailbox.OAuthScopes = config.OidcRequireScopes;

            // A new client means a new secret, so a token minted from the old one is no longer good.
            MailboxTokenProvider.Forget(mailbox.Id);

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not create the Keycloak service account for the support mailbox of tenant "
                + "{Tenant}.", mailbox.TenantId);

            mailbox.LastError = $"Could not create the service account: {ex.Message}";
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Gives the chosen mailbox a password on a server that checks passwords itself, creating one if
    /// it has none.
    ///
    /// <para>This is what makes choosing a server and an account the whole of the configuration. A
    /// Stalwart account is created by the Mailboxes tab with no credential at all — only the
    /// administrator gets one — so before this existed, a support mailbox pointed at an internal-auth
    /// server could never authenticate, and the form deliberately does not ask for a password because
    /// the operator has no way to know one.</para>
    ///
    /// <para>Minted once and then left alone. Rotating it on every save would invalidate the password
    /// the running server is currently checking against, so every save would break fetching until the
    /// mail server was applied again — a save that changed nothing would be enough to do it.</para>
    /// </summary>
    private async Task EnsureAccountPasswordAsync(
        ApplicationDbContext db, SupportMailbox mailbox, Guid componentId,
        Guid accountId, CancellationToken ct)
    {
        StalwartMailAccount? account = await db.StalwartMailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct);

        if (account is null)
        {
            return;
        }

        string secretName = StalwartManifestBuilder.AccountPasswordSecretName(account.Id);

        bool stored = await vault.GetComponentSecretValueAsync(
            mailbox.TenantId, componentId, secretName, ct) is { Length: > 0 };

        if (!stored || account.PasswordSetAt is null)
        {
            // Either half missing is repaired, not just both: a stored secret with no timestamp cannot
            // be told from one the server already knows, and a timestamp with no secret is a password
            // the plan will never carry.
            await vault.SetComponentSecretAsync(
                mailbox.TenantId, componentId, secretName, GeneratePassword(), ct);

            account.PasswordSetAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // The password from any earlier hand-entered configuration is for a different account on a
        // different server, and it is no longer reachable by any code path. Left in the vault it would
        // still make the settings screen report that a password is stored, and it is a live credential
        // for something nobody is watching.
        await vault.DeleteSupportMailboxPasswordAsync(mailbox.TenantId, mailbox.Id, ct);
    }

    /// <summary>
    /// A password for a mailbox nobody will ever type. Long and random because it is only ever
    /// presented by the poller, so there is no reason for it to be memorable or short.
    /// </summary>
    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "x").Replace("/", "y")[..32];

    /// <summary>
    /// How much of "entkube-support-mailbox-&lt;id&gt;" the Keycloak client is named with: the whole
    /// prefix and the first 12 hex digits of the mailbox id.
    /// </summary>
    private const int IdentifierLength = 36;

    /// <summary>
    /// A Stalwart server the tenant could read a support mailbox from.
    ///
    /// <para>Carries the auth mode rather than a single "uses OIDC" flag, because what can be done
    /// about the credential differs three ways and not two: OIDC mints a service account, internal
    /// mints a password, and LDAP can do neither — the credential is an entry in a directory EntKube
    /// does not write.</para>
    /// </summary>
    public sealed record StalwartMailServerOption(
        Guid ComponentId, string Label, StalwartAuthMode AuthMode)
    {
        public bool UsesOidc => AuthMode == StalwartAuthMode.Oidc;

        /// <summary>Whether choosing a mailbox here is the whole of the configuration.</summary>
        public bool CredentialIsAutomatic => AuthMode != StalwartAuthMode.Ldap;
    }

    /// <summary>A mailbox on one of those servers.</summary>
    public sealed record StalwartMailboxOption(Guid AccountId, string Address);

    /// <summary>
    /// The mail servers this tenant has, for choosing one instead of typing a hostname.
    ///
    /// <para>Only servers that are installed and have IMAP on: a mailbox cannot be read from a
    /// component that was added and never deployed, and offering one would produce a connection
    /// error rather than a mailbox.</para>
    /// </summary>
    public async Task<List<StalwartMailServerOption>> GetStalwartServersAsync(
        Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await db.StalwartComponentConfigs
            .Where(c => c.TenantId == tenantId && c.ImapEnabled)
            .Join(
                db.ClusterComponents.Include(k => k.Cluster)
                    .Where(k => k.Status == ComponentStatus.Installed),
                c => c.ClusterComponentId,
                k => k.Id,
                (c, k) => new { Config = c, Component = k })
            .ToListAsync(ct);

        return [.. rows
            .Select(r => new StalwartMailServerOption(
                r.Component.Id,
                // The hostname is what an operator recognises; the cluster disambiguates two servers
                // that serve the same domain from different clusters.
                $"{r.Config.Hostname} ({r.Component.Cluster?.Name ?? r.Component.Name})",
                r.Config.AuthMode))
            .OrderBy(o => o.Label, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The mailboxes on one of those servers, as addresses.
    ///
    /// <para>These are the accounts EntKube authored, which is why no lookup against the server is
    /// needed — and also why one that has been added but not yet applied appears here before it
    /// exists. That is the right way round: the alternative is an operator unable to select the
    /// mailbox they just created.</para>
    /// </summary>
    public async Task<List<StalwartMailboxOption>> GetStalwartMailboxesAsync(
        Guid tenantId, Guid componentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        StalwartComponentConfig? config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == componentId && c.TenantId == tenantId, ct);

        if (config is null)
        {
            return [];
        }

        var rows = await db.StalwartMailAccounts
            .Where(a => a.ConfigId == config.Id)
            .Join(db.StalwartMailDomains, a => a.DomainId, d => d.Id, (a, d) => new { a, d })
            .ToListAsync(ct);

        return [.. rows
            .Select(r => new StalwartMailboxOption(
                r.a.Id,
                $"{r.a.LocalPart.Trim().ToLowerInvariant()}@{r.d.Name.Trim().ToLowerInvariant()}"))
            .OrderBy(o => o.Address, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task DeleteAsync(Guid tenantId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportMailbox? existing = await db.SupportMailboxes
            .FirstOrDefaultAsync(m => m.TenantId == tenantId, ct);

        if (existing is not null)
        {
            db.SupportMailboxes.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Whether this mailbox has a credential of the kind its server will actually check.
    ///
    /// <para>Shape-aware, because "a password is stored" is the wrong question for two of the three
    /// shapes. A mailbox on an OIDC server has no password by design and a bearer token instead; one on
    /// an internal-auth server has a password EntKube minted against the account rather than anything
    /// typed into this screen. Asking only about the typed-in slot reported both as unconfigured, which
    /// is how a working mailbox comes to be labelled "No password stored".</para>
    /// </summary>
    public async Task<bool> HasCredentialAsync(
        SupportMailbox mailbox, CancellationToken ct = default)
    {
        if (mailbox.StalwartComponentId is not Guid componentId
            || mailbox.StalwartAccountId is not Guid accountId)
        {
            return await vault.HasSupportMailboxPasswordAsync(mailbox.TenantId, mailbox.Id, ct);
        }

        if (!string.IsNullOrWhiteSpace(mailbox.OAuthClientId))
        {
            return true;
        }

        return await vault.GetComponentSecretValueAsync(
            mailbox.TenantId, componentId,
            StalwartManifestBuilder.AccountPasswordSecretName(accountId), ct) is { Length: > 0 };
    }

    /// <summary>
    /// Connects, authenticates and opens the folder, then disconnects without reading
    /// anything. What the button beside the settings does, so a wrong password is found
    /// while someone is looking at the screen rather than by the queue staying empty.
    /// </summary>
    public async Task<MailPollResult> TestAsync(Guid tenantId, CancellationToken ct = default)
    {
        SupportMailbox? mailbox = await GetAsync(tenantId, ct);

        if (mailbox is null)
        {
            return new MailPollResult(false, "No mailbox is configured.");
        }

        try
        {
            using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
            (MailboxConnection where, string credential) = await ResolveAsync(db, mailbox, ct);

            using ImapClient client = new();
            await ConnectAsync(client, where, credential, ct);

            IMailFolder folder = await client.GetFolderAsync(mailbox.Folder, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            int count = folder.Count;

            await folder.CloseAsync(false, ct);
            await client.DisconnectAsync(true, ct);

            return new MailPollResult(true, null, 0, count);
        }
        catch (Exception ex)
        {
            return new MailPollResult(false, Explain(ex));
        }
    }

    /// <summary>
    /// Fetches what is new and hands each message to triage.
    ///
    /// <para>Progress is tracked by UID rather than by the read flag, so a mailbox
    /// configured to touch nothing still only reads each message once. UIDs are only
    /// meaningful within one UIDVALIDITY; when the server reports a different one the
    /// mailbox has been rebuilt underneath us, the cursor is dropped, and the message-id
    /// check is what stops the re-read becoming a second set of tickets.</para>
    /// </summary>
    public async Task<MailPollResult> PollAsync(Guid tenantId, CancellationToken ct = default)
    {
        SemaphoreSlim gate = Gates.GetOrAdd(tenantId, static _ => new SemaphoreSlim(1, 1));

        // Not awaited: a fetch already running is doing the same work, and queueing behind
        // it would only make the button appear to hang.
        if (!await gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return new MailPollResult(true, null, 0, 0, AlreadyRunning: true);
        }

        try
        {
            return await PollOnceAsync(tenantId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<MailPollResult> PollOnceAsync(Guid tenantId, CancellationToken ct)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        SupportMailbox? mailbox = await db.SupportMailboxes
            .FirstOrDefaultAsync(m => m.TenantId == tenantId, ct);

        if (mailbox is null)
        {
            return new MailPollResult(false, "No mailbox is configured.");
        }

        try
        {
            // The register of customer domains, so a message the filter junked can be told from
            // the spam beside it. Read here rather than in the fetch: one query per poll, not one
            // per message.
            HashSet<string> customerDomains = new(
                await db.CustomerEmailDomains
                    .Where(d => d.TenantId == tenantId)
                    .Select(d => d.Domain)
                    .ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            (MailboxConnection where, string credential) = await ResolveAsync(db, mailbox, ct);

            (int taken, int seen, int fromJunk, bool junkUnavailable) =
                await FetchAsync(mailbox, where, credential, customerDomains, ct);

            mailbox.LastPolledAt = DateTime.UtcNow;
            mailbox.LastError = null;
            mailbox.ConsecutiveFailures = 0;
            mailbox.LastErrorWasRejection = false;

            if (taken > 0)
            {
                mailbox.LastMessageAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            return new MailPollResult(
                true, null, taken, seen, FromJunk: fromJunk, JunkUnavailable: junkUnavailable);
        }
        catch (Exception ex)
        {
            string explained = Explain(ex);

            // A token can stop working before it expires — the client disabled, its secret rotated,
            // the mailbox renamed — and a cached one would then be presented on every poll until it
            // aged out on its own. Dropping it means the next poll asks for a fresh one, so a fixed
            // Keycloak recovers without waiting.
            if (ex is AuthenticationException)
            {
                MailboxTokenProvider.Forget(mailbox.Id);
            }

            mailbox.LastPolledAt = DateTime.UtcNow;
            mailbox.LastError = explained;
            mailbox.LastErrorWasRejection = ex is AuthenticationException;

            // A credential the server has not been given yet is not a failed attempt: none was made,
            // so nothing can be locked out by trying again. Counting it would back the mailbox off
            // permanently within five polls, and it would still be backed off after the operator
            // applied the mail server — the one action the message asks for.
            if (ex is not CredentialPendingApplyException)
            {
                mailbox.ConsecutiveFailures++;
            }

            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                ex,
                "Support mailbox poll failed for tenant {Tenant} ({Failures} in a row): {Error}",
                tenantId, mailbox.ConsecutiveFailures, explained);

            return new MailPollResult(false, explained);
        }
    }

    /// <summary>The fetch itself, separated from the bookkeeping around it.</summary>
    private async Task<(int Taken, int Seen, int FromJunk, bool JunkUnavailable)> FetchAsync(
        SupportMailbox mailbox, MailboxConnection where, string credential,
        IReadOnlySet<string> customerDomains, CancellationToken ct)
    {
        using ImapClient client = new();
        await ConnectAsync(client, where, credential, ct);

        IMailFolder folder = await client.GetFolderAsync(mailbox.Folder, ct);

        FolderAccess access = mailbox.Disposition == MailboxDisposition.LeaveAlone
            ? FolderAccess.ReadOnly
            : FolderAccess.ReadWrite;

        await folder.OpenAsync(access, ct);

        MailboxResumePoint resume = MailboxCursor.Resume(
            mailbox.LastSeenUid, mailbox.LastUidValidity, folder.UidValidity);

        if (resume.Reset)
        {
            logger.LogWarning(
                "Support mailbox for tenant {Tenant} was rebuilt (UIDVALIDITY {Was} → {Now}); "
                + "reading the folder again. Messages already taken in are recognised by "
                + "their message id and will not be duplicated.",
                mailbox.TenantId, mailbox.LastUidValidity, folder.UidValidity);
        }

        IList<UniqueId> ids = await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(resume.FirstUid), UniqueId.MaxValue)),
            ct);

        int taken = 0;
        List<UniqueId> handled = [];

        foreach (UniqueId id in ids)
        {
            ct.ThrowIfCancellationRequested();

            MimeMessage message = await folder.GetMessageAsync(id, ct);

            InboundMailMessage row = MailMessageReader.Read(
                message, mailbox.TenantId, DateTime.UtcNow, mailbox.TrustedAuthenticationServer);

            if (await mail.IngestAsync(row, ct) is not null)
            {
                taken++;
            }

            handled.Add(id);
        }

        await DisposeOfAsync(folder, handled, mailbox, ct);

        // Written only after the messages are in: a crash between fetching and saving
        // should re-read them, which dedupe makes harmless, rather than skip them.
        mailbox.LastSeenUid = MailboxCursor.Advance(resume.After, handled.Select(u => u.Id));
        mailbox.LastUidValidity = folder.UidValidity;

        await folder.CloseAsync(false, ct);

        (int fromJunk, bool junkUnavailable) =
            await SweepJunkAsync(client, mailbox, customerDomains, ct);

        await client.DisconnectAsync(true, ct);

        return (taken + fromJunk, ids.Count, fromJunk, junkUnavailable);
    }

    /// <summary>
    /// How far back the Junk folder is read on each poll.
    ///
    /// <para>A window rather than a cursor, because a cursor here would need two more columns and
    /// buy nothing: ingestion dedupes on the message id, so re-reading the same messages is free,
    /// and a window bounds the work as the folder grows. Fourteen days is longer than any support
    /// mailbox should go unpolled, which is what it has to cover.</para>
    /// </summary>
    private static readonly TimeSpan JunkLookback = TimeSpan.FromDays(14);

    /// <summary>
    /// Takes in what the spam filter put in Junk, but only from a customer's own domain.
    ///
    /// <para>The reason this exists is that the filter cannot be made not to do it. Stalwart has no
    /// allow-list — it is an open feature request — and its trusted-domains list only skips DNS
    /// block-list checks, which is not what junks legitimate mail. A user Sieve script cannot rescue
    /// one either: filing into INBOX is overridden by the spam filter, and only non-Inbox folders
    /// are honoured. So the mailbox is where this has to be handled, not the mail server.</para>
    ///
    /// <para>And it has to be handled somewhere, because the alternative is silent. The poller reads
    /// one folder; a support request the filter junked was not delayed, it was lost, and nothing
    /// anywhere said so. A real one scored 6.00 against a threshold of 5 on VIOLATED_DIRECT_SPF and
    /// RDNS_NONE alone — a message whose DKIM verified and whose DMARC passed — because a load
    /// balancer had rewritten the sender's address.</para>
    ///
    /// <para>Scoped to the customer register on purpose. Sweeping all of Junk would turn spam
    /// addressed to the support address into support tickets, which moves the filtering problem onto
    /// whoever triages them. Matching the register means the only mail rescued is mail from somebody
    /// entitled to support, and the register is the same one that decides which customer a message
    /// belongs to — so a domain that is wrong here is wrong in a way already visible elsewhere.</para>
    ///
    /// <para>Nothing in Junk is flagged, moved or deleted. The message stays where the filter put it,
    /// so an operator looking at the folder sees what it has been doing, and the copy EntKube took is
    /// recognised by its message id if the folder is read again.</para>
    /// </summary>
    private async Task<(int Rescued, bool Unavailable)> SweepJunkAsync(
        ImapClient client, SupportMailbox mailbox, IReadOnlySet<string> customerDomains,
        CancellationToken ct)
    {
        if (customerDomains.Count == 0)
        {
            // Nothing registered, so nothing was expected of the sweep and its absence is not a
            // finding — unlike a sweep that was wanted and had nowhere to look.
            return (0, false);
        }

        IMailFolder? junk;
        try
        {
            // By special-use flag, not by name: the folder is "Junk" on one server and "Junk Mail"
            // on the next, and guessing wrong would silently do nothing at all.
            junk = client.GetFolder(SpecialFolder.Junk);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "No Junk folder for the support mailbox of tenant {Tenant}; nothing to sweep.",
                mailbox.TenantId);
            return (0, true);
        }

        if (junk is null)
        {
            // The realistic shape of this, and it used to return silently. MailKit answers a
            // special-use lookup with null rather than an exception when the server does not advertise
            // the attribute — measured, not assumed, and it returns null even when a folder plainly
            // named "Junk" exists, because a name is not an attribute. So the sweep has nowhere to
            // look, protects nothing, and the operator has no reason to suspect it.
            //
            // Reported through the result rather than logged every poll: a warning every two minutes
            // is noise that gets filtered, which is how the rest of this stack's real problems stayed
            // hidden behind a wall of repeated lines.
            return (0, true);
        }

        int rescued = 0;

        try
        {
            await junk.OpenAsync(FolderAccess.ReadOnly, ct);

            IList<UniqueId> ids = await junk.SearchAsync(
                SearchQuery.DeliveredAfter(DateTime.UtcNow - JunkLookback), ct);

            foreach (UniqueId id in ids)
            {
                ct.ThrowIfCancellationRequested();

                MimeMessage message = await junk.GetMessageAsync(id, ct);

                InboundMailMessage row = MailMessageReader.Read(
                    message, mailbox.TenantId, DateTime.UtcNow, mailbox.TrustedAuthenticationServer);

                string? senderDomain = SenderDomain.Of(row.FromAddress);

                if (senderDomain is null
                    || !customerDomains.Any(registered => SenderDomain.Claims(registered, senderDomain)))
                {
                    continue;
                }

                if (await mail.IngestAsync(row, ct) is not null)
                {
                    rescued++;

                    logger.LogWarning(
                        "Took a support message in from Junk for tenant {Tenant}: {From} is a "
                        + "registered customer domain, so the spam filter classified a customer's "
                        + "mail as spam. The message has not been moved.",
                        mailbox.TenantId, row.FromAddress);
                }
            }

            await junk.CloseAsync(false, ct);
        }
        catch (Exception ex)
        {
            // Never the reason a poll fails. The folder that matters has already been read, and a
            // mailbox reported broken because its Junk folder could not be opened would send
            // somebody looking in the wrong place.
            logger.LogWarning(ex,
                "Could not sweep Junk for the support mailbox of tenant {Tenant}. The configured "
                + "folder was read normally.", mailbox.TenantId);
        }

        // Found the folder and read it, whatever happened to individual messages.
        return (rescued, false);
    }

    /// <summary>What happens to a message in the mailbox once it has been taken in.</summary>
    private static async Task DisposeOfAsync(
        IMailFolder folder, List<UniqueId> handled, SupportMailbox mailbox, CancellationToken ct)
    {
        if (handled.Count == 0)
        {
            return;
        }

        switch (mailbox.Disposition)
        {
            case MailboxDisposition.MarkSeen:
                await folder.AddFlagsAsync(handled, MessageFlags.Seen, true, ct);
                break;

            case MailboxDisposition.MoveToFolder when !string.IsNullOrWhiteSpace(mailbox.MoveToFolder):
                IMailFolder destination = await folder.GetSubfolderAsync(mailbox.MoveToFolder, ct);
                await folder.MoveToAsync(handled, destination, ct);
                break;

            case MailboxDisposition.MoveToFolder:
                // Configured to move, with nowhere named. Marking read at least makes
                // progress visible rather than silently leaving the inbox as it was.
                await folder.AddFlagsAsync(handled, MessageFlags.Seen, true, ct);
                break;

            case MailboxDisposition.LeaveAlone:
                break;
        }
    }

    /// <summary>
    /// Where this mailbox is, and what to authenticate with.
    ///
    /// <para>Two shapes. A mailbox pointed at a Stalwart server EntKube manages derives everything
    /// from the component and the chosen account, and its credential is a token minted for the
    /// service account created beside it — nothing about the connection is typed in or stored, so a
    /// server renamed or re-addressed is followed rather than remembered wrongly. A mailbox somebody
    /// entered by hand is exactly what they entered, with the password from the vault.</para>
    /// </summary>
    private async Task<(MailboxConnection Where, string Credential)> ResolveAsync(
        ApplicationDbContext db, SupportMailbox mailbox, CancellationToken ct)
    {
        if (mailbox.StalwartComponentId is not Guid componentId
            || mailbox.StalwartAccountId is not Guid accountId)
        {
            string? password = await vault.GetSupportMailboxPasswordAsync(
                mailbox.TenantId, mailbox.Id, ct);

            return password is null
                ? throw new InvalidOperationException(
                    "No password has been stored for this mailbox.")
                : (MailboxConnectionResolver.FromStoredSettings(mailbox), password);
        }

        // Scoped to the mailbox's own tenant. A mailbox row naming another tenant's Stalwart
        // component would otherwise resolve, and this method goes on to mint a token and connect:
        // the mailbox would be read over IMAP from a server its tenant does not own.
        ClusterComponent component = await db.ClusterComponents
            .FirstOrDefaultAsync(
                c => c.Id == componentId && c.Cluster.TenantId == mailbox.TenantId, ct)
            ?? throw new InvalidOperationException(
                "The mail server this mailbox is on no longer exists. Choose another on the "
                + "Support mailbox tab.");

        StalwartComponentConfig config = await db.StalwartComponentConfigs
            .FirstOrDefaultAsync(c => c.ClusterComponentId == componentId, ct)
            ?? throw new InvalidOperationException(
                "The mail server this mailbox is on is no longer configured.");

        StalwartMailAccount account = await db.StalwartMailAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new InvalidOperationException(
                "The mail account this mailbox uses has been removed. Choose another on the "
                + "Support mailbox tab.");

        StalwartMailDomain domain = await db.StalwartMailDomains
            .FirstOrDefaultAsync(d => d.Id == account.DomainId, ct)
            ?? throw new InvalidOperationException(
                "The domain of the mail account this mailbox uses has been removed.");

        MailboxConnection where =
            MailboxConnectionResolver.Resolve(config, component, account, domain)
            ?? throw new InvalidOperationException(
                "IMAP is switched off on that mail server, so there is nothing to read. Turn it on "
                + "in the Mail tab's protocols and apply the configuration.");

        if (!where.UseOAuth)
        {
            // The account's own credential, not the mailbox's. The mailbox slot holds whatever was
            // typed for a hand-entered server, and presenting that to a different account on a
            // different server is a wrong password offered on every poll — which is how an account
            // gets locked out and, on a server with auto-ban, how the poller's address gets banned.
            string? password = await vault.GetComponentSecretValueAsync(
                mailbox.TenantId, componentId,
                StalwartManifestBuilder.AccountPasswordSecretName(account.Id), ct);

            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "No password has been created for this mailbox yet. Save it again on the Support "
                    + "mailbox tab and one is created for it.");
            }

            // The password exists here and the server has never been told it. Distinguished on
            // purpose: it is not a rejected credential, and the remedy is applying the mail server
            // rather than anything about this mailbox.
            if (account.PasswordSetAt is DateTime setAt
                && (config.LastAppliedAt is null || config.LastAppliedAt < setAt))
            {
                throw new CredentialPendingApplyException(
                    $"A password was created for {where.Username}, but the mail server has not been "
                    + "applied since — so Stalwart does not know it yet and answers every login with a "
                    + "temporary failure. Apply the mail server's configuration on its Components tab, "
                    + "and fetching starts on its own.");
            }

            return (where, password);
        }

        if (string.IsNullOrWhiteSpace(mailbox.OAuthClientId)
            || string.IsNullOrWhiteSpace(mailbox.OAuthTokenEndpoint))
        {
            throw new InvalidOperationException(
                "That mail server authenticates against OIDC, which accepts only bearer tokens, and "
                + "no service account has been created for this mailbox yet. Save it again on the "
                + "Support mailbox tab.");
        }

        string? clientSecret = await vault.GetSupportMailboxOAuthSecretAsync(
            mailbox.TenantId, mailbox.Id, ct);

        if (clientSecret is null)
        {
            throw new InvalidOperationException(
                $"No secret is stored for the service account '{mailbox.OAuthClientId}'. Save the "
                + "mailbox again to create it.");
        }

        string token = await tokens.GetAsync(
            mailbox.Id, mailbox.OAuthTokenEndpoint!, mailbox.OAuthClientId!, clientSecret,
            mailbox.OAuthScopes ?? "", ct);

        return (where, token);
    }

    private static async Task ConnectAsync(
        ImapClient client, MailboxConnection where, string credential, CancellationToken ct)
    {
        // Implicit TLS on 993, STARTTLS where the server offers it on 143. Never plain:
        // these are patient-facing support messages and a service account's credential.
        SecureSocketOptions tls = where.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        // Explicit, so how long a wedged server can hold the sweep is a decision here
        // rather than whatever MailKit's default happens to be.
        client.Timeout = (int)ConnectionTimeout.TotalMilliseconds;

        if (!where.ValidateCertificateName)
        {
            // Only the name check, and only for a server EntKube deployed and reaches by its
            // in-cluster Service. The certificate is for the hostname its users arrive on, so the
            // name cannot match — and insisting on a match is the wrong check rather than a
            // protection: the connection does not leave the cluster, and the server presenting the
            // certificate is the one EntKube issued it to. Everything else about the chain is still
            // verified, and a mailbox somebody typed in gets the full check.
            client.ServerCertificateValidationCallback = (_, _, _, errors) =>
                errors is System.Net.Security.SslPolicyErrors.None
                    or System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch;
        }

        try
        {
            await client.ConnectAsync(where.Host, where.Port, tls, ct);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            // Name what could not be reached. For a mailbox on a managed server none of this was typed
            // in — the host is derived from the component's release name and namespace — so an operator
            // reading "Resource temporarily unavailable" has no way to know which name was tried, or
            // that a name was involved at all. That address is also the assumption most likely to be
            // wrong: it is only resolvable from inside the cluster the mail server runs in.
            // The guidance belongs here, where which address was used is known. Named for the same
            // reason either way: none of this was typed in, so an operator reading a socket error has no
            // way to see which name was tried — but what to check differs, and a message explaining the
            // in-cluster Service to somebody whose connection went out through the load balancer would
            // send them to the wrong place.
            throw new MailboxUnreachableException(
                $"Could not reach {where.Host}:{where.Port} — {ex.Message}. "
                + (where.ValidateCertificateName
                    ? "That is the mail server's public hostname, which is the address a server "
                      + "authenticating against OIDC has to be reached on. It has to resolve from here "
                      + "and lead back to the mail server's load balancer."
                    : "That is the mail server's in-cluster Service, built from the component's release "
                      + "name and namespace, and it only resolves from inside the cluster that "
                      + "component runs in."),
                ex);
        }

        if (where.UseOAuth)
        {
            // The mail server's directory is OIDC, which validates bearer tokens and nothing else, so
            // the credential is a token and the mechanism has to say so — sending it as a password
            // would be refused by a server that never checks passwords.
            await client.AuthenticateAsync(
                new SaslMechanismOAuthBearer(where.Username, credential), ct);
        }
        else
        {
            await client.AuthenticateAsync(where.Username, credential, ct);
        }
    }

    /// <summary>
    /// The failure in words. MailKit's own messages are usually the server's, which is
    /// what somebody fixing this needs; what it omits is which of the several things that
    /// can go wrong actually did.
    /// </summary>
    public static string Explain(Exception ex) => ex switch
    {
        AuthenticationException => $"The server rejected the username or password: {ex.Message}",
        SslHandshakeException => $"TLS failed — check the port and whether SSL should be on: {ex.Message}",
        FolderNotFoundException => $"No such folder: {ex.Message}",

        // A server that is waiting for something the client will never send looks exactly like this:
        // the greeting never arrives, or arrives as bytes that are not a greeting. On a mail server
        // with the PROXY protocol enabled that is the expected outcome of connecting from an address
        // in its trusted-networks list, because Stalwart requires the header from every peer that
        // matches — there is no per-listener exemption — and an IMAP client sends none.
        // The message already names the address and what it is, decided where that was known.
        MailboxUnreachableException => $"{ex.Message} Nothing was authenticated, so this is not the "
            + "credential.",

        ImapProtocolException or System.Net.Sockets.SocketException or IOException =>
            $"Could not complete the connection: {ex.Message}. Nothing was authenticated, so this is "
            + "not the credential. If the mail server has the PROXY protocol turned on, check that its "
            + "trusted-networks list covers only the load balancer's own subnet — an entry that also "
            + "matches addresses inside the cluster makes the server wait for a PROXY header on every "
            + "connection from them, including this one.",

        OperationCanceledException => "The connection timed out.",
        _ => ex.Message,
    };
}
