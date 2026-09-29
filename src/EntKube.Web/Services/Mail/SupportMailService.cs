using EntKube.Web.Data;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Mail;

/// <summary>
/// Which application a person chose for a message, as distinct from not having chosen.
///
/// <para>A plain <c>Guid?</c> cannot say "somebody looked and it is none of these", which
/// is a real answer and a different one from "nobody said". The analyst recognises an
/// application by its name appearing in a sentence, so it is sometimes confidently wrong,
/// and clearing its guess has to be possible.</para>
/// </summary>
/// <param name="AppId">The application, or null for none of them.</param>
public readonly record struct AppChoice(Guid? AppId)
{
    /// <summary>A person looked and it is none of the customer's applications.</summary>
    public static AppChoice None => new((Guid?)null);
}

/// <summary>
/// The support mailbox: takes messages in, has the analyst propose what to do, and applies
/// what a person accepts.
///
/// <para><b>One thing here acts on its own, and only one.</b> A fault report from a known
/// contact opens its ticket the moment it lands, which is what gives the customer a number
/// to quote — <see cref="ArrivalPolicy"/> holds the rule and says why a receipt is the one
/// message that may go out unread. Everything after that still goes through a person
/// accepting a suggestion, with their name on the resulting ticket event: §14.3 makes
/// confirming a priority a written act, §14.6 makes the timestamps evidence between the
/// parties, and §14.4 makes resolution something the customer agrees to. The value here is
/// that the paperwork is ready, not that the decision is made.</para>
///
/// <para><b>Where the messages come from is somebody else's problem.</b> This takes in
/// whatever it is handed and is testable without a mail server;
/// <see cref="SupportMailboxService"/> does the fetching over IMAP, and a message can
/// equally well be handed over by hand or by a test.</para>
/// </summary>
public class SupportMailService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ISupportMailAnalyst analyst,
    MailTriageRuleService rules,
    TicketService tickets,
    TimeService time,
    // Optional so that the tests, which build this by hand, are not made to care. What it
    // records is why a message was or was not answered on arrival, which is the only
    // account there is of a decision that leaves no mark when it goes the quiet way.
    ILogger<SupportMailService>? logger = null)
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
        bool placedOnTheSendersWord = false;
        bool placedOnTheFromAddress = false;

        if (message.CustomerId is null)
        {
            (message.CustomerId, placedOnTheSendersWord, placedOnTheFromAddress) =
                await MatchCustomerAsync(
                    db, message.TenantId, message.FromAddress,
                    message.DeliveredTo, message.ToAddresses, ct);
        }

        message.Suggestions.AddRange(
            await ProposeAsync(db, message, ct, placedOnTheSendersWord, placedOnTheFromAddress));

        message.State = message.Suggestions.Count > 0
            ? MailTriageState.Proposed
            : MailTriageState.Received;

        db.InboundMailMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return await AcknowledgeOnArrivalAsync(db, message, ct);
    }

    /// <summary>
    /// Opens the ticket straight away where <see cref="ArrivalPolicy"/> allows it, so that
    /// the person who reported the fault has its number within a poll of writing in rather
    /// than whenever the queue is next read.
    ///
    /// <para><b>The receipt is not sent from here.</b> Opening the ticket is what sends it:
    /// <c>TicketService.CreateAsync</c> announces every ticket it creates, whatever opened
    /// it, from the support address and with the reference in the subject. Adding a second
    /// path that sends mail would be a second wording to keep in step with the first.</para>
    ///
    /// <para><b>It re-reads the message afterwards.</b> Accepting runs in its own context
    /// and moves the row to handled; returning the copy this method was given would hand
    /// the caller a message that still says nobody has touched it.</para>
    ///
    /// <para>Never throws. A mail server that will not take the receipt, or a ticket that
    /// cannot be opened, must leave the message in the queue where a person will find it —
    /// not lose the report.</para>
    /// </summary>
    private async Task<InboundMailMessage> AcknowledgeOnArrivalAsync(
        ApplicationDbContext db, InboundMailMessage message, CancellationToken ct)
    {
        try
        {
            bool acknowledges = await db.SupportMailboxes.AsNoTracking()
                .Where(m => m.TenantId == message.TenantId)
                .Select(m => m.AcknowledgeOnArrival)
                .FirstOrDefaultAsync(ct);

            ArrivalDecision decision =
                ArrivalPolicy.Decide(message, message.Suggestions, acknowledges);

            if (!decision.OpenNow)
            {
                logger?.LogInformation(
                    "Support mail from {From} was left for a person: {Reason}",
                    message.FromAddress, decision.Reason);

                return message;
            }

            MailSuggestion open =
                message.Suggestions.First(s => s.Kind == MailSuggestionKind.OpenTicket);

            Ticket? ticket = await AcceptAsync(
                open.Id, ArrivalPolicy.Actor, DateTime.UtcNow, ct: ct);

            logger?.LogInformation(
                "Support mail from {From} opened ticket #{Number} on arrival: {Reason}",
                message.FromAddress, ticket?.Number, decision.Reason);

            using ApplicationDbContext fresh = await dbFactory.CreateDbContextAsync(ct);

            return await fresh.InboundMailMessages
                .Include(m => m.Suggestions)
                .FirstOrDefaultAsync(m => m.Id == message.Id, ct) ?? message;
        }
        catch (Exception ex)
        {
            logger?.LogError(
                ex,
                "Could not open a ticket on arrival for support mail from {From}; it stays in "
                + "the queue.",
                message.FromAddress);

            return message;
        }
    }

    /// <summary>
    /// What the analyst makes of a message, given everything known about who sent it.
    ///
    /// <para>Separated from ingestion because it has to be able to run twice. Almost
    /// everything the analyst can say depends on the customer having been placed — which
    /// application is named is decided against <em>that customer's</em> applications, and
    /// the hour bank is theirs — so a message that arrives unplaced and is assigned by hand
    /// afterwards deserves the analysis it could not have had the first time.</para>
    /// </summary>
    /// <param name="placedOnTheSendersWord">
    /// Whether the customer was decided only by an address the sender typed. False when a
    /// person said so, which is the other way a message gets placed.
    /// </param>
    /// <param name="placedOnTheFromAddress">
    /// Whether the customer was decided by the From address. Also false when a person said
    /// so — their judgement does not rest on a header.
    /// </param>
    private async Task<IReadOnlyList<MailSuggestion>> ProposeAsync(
        ApplicationDbContext db, InboundMailMessage message, CancellationToken ct,
        bool placedOnTheSendersWord = false, bool placedOnTheFromAddress = false)
    {
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

        return await analyst.AnalyseAsync(
            new MailContext(
                message, customer, apps, openTickets, bankSpent, ruleSet,
                placedOnTheSendersWord, placedOnTheFromAddress),
            ct);
    }

    /// <summary>
    /// Says who a message was from, when nothing placed it automatically, and analyses it
    /// again now that the answer is known.
    /// </summary>
    /// <param name="rememberDomain">
    /// Also add the sender's domain to that customer's register, so the next message from
    /// anyone there is placed without being asked about. Offered rather than done, because
    /// it is a statement about every future sender at that domain and not only this one.
    /// </param>
    public async Task<InboundMailMessage?> AssignCustomerAsync(
        Guid messageId, Guid customerId, string actor, bool rememberDomain = false,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        InboundMailMessage? message = await db.InboundMailMessages
            .Include(m => m.Suggestions)
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null)
        {
            return null;
        }

        Customer? customer = await db.Customers
            .FirstOrDefaultAsync(c => c.Id == customerId && c.TenantId == message.TenantId, ct);

        if (customer is null)
        {
            throw new InvalidOperationException("That customer is not in this tenant.");
        }

        message.CustomerId = customerId;

        // Only the proposals nobody has acted on. A suggestion already accepted or
        // rejected is a record of what a person decided, not a working note.
        //
        // Removed from the collection and not also from the set: the relationship cascades,
        // so dropping the orphan is the delete. Doing both marks the same row deleted twice
        // and the second attempt finds nothing to delete.
        foreach (MailSuggestion stale in
            message.Suggestions.Where(s => s.State == MailSuggestionState.Pending).ToList())
        {
            message.Suggestions.Remove(stale);
        }

        IReadOnlyList<MailSuggestion> fresh = await ProposeAsync(db, message, ct);

        // Added to the set explicitly, not merely hung off the tracked parent. The analyst
        // stamps each suggestion with a fresh Guid, and EF reads a key that is already set
        // as "this row exists" — so discovering them through the navigation marks them
        // Modified and issues an UPDATE against a row that was never inserted. Ingestion
        // gets away with it only because Add on the parent marks the whole graph Added.
        db.Set<MailSuggestion>().AddRange(fresh);
        message.Suggestions.AddRange(fresh);

        if (message.State is MailTriageState.Received or MailTriageState.Proposed)
        {
            message.State = message.Suggestions.Any(s => s.State == MailSuggestionState.Pending)
                ? MailTriageState.Proposed
                : MailTriageState.Received;
        }

        if (rememberDomain && SenderDomain.Of(message.FromAddress) is string domain)
        {
            await RememberDomainAsync(db, message.TenantId, customerId, domain, actor, ct);
        }

        await db.SaveChangesAsync(ct);

        return message;
    }

    /// <summary>
    /// Adds a domain to a customer's register, unless somebody already claimed it or it
    /// belongs to everybody.
    /// </summary>
    private static async Task RememberDomainAsync(
        ApplicationDbContext db, Guid tenantId, Guid customerId, string domain, string actor,
        CancellationToken ct)
    {
        if (SenderDomain.PublicProviders.Contains(domain))
        {
            // Registering a public provider would hand every consumer address at it to one
            // customer. Assigning this one message still stands; the register does not.
            return;
        }

        bool taken = await db.CustomerEmailDomains
            .AnyAsync(d => d.TenantId == tenantId && d.Domain == domain, ct);

        if (taken)
        {
            return;
        }

        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Domain = domain,
            AddedBy = actor,
            Notes = "Added from the support inbox.",
        });
    }

    public async Task<List<InboundMailMessage>> GetQueueAsync(
        Guid tenantId, bool includeHandled = false, CancellationToken ct = default)
    {
        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        IQueryable<InboundMailMessage> query = db.InboundMailMessages
            .Include(m => m.Suggestions)
            .Include(m => m.Customer)
            // The ticket, so a message answered on arrival can show the number the sender
            // was given. Without it the queue can say a receipt went out but not what it
            // said, which is the one thing an operator picking the thread up needs.
            .Include(m => m.Ticket)
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
    /// <param name="chosenApp">
    /// The application a person picked, which overrides whatever was recognised in the
    /// text. Null means "use what was recognised"; <see cref="AppChoice.None"/> means a
    /// person looked and said it is none of them — a distinction that matters, because
    /// the analyst guesses from a name appearing in a sentence and is sometimes wrong in
    /// the direction of confidence.
    /// </param>
    public async Task<Ticket?> AcceptAsync(
        Guid suggestionId, string actor, DateTime at, AppChoice? chosenApp = null,
        CancellationToken ct = default)
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

                Guid? appId = chosenApp is AppChoice chosen ? chosen.AppId : suggestion.AppId;

                // Recorded on the suggestion too, so the reasoning shown beside it does not
                // go on claiming an application nobody accepted.
                suggestion.AppId = appId;

                ticket = await tickets.CreateAsync(
                    message.TenantId,
                    customerId,
                    appId,
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
    /// The customer a message belongs to. Three registers, in order.
    ///
    /// <para><b>The address it was sent to first</b>, when the customer has one of their
    /// own. That is a choice somebody made about this message, and it outranks anything
    /// inferred about the sender — a consultant at a third company writing to
    /// <c>entit-support@</c> has a domain that identifies nobody useful, and a supplier's
    /// engineer has one that identifies the wrong customer entirely.</para>
    ///
    /// <para><b>Then the §23 contacts</b>, by exact address. Somebody named in the
    /// agreement is the strongest statement there is about who a <em>sender</em> is, and it
    /// beats a domain — a consultant named as a customer's technical contact belongs to
    /// that customer whatever their own address says.</para>
    ///
    /// <para><b>Then the customer's registered domains</b>, longest match first. This is
    /// still not guessing: somebody stated that mail from this domain is that customer's.
    /// It exists for the eighty people at a customer who write in once and were never going
    /// to be listed individually.</para>
    ///
    /// <para>No match is a real answer and leaves the message unplaced, where the inbox
    /// flags it and an operator says who it was.</para>
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>
    /// The customer, and which register placed them: <c>OnTheSendersWord</c> for an address
    /// the sender typed into To or Cc, <c>OnTheFromAddress</c> for the two registers that
    /// match on From. Both say what the placement is worth, which is not something the
    /// customer id alone can carry.
    /// </returns>
    private static async Task<(Guid? CustomerId, bool OnTheSendersWord, bool OnTheFromAddress)>
        MatchCustomerAsync(
        ApplicationDbContext db, Guid tenantId, string fromAddress,
        string? deliveredTo, string? toAddresses, CancellationToken ct)
    {
        string address = fromAddress.Trim().ToLowerInvariant();

        List<CustomerSupportAddress> mailboxes = await db.CustomerSupportAddresses
            .AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .ToListAsync(ct);

        // What our own server recorded, first. A reply-all can carry several of ours; any
        // one places the message, and two customers' addresses on one message is a
        // situation no ordering rescues, so the first found is as good as any.
        if (Addressed(mailboxes, deliveredTo) is CustomerSupportAddress delivered)
        {
            return (delivered.CustomerId, false, false);
        }

        ContractContact? contact = await db.ContractContacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Email != null && c.IsActive)
            .FirstOrDefaultAsync(c => c.Email!.ToLower() == address, ct);

        if (contact is not null)
        {
            return (contact.CustomerId, false, true);
        }

        string? domain = SenderDomain.Of(address);

        List<CustomerEmailDomain> registered = domain is null
            ? []
            : await db.CustomerEmailDomains.AsNoTracking()
                .Where(d => d.TenantId == tenantId)
                .ToListAsync(ct);

        if (SenderDomain.BestMatch(registered, d => d.Domain, domain) is CustomerEmailDomain byDomain)
        {
            return (byDomain.CustomerId, false, true);
        }

        // Last: an address the sender put in To or Cc. Usually true, and not evidence —
        // anybody can name a customer's alias there without the message going near it.
        // Placing on it is still right more often than not, but the placement is flagged
        // so it does not silence the prompt that would have invited a second look.
        return Addressed(mailboxes, toAddresses) is CustomerSupportAddress claimed
            ? (claimed.CustomerId, true, false)
            : (null, false, false);
    }

    /// <summary>Which of our registered support addresses appears among a recipient list.</summary>
    private static CustomerSupportAddress? Addressed(
        List<CustomerSupportAddress> mailboxes, string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return null;
        }

        HashSet<string> recipients = new(
            addresses.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);

        return mailboxes.FirstOrDefault(a => recipients.Contains(a.Address));
    }
}
