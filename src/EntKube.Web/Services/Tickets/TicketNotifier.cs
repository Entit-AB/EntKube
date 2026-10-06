using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace EntKube.Web.Services.Tickets;

/// <summary>
/// Tells the people a new ticket concerns that it exists.
///
/// <para>Three audiences, and only the first of them is new work for a person:</para>
/// <list type="bullet">
/// <item>The person who reported it, who gets a receipt with the number and the response
/// target. Sent without anyone pressing a button — see <see cref="TicketAcknowledgement"/>
/// for why a receipt is the one thing that may be.</item>
/// <item>The customer's designated contact, whom §14.1 requires be told of every incident.
/// The agreement has said so since the beginning and nothing has ever sent them
/// anything.</item>
/// <item>Whoever is on call, because a ticket nobody is told about is a ticket found by
/// looking.</item>
/// </list>
///
/// <para><b>Failing to tell somebody never fails the ticket.</b> A P1 that cannot be
/// recorded because a mail server is refusing connections is an outage made out of
/// bookkeeping. Every failure here is logged and swallowed, and the ticket stands.</para>
/// </summary>
public class TicketNotifier(
    IDbContextFactory<SupportDbContext> dbFactory,
    SmtpSettingsResolver smtp,
    OnCallService onCall,
    TicketReference references,
    ILogger<TicketNotifier> logger)
{
    /// <summary>
    /// Announces a newly created ticket. Never throws.
    /// </summary>
    /// <param name="ticket">The ticket as it was just written.</param>
    /// <param name="window">The support window resolved for it.</param>
    /// <param name="responseDue">When §14.4's response target expires, if it has one.</param>
    public async Task AnnounceAsync(
        Ticket ticket, SupportWindow window, DateTime? responseDue,
        CancellationToken ct = default)
    {
        try
        {
            using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

            // The tenant's own support mail server first. It is where the address the receipt
            // comes from actually lives, so it is the only relay whose SPF and DKIM match the
            // message — and for a mailbox chosen rather than typed it is the only thing anybody
            // configured, which is why receipts used to go nowhere at all.
            SmtpSettings settings = await smtp.ResolveAsync(ticket.TenantId, ct);

            if (!settings.IsConfigured)
            {
                logger.LogWarning(
                    "Ticket #{Number} was opened and nobody could be told: there is nowhere to "
                    + "send from ({Source}). Either the tenant's support mailbox is not on a mail "
                    + "server EntKube manages, or submission is switched off on it, or no SMTP "
                    + "provider is configured.",
                    ticket.Number, settings.Source);
                return;
            }

            string? appName = ticket.AppId is Guid appId
                ? await db.Apps.AsNoTracking()
                    .Where(a => a.Id == appId).Select(a => a.Name).FirstOrDefaultAsync(ct)
                : null;

            string from = await SenderAddressAsync(db, ticket, settings, ct);

            string senderName = await SenderNameAsync(db, ticket, ct);

            // Minted once and used in both receipts, so the customer and the engineer are
            // quoting the same string at each other.
            string reference = references.For(ticket.Number);

            Acknowledgement receipt =
                TicketAcknowledgement.For(ticket, window, appName, responseDue, reference);

            // ── The reporter ──
            if (!string.IsNullOrWhiteSpace(ticket.RequestedByEmail))
            {
                await SendAsync(settings, from, ticket.RequestedByEmail, receipt, ticket, senderName, ct);
            }
            else
            {
                logger.LogInformation(
                    "Ticket #{Number} has no reporter address, so no receipt was sent.",
                    ticket.Number);
            }

            // ── §14.1's designated contact, when that is somebody else ──
            foreach (string address in await DesignatedContactsAsync(db, ticket, ct))
            {
                if (!address.Equals(ticket.RequestedByEmail, StringComparison.OrdinalIgnoreCase))
                {
                    await SendAsync(settings, from, address, receipt, ticket, senderName, ct);
                }
            }

            // ── Whoever is on call ──
            OnCallShift? shift = await onCall.GetCurrentOnCallAsync(ticket.TenantId, ct);

            if (!string.IsNullOrWhiteSpace(shift?.AssigneeEmail))
            {
                await SendAsync(
                    settings, from, shift.AssigneeEmail,
                    Internal(ticket, appName, responseDue, reference), ticket, senderName, ct);
            }
        }
        catch (Exception ex)
        {
            // The outer net, for everything that is not the sending itself — resolving the
            // SMTP settings, reading the contacts, asking who is on call. Sending has its
            // own guard per recipient, which is the one that catches a dead mail server.
            logger.LogError(
                ex, "Could not announce ticket #{Number}; the ticket stands.", ticket.Number);
        }
    }

    /// <summary>
    /// Tells a technician a ticket is theirs, in the same mail the on-call engineer gets —
    /// so a reply to either reaches the customer and there is one wording to keep right.
    ///
    /// <para><b>Why assignment sends mail at all.</b> A queue nobody is looking at is the
    /// state this whole subsystem exists to get out of. The assignment is the moment there
    /// is a particular person to tell, and telling them by mail is also what gives them
    /// something to reply to — which is the only way to answer the customer without opening
    /// the application.</para>
    ///
    /// <para>Never throws. An assignment that could not be announced is still an
    /// assignment, and losing it because a mail server was down would be worse than a
    /// technician finding out from the queue.</para>
    /// </summary>
    public async Task AnnounceAssignmentAsync(
        Ticket ticket, string toEmail, SupportWindow window, DateTime? responseDue,
        CancellationToken ct = default)
    {
        try
        {
            using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

            SmtpSettings settings = await smtp.ResolveAsync(ticket.TenantId, ct);

            if (!settings.IsConfigured)
            {
                logger.LogWarning(
                    "Ticket #{Number} was assigned to {To} and they could not be told: there "
                    + "is nowhere to send from ({Source}).",
                    ticket.Number, toEmail, settings.Source);
                return;
            }

            string? appName = ticket.AppId is Guid appId
                ? await db.Apps.AsNoTracking()
                    .Where(a => a.Id == appId).Select(a => a.Name).FirstOrDefaultAsync(ct)
                : null;

            // From the support address, not from a no-reply: the whole point is that the
            // reply goes somewhere we read.
            string from = await SenderAddressAsync(db, ticket, settings, ct);

            await SendAsync(
                settings, from, toEmail,
                Internal(ticket, appName, responseDue, references.For(ticket.Number)),
                ticket, await SenderNameAsync(db, ticket, ct), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not tell {To} about ticket #{Number}; the assignment stands.",
                toEmail, ticket.Number);
        }
    }

    /// <summary>
    /// Sends one of our own people's words on to the customer, from the support address.
    ///
    /// <para><b>Why it goes to the §14.1 contacts and not only to the reporter.</b> The
    /// reporter is who asked, but §14.1 names the technical contact and their deputy as the
    /// people kept informed, and the receipt already went to all three. Answering a narrower
    /// set than we acknowledged would leave somebody holding a reference to a conversation
    /// they are not party to.</para>
    ///
    /// <para>Returns who it actually reached, so the ticket's history can record that rather
    /// than an intention. Never throws: see the class remarks.</para>
    /// </summary>
    public async Task<List<string>> ReplyToCustomerAsync(
        Ticket ticket, string said, CancellationToken ct = default)
    {
        List<string> reached = [];

        try
        {
            using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

            SmtpSettings settings = await smtp.ResolveAsync(ticket.TenantId, ct);

            if (!settings.IsConfigured)
            {
                logger.LogWarning(
                    "A reply on ticket #{Number} could not be sent: there is nowhere to send "
                    + "from ({Source}).",
                    ticket.Number, settings.Source);

                return reached;
            }

            string from = await SenderAddressAsync(db, ticket, settings, ct);
            string reference = references.For(ticket.Number);

            // The reference in the subject and the ticket's own Message-Id on the message,
            // so the customer's reply comes back onto this ticket by either route.
            Acknowledgement reply = new($"{reference} {ticket.Title}", said);

            List<string> to = [];

            if (!string.IsNullOrWhiteSpace(ticket.RequestedByEmail))
            {
                to.Add(ticket.RequestedByEmail);
            }

            foreach (string address in await DesignatedContactsAsync(db, ticket, ct))
            {
                if (!to.Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)))
                {
                    to.Add(address);
                }
            }

            string senderName = await SenderNameAsync(db, ticket, ct);

            foreach (string address in to)
            {
                if (await SendAsync(settings, from, address, reply, ticket, senderName, ct))
                {
                    reached.Add(address);
                }
            }

            if (reached.Count == 0)
            {
                logger.LogWarning(
                    "A reply on ticket #{Number} reached nobody: it has no reporter address "
                    + "and no §14.1 contacts.",
                    ticket.Number);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not send a reply on ticket #{Number}.", ticket.Number);
        }

        return reached;
    }

    /// <summary>
    /// What the on-call engineer gets. Deliberately not the customer's receipt: they need
    /// the number, the clock and where to look, not an explanation of what a priority is.
    /// </summary>
    private static Acknowledgement Internal(
        Ticket ticket, string? appName, DateTime? responseDue, string reference)
    {
        string due = responseDue is DateTime d
            ? BusinessCalendar.ToLocal(d).ToString("ddd d MMM HH:mm")
            : "no response target";

        // The sentinel first, before anything the customer must not be sent. Everything
        // below it — the deadline, the unconfirmed priority, the reporter's name, the
        // description — is held back by SupportReplyBody when this mail is replied to.
        return new Acknowledgement(
            $"{reference} {ticket.Priority} — {ticket.Title}",
            $"""
             {SupportReplyBody.Block}
             A new ticket is open. Replying to this message answers the customer directly,
             from the support address, and is recorded on the ticket.

               Reference:   {reference}
               Priority:    {ticket.Priority} (as reported — confirm it in writing, §14.3)
               Application: {appName ?? "not identified"}
               Reported by: {ticket.RequestedBy ?? ticket.RequestedByEmail ?? "unknown"}
               Respond by:  {due}

             {ticket.Description}
             """);
    }

    /// <summary>
    /// Whose support desk this is, for the From line. The tenant's own name, because the
    /// customer knows who they bought the service from.
    ///
    /// <para>One derivation rather than three. Assignment mail and a relayed reply go out
    /// from the same address as the receipt, so a different name on them would read as a
    /// different desk.</para>
    /// </summary>
    private static async Task<string> SenderNameAsync(
        SupportDbContext db, Ticket ticket, CancellationToken ct) =>
        await db.Tenants.AsNoTracking()
            .Where(t => t.Id == ticket.TenantId).Select(t => t.Name).FirstOrDefaultAsync(ct)
            is string tenant && !string.IsNullOrWhiteSpace(tenant)
                ? $"{tenant} support"
                : "Support";

    /// <summary>
    /// The addresses §14.1 says must hear about an incident: the customer's technical
    /// contact and their deputy.
    /// </summary>
    private static async Task<List<string>> DesignatedContactsAsync(
        SupportDbContext db, Ticket ticket, CancellationToken ct) =>
        await db.ContractContacts.AsNoTracking()
            .Where(c => c.CustomerId == ticket.CustomerId
                        && c.IsActive
                        && c.Party == ContractParty.Customer
                        && (c.Role == ContractContactRole.TechnicalContact
                            || c.Role == ContractContactRole.Deputy)
                        && c.Email != null)
            .Select(c => c.Email!)
            .ToListAsync(ct);

    /// <summary>
    /// Who the mail comes from: the customer's own support address if they have one, then
    /// the tenant's, then whatever the SMTP configuration names.
    ///
    /// <para>The customer's own address first is the point of giving them one. Answering
    /// from the generic address a customer who wrote to theirs teaches them to use the
    /// generic address, and the routing that address exists for stops happening.</para>
    /// </summary>
    private static async Task<string> SenderAddressAsync(
        SupportDbContext db, Ticket ticket, SmtpSettings settings, CancellationToken ct)
    {
        string? theirs = await db.CustomerSupportAddresses.AsNoTracking()
            .Where(a => a.CustomerId == ticket.CustomerId && a.ReplyFromThis)
            .OrderBy(a => a.Address)
            .Select(a => a.Address)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(theirs))
        {
            return theirs;
        }

        string? tenant = await db.SupportMailboxes.AsNoTracking()
            .Where(m => m.TenantId == ticket.TenantId)
            .Select(m => m.Address)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(tenant) ? settings.From : tenant;
    }

    /// <summary>
    /// Sends one message to one recipient. Returns whether it left, because a caller that
    /// records who was told cannot record an intention — a reply logged on a ticket as
    /// having reached the customer, when the relay refused it, is worse than no log at all.
    /// </summary>
    private async Task<bool> SendAsync(
        SmtpSettings settings, string from, string to, Acknowledgement message, Ticket ticket,
        string senderName, CancellationToken ct)
    {
        try
        {
            MimeMessage mail = new();

            // With a name on it. A bare address in From is a small thing on its own and a
            // consistent one across every signal a filter weighs — and the person reading
            // this on a phone sees who it is from before they see anything else.
            MailboxAddress sender = MailboxAddress.Parse(from);
            mail.From.Add(string.IsNullOrWhiteSpace(sender.Name)
                ? new MailboxAddress(senderName, sender.Address)
                : sender);

            mail.To.Add(MailboxAddress.Parse(to));
            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Body };

            // A reply carries this back as In-Reply-To, which is how it is threaded onto
            // the ticket even where the subject has been mangled by a client or a forward.
            // SupportMessageId is the only thing that writes this format and the only thing
            // that reads it.
            //
            // Built from the sending address, so its right-hand side is a domain that
            // exists and matches the From. It used to be the bare word "entkube", which
            // resolves to nothing and matches nothing — a cheap, old signal to a filter
            // that a message came from something which does not send much mail, spent at
            // the moment a receipt is judged by a mailbox that has never heard of us.
            mail.MessageId = SupportMessageId.For(ticket.Number, from);

            // We say what we are, in the two places a mail system looks. RFC 3834's header
            // is what stops the recipient's out-of-office answering this, and Exchange reads
            // the second one. It matters more now that a ticket can be opened by an arriving
            // message: without these, our receipt and somebody's holiday responder can keep
            // each other company all week, and our own door reads the same headers before
            // answering anything (see MailMessageReader.LooksAutomated).
            mail.Headers.Add("Auto-Submitted", "auto-replied");
            mail.Headers.Add("X-Auto-Response-Suppress", "All");

            using SmtpClient client = new();

            // A mail server EntKube deployed holds a certificate for the hostname its users
            // reach it on, and this connection is made to its in-cluster Service name instead —
            // so the name cannot match, and insisting fails a healthy handshake. The same
            // reasoning the fetching side has always applied; see MailboxConnection.
            if (!settings.ValidateCertificateName)
            {
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            }

            await client.ConnectAsync(
                settings.Host, settings.Port,
                settings.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None, ct);

            if (!string.IsNullOrEmpty(settings.Username))
            {
                if (settings.UseOAuth)
                {
                    // An OIDC directory validates bearer tokens and nothing else, so a password
                    // presented here is not wrong — it is unverifiable. OAUTHBEARER rather than
                    // XOAUTH2, and the same mechanism the fetching side presents to the same
                    // server: two halves of one login disagreeing about how to say "this is a
                    // token" is a failure that only shows up in one direction.
                    await client.AuthenticateAsync(
                        new SaslMechanismOAuthBearer(settings.Username, settings.Password ?? ""), ct);
                }
                else
                {
                    await client.AuthenticateAsync(settings.Username, settings.Password ?? "", ct);
                }
            }

            await client.SendAsync(mail, ct);
            await client.DisconnectAsync(true, ct);

            return true;
        }
        catch (Exception ex)
        {
            // One recipient failing must not stop the others being told. The host is named
            // because "could not send" without it leaves an operator guessing which of the
            // three possible senders was even tried.
            logger.LogWarning(
                ex, "Could not tell {To} about ticket #{Number} via {Host} ({Source}).",
                to, ticket.Number, settings.Host, settings.Source);

            return false;
        }
    }
}
