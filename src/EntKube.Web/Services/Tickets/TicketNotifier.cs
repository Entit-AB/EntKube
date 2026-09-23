using EntKube.Web.Data;
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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    SmtpSettingsResolver smtp,
    OnCallService onCall,
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
            using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

            SmtpSettings settings = await smtp.ResolveAsync(ct);

            if (!settings.IsConfigured)
            {
                logger.LogWarning(
                    "Ticket #{Number} was opened and nobody could be told: no SMTP is configured.",
                    ticket.Number);
                return;
            }

            string? appName = ticket.AppId is Guid appId
                ? await db.Apps.AsNoTracking()
                    .Where(a => a.Id == appId).Select(a => a.Name).FirstOrDefaultAsync(ct)
                : null;

            string from = await SenderAddressAsync(db, ticket.TenantId, settings, ct);

            Acknowledgement receipt =
                TicketAcknowledgement.For(ticket, window, appName, responseDue);

            // ── The reporter ──
            if (!string.IsNullOrWhiteSpace(ticket.RequestedByEmail))
            {
                await SendAsync(settings, from, ticket.RequestedByEmail, receipt, ticket, ct);
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
                    await SendAsync(settings, from, address, receipt, ticket, ct);
                }
            }

            // ── Whoever is on call ──
            OnCallShift? shift = await onCall.GetCurrentOnCallAsync(ticket.TenantId, ct);

            if (!string.IsNullOrWhiteSpace(shift?.AssigneeEmail))
            {
                await SendAsync(settings, from, shift.AssigneeEmail, Internal(ticket, appName, responseDue), ticket, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Could not announce ticket #{Number}; the ticket stands.", ticket.Number);
        }
    }

    /// <summary>
    /// What the on-call engineer gets. Deliberately not the customer's receipt: they need
    /// the number, the clock and where to look, not an explanation of what a priority is.
    /// </summary>
    private static Acknowledgement Internal(Ticket ticket, string? appName, DateTime? responseDue)
    {
        string due = responseDue is DateTime d
            ? BusinessCalendar.ToLocal(d).ToString("ddd d MMM HH:mm")
            : "no response target";

        return new Acknowledgement(
            $"{TicketAcknowledgement.Reference(ticket.Number)} {ticket.Priority} — {ticket.Title}",
            $"""
             A new ticket is open.

               Reference:   {TicketAcknowledgement.Reference(ticket.Number)}
               Priority:    {ticket.Priority} (as reported — confirm it in writing, §14.3)
               Application: {appName ?? "not identified"}
               Reported by: {ticket.RequestedBy ?? ticket.RequestedByEmail ?? "unknown"}
               Respond by:  {due}

             {ticket.Description}
             """);
    }

    /// <summary>
    /// The addresses §14.1 says must hear about an incident: the customer's technical
    /// contact and their deputy.
    /// </summary>
    private static async Task<List<string>> DesignatedContactsAsync(
        ApplicationDbContext db, Ticket ticket, CancellationToken ct) =>
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
    /// The tenant's support address when one is configured, so a reply lands back in the
    /// mailbox that is being polled rather than wherever alerts happen to come from.
    /// </summary>
    private static async Task<string> SenderAddressAsync(
        ApplicationDbContext db, Guid tenantId, SmtpSettings settings, CancellationToken ct)
    {
        string? support = await db.SupportMailboxes.AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Address)
            .FirstOrDefaultAsync(ct);

        return string.IsNullOrWhiteSpace(support) ? settings.From : support;
    }

    private async Task SendAsync(
        SmtpSettings settings, string from, string to, Acknowledgement message, Ticket ticket,
        CancellationToken ct)
    {
        try
        {
            MimeMessage mail = new();
            mail.From.Add(MailboxAddress.Parse(from));
            mail.To.Add(MailboxAddress.Parse(to));
            mail.Subject = message.Subject;
            mail.Body = new TextPart("plain") { Text = message.Body };

            // A reply carries this back as In-Reply-To, which threads it onto the ticket
            // even where the subject has been mangled by a client or a forward.
            mail.MessageId = $"ticket-{ticket.Number}.{Guid.NewGuid():N}@entkube";

            using SmtpClient client = new();
            await client.ConnectAsync(
                settings.Host, settings.Port,
                settings.UseSsl ? SecureSocketOptions.Auto : SecureSocketOptions.None, ct);

            if (!string.IsNullOrEmpty(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password ?? "", ct);
            }

            await client.SendAsync(mail, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            // One recipient failing must not stop the others being told.
            logger.LogWarning(
                ex, "Could not tell {To} about ticket #{Number}.", to, ticket.Number);
        }
    }
}
