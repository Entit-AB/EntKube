using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The support mailbox: what the analyst proposes, and the fact that it only ever proposes.
///
/// <para>The property worth defending above all others is that nothing reaches a customer
/// or moves a clock without a person's name on it. §14.3 makes confirming a priority a
/// written act and §14.6 attaches 10% of the window fee to a mis-clocked P1 — neither is
/// a judgement to hand to a keyword match.</para>
/// </summary>
public class SupportMailTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly SupportMailService mail;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    private const string KnownSender = "tech@capio.example";

    public SupportMailTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });

        db.ContractContacts.Add(new ContractContact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Party = ContractParty.Customer,
            Role = ContractContactRole.TechnicalContact,
            Name = "Karin Karlsson",
            Email = KnownSender,
        });

        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = Swedish(2026, 1, 1, 0),
        };
        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Standard,
            SupportWindow = SupportWindow.S1,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            Reason = "On-boarding",
        });
        db.ApplicationContracts.Add(contract);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts);
        MailTriageRuleService ruleService = new(factory);
        mail = new SupportMailService(
            factory, new RuleBasedMailAnalyst(), ruleService, tickets,
            new TimeService(factory, contracts));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<InboundMailMessage?> Receive(
        string subject, string body, string from = KnownSender, DateTime? sentAt = null) =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            FromName = "Karin Karlsson",
            Subject = subject,
            Body = body,
            SentAt = sentAt ?? Tue(10),
        });

    private void RegisterDomain(string domain, Guid? forCustomer = null) =>
        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = forCustomer ?? customerId,
            Domain = domain,
        });

    // ---- Matching by registered domain ----------------------------------------------------

    /// <summary>
    /// The eighty people at a customer who write in once were never going to be listed as
    /// §23 contacts. A registered domain places them, which is what lets everything after
    /// it work — the analyst can only recognise an application among that customer's
    /// applications.
    /// </summary>
    [Fact]
    public async Task A_sender_at_a_registered_domain_is_placed()
    {
        RegisterDomain("capio.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel i systemet", "Det gar inte att logga in.", from: "someone.else@capio.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>A subdomain is the same customer; making them register each one would
    /// mean mail from a new one silently going unplaced.</summary>
    [Fact]
    public async Task A_sender_at_a_subdomain_of_a_registered_domain_is_placed()
    {
        RegisterDomain("capio.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "helpdesk@it.capio.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// A domain that merely ends the same is somebody else's — anybody can register
    /// notcapio.example, and its mail would otherwise be triaged into this customer's queue.
    /// </summary>
    [Fact]
    public async Task A_lookalike_domain_is_not_placed()
    {
        RegisterDomain("capio.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "attacker@notcapio.example");

        message!.CustomerId.Should().BeNull();
    }

    /// <summary>
    /// A person named in the agreement beats a domain. A consultant at another company
    /// named as a customer's technical contact belongs to that customer, whatever their
    /// address says.
    /// </summary>
    [Fact]
    public async Task A_named_contact_beats_another_customers_domain()
    {
        Guid otherCustomer = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = otherCustomer, TenantId = tenantId, Name = "Other" });

        // The known contact's own domain is registered to somebody else entirely.
        RegisterDomain("capio.example", forCustomer: otherCustomer);
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive("Fel", "Beskrivning.", from: KnownSender);

        message!.CustomerId.Should().Be(customerId, "the §23 register is the stronger statement");
    }

    // ---- What being placed makes possible --------------------------------------------------

    /// <summary>
    /// Placing the customer is what lets the application be recognised: the analyst matches
    /// names among <em>that customer's</em> applications, so an unplaced message cannot be
    /// placed on an app either.
    /// </summary>
    [Fact]
    public async Task A_message_placed_by_domain_can_also_be_placed_on_an_application()
    {
        RegisterDomain("capio.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte",
            "Ingen kommer in.",
            from: "someone.else@capio.example");

        message!.Suggestions.Should().Contain(s => s.AppId == appId);
    }

    // ---- Saying who it was, afterwards --------------------------------------------------------

    /// <summary>
    /// A message nobody could place has to be placeable by hand, and analysing it again
    /// afterwards is the point — the first pass had no customer, so it could say almost
    /// nothing.
    /// </summary>
    [Fact]
    public async Task Assigning_a_customer_analyses_the_message_again()
    {
        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Ingen kommer in.", from: "stranger@elsewhere.example");

        message!.CustomerId.Should().BeNull();
        message.Suggestions.Should().NotContain(s => s.AppId == appId);

        InboundMailMessage? placed = await mail.AssignCustomerAsync(
            message.Id, customerId, "nils");

        placed!.CustomerId.Should().Be(customerId);
        placed.Suggestions.Should().Contain(s => s.AppId == appId,
            "the application is recognisable now that the customer is known");
    }

    /// <summary>
    /// Remembering the domain is what stops the next person at the same company being asked
    /// about — offered rather than done, because it is a statement about every future
    /// sender there.
    /// </summary>
    [Fact]
    public async Task Remembering_the_domain_places_the_next_message_by_itself()
    {
        InboundMailMessage? first = await Receive(
            "Fel", "Beskrivning.", from: "stranger@partner.example");

        await mail.AssignCustomerAsync(first!.Id, customerId, "nils", rememberDomain: true);

        InboundMailMessage? second = await Receive(
            "Ett annat fel", "Beskrivning.", from: "somebody.new@partner.example");

        second!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// Registering a public provider would hand every consumer address at it to one
    /// customer, so the message is placed and the register is not.
    /// </summary>
    [Fact]
    public async Task Remembering_a_public_provider_does_not_register_it()
    {
        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "someone@gmail.com");

        await mail.AssignCustomerAsync(message!.Id, customerId, "nils", rememberDomain: true);

        db.CustomerEmailDomains.Should().BeEmpty();

        InboundMailMessage? fromAStranger = await Receive(
            "Fel", "Beskrivning.", from: "a.total.stranger@gmail.com");

        fromAStranger!.CustomerId.Should().BeNull();
    }

    /// <summary>A decision a person already made is a record, not a working note.</summary>
    [Fact]
    public async Task Re_analysis_keeps_the_suggestions_somebody_already_acted_on()
    {
        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "stranger@elsewhere.example");

        MailSuggestion decided = db.Set<MailSuggestion>().First(s => s.MessageId == message!.Id);
        decided.State = MailSuggestionState.Rejected;
        decided.DecidedBy = "nils";
        await db.SaveChangesAsync();

        await mail.AssignCustomerAsync(message!.Id, customerId, "nils");

        db.Set<MailSuggestion>().Any(s => s.Id == decided.Id && s.State == MailSuggestionState.Rejected)
            .Should().BeTrue();
    }

    /// <summary>A message can only be placed with a customer of its own tenant.</summary>
    [Fact]
    public async Task A_message_cannot_be_assigned_to_another_tenants_customer()
    {
        Guid elsewhere = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = elsewhere, Name = "Other", Slug = "other" });
        Guid theirCustomer = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = elsewhere, Name = "Theirs" });
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "stranger@elsewhere.example");

        Func<Task> assign = () => mail.AssignCustomerAsync(message!.Id, theirCustomer, "nils");

        await assign.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Matching ---------------------------------------------------------------------

    [Fact]
    public async Task A_known_sender_is_matched_to_their_customer()
    {
        InboundMailMessage? message = await Receive("Fel i exporten", "Exporten fungerar inte.");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// Guessing by domain would attach one customer's mail to another's tickets. The §23
    /// contact register exists so this does not have to be guessed.
    /// </summary>
    [Fact]
    public async Task An_unknown_sender_is_flagged_rather_than_guessed()
    {
        InboundMailMessage? message = await Receive(
            "Hjälp", "Det är trasigt.", from: "someone@elsewhere.example");

        message!.CustomerId.Should().BeNull();
        message.Suggestions.Should().ContainSingle()
            .Which.Kind.Should().Be(MailSuggestionKind.FlagUnknownSender);
    }

    [Fact]
    public async Task The_application_named_in_the_text_is_picked_up()
    {
        InboundMailMessage? message = await Receive(
            "Problem i Journalportalen", "Inget händer när vi sparar.");

        message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.OpenTicket)
            .AppId.Should().Be(appId);
    }

    [Fact]
    public async Task An_unrecognised_application_is_left_for_a_person_to_choose()
    {
        InboundMailMessage? message = await Receive("Något strular", "Det går inte.");

        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        open.AppId.Should().BeNull();
        open.Reasoning.Should().Contain("needs choosing");
    }

    /// <summary>A mailbox poll hands the same message over repeatedly.</summary>
    [Fact]
    public async Task The_same_message_is_not_ingested_twice()
    {
        InboundMailMessage first = new()
        {
            TenantId = tenantId, MessageId = "abc123", FromAddress = KnownSender,
            Subject = "Fel", Body = "Trasigt.", SentAt = Tue(10),
        };

        await mail.IngestAsync(first);

        InboundMailMessage? again = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId, MessageId = "abc123", FromAddress = KnownSender,
            Subject = "Fel", Body = "Trasigt.", SentAt = Tue(10),
        });

        again.Should().BeNull();
        db.InboundMailMessages.Count().Should().Be(1);
    }

    // ---- Priority is proposed with its reasoning -----------------------------------------

    [Theory]
    [InlineData("Tjänsten svarar inte", TicketPriority.P1)]
    [InlineData("Ingen kan logga in", TicketPriority.P1)]
    [InlineData("Misstänkt dataläcka", TicketPriority.P1)]
    [InlineData("Exporten fungerar inte", TicketPriority.P2)]
    [InlineData("Ett stavfel på startsidan", TicketPriority.P4)]
    [InlineData("Vi undrar en sak", TicketPriority.P4)]
    [InlineData("Lite konstigt beteende", TicketPriority.P3)]
    public void The_proposed_priority_follows_the_wording_of_14_2(string text, TicketPriority expected)
    {
        (TicketPriority priority, _) = MailTriageRuleSet.BuiltIn.ProposePriority(text.ToLowerInvariant());

        priority.Should().Be(expected);
    }

    /// <summary>
    /// The operator confirming a priority under §14.3 puts their name to it, so they need
    /// the reasoning rather than a verdict.
    /// </summary>
    [Fact]
    public async Task A_proposed_priority_carries_the_criterion_it_matched()
    {
        InboundMailMessage? message = await Receive(
            "Akut", "Ingen kan logga in i Journalportalen.");

        MailSuggestion proposal = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.ProposePriority);

        proposal.Priority.Should().Be(TicketPriority.P1);
        proposal.Reasoning.Should().Contain("login impossible for all users");
        proposal.Reasoning.Should().Contain("§14.3");
    }

    [Fact]
    public async Task A_default_priority_says_it_matched_nothing()
    {
        InboundMailMessage? message = await Receive("Hej", "En liten sak bara.");

        MailSuggestion proposal = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.ProposePriority);

        proposal.Priority.Should().Be(TicketPriority.P3);
        proposal.Reasoning.Should().Contain("default rather than an assessment");
    }

    // ---- Threading ---------------------------------------------------------------------------

    [Fact]
    public async Task A_reply_quoting_a_ticket_number_is_appended_to_it()
    {
        Ticket existing = await tickets.CreateAsync(
            tenantId, customerId, appId, "Fel i exporten", "", TicketChannel.Portal,
            TicketPriority.P3, Tue(9));

        InboundMailMessage? message = await Receive(
            $"Re: [#{existing.Number}] Fel i exporten", "Här är loggen ni bad om.");

        MailSuggestion suggestion = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.AppendToTicket);

        suggestion.TicketId.Should().Be(existing.Id);
        message.Suggestions.Should().NotContain(s => s.Kind == MailSuggestionKind.OpenTicket);
    }

    [Fact]
    public async Task A_reference_to_a_closed_ticket_opens_a_new_one()
    {
        Ticket existing = await tickets.CreateAsync(
            tenantId, customerId, appId, "Gammalt fel", "", TicketChannel.Portal,
            TicketPriority.P3, Tue(8));
        await tickets.CloseAsync(existing.Id, "nils", Tue(9));

        InboundMailMessage? message = await Receive(
            $"Re: [#{existing.Number}] Gammalt fel", "Det har hänt igen.");

        message!.Suggestions.Should().Contain(s => s.Kind == MailSuggestionKind.OpenTicket);
    }

    // ---- The other flags -----------------------------------------------------------------------

    [Fact]
    public async Task A_request_for_new_functionality_is_flagged_as_development()
    {
        InboundMailMessage? message = await Receive(
            "Önskemål", "Vi skulle vilja ha en ny funktion för massutskick.");

        message!.Suggestions.Should().Contain(s =>
            s.Kind == MailSuggestionKind.FlagDevelopment && s.Reasoning!.Contains("§15.1"));
    }

    [Fact]
    public async Task A_message_naming_a_third_party_suggests_a_pause()
    {
        InboundMailMessage? message = await Receive(
            "Uppdatering", "Vi väntar på vår hosting-leverantör innan vi kan testa.");

        message!.Suggestions.Should().Contain(s => s.Kind == MailSuggestionKind.SuggestPause);
    }

    [Fact]
    public async Task A_spent_timbank_is_flagged_on_arrival()
    {
        db.PortfolioAgreements.Add(new PortfolioAgreement
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            PricingModel = PricingModel.HourBank, HourBankHoursPerMonth = 1m,
        });
        db.TimeEntries.Add(new TimeEntry
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId, AppId = appId,
            StartedAt = Tue(9), EndedAt = Swedish(2026, 9, 22, 13), Description = "Investigation",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive("Ny fråga", "Kan ni titta på det här?");

        message!.Suggestions.Should().Contain(s => s.Kind == MailSuggestionKind.FlagTimebank);
    }

    [Fact]
    public async Task An_acknowledgement_is_drafted_but_not_sent()
    {
        InboundMailMessage? message = await Receive("Fel", "Det fungerar inte.");

        MailSuggestion draft = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.DraftReply);

        draft.State.Should().Be(MailSuggestionState.Pending);
        draft.DraftText.Should().Contain("Thank you for your report");
        draft.DraftText.Should().Contain("14.3");
    }

    // ---- Nothing happens without a person -------------------------------------------------------

    /// <summary>
    /// Ingesting a message creates no ticket, starts no clock and sends nothing. Every
    /// suggestion sits pending until somebody accepts it.
    /// </summary>
    [Fact]
    public async Task Ingesting_a_message_changes_nothing_by_itself()
    {
        await Receive("Tjänsten svarar inte", "Ingen kommer in i Journalportalen.");

        db.Tickets.Should().BeEmpty();
        db.MailSuggestions.Should().OnlyContain(s => s.State == MailSuggestionState.Pending);
    }

    /// <summary>
    /// §14.3 and §9.1: the response time is counted from when the message arrived, not from
    /// when somebody got round to triaging it. A mail that lands on Friday evening under S1
    /// starts its clock on Monday morning — but from Friday's arrival, not Monday's triage.
    /// </summary>
    [Fact]
    public async Task Accepting_opens_the_ticket_at_the_time_the_mail_arrived()
    {
        DateTime fridayEvening = Swedish(2026, 9, 25, 18);

        InboundMailMessage? message = await Receive(
            "Tjänsten svarar inte", "Ingen kommer in i Journalportalen.", sentAt: fridayEvening);

        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        Ticket? ticket = await mail.AcceptAsync(open.Id, "nils", Swedish(2026, 9, 28, 9));

        ticket!.ReportedAt.Should().Be(fridayEvening);
        ticket.ClockStartsAt.Should().Be(Swedish(2026, 9, 28, 8));
        ticket.Channel.Should().Be(TicketChannel.Email);
    }

    /// <summary>
    /// The analyst's priority rides along as the customer's proposal — which is what §14.3
    /// treats it as — and is left unconfirmed for a person to reason about in writing.
    /// </summary>
    [Fact]
    public async Task The_opened_ticket_carries_the_priority_as_an_unconfirmed_proposal()
    {
        InboundMailMessage? message = await Receive(
            "Akut", "Ingen kan logga in i Journalportalen.");

        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);
        Ticket? ticket = await mail.AcceptAsync(open.Id, "nils", Tue(11));

        ticket!.ProposedPriority.Should().Be(TicketPriority.P1);
        ticket.Priority.Should().Be(TicketPriority.P1);
        ticket.PriorityConfirmedAt.Should().BeNull();
        ticket.PriorityConfirmedBy.Should().BeNull();
    }

    [Fact]
    public async Task Accepting_records_who_decided()
    {
        InboundMailMessage? message = await Receive("Fel", "Trasigt.");
        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        await mail.AcceptAsync(open.Id, "nils", Tue(11));

        MailSuggestion decided = db.MailSuggestions.Single(s => s.Id == open.Id);

        decided.State.Should().Be(MailSuggestionState.Accepted);
        decided.DecidedBy.Should().Be("nils");
        decided.DecidedAt.Should().Be(Tue(11));
    }

    [Fact]
    public async Task A_suggestion_cannot_be_accepted_twice()
    {
        InboundMailMessage? message = await Receive("Fel", "Trasigt.");
        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        await mail.AcceptAsync(open.Id, "nils", Tue(11));
        Ticket? second = await mail.AcceptAsync(open.Id, "anna", Tue(12));

        second.Should().BeNull();
        db.Tickets.Count().Should().Be(1);
    }

    [Fact]
    public async Task Appending_puts_the_message_on_the_ticket_history()
    {
        Ticket existing = await tickets.CreateAsync(
            tenantId, customerId, appId, "Fel i exporten", "", TicketChannel.Portal,
            TicketPriority.P3, Tue(9));

        InboundMailMessage? message = await Receive(
            $"Re: [#{existing.Number}] Fel i exporten", "Här är loggen.");

        MailSuggestion append = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.AppendToTicket);

        await mail.AcceptAsync(append.Id, "nils", Tue(11));

        Ticket? loaded = await tickets.GetAsync(existing.Id);

        loaded!.Events.Should().Contain(e => e.Detail.Contains("Här är loggen"));
    }

    [Fact]
    public async Task A_rejected_suggestion_does_nothing()
    {
        InboundMailMessage? message = await Receive("Fel", "Trasigt.");
        MailSuggestion open = message!.Suggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        await mail.RejectAsync(open.Id, "nils", Tue(11));

        db.Tickets.Should().BeEmpty();
        db.MailSuggestions.Single(s => s.Id == open.Id).State
            .Should().Be(MailSuggestionState.Rejected);
    }

    [Fact]
    public async Task A_dismissed_message_leaves_the_queue()
    {
        InboundMailMessage? message = await Receive("Out of office", "Jag är på semester.");

        await mail.DismissAsync(message!.Id, "nils", Tue(11));

        List<InboundMailMessage> queue = await mail.GetQueueAsync(tenantId);

        queue.Should().BeEmpty();
        (await mail.GetQueueAsync(tenantId, includeHandled: true)).Should().ContainSingle();
    }
}
