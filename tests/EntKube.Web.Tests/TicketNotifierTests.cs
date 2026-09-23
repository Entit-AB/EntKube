using EntKube.Web.Data;
using EntKube.Web.Services;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Mail;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Telling people a ticket exists — over a real socket, to a server that keeps what it is
/// given.
///
/// <para>The wording of the receipt is argued over in
/// <see cref="TicketAcknowledgementTests"/>, which needs nothing but a string. What is
/// checked here is the half that string tests cannot see: that a message actually leaves,
/// that everyone who should hear does, and that one bad address does not silence the
/// rest.</para>
/// </summary>
public class TicketNotifierTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, 09:00 — inside S1.</summary>
    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly SmtpSink sink = new();

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TicketNotifierTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });

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

        factory = new TestDbContextFactory(connection);
    }

    public void Dispose()
    {
        sink.Dispose();
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A ticket service whose notifier sends to the sink.</summary>
    private TicketService Tickets()
    {
        IConfiguration smtp = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Smtp:Host"] = sink.Host,
                ["Smtp:Port"] = sink.Port.ToString(),
                ["Smtp:FromAddress"] = "alerts@entkube.io",
            }).Build();

        ContractService contracts = new(factory);

        return new TicketService(factory, contracts, new TicketNotifier(
            factory,
            new SmtpSettingsResolver(factory, smtp),
            new OnCallService(factory),
            NullLogger<TicketNotifier>.Instance));
    }

    private Task<Ticket> Raise(
        TicketPriority priority = TicketPriority.P2,
        string? reporter = "karin@capio.example") =>
        Tickets().CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Portal, priority, Tue(9), "Karin", reporter);

    private void AddContact(ContractContactRole role, string email) =>
        db.ContractContacts.Add(new ContractContact
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            Party = ContractParty.Customer,
            Role = role,
            Name = email,
            Email = email,
        });

    /// <summary>
    /// A shift that straddles the wall clock, which everything else in this class avoids.
    ///
    /// <para>The notifier asks who is on call <em>now</em>, not who was on call when the
    /// fault was reported, and that is the right question: the person to wake is whoever
    /// is holding the phone when it lands. So the rota is the one fixture that cannot use
    /// the fixed September dates the tickets do.</para>
    /// </summary>
    private void PutOnCall(string email)
    {
        Guid scheduleId = Guid.NewGuid();

        db.OnCallSchedules.Add(new OnCallSchedule
        {
            Id = scheduleId, TenantId = tenantId, Name = "Primary", IsEnabled = true,
        });
        db.OnCallShifts.Add(new OnCallShift
        {
            Id = Guid.NewGuid(),
            ScheduleId = scheduleId,
            AssigneeName = email,
            AssigneeEmail = email,
            StartsAt = DateTime.UtcNow.AddHours(-1),
            EndsAt = DateTime.UtcNow.AddHours(11),
        });
    }

    // ---- Something actually leaves --------------------------------------------------------

    /// <summary>
    /// The receipt reaches the person who reported the fault, with the reference in the
    /// subject where a reply will carry it back.
    /// </summary>
    [Fact]
    public async Task The_reporter_gets_a_receipt()
    {
        Ticket ticket = await Raise();

        SentMail receipt = sink.Received.Should()
            .ContainSingle(m => m.To == "karin@capio.example").Subject;

        receipt.Message.Subject.Should().StartWith($"[#{ticket.Number}]");
        receipt.Message.TextBody.Should().Contain("We have received your report");
        receipt.Message.TextBody.Should().Contain("as reported, not yet confirmed");
    }

    /// <summary>
    /// A reply carries our Message-Id back as In-Reply-To, which threads it onto the ticket
    /// even where the subject has been mangled by a client or a forward.
    /// </summary>
    [Fact]
    public async Task The_receipt_carries_a_message_id_a_reply_can_be_threaded_by()
    {
        Ticket ticket = await Raise();

        sink.Received.Single(m => m.To == "karin@capio.example")
            .Message.MessageId.Should().StartWith($"ticket-{ticket.Number}.");
    }

    /// <summary>
    /// §14.1 requires the customer's designated contact to hear about every incident. Four
    /// places in the code said so and nothing sent them anything.
    /// </summary>
    [Fact]
    public async Task The_designated_contact_and_the_deputy_are_told()
    {
        AddContact(ContractContactRole.TechnicalContact, "tech@capio.example");
        AddContact(ContractContactRole.Deputy, "deputy@capio.example");
        await db.SaveChangesAsync();

        await Raise();

        sink.Received.Select(m => m.To).Should()
            .Contain("tech@capio.example").And.Contain("deputy@capio.example");
    }

    /// <summary>
    /// The reporter who is also the designated contact gets one message, not two. A
    /// duplicate looks like a system that does not know who it is talking to.
    /// </summary>
    [Fact]
    public async Task Somebody_who_is_both_reporter_and_contact_is_told_once()
    {
        AddContact(ContractContactRole.TechnicalContact, "karin@capio.example");
        await db.SaveChangesAsync();

        await Raise(reporter: "karin@capio.example");

        sink.Received.Count(m => m.To == "karin@capio.example").Should().Be(1);
    }

    /// <summary>
    /// A ticket nobody is told about is a ticket found by looking, so whoever is on call
    /// hears — and gets the operator's version rather than the customer's receipt.
    /// </summary>
    [Fact]
    public async Task Whoever_is_on_call_is_told_in_their_own_words()
    {
        PutOnCall("jour@entit.se");
        await db.SaveChangesAsync();

        await Raise();

        SentMail internalMail = sink.Received.Single(m => m.To == "jour@entit.se");

        internalMail.Message.TextBody.Should().Contain("A new ticket is open");
        internalMail.Message.TextBody.Should().Contain("confirm it in writing");
        internalMail.Message.TextBody.Should().NotContain("We have received your report");
    }

    // ---- The sender ---------------------------------------------------------------------------

    /// <summary>
    /// From the tenant's support address when one is configured, so a reply lands back in
    /// the mailbox being polled rather than wherever alerts happen to come from.
    /// </summary>
    [Fact]
    public async Task Mail_comes_from_the_support_address_when_there_is_one()
    {
        db.SupportMailboxes.Add(new SupportMailbox
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Host = "imap.example.com",
            Username = "support@entit.se",
            Address = "support@entit.se",
        });
        await db.SaveChangesAsync();

        await Raise();

        sink.Received.Should().OnlyContain(m => m.From == "support@entit.se");
    }

    [Fact]
    public async Task Without_a_support_address_it_falls_back_to_the_configured_sender()
    {
        await Raise();

        sink.Received.Should().OnlyContain(m => m.From == "alerts@entkube.io");
    }

    // ---- When it goes wrong ---------------------------------------------------------------------

    /// <summary>
    /// <b>The claim worth checking.</b> One recipient failing must not silence the others —
    /// a stale address in the contact register should not cost the on-call engineer their
    /// notification.
    /// </summary>
    [Fact]
    public async Task One_bad_address_does_not_stop_the_others_being_told()
    {
        AddContact(ContractContactRole.TechnicalContact, "gone@capio.example");
        PutOnCall("jour@entit.se");
        await db.SaveChangesAsync();

        sink.Reject.Add("gone@capio.example");

        await Raise();

        sink.Received.Select(m => m.To).Should()
            .Contain("karin@capio.example").And.Contain("jour@entit.se");
    }

    /// <summary>
    /// A ticket raised by monitoring has nobody to acknowledge. That is not an error and
    /// must not become one.
    /// </summary>
    [Fact]
    public async Task A_ticket_with_no_reporter_still_opens()
    {
        PutOnCall("jour@entit.se");
        await db.SaveChangesAsync();

        Ticket ticket = await Raise(reporter: null);

        ticket.Number.Should().BeGreaterThan(0);
        sink.Received.Select(m => m.To).Should().Contain("jour@entit.se");
    }

    /// <summary>
    /// <b>The one that matters most.</b> A P1 that cannot be recorded because a mail server
    /// is refusing connections is an outage made out of bookkeeping.
    /// </summary>
    [Fact]
    public async Task A_mail_server_that_is_not_there_does_not_stop_a_ticket_being_opened()
    {
        IConfiguration nowhere = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                // A port nothing is listening on.
                ["Smtp:Host"] = "127.0.0.1",
                ["Smtp:Port"] = "1",
            }).Build();

        ContractService contracts = new(factory);

        TicketService tickets = new(factory, contracts, new TicketNotifier(
            factory,
            new SmtpSettingsResolver(factory, nowhere),
            new OnCallService(factory),
            NullLogger<TicketNotifier>.Instance));

        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Portal, TicketPriority.P1, Tue(9), "Karin", "karin@capio.example");

        ticket.Number.Should().BeGreaterThan(0);
        db.Tickets.Should().ContainSingle(t => t.Id == ticket.Id);
    }

    /// <summary>
    /// No SMTP configured at all is the state a fresh installation is in, and opening a
    /// ticket has to work there too.
    /// </summary>
    [Fact]
    public async Task No_mail_configuration_at_all_does_not_stop_a_ticket_being_opened()
    {
        ContractService contracts = new(factory);

        TicketService tickets = new(
            factory, contracts, SilentTicketNotifier.For(factory));

        Ticket ticket = await tickets.CreateAsync(
            tenantId, customerId, appId, "Journalen svarar inte", "Ingen kommer in.",
            TicketChannel.Portal, TicketPriority.P1, Tue(9), "Karin", "karin@capio.example");

        ticket.Number.Should().BeGreaterThan(0);
    }
}
