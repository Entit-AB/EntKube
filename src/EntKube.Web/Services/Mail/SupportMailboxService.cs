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
public readonly record struct MailPollResult(
    bool Ok, string? Error, int Taken = 0, int Seen = 0, bool AlreadyRunning = false);

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

            (int taken, int seen) = await FetchAsync(mailbox, password, ct);

            mailbox.LastPolledAt = DateTime.UtcNow;
            mailbox.LastError = null;
            mailbox.ConsecutiveFailures = 0;

            if (taken > 0)
            {
                mailbox.LastMessageAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync(ct);

            return new MailPollResult(true, null, taken, seen);
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
    private async Task<(int Taken, int Seen)> FetchAsync(
        SupportMailbox mailbox, string password, CancellationToken ct)
    {
        using ImapClient client = new();
        await ConnectAsync(client, mailbox, password, ct);

        IMailFolder folder = await client.GetFolderAsync(mailbox.Folder, ct);

        FolderAccess access = mailbox.Disposition == MailboxDisposition.LeaveAlone
            ? FolderAccess.ReadOnly
            : FolderAccess.ReadWrite;

        await folder.OpenAsync(access, ct);

        // A rebuilt mailbox renumbers everything, so the old cursor means nothing.
        bool cursorValid = mailbox.LastUidValidity == folder.UidValidity;
        uint after = cursorValid ? mailbox.LastSeenUid ?? 0 : 0;

        IList<UniqueId> ids = await folder.SearchAsync(
            SearchQuery.Uids(new UniqueIdRange(new UniqueId(after + 1), UniqueId.MaxValue)), ct);

        int taken = 0;
        uint highest = after;
        List<UniqueId> handled = [];

        foreach (UniqueId id in ids)
        {
            ct.ThrowIfCancellationRequested();

            MimeMessage message = await folder.GetMessageAsync(id, ct);

            InboundMailMessage row = MailMessageReader.Read(message, mailbox.TenantId, DateTime.UtcNow);

            if (await mail.IngestAsync(row, ct) is not null)
            {
                taken++;
            }

            handled.Add(id);
            highest = Math.Max(highest, id.Id);
        }

        await DisposeOfAsync(folder, handled, mailbox, ct);

        // Written only after the messages are in: a crash between fetching and saving
        // should re-read them, which dedupe makes harmless, rather than skip them.
        mailbox.LastSeenUid = highest;
        mailbox.LastUidValidity = folder.UidValidity;

        await folder.CloseAsync(false, ct);
        await client.DisconnectAsync(true, ct);

        return (taken, ids.Count);
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
