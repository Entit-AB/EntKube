using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// The support mailbox: takes messages in, has the analyst propose what to do, and applies
/// what a person accepts.
///
/// <para><b>Nothing here acts on its own.</b> The analyst reads and drafts; every state
/// change goes through a person accepting a suggestion, and their name lands on the
/// resulting ticket event. §14.3 makes confirming a priority a written act, §14.6 makes the
/// timestamps evidence between the parties, and §14.4 makes resolution something the
/// customer agrees to. The value here is that the paperwork is ready, not that the decision
/// is made.</para>
///
/// <para><b>Connecting a mailbox is a separate step.</b> This ingests messages it is
/// handed; fetching them over IMAP needs credentials and a per-tenant mailbox
/// configuration, which is not wired up. The pipeline is complete and testable without it.</para>
/// </summary>
public class SupportMailService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISupportMailAnalyst analyst,
    MailTriageRuleService rules,
    TicketService tickets,
    TimeService time)
{
    /// <summary>
    /// Takes a message in, matches the sender to a customer, and records what the analyst
    /// proposes. Ignores a message already ingested — a mailbox poll will hand over the
    /// same one more than once.
    /// </summary>
    public async Task<InboundMailMessage?> IngestAsync(
        InboundMailMessage message, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        bool seen = await db.InboundMailMessages
            .AnyAsync(m => m.TenantId == message.TenantId && m.MessageId == message.MessageId, ct);

        if (seen)
        {
            return null;
        }

        message.Id = message.Id == Guid.Empty ? Guid.NewGuid() : message.Id;
        message.CustomerId ??= await MatchCustomerAsync(db, message.TenantId, message.FromAddress, ct);

        Customer? customer = message.CustomerId is Guid customerId
            ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct)
            : null;

        List<App> apps = customer is null
            ? []
            : await db.Apps.AsNoTracking().Where(a => a.CustomerId == customer.Id).ToListAsync(ct);

        List<Ticket> openTickets = customer is null
            ? []
            : await db.Tickets.AsNoTracking()
                .Where(t => t.CustomerId == customer.Id
                            && t.Status != TicketStatus.Closed
                            && t.Status != TicketStatus.Rejected)
                .ToListAsync(ct);

        bool bankSpent = false;

        if (customer is not null)
        {
            TimebankStatement bank = await time.GetTimebankAsync(customer.Id, message.SentAt, ct);
            bankSpent = bank.IsExhausted;
        }

        MailTriageRuleSet ruleSet = await rules.GetEffectiveAsync(message.TenantId, ct);

        IReadOnlyList<MailSuggestion> suggestions = await analyst.AnalyseAsync(
            new MailContext(message, customer, apps, openTickets, bankSpent, ruleSet), ct);

        message.Suggestions.AddRange(suggestions);
        message.State = suggestions.Count > 0 ? MailTriageState.Proposed : MailTriageState.Received;

        db.InboundMailMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return message;
    }

    public async Task<List<InboundMailMessage>> GetQueueAsync(
        Guid tenantId, bool includeHandled = false, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<InboundMailMessage> query = db.InboundMailMessages
            .Include(m => m.Suggestions)
            .Include(m => m.Customer)
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId);

        if (!includeHandled)
        {
            query = query.Where(m =>
                m.State == MailTriageState.Received || m.State == MailTriageState.Proposed);
        }

        return await query.OrderByDescending(m => m.SentAt).Take(200).ToListAsync(ct);
    }

    /// <summary>
    /// Applies a suggestion because a person said so, and records that they did.
    ///
    /// <para>Only the two suggestions that create or extend a ticket actually do anything
    /// here. A proposed priority is deliberately not applied on its own: §14.3 requires a
    /// written reason, which comes from the person confirming it on the ticket, not from a
    /// keyword match. The flags are there to be read.</para>
    /// </summary>
    public async Task<Ticket?> AcceptAsync(
        Guid suggestionId, string actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        MailSuggestion? suggestion = await db.MailSuggestions
            .Include(s => s.Message)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct);

        if (suggestion is null || suggestion.State != MailSuggestionState.Pending)
        {
            return null;
        }

        InboundMailMessage message = suggestion.Message;
        Ticket? ticket = null;

        switch (suggestion.Kind)
        {
            case MailSuggestionKind.OpenTicket when message.CustomerId is Guid customerId:
            {
                // The priority the analyst proposed rides along as the customer's proposal,
                // which is exactly what §14.3 treats it as until the first assessment.
                MailSuggestion? proposed = await db.MailSuggestions
                    .FirstOrDefaultAsync(s => s.MessageId == message.Id
                                              && s.Kind == MailSuggestionKind.ProposePriority, ct);

                ticket = await tickets.CreateAsync(
                    message.TenantId,
                    customerId,
                    suggestion.AppId,
                    message.Subject,
                    message.Body,
                    TicketChannel.Email,
                    proposed?.Priority,

                    // §14.3 and §9.1: the clock starts from when it arrived, not from when
                    // somebody got round to triaging it.
                    message.SentAt,
                    message.FromName ?? message.FromAddress,
                    message.FromAddress,
                    ct: ct);

                message.TicketId = ticket.Id;
                break;
            }

            case MailSuggestionKind.AppendToTicket when suggestion.TicketId is Guid ticketId:
            {
                await tickets.AddEventAsync(
                    ticketId, TicketEventKind.Note,
                    $"From {message.FromAddress}: {message.Body}",
                    actor, message.SentAt, customerVisible: true, ct);

                message.TicketId = ticketId;
                ticket = await tickets.GetAsync(ticketId, ct);
                break;
            }
        }

        suggestion.State = MailSuggestionState.Accepted;
        suggestion.DecidedBy = actor;
        suggestion.DecidedAt = at;

        message.State = MailTriageState.Handled;
        message.HandledBy = actor;
        message.HandledAt = at;

        await db.SaveChangesAsync(ct);
        return ticket;
    }

    public async Task RejectAsync(
        Guid suggestionId, string actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        MailSuggestion? suggestion = await db.MailSuggestions.FindAsync([suggestionId], ct);

        if (suggestion is not null)
        {
            suggestion.State = MailSuggestionState.Rejected;
            suggestion.DecidedBy = actor;
            suggestion.DecidedAt = at;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Marks a message as needing nothing — an autoreply, a bounce, spam.</summary>
    public async Task DismissAsync(
        Guid messageId, string actor, DateTime at, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        InboundMailMessage? message = await db.InboundMailMessages.FindAsync([messageId], ct);

        if (message is not null)
        {
            message.State = MailTriageState.Dismissed;
            message.HandledBy = actor;
            message.HandledAt = at;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The customer a sender belongs to, from the §23 contact register. Exact address only:
    /// guessing by domain would attach one customer's mail to another's tickets, and the
    /// register exists precisely so this does not have to be guessed.
    /// </summary>
    private static async Task<Guid?> MatchCustomerAsync(
        ApplicationDbContext db, Guid tenantId, string fromAddress, CancellationToken ct)
    {
        string address = fromAddress.Trim().ToLowerInvariant();

        ContractContact? contact = await db.ContractContacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Email != null && c.IsActive)
            .FirstOrDefaultAsync(c => c.Email!.ToLower() == address, ct);

        return contact?.CustomerId;
    }
}
