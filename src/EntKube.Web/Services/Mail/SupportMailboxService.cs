using System.Collections.Concurrent;
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
public readonly record struct MailPollResult(
    bool Ok, string? Error, int Taken = 0, int Seen = 0, bool AlreadyRunning = false,
    int FromJunk = 0);

/// <summary>
/// The tenant's support mailbox: its settings, and the fetch that fills the triage queue.
///
/// <para><b>Fetching is all this does.</b> What arrives goes to
/// <see cref="SupportMailService.IngestAsync"/>, which records it and has the analyst
/// propose; no ticket is opened and no priority is set without a person. A mail server
/// being reachable does not change who decides anything.</para>
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
                || existing.Folder != settings.Folder;

            existing.Host = settings.Host;
            existing.Port = settings.Port;
            existing.UseSsl = settings.UseSsl;
            existing.Username = settings.Username;
            existing.Address = settings.Address;
            existing.Folder = settings.Folder;
            existing.IsEnabled = settings.IsEnabled;
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
        }

        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrEmpty(password))
        {
            await vault.SetSupportMailboxPasswordAsync(
                existing.TenantId, existing.Id, password, ct);
        }

        return existing;
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

    public Task<bool> HasPasswordAsync(SupportMailbox mailbox, CancellationToken ct = default) =>
        vault.HasSupportMailboxPasswordAsync(mailbox.TenantId, mailbox.Id, ct);

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

        string? password = await vault.GetSupportMailboxPasswordAsync(tenantId, mailbox.Id, ct);

        if (password is null)
        {
            return new MailPollResult(false, "No password has been stored for this mailbox.");
        }

        try
        {
            using ImapClient client = new();
            await ConnectAsync(client, mailbox, password, ct);

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
            string? password = await vault.GetSupportMailboxPasswordAsync(tenantId, mailbox.Id, ct);

            if (password is null)
            {
                throw new InvalidOperationException("No password has been stored for this mailbox.");
            }

            // The register of customer domains, so a message the filter junked can be told from
            // the spam beside it. Read here rather than in the fetch: one query per poll, not one
            // per message.
            HashSet<string> customerDomains = new(
                await db.CustomerEmailDomains
                    .Where(d => d.TenantId == tenantId)
                    .Select(d => d.Domain)
                    .ToListAsync(ct),
                StringComparer.OrdinalIgnoreCase);

            (int taken, int seen, int fromJunk) =
                await FetchAsync(mailbox, password, customerDomains, ct);

            mailbox.LastPolledAt = DateTime.UtcNow;
            mailbox.LastError = null;
            mailbox.ConsecutiveFailures = 0;

            if (taken > 0)
            {
                mailbox.LastMessageAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            return new MailPollResult(true, null, taken, seen, FromJunk: fromJunk);
        }
        catch (Exception ex)
        {
            string explained = Explain(ex);

            mailbox.LastPolledAt = DateTime.UtcNow;
            mailbox.LastError = explained;
            mailbox.ConsecutiveFailures++;
            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                ex,
                "Support mailbox poll failed for tenant {Tenant} ({Failures} in a row): {Error}",
                tenantId, mailbox.ConsecutiveFailures, explained);

            return new MailPollResult(false, explained);
        }
    }

    /// <summary>The fetch itself, separated from the bookkeeping around it.</summary>
    private async Task<(int Taken, int Seen, int FromJunk)> FetchAsync(
        SupportMailbox mailbox, string password, IReadOnlySet<string> customerDomains,
        CancellationToken ct)
    {
        using ImapClient client = new();
        await ConnectAsync(client, mailbox, password, ct);

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

        int fromJunk = await SweepJunkAsync(client, mailbox, customerDomains, ct);

        await client.DisconnectAsync(true, ct);

        return (taken + fromJunk, ids.Count, fromJunk);
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
    private async Task<int> SweepJunkAsync(
        ImapClient client, SupportMailbox mailbox, IReadOnlySet<string> customerDomains,
        CancellationToken ct)
    {
        if (customerDomains.Count == 0)
        {
            return 0;
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
            return 0;
        }

        if (junk is null)
        {
            return 0;
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

        return rescued;
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

    private static async Task ConnectAsync(
        ImapClient client, SupportMailbox mailbox, string password, CancellationToken ct)
    {
        // Implicit TLS on 993, STARTTLS where the server offers it on 143. Never plain:
        // these are patient-facing support messages and a service account's password.
        SecureSocketOptions tls = mailbox.UseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.StartTls;

        // Explicit, so how long a wedged server can hold the sweep is a decision here
        // rather than whatever MailKit's default happens to be.
        client.Timeout = (int)ConnectionTimeout.TotalMilliseconds;

        await client.ConnectAsync(mailbox.Host, mailbox.Port, tls, ct);
        await client.AuthenticateAsync(mailbox.Username, password, ct);
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
        ImapProtocolException => $"The server answered unexpectedly: {ex.Message}",
        OperationCanceledException => "The connection timed out.",
        _ => ex.Message,
    };
}
