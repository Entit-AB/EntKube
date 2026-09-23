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
/// <para><b>Nothing here acts on its own.</b> The analyst reads and drafts; every state
/// change goes through a person accepting a suggestion, and their name lands on the
/// resulting ticket event. §14.3 makes confirming a priority a written act, §14.6 makes the
/// timestamps evidence between the parties, and §14.4 makes resolution something the
/// customer agrees to. The value here is that the paperwork is ready, not that the decision
/// is made.</para>
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
        message.CustomerId ??= await MatchCustomerAsync(
            db, message.TenantId, message.FromAddress, message.ToAddresses, ct);

        message.Suggestions.AddRange(await ProposeAsync(db, message, ct));
        message.State = message.Suggestions.Count > 0
            ? MailTriageState.Proposed
            : MailTriageState.Received;

        db.InboundMailMessages.Add(message);
        await db.SaveChangesAsync(ct);

        return message;
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
    private async Task<IReadOnlyList<MailSuggestion>> ProposeAsync(
        ApplicationDbContext db, InboundMailMessage message, CancellationToken ct)
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
            new MailContext(message, customer, apps, openTickets, bankSpent, ruleSet), ct);
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
    /// <c>capio-support@</c> has a domain that identifies nobody useful, and a supplier's
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
    private static async Task<Guid?> MatchCustomerAsync(
        ApplicationDbContext db, Guid tenantId, string fromAddress, string? toAddresses,
        CancellationToken ct)
    {
        string address = fromAddress.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(toAddresses))
        {
            List<CustomerSupportAddress> mailboxes = await db.CustomerSupportAddresses
                .AsNoTracking()
                .Where(a => a.TenantId == tenantId)
                .ToListAsync(ct);

            // A reply-all can carry several of ours; any one of them places the message,
            // and two different customers' addresses on one message is a situation no
            // ordering can rescue, so the first found is as good as any.
            HashSet<string> recipients = new(
                toAddresses.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

            CustomerSupportAddress? addressed =
                mailboxes.FirstOrDefault(a => recipients.Contains(a.Address));

            if (addressed is not null)
            {
                return addressed.CustomerId;
            }
        }

        ContractContact? contact = await db.ContractContacts.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Email != null && c.IsActive)
            .FirstOrDefaultAsync(c => c.Email!.ToLower() == address, ct);

        if (contact is not null)
        {
            return contact.CustomerId;
        }

        string? domain = SenderDomain.Of(address);

        if (domain is null)
        {
            return null;
        }

        List<CustomerEmailDomain> registered = await db.CustomerEmailDomains.AsNoTracking()
            .Where(d => d.TenantId == tenantId)
            .ToListAsync(ct);

        return SenderDomain.BestMatch(registered, d => d.Domain, domain)?.CustomerId;
    }
}
