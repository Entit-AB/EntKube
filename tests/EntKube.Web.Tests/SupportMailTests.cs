using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using EntKube.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using MimeKit;
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

    /// <summary>
    /// A real SMTP server on a loopback port, rather than a notifier that says nothing.
    ///
    /// <para>What the customer actually receives is the whole point of the reply path, and a
    /// notifier with nowhere to send reports that it reached nobody — which is correct
    /// behaviour and tests nothing. With a sink, the assertions can be about the message on
    /// the wire.</para>
    /// </summary>
    private readonly SmtpSink sink = new();

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    private const string KnownSender = "tech@entit.example";

    public SupportMailTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
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
        IConfiguration smtp = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Smtp:Host"] = sink.Host,
                ["Smtp:Port"] = sink.Port.ToString(),
                ["Smtp:FromAddress"] = "support@entit.se",
            }).Build();

        tickets = new TicketService(
            factory, contracts,
            new TicketNotifier(
                factory,
                new SmtpSettingsResolver(factory, smtp),
                new OnCallService(factory),
                TestTicketReference.Instance,
                NullLogger<TicketNotifier>.Instance),
            new SupportDutyService(factory));

        MailTriageRuleService ruleService = new(factory);
        mail = new SupportMailService(
            factory, new RuleBasedMailAnalyst(TestTicketReference.Instance), ruleService, tickets,
            new TimeService(factory, contracts), TestTicketReference.Instance,
            new SupportDutyService(factory));
    }

    public void Dispose()
    {
        sink.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<InboundMailMessage?> Receive(
        string subject, string body, string from = KnownSender, DateTime? sentAt = null,
        SenderAuthenticity authenticity = SenderAuthenticity.Unknown) =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            FromName = "Karin Karlsson",
            Subject = subject,
            Body = body,
            SentAt = sentAt ?? Tue(10),
            SenderAuthenticity = authenticity,
        });

    /// <summary>One of our own people: a member of the mailbox's tenant.</summary>
    private const string Technician = "nils@entit.example";

    private void TechnicianOnStaff()
    {
        Guid roleId = Guid.NewGuid();

        db.TenantRoles.Add(new TenantRole { Id = roleId, TenantId = tenantId, Name = "Support" });
        db.Users.Add(new ApplicationUser
        {
            Id = "user-nils", UserName = "nils", Email = Technician,
        });
        db.TenantMemberships.Add(new TenantMembership
        {
            UserId = "user-nils", TenantId = tenantId, RoleId = roleId,
        });

        db.SaveChanges();
    }

    /// <summary>A technician's reply, shaped as their client sends it back.</summary>
    private static string Replying(string said) =>
        $"""
         {said}

         > {SupportReplyBody.Sentinel}
         > ───────────────────────────────────────────────
         >   Priority:    P1 (as reported — confirm it in writing, §14.3)
         >   Respond by:  Tue 22 Sep 14:00
         >   Reported by: Karin Karlsson
         """;

    private void RegisterDomain(string domain, Guid? forCustomer = null) =>
        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = forCustomer ?? customerId,
            Domain = domain,
        });

    private void RegisterAddress(string address, Guid? forCustomer = null) =>
        db.CustomerSupportAddresses.Add(new CustomerSupportAddress
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = forCustomer ?? customerId,
            Address = address,
        });

    /// <summary>A message our own server recorded as delivered to an address.</summary>
    private Task<InboundMailMessage?> ReceiveAddressedTo(
        string to, string from, string subject = "Fel", string body = "Beskrivning.") =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            DeliveredTo = to,
            ToAddresses = to,
            Subject = subject,
            Body = body,
            SentAt = Tue(10),
        });

    /// <summary>A message that merely names an address in To or Cc, which anybody may do.</summary>
    private Task<InboundMailMessage?> ReceiveClaimingTo(string to, string from) =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            ToAddresses = to,
            Subject = "Fel",
            Body = "Beskrivning.",
            SentAt = Tue(10),
        });

    /// <summary>A message whose From our own server could not verify.</summary>
    private Task<InboundMailMessage?> ReceiveFailingAuthentication(
        string from, string? deliveredTo = null) =>
        mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = from,
            DeliveredTo = deliveredTo,
            Subject = "Fel",
            Body = "Beskrivning.",
            SentAt = Tue(10),
            SenderAuthenticity = SenderAuthenticity.Failed,
        });

    /// <summary>
    /// <b>From is written by the sender too, and it is what places a message.</b> A §23
    /// contact match carries no caveat at all — somebody named in the agreement is the
    /// strongest statement there is about who a sender is — and it reads a header anybody
    /// can forge. Where our own server says that header failed, the placement still stands,
    /// because a forward and a mailing list fail it innocently; what it must not do is stay
    /// quiet about resting on the very thing that failed.
    /// </summary>
    [Fact]
    public async Task A_contact_placed_on_a_from_our_server_rejected_is_flagged()
    {
        InboundMailMessage? message = await ReceiveFailingAuthentication(from: KnownSender);

        message!.CustomerId.Should().Be(customerId);

        MailSuggestion flag = message.Suggestions
            .Should().ContainSingle(s => s.Kind == MailSuggestionKind.FlagForgedSender)
            .Subject;

        flag.Summary.Should().Contain(KnownSender);
        flag.Reasoning.Should().Contain("§23");
    }

    /// <summary>
    /// The same failure, on a message placed by where our own server delivered it. The
    /// routing does not rest on the forged header, so it is left alone — but a ticket is
    /// about to be opened in that person's name, and §14.1 will notify a contact about it.
    /// </summary>
    [Fact]
    public async Task A_failed_sender_is_flagged_even_where_the_routing_does_not_rest_on_it()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveFailingAuthentication(
            from: "stranger@nowhere.example", deliveredTo: "entit-support@entit.se");

        message!.CustomerId.Should().Be(customerId);

        MailSuggestion flag = message.Suggestions
            .Should().ContainSingle(s => s.Kind == MailSuggestionKind.FlagForgedSender)
            .Subject;

        flag.Reasoning.Should().NotContain("§23");
    }

    /// <summary>
    /// <b>Silence is the default, and has to be.</b> Most tenants will never name a server
    /// to trust, so every message arrives unchecked — and a caveat on every one of those
    /// would be ignored inside a week, which is worse than none at all. Only a server we
    /// trust saying the sender is wrong is worth interrupting for.
    /// </summary>
    [Theory]
    [InlineData(SenderAuthenticity.Unknown)]
    [InlineData(SenderAuthenticity.Verified)]
    public async Task Nothing_is_flagged_unless_a_trusted_server_rejected_the_sender(
        SenderAuthenticity verdict)
    {
        InboundMailMessage? message = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            Subject = "Fel",
            Body = "Beskrivning.",
            SentAt = Tue(10),
            SenderAuthenticity = verdict,
        });

        message!.Suggestions.Should()
            .NotContain(s => s.Kind == MailSuggestionKind.FlagForgedSender);
    }

    /// <summary>
    /// <b>To and Cc are written by the sender.</b> Naming a customer's support alias there
    /// is a claim, not evidence — so the message is still placed, because that is usually
    /// what a consultant writing on their behalf does, but the prompt that would have had
    /// somebody look twice is not silenced by it.
    /// </summary>
    [Fact]
    public async Task An_address_only_claimed_in_the_headers_is_placed_but_flagged()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveClaimingTo(
            "entit-support@entit.se", from: "stranger@nowhere.example");

        message!.CustomerId.Should().Be(customerId);
        message.Suggestions.Should().Contain(s => s.Kind == MailSuggestionKind.FlagUnknownSender);
    }

    /// <summary>
    /// What our own server recorded is not a claim, so it places the message without a
    /// caveat. This is the ordinary case — an alias expands before the message is written.
    /// </summary>
    [Fact]
    public async Task An_address_our_server_recorded_is_placed_without_a_caveat()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "entit-support@entit.se", from: "stranger@nowhere.example");

        message!.CustomerId.Should().Be(customerId);
        message.Suggestions.Should().NotContain(s => s.Kind == MailSuggestionKind.FlagUnknownSender);
    }

    /// <summary>
    /// A sender we already recognise needs no caveat either: the claim added nothing,
    /// because the contact register had already answered.
    /// </summary>
    [Fact]
    public async Task A_known_sender_naming_the_address_gets_no_caveat()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveClaimingTo(
            "entit-support@entit.se", from: KnownSender);

        message!.CustomerId.Should().Be(customerId);
        message.Suggestions.Should().NotContain(s => s.Kind == MailSuggestionKind.FlagUnknownSender);
    }

    // ---- Threading a reply onto its ticket ---------------------------------------------------

    /// <summary>
    /// <b>The case a comment claimed was covered and was not.</b> A reply whose subject has
    /// been edited, translated by a client or lost to a forward still belongs on its
    /// ticket — and used to open a second one about the fault already being worked.
    /// </summary>
    [Fact]
    public async Task A_reply_with_a_mangled_subject_is_still_placed_on_its_ticket()
    {
        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, TicketPriority.P3, Tue(9), "Karin", KnownSender);

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            // Nothing of the original subject survives.
            Subject = "SV: nagot annat",
            Body = "Det gäller fortfarande avdelning 4.",
            InReplyTo = SupportMessageId.For(ticket.Number),
            SentAt = Tue(11),
        });

        reply!.Suggestions.Should().Contain(s =>
            s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);
    }

    /// <summary>
    /// A reply that threads on somebody else's message is not mined for a number. Placing
    /// it on a stranger's ticket would be worse than leaving it unplaced.
    /// </summary>
    [Fact]
    public async Task A_reply_threading_on_somebody_elses_message_opens_its_own_ticket()
    {
        await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, TicketPriority.P3, Tue(9), "Karin", KnownSender);

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            Subject = "Ett helt annat fel",
            Body = "Beskrivning.",
            InReplyTo = "kollega@entit.example",
            SentAt = Tue(11),
        });

        reply!.Suggestions.Should().Contain(s => s.Kind == MailSuggestionKind.OpenTicket);
        reply.Suggestions.Should().NotContain(s => s.Kind == MailSuggestionKind.AppendToTicket);
    }

    /// <summary>The subject reference still works; the thread header is an addition.</summary>
    [Fact]
    public async Task A_reply_that_only_keeps_the_reference_in_the_subject_still_threads()
    {
        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, TicketPriority.P3, Tue(9), "Karin", KnownSender);

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            Subject = $"Re: [#{ticket.Number}] Journalen svarar inte",
            Body = "Fortfarande nere.",
            SentAt = Tue(11),
        });

        reply!.Suggestions.Should().Contain(s =>
            s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);
    }

    // ---- Matching by the address it was sent to ---------------------------------------------

    /// <summary>
    /// The case a domain cannot cover. A consultant at a third company reporting a fault on
    /// a customer's system has a domain that identifies nobody useful — but the address
    /// they were given says plainly whose system it is.
    /// </summary>
    [Fact]
    public async Task A_message_sent_to_a_customers_own_address_is_placed_with_them()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "entit-support@entit.se", from: "consultant@thirdparty.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// The address beats what can be inferred about the sender. Somebody who consults for
    /// two customers is telling us which one they mean by the address they chose, and that
    /// is a decision about this message rather than a standing fact about them.
    /// </summary>
    [Fact]
    public async Task The_address_written_to_beats_the_senders_own_domain()
    {
        Guid other = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = other, TenantId = tenantId, Name = "Other" });

        RegisterDomain("entit.example");                        // the sender is Entit AB's
        RegisterAddress("other-support@entit.se", forCustomer: other);
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "other-support@entit.se", from: "karin@entit.example");

        message!.CustomerId.Should().Be(other, "they wrote to the other customer's address");
    }

    /// <summary>
    /// A reply-all carries several recipients. Finding ours among the customer's own staff,
    /// the generic address and a colleague is the ordinary case, not the exception.
    /// </summary>
    [Fact]
    public async Task The_address_is_found_among_the_other_recipients()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "kollega@entit.example support@entit.se entit-support@entit.se",
            from: "stranger@nowhere.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// Addresses are compared without regard to case, since a client is free to write them
    /// however it likes.
    /// </summary>
    [Fact]
    public async Task The_address_is_matched_whatever_case_it_arrives_in()
    {
        RegisterAddress("entit-support@entit.se");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "Entit-Support@Entit.SE", from: "stranger@nowhere.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// Nothing addressed to a customer still falls through to who sent it — the two
    /// registers work together rather than one replacing the other.
    /// </summary>
    [Fact]
    public async Task Without_a_matching_address_the_sender_still_places_it()
    {
        RegisterDomain("entit.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await ReceiveAddressedTo(
            "support@entit.se", from: "anyone@entit.example");

        message!.CustomerId.Should().Be(customerId);
    }

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
        RegisterDomain("entit.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel i systemet", "Det gar inte att logga in.", from: "someone.else@entit.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>A subdomain is the same customer; making them register each one would
    /// mean mail from a new one silently going unplaced.</summary>
    [Fact]
    public async Task A_sender_at_a_subdomain_of_a_registered_domain_is_placed()
    {
        RegisterDomain("entit.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "helpdesk@it.entit.example");

        message!.CustomerId.Should().Be(customerId);
    }

    /// <summary>
    /// A domain that merely ends the same is somebody else's — anybody can register
    /// notentit.example, and its mail would otherwise be triaged into this customer's queue.
    /// </summary>
    [Fact]
    public async Task A_lookalike_domain_is_not_placed()
    {
        RegisterDomain("entit.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "attacker@notentit.example");

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
        RegisterDomain("entit.example", forCustomer: otherCustomer);
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
        RegisterDomain("entit.example");
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte",
            "Ingen kommer in.",
            from: "someone.else@entit.example");

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

    /// <summary>
    /// <b>A message can be placed with a customer in another tenant.</b> A support address
    /// takes what it is sent: a report about an application run for somebody else's customer
    /// arrives at whichever mailbox the sender happened to know, and the operator reading it
    /// has to be able to say where it belongs. Retyping it there instead would lose the
    /// arrival time §14.3 counts from.
    ///
    /// <para>What may be placed where is settled by the screen, which only offers the
    /// tenants that person can already reach. What is enforced here is that the customer is
    /// real — see <see cref="A_message_cannot_be_placed_with_a_customer_that_does_not_exist"/>.</para>
    /// </summary>
    [Fact]
    public async Task A_message_can_be_placed_with_another_tenants_customer()
    {
        Guid elsewhere = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = elsewhere, Name = "Other", Slug = "other" });
        Guid theirCustomer = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = elsewhere, Name = "Theirs" });
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "stranger@elsewhere.example");

        InboundMailMessage? placed =
            await mail.AssignCustomerAsync(message!.Id, theirCustomer, "nils");

        placed!.CustomerId.Should().Be(theirCustomer);
    }

    /// <summary>
    /// <b>And the ticket opens where the customer is.</b> The mailbox's tenant says where
    /// the message physically arrived; the customer's says whose agreement it is measured
    /// against and whose queue somebody is watching. A ticket filed under the first would be
    /// invisible to the people who have to answer it.
    /// </summary>
    [Fact]
    public async Task A_ticket_opens_in_the_tenant_the_customer_belongs_to()
    {
        Guid elsewhere = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = elsewhere, Name = "Other", Slug = "other" });
        Guid theirCustomer = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = theirCustomer, TenantId = elsewhere, Name = "Theirs" });
        await db.SaveChangesAsync();

        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "stranger@elsewhere.example");

        InboundMailMessage? placed =
            await mail.AssignCustomerAsync(message!.Id, theirCustomer, "nils");

        MailSuggestion open = placed!.Suggestions
            .First(s => s.Kind == MailSuggestionKind.OpenTicket
                        && s.State == MailSuggestionState.Pending);

        Ticket? ticket = await mail.AcceptAsync(open.Id, "nils", Tue(11));

        ticket!.TenantId.Should().Be(elsewhere);
        ticket.CustomerId.Should().Be(theirCustomer);
    }

    /// <summary>The one thing still refused: a customer that is not there at all.</summary>
    [Fact]
    public async Task A_message_cannot_be_placed_with_a_customer_that_does_not_exist()
    {
        InboundMailMessage? message = await Receive(
            "Fel", "Beskrivning.", from: "stranger@elsewhere.example");

        Func<Task> assign = () => mail.AssignCustomerAsync(message!.Id, Guid.NewGuid(), "nils");

        await assign.Should().ThrowAsync<InvalidOperationException>();
    }

    // ---- Choosing the application --------------------------------------------------------

    /// <summary>
    /// Nothing named an application, so the ticket is opened for the customer without one.
    /// That has to be possible: a message saying "everything is broken" names nothing, and
    /// refusing to open a ticket until somebody picks would be worse than a ticket with no
    /// application on it.
    /// </summary>
    [Fact]
    public async Task A_message_that_names_no_application_still_opens_a_ticket()
    {
        InboundMailMessage? message = await Receive("Allt är nere", "Inget fungerar.");

        MailSuggestion open = message!.Suggestions.Single(
            s => s.Kind == MailSuggestionKind.OpenTicket);

        open.AppId.Should().BeNull();

        Ticket? ticket = await tickets.GetAsync(
            (await mail.AcceptAsync(open.Id, "nils", Tue(11)))!.Id);

        ticket!.AppId.Should().BeNull();
    }

    /// <summary>
    /// The operator picks the application the text did not name. This is the other half of
    /// "no match has to end up somewhere" — the suggestion used to say which one this is
    /// about has to be chosen, and there was no way to choose it.
    /// </summary>
    [Fact]
    public async Task An_application_can_be_chosen_for_a_message_that_named_none()
    {
        InboundMailMessage? message = await Receive("Allt är nere", "Inget fungerar.");

        MailSuggestion open = message!.Suggestions.Single(
            s => s.Kind == MailSuggestionKind.OpenTicket);

        Ticket? created = await mail.AcceptAsync(
            open.Id, "nils", Tue(11), new AppChoice(appId));

        (await tickets.GetAsync(created!.Id))!.AppId.Should().Be(appId);
    }

    /// <summary>
    /// The analyst recognises an application by its name appearing in a sentence, so it is
    /// sometimes confidently wrong. A person saying "none of them" has to be able to
    /// override that, and is a different answer from nobody having said anything.
    /// </summary>
    [Fact]
    public async Task A_recognised_application_can_be_cleared_by_a_person()
    {
        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Fast egentligen gäller det något annat.");

        MailSuggestion open = message!.Suggestions.Single(
            s => s.Kind == MailSuggestionKind.OpenTicket);

        open.AppId.Should().Be(appId, "the name is in the subject");

        Ticket? created = await mail.AcceptAsync(open.Id, "nils", Tue(11), AppChoice.None);

        (await tickets.GetAsync(created!.Id))!.AppId.Should().BeNull();
    }

    /// <summary>Saying nothing keeps what was recognised.</summary>
    [Fact]
    public async Task Not_choosing_keeps_what_was_recognised()
    {
        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Ingen kommer in.");

        MailSuggestion open = message!.Suggestions.Single(
            s => s.Kind == MailSuggestionKind.OpenTicket);

        Ticket? created = await mail.AcceptAsync(open.Id, "nils", Tue(11));

        (await tickets.GetAsync(created!.Id))!.AppId.Should().Be(appId);
    }

    /// <summary>
    /// The suggestion records what was actually accepted, so the reasoning shown beside it
    /// afterwards does not go on claiming an application nobody agreed to.
    /// </summary>
    [Fact]
    public async Task The_suggestion_records_the_application_that_was_accepted()
    {
        InboundMailMessage? message = await Receive(
            "Journalportalen svarar inte", "Ingen kommer in.");

        MailSuggestion open = message!.Suggestions.Single(
            s => s.Kind == MailSuggestionKind.OpenTicket);

        await mail.AcceptAsync(open.Id, "nils", Tue(11), AppChoice.None);

        db.ChangeTracker.Clear();
        db.Set<MailSuggestion>().Single(s => s.Id == open.Id).AppId.Should().BeNull();
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
    ///
    /// <para>This is the state of a tenant whose mailbox is not set to answer on arrival —
    /// which is every tenant in this class, because none of them has a mailbox row at all.
    /// A message handed in by some other route was not sent to an address whose owner agreed
    /// to answer from it. <see cref="ArrivalPolicy"/> holds the other half.</para>
    /// </summary>
    [Fact]
    public async Task Ingesting_a_message_changes_nothing_by_itself()
    {
        await Receive("Tjänsten svarar inte", "Ingen kommer in i Journalportalen.");

        db.Tickets.Should().BeEmpty();
        db.MailSuggestions.Should().OnlyContain(s => s.State == MailSuggestionState.Pending);
    }

    // ---- Answering on arrival ---------------------------------------------------------------

    /// <summary>
    /// A mailbox set to answer a new report with its number as it arrives.
    /// </summary>
    private void MailboxAnswersOnArrival(bool answers = true)
    {
        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Host = "mail.entit.se",
            Username = "support@entit.se",
            Address = "support@entit.se",
            IsEnabled = true,
            AcknowledgeOnArrival = answers,
        });

        db.SaveChanges();
    }

    /// <summary>
    /// <b>The point of the whole arrangement.</b> A fault report from a recorded contact has
    /// its number before anybody has read it — which is what the reporter is answered with,
    /// since opening a ticket is what sends the receipt.
    ///
    /// <para>The clock still runs from when they wrote, not from when the machine got to
    /// it, and the priority is still the one they proposed, unconfirmed.</para>
    /// </summary>
    [Fact]
    public async Task A_report_from_a_known_contact_gets_its_number_on_arrival()
    {
        MailboxAnswersOnArrival();

        InboundMailMessage? message = await Receive(
            "Journalen svarar inte", "Ingen på avdelning 4 kommer in.");

        Ticket ticket = db.Tickets.Should().ContainSingle().Subject;

        ticket.Number.Should().BeGreaterThan(0);
        ticket.CustomerId.Should().Be(customerId);
        ticket.Channel.Should().Be(TicketChannel.Email);
        ticket.ReportedAt.Should().Be(Tue(10));
        ticket.RequestedByEmail.Should().Be(KnownSender);

        message!.TicketId.Should().Be(ticket.Id);
        message.State.Should().Be(MailTriageState.Handled);
    }

    /// <summary>
    /// §14.6 makes the record between the parties a matter of who did what and when, so a
    /// ticket nobody opened says so in the same field a person's name would have gone in.
    /// </summary>
    [Fact]
    public async Task A_ticket_opened_on_arrival_says_that_nobody_opened_it()
    {
        MailboxAnswersOnArrival();

        InboundMailMessage? message = await Receive("Fel", "Det fungerar inte.");

        message!.HandledBy.Should().Be(ArrivalPolicy.Actor);

        db.MailSuggestions.Single(s => s.Kind == MailSuggestionKind.OpenTicket)
            .DecidedBy.Should().Be(ArrivalPolicy.Actor);
    }

    /// <summary>
    /// The priority is still the customer's proposal and still unconfirmed — §14.3 makes
    /// confirming it a written act with reasons, and answering on arrival does not touch it.
    /// </summary>
    [Fact]
    public async Task Answering_on_arrival_confirms_no_priority()
    {
        MailboxAnswersOnArrival();

        await Receive("Helt nere", "Ingen kan logga in, det är helt nere.");

        Ticket ticket = db.Tickets.Should().ContainSingle().Subject;

        ticket.Priority.Should().Be(TicketPriority.P1);
        ticket.PriorityConfirmedAt.Should().BeNull();
    }

    /// <summary>
    /// The switch is the tenant's. Off, the mailbox behaves exactly as it always did.
    /// </summary>
    [Fact]
    public async Task A_mailbox_told_not_to_answer_leaves_the_message_in_the_queue()
    {
        MailboxAnswersOnArrival(answers: false);

        await Receive("Journalen svarar inte", "Ingen kommer in.");

        db.Tickets.Should().BeEmpty();
        db.MailSuggestions.Should().OnlyContain(s => s.State == MailSuggestionState.Pending);
    }

    /// <summary>
    /// <b>The loop.</b> Answering an out-of-office reply or a bounce with a receipt is two
    /// programs writing to each other, and the account it happens to is ours.
    /// </summary>
    [Fact]
    public async Task A_message_from_a_program_is_not_answered()
    {
        MailboxAnswersOnArrival();

        await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            Subject = "Automatiskt svar: Journalen svarar inte",
            Body = "Jag är tillbaka på måndag.",
            SentAt = Tue(10),
            IsMachineGenerated = true,
        });

        db.Tickets.Should().BeEmpty();
    }

    /// <summary>
    /// A sender nobody has placed gets no ticket in anybody's name and no reply quoting a
    /// number. It goes to the queue, where the flag says who needs deciding.
    /// </summary>
    [Fact]
    public async Task An_unplaced_message_is_not_answered()
    {
        MailboxAnswersOnArrival();

        await Receive("Fel", "Det fungerar inte.", from: "stranger@nowhere.example");

        db.Tickets.Should().BeEmpty();
    }

    /// <summary>
    /// Our own server said the From is not who it claims to be. The receipt would go to
    /// whoever wrote that header, quoting a real ticket number in the customer's matter.
    /// </summary>
    [Fact]
    public async Task A_sender_our_server_could_not_verify_is_not_answered()
    {
        MailboxAnswersOnArrival();

        await ReceiveFailingAuthentication(from: KnownSender);

        db.Tickets.Should().BeEmpty();
    }

    /// <summary>
    /// <b>The bound on a correspondent that loops without saying it is one.</b> A broken
    /// integration or a forwarding rule pointed at us passes every test the policy applies to
    /// a message, every time — and nobody is pressing Accept any more. After the cap its mail
    /// goes to the queue, which is exactly where all of it went before any of this existed.
    /// </summary>
    [Fact]
    public async Task One_address_cannot_be_answered_without_end()
    {
        MailboxAnswersOnArrival();

        for (int i = 0; i < ArrivalPolicy.AutomaticRepliesPerSender + 3; i++)
        {
            await Receive($"Fel {i}", "Det fungerar inte.");
        }

        db.ChangeTracker.Clear();

        db.Tickets.Should().HaveCount(ArrivalPolicy.AutomaticRepliesPerSender);

        // Nothing was lost: every message is in the queue, and the ones over the cap are
        // waiting for a person exactly as they used to.
        db.InboundMailMessages.Should()
            .HaveCount(ArrivalPolicy.AutomaticRepliesPerSender + 3).And
            .Contain(m => m.State == MailTriageState.Proposed);
    }

    /// <summary>
    /// The cap is per address, so one runaway correspondent does not silence the answer to
    /// everybody else at the same customer.
    /// </summary>
    [Fact]
    public async Task The_cap_does_not_follow_the_customer()
    {
        MailboxAnswersOnArrival();

        db.ContractContacts.Add(new ContractContact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Party = ContractParty.Customer,
            Role = ContractContactRole.Deputy,
            Name = "Erik Eriksson",
            Email = "erik@entit.example",
        });
        await db.SaveChangesAsync();

        for (int i = 0; i < ArrivalPolicy.AutomaticRepliesPerSender + 2; i++)
        {
            await Receive($"Fel {i}", "Det fungerar inte.");
        }

        await Receive("Ett annat fel", "Går inte att exportera.", from: "erik@entit.example");

        db.ChangeTracker.Clear();

        db.Tickets.Should().HaveCount(ArrivalPolicy.AutomaticRepliesPerSender + 1);
        db.Tickets.Should().Contain(t => t.RequestedByEmail == "erik@entit.example");
    }

    /// <summary>
    /// A reply already has a number. Answering it with a second one would teach the customer
    /// to quote the newer of two tickets about the same fault.
    /// </summary>
    [Fact]
    public async Task A_reply_to_an_open_ticket_opens_nothing_new()
    {
        MailboxAnswersOnArrival();

        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Email, TicketPriority.P3, Tue(9), "Karin", KnownSender);

        await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = tenantId,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = KnownSender,
            Subject = $"Re: [#{ticket.Number}] Journalen svarar inte",
            Body = "Fortfarande nere.",
            SentAt = Tue(11),
        });

        db.Tickets.Should().ContainSingle();
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

    // ---- A reply that can be placed is placed -------------------------------------------------

    /// <summary>
    /// <b>What used to happen, and no longer does.</b> A reply carrying a reference we minted
    /// was recognised — the duplicate ticket was prevented, which was the point — and then sat
    /// in the queue until somebody pressed a button to put it where the reference already said
    /// it went. It now goes there on arrival.
    ///
    /// <para>No second ticket, no second receipt, and the reply is on the history §14.6 makes
    /// the record between the parties, timed from when it was written.</para>
    /// </summary>
    [Fact]
    public async Task A_reply_carrying_our_reference_lands_on_its_ticket_without_a_person()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen på avdelning 4 kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? reply = await Receive(
            $"Re: {TestTicketReference.Instance.For(opened.Number)} Journalen svarar inte",
            "Nu är det fler avdelningar.",
            sentAt: Tue(11));

        db.Tickets.Should().ContainSingle("a reply must never open a second ticket");

        reply!.TicketId.Should().Be(opened.Id);
        reply.State.Should().Be(MailTriageState.Handled);
        reply.HandledBy.Should().Be(ArrivalPolicy.Actor);

        db.TicketEvents
            .Where(e => e.TicketId == opened.Id && e.Kind == TicketEventKind.Note)
            .Should().Contain(e => e.Detail.Contains("Nu är det fler avdelningar")
                                   && e.At == Tue(11));
    }

    /// <summary>
    /// The same reply with a number somebody typed instead of a reference we minted. It still
    /// finds its ticket and still opens nothing — but a typed digit can be the wrong digit, so
    /// putting a customer's words on that history stays a person's decision.
    /// </summary>
    [Fact]
    public async Task A_reply_quoting_a_bare_number_is_recognised_but_left_in_the_queue()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen på avdelning 4 kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? reply = await Receive(
            $"Re: [#{opened.Number}] Journalen svarar inte", "Nu är det fler avdelningar.");

        db.Tickets.Should().ContainSingle();

        reply!.State.Should().Be(MailTriageState.Proposed);
        reply.TicketId.Should().BeNull();
        reply.Suggestions.Should()
            .Contain(s => s.Kind == MailSuggestionKind.AppendToTicket
                          && s.TicketId == opened.Id
                          && s.State == MailSuggestionState.Pending);
    }

    /// <summary>
    /// A number in a subject that is not a reference at all. The old reader took the first
    /// <c>#</c> it found, so an order number could reach a ticket that happened to carry it;
    /// now the reference is looked for first, and this one is simply a new report.
    /// </summary>
    [Fact]
    public async Task Another_number_in_the_subject_does_not_win_over_the_reference()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? unrelated = await Receive(
            $"Order #90210 — re: {TestTicketReference.Instance.For(opened.Number)}",
            "Samma fel igen.");

        unrelated!.TicketId.Should().Be(opened.Id);
        db.Tickets.Should().ContainSingle();
    }

    /// <summary>
    /// <b>The budget an append must not spend.</b> <see cref="ArrivalPolicy.RepeatWindow"/>
    /// bounds how much mail one address can make us send. Appends send none, so a morning of
    /// replies into one ticket must leave a genuine new report still able to be answered.
    /// </summary>
    [Fact]
    public async Task A_morning_of_replies_does_not_use_up_the_budget_for_answering()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;
        string reference = TestTicketReference.Instance.For(opened.Number);

        for (int i = 0; i < ArrivalPolicy.AutomaticRepliesPerSender + 2; i++)
        {
            await Receive($"Re: {reference} Journalen svarar inte", $"Uppdatering {i}.");
        }

        InboundMailMessage? fresh = await Receive("Nytt fel i exporten", "Exporten stannar.");

        db.Tickets.Should().HaveCount(2);
        fresh!.TicketId.Should().NotBeNull();
    }


    // ---- One of ours answering the customer --------------------------------------------------

    /// <summary>
    /// <b>The whole point of assigning by mail.</b> A technician replies to the message
    /// about their ticket, and what they wrote above the line reaches the customer — on the
    /// history §14.6 makes the record, visible to them, without anybody opening the
    /// application.
    /// </summary>
    [Fact]
    public async Task A_technicians_reply_is_sent_to_the_customer_and_recorded()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? reply = await Receive(
            $"Re: {TestTicketReference.Instance.For(opened.Number)} Journalen svarar inte",
            Replying("Vi har hittat felet och rullar ut en rättning i kväll."),
            from: Technician,
            sentAt: Tue(11),
            authenticity: SenderAuthenticity.Verified);

        db.Tickets.Should().ContainSingle("answering is not opening a new ticket");

        reply!.State.Should().Be(MailTriageState.Handled);
        reply.HandledBy.Should().Be(ArrivalPolicy.Actor);
        reply.TicketId.Should().Be(opened.Id);

        // Placed with the ticket's customer, not the sender's — the From was one of ours.
        reply.CustomerId.Should().Be(customerId);

        db.ChangeTracker.Clear();

        TicketEvent said = db.TicketEvents
            .Where(e => e.TicketId == opened.Id && e.CustomerVisible)
            .ToList()
            .Should().ContainSingle(e => e.Detail.Contains("rullar ut en rättning"))
            .Subject;

        // The history is the record §14.6 settles disputes from, so it names who heard it.
        said.Detail.Should().Contain(KnownSender);

        // And none of what we sent the technician, which is the thing this must never leak.
        said.Detail.Should().NotContain("Respond by");
        said.Detail.Should().NotContain("Reported by");
        said.Detail.Should().NotContain("as reported");

        // What actually went out: from the support address, to the reporter, with the
        // reference in the subject so their reply comes back onto this ticket.
        SentMail toCustomer = sink.Received
            .Where(m => m.To == KnownSender)
            .Should().HaveCount(2, "the receipt, then the answer")
            .And.Subject.Last();

        toCustomer.Message.Subject.Should()
            .StartWith(TestTicketReference.Instance.For(opened.Number));
        toCustomer.Message.TextBody.Should().Contain("rullar ut en rättning i kväll");
        toCustomer.Message.TextBody.Should().NotContain("Respond by");
        toCustomer.Message.TextBody.Should().NotContain(SupportReplyBody.Sentinel);
    }

    /// <summary>
    /// Answering the customer is the §14.4 response. The arrival receipt deliberately does
    /// not discharge it — it is a fact about the past with nobody's name on it — but this is
    /// a person writing to them about their fault, which is what the clock was waiting for.
    /// </summary>
    [Fact]
    public async Task Answering_the_customer_is_the_response_the_clock_was_waiting_for()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        opened.FirstResponseAt.Should().BeNull("a receipt is not an assessment");

        await Receive(
            $"Re: {TestTicketReference.Instance.For(opened.Number)} Journalen svarar inte",
            Replying("Vi tittar på det nu."),
            from: Technician, sentAt: Tue(11),
            authenticity: SenderAuthenticity.Verified);

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Single();

        after.FirstResponseAt.Should().Be(Tue(11));
        after.Status.Should().Be(TicketStatus.InProgress);
    }

    /// <summary>
    /// Our own server could not verify the sender, so nothing goes out. The message is in
    /// the queue with the reply on it, which a person can send in one click — what must not
    /// happen is mail leaving in our name on a From nobody checked.
    /// </summary>
    [Fact]
    public async Task An_unverified_reply_waits_in_the_queue_with_the_text_on_it()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? reply = await Receive(
            $"Re: {TestTicketReference.Instance.For(opened.Number)} Journalen svarar inte",
            Replying("Vi tittar på det."),
            from: Technician, sentAt: Tue(11));

        reply!.State.Should().Be(MailTriageState.Proposed);

        reply.Suggestions.Should().ContainSingle()
            .Which.Should().Match<MailSuggestion>(s =>
                s.Kind == MailSuggestionKind.ReplyToCustomer
                && s.State == MailSuggestionState.Pending
                && s.TicketId == opened.Id
                && s.DraftText == "Vi tittar på det.");

        db.ChangeTracker.Clear();
        db.TicketEvents.Where(e => e.TicketId == opened.Id && e.CustomerVisible)
            .Should().NotContain(e => e.Detail.Contains("Vi tittar"));
    }

    /// <summary>
    /// A colleague writing with nothing above the line. There is nothing to send, and what
    /// is below the line is what we sent them — which the customer must not be handed. So it
    /// says so rather than opening a ticket in a technician's name.
    /// </summary>
    [Fact]
    public async Task A_reply_with_nothing_above_the_line_says_so_and_opens_nothing()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? reply = await Receive(
            $"Re: {TestTicketReference.Instance.For(opened.Number)} Journalen svarar inte",
            Replying(""),
            from: Technician, sentAt: Tue(11),
            authenticity: SenderAuthenticity.Verified);

        db.Tickets.Should().ContainSingle();

        reply!.Suggestions.Should().ContainSingle()
            .Which.Kind.Should().Be(MailSuggestionKind.FlagInternalSender);
    }

    /// <summary>
    /// One of ours writing to support about nothing in particular. It must not become a
    /// ticket in their own name against a customer nobody chose.
    /// </summary>
    [Fact]
    public async Task A_colleague_naming_no_ticket_does_not_open_one()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        InboundMailMessage? message = await Receive(
            "Har vi hört något från kunden?", "Undrar bara.",
            from: Technician, authenticity: SenderAuthenticity.Verified);

        db.Tickets.Should().BeEmpty();

        message!.Suggestions.Should().ContainSingle()
            .Which.Kind.Should().Be(MailSuggestionKind.FlagInternalSender);
    }

    // ---- Handing a new ticket to somebody ----------------------------------------------------

    /// <summary>
    /// <b>The chain the whole feature is.</b> A report arrives, the rota hands it to
    /// somebody, and that person's mailbox gets a message they can reply to in order to
    /// answer the customer. Nobody opened the application.
    /// </summary>
    [Fact]
    public async Task A_new_report_is_handed_to_whoever_is_taking_work()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();
        await new SupportDutyService(new TestDbContextFactory(connection))
            .SetDutyAsync(tenantId, "user-nils", true, null, "nils");

        await Receive("Journalen svarar inte", "Ingen kommer in.");

        db.ChangeTracker.Clear();
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        opened.Assignee.Should().Be("nils");
        opened.AssigneeUserId.Should().Be("user-nils");

        // Not the customer's business whose it is — §14.1 promises them a named contact,
        // not our rota.
        db.TicketEvents.Should().Contain(e =>
            e.Detail.Contains("Assigned to nils") && !e.CustomerVisible);

        SentMail toTechnician = sink.Received
            .Where(m => m.To == Technician)
            .Should().ContainSingle().Subject;

        toTechnician.Message.Subject.Should()
            .StartWith(TestTicketReference.Instance.For(opened.Number));

        // The line that makes replying possible, and the deadline that must stay behind it.
        toTechnician.Message.TextBody.Should().Contain(SupportReplyBody.Sentinel);
        toTechnician.Message.TextBody.Should().Contain("Respond by");

        SupportReplyBody.Above(toTechnician.Message.TextBody)
            .Should().BeNull("there is nothing above the line in what we send");
    }

    /// <summary>
    /// Nobody taking work is an ordinary state, not a failure. The ticket opens unassigned
    /// and waits in the queue, exactly as every ticket did before the rota existed — what
    /// must not happen is it being handed to somebody who is not there.
    /// </summary>
    [Fact]
    public async Task With_nobody_on_the_rota_a_ticket_opens_unassigned()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        await Receive("Journalen svarar inte", "Ingen kommer in.");

        db.ChangeTracker.Clear();
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        opened.Assignee.Should().BeNull();
        opened.AssigneeUserId.Should().BeNull();
        sink.Received.Should().NotContain(m => m.To == Technician);
    }

    /// <summary>
    /// Standing down takes somebody out of the rotation. Somebody has to be able to be free
    /// of work, and the test of that is a ticket arriving while they are.
    /// </summary>
    [Fact]
    public async Task Somebody_standing_down_is_not_handed_a_new_ticket()
    {
        MailboxAnswersOnArrival();
        TechnicianOnStaff();

        SupportDutyService roster = new(new TestDbContextFactory(connection));
        await roster.SetDutyAsync(tenantId, "user-nils", true, null, "nils");
        await roster.SetDutyAsync(tenantId, "user-nils", false, "Semester", "nils");

        await Receive("Journalen svarar inte", "Ingen kommer in.");

        db.ChangeTracker.Clear();
        db.Tickets.Should().ContainSingle().Subject.Assignee.Should().BeNull();
        sink.Received.Should().NotContain(m => m.To == Technician);
    }

    // ---- When our own mail comes back ---------------------------------------------------------

    /// <summary>
    /// <b>The failure this was written for.</b> A receipt to a Gmail address was refused —
    /// no reverse DNS on the sending IP — and the bounce came back to the support address,
    /// was placed with the customer by the address it had been delivered to, and opened a
    /// second ticket in a daemon's name about our own failure to deliver.
    ///
    /// <para>It now lands on the ticket whose receipt failed, where somebody can see that
    /// the customer never learned their number — and not in front of the customer, who is
    /// the one person it must not be explained to this way.</para>
    /// </summary>
    [Fact]
    public async Task A_refused_receipt_lands_on_its_ticket_and_opens_nothing()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        InboundMailMessage? bounce = await mail.IngestAsync(BounceOf(opened, Tue(11)));

        db.Tickets.Should().ContainSingle("a bounce is not a report of a new fault");

        bounce!.State.Should().Be(MailTriageState.Handled);
        bounce.HandledBy.Should().Be(ArrivalPolicy.Actor);
        bounce.TicketId.Should().Be(opened.Id);
        bounce.IsDeliveryReport.Should().BeTrue();
        bounce.FailedRecipient.Should().Be("marie.stahle@gmail.com");

        db.ChangeTracker.Clear();

        TicketEvent note = db.TicketEvents
            .Where(e => e.TicketId == opened.Id)
            .ToList()
            .Should().ContainSingle(e => e.Detail.Contains("was refused"))
            .Subject;

        note.CustomerVisible.Should().BeFalse(
            "the one person who must not be told this way is the person it failed to reach");
        note.Detail.Should().Contain("marie.stahle@gmail.com");
        note.Detail.Should().Contain("PTR record");
    }

    /// <summary>
    /// And no receipt goes back to the daemon. Answering a bounce is writing to a wall at
    /// best and to a loop at worst.
    /// </summary>
    [Fact]
    public async Task Nothing_is_sent_back_to_a_bounce()
    {
        MailboxAnswersOnArrival();

        await Receive("Journalen svarar inte", "Ingen kommer in.");
        Ticket opened = db.Tickets.Should().ContainSingle().Subject;

        int before = sink.Received.Count;

        await mail.IngestAsync(BounceOf(opened, Tue(11)));

        sink.Received.Count.Should().Be(before);
    }

    /// <summary>
    /// A bounce for a ticket that has since closed, or for mail that was not about a ticket
    /// at all. It must still not become a ticket — but it is said out loud rather than
    /// dismissed, because mail of ours being refused is worth somebody's attention.
    /// </summary>
    [Fact]
    public async Task A_bounce_naming_no_open_ticket_is_flagged_and_not_opened()
    {
        MailboxAnswersOnArrival();

        InboundMailMessage bounce = BounceOf(number: 9999, sentAt: Tue(11));
        InboundMailMessage? read = await mail.IngestAsync(bounce);

        db.Tickets.Should().BeEmpty();

        read!.Suggestions.Should().ContainSingle()
            .Which.Should().Match<MailSuggestion>(s =>
                s.Kind == MailSuggestionKind.FlagDeliveryFailure && s.TicketId == null);

        read.State.Should().Be(MailTriageState.Proposed);
    }

    private InboundMailMessage BounceOf(Ticket ticket, DateTime sentAt) =>
        BounceOf(ticket.Number, sentAt);

    /// <summary>
    /// The bounce as the reader produces it from what the mail server sends — built through
    /// MailMessageReader rather than by hand, so the test cannot disagree with the parsing
    /// about what a report looks like.
    /// </summary>
    private InboundMailMessage BounceOf(int number, DateTime sentAt)
    {
        MultipartReport report = new("delivery-status");

        report.Add(new TextPart("plain")
        {
            Text = "<marie.stahle@gmail.com> (host 'gmail-smtp-in.l.google.com' rejected "
                 + "command 'BDAT 2084 LAST' with code 550 (5.7.25) 'The IP address sending "
                 + "this message does not have a PTR record setup')",
        });

        MessageDeliveryStatus status = new();
        status.StatusGroups.Add(new HeaderList
        {
            { "Final-Recipient", "rfc822;marie.stahle@gmail.com" },
            { "Action", "failed" },
            { "Status", "5.7.25" },
        });
        report.Add(status);
        report.Add(new TextPart("rfc822-headers")
        {
            Text = $"Message-Id: <{SupportMessageId.For(number)}>\r\n",
        });

        MimeMessage raw = new();
        raw.From.Add(new MailboxAddress("Mail Delivery Subsystem", "MAILER-DAEMON@mail.entit.se"));
        raw.To.Add(new MailboxAddress("", "support@entit.se"));
        raw.Subject = "Delivery Status Notification (Failure)";
        raw.Date = sentAt;
        raw.Body = report;

        InboundMailMessage read = MailMessageReader.Read(raw, tenantId, sentAt);

        // Delivered to the customer's own support address, which is what placed the bounce
        // with that customer and opened the second ticket.
        read.DeliveredTo = "support@entit.se";

        return read;
    }
}
