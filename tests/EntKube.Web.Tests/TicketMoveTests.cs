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
/// Moving a ticket to another customer's application, including one in another tenant.
///
/// <para><b>What a move must not cost.</b> A support address takes what it is sent, so a
/// report about somebody else's application arrives at whichever mailbox the sender happened
/// to know. Retyping it in the right place loses the arrival time §14.3 counts from, the
/// history §14.6 makes evidence between the parties, and — since the receipt now goes out as
/// the message lands — the number the reporter has already been told to quote.</para>
///
/// <para>So the tests here are about what survives the move and what correctly does not:
/// the number and the record survive, the agreement it is measured against does not.</para>
/// </summary>
public class TicketMoveTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026.</summary>
    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;
    private readonly SupportMailService mail;

    private readonly Guid ours = Guid.NewGuid();
    private readonly Guid theirs = Guid.NewGuid();
    private readonly Guid ourCustomer = Guid.NewGuid();
    private readonly Guid theirCustomer = Guid.NewGuid();
    private readonly Guid ourApp = Guid.NewGuid();
    private readonly Guid theirApp = Guid.NewGuid();

    private const string Reporter = "karin@kund.example";

    public TicketMoveTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = ours, Name = "ENTIT", Slug = "entit" });
        db.Tenants.Add(new Tenant { Id = theirs, Name = "Nordvik Drift", Slug = "nordvik" });

        db.Customers.Add(new Customer { Id = ourCustomer, TenantId = ours, Name = "Vårdbolaget" });
        db.Customers.Add(new Customer
        {
            Id = theirCustomer, TenantId = theirs, Name = "Regionen",
        });

        db.Apps.Add(new App { Id = ourApp, CustomerId = ourCustomer, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = theirApp, CustomerId = theirCustomer, Name = "Remissen" });

        // Their application is covered around the clock; ours only in office hours. The
        // difference is the point: a moved ticket must be measured against the agreement it
        // arrives under, not the one it left.
        Cover(theirApp, theirs, SupportWindow.S4);
        Cover(ourApp, ours, SupportWindow.S1);

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));
        mail = new SupportMailService(
            factory, new RuleBasedMailAnalyst(TestTicketReference.Instance),
            new MailTriageRuleService(factory), tickets,
            new TimeService(factory, contracts), TestTicketReference.Instance,
            new SupportDutyService(factory));
    }

    private void Cover(Guid appId, Guid tenantId, SupportWindow window)
    {
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
            SupportWindow = window,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            Reason = "On-boarding",
        });

        db.ApplicationContracts.Add(contract);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Reported(DateTime? at = null) => tickets.CreateAsync(
        ours, ourCustomer, ourApp, "Remisser kommer inte fram", "Inget syns i listan.",
        TicketChannel.Email, TicketPriority.P2, at ?? Tue(10), "Karin", Reporter);

    // ---- What survives ----------------------------------------------------------------------

    /// <summary>
    /// <b>The number is the promise.</b> The receipt told the reporter to keep it in the
    /// subject for as long as the ticket is open, and their client threads replies on a
    /// Message-Id whose only identifying part is that number. A move that renumbered would
    /// strand every reply already in flight and make a liar of the receipt.
    /// </summary>
    [Fact]
    public async Task A_moved_ticket_keeps_its_number()
    {
        Ticket ticket = await Reported();
        int number = ticket.Number;

        TicketService.TicketMove? moved = await tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", "It is their application.", Tue(11));

        moved!.Value.Ticket.Number.Should().Be(number);
        moved.Value.Ticket.TenantId.Should().Be(theirs);
        moved.Value.Ticket.CustomerId.Should().Be(theirCustomer);
        moved.Value.Ticket.AppId.Should().Be(theirApp);
    }

    /// <summary>
    /// Numbers are handed out across the installation for exactly this reason. Two tenants
    /// each numbering from one would collide the first time a ticket crossed.
    /// </summary>
    [Fact]
    public async Task Numbers_do_not_repeat_across_tenants()
    {
        Ticket first = await Reported();

        Ticket second = await tickets.CreateAsync(
            theirs, theirCustomer, theirApp, "Annat fel", "Beskrivning.",
            TicketChannel.Email, TicketPriority.P3, Tue(10));

        second.Number.Should().NotBe(first.Number);
    }

    /// <summary>
    /// §14.3 counts from when the report arrived and §14.6 makes the history the record
    /// between the parties. Neither is ours to rewrite by moving the ticket.
    /// </summary>
    [Fact]
    public async Task The_arrival_time_and_the_history_are_left_alone()
    {
        Ticket ticket = await Reported();
        int eventsBefore = db.TicketEvents.Count(e => e.TicketId == ticket.Id);

        await tickets.MoveAsync(ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        db.ChangeTracker.Clear();
        Ticket after = db.Tickets.Single(t => t.Id == ticket.Id);

        after.ReportedAt.Should().Be(Tue(10));
        after.Priority.Should().Be(TicketPriority.P2);
        db.TicketEvents.Count(e => e.TicketId == ticket.Id).Should().BeGreaterThan(eventsBefore);
    }

    /// <summary>
    /// The move itself is evidence: it changes which agreement the ticket is measured
    /// against, and §14.6 makes that history the record. Not customer-visible — the portal
    /// shows this to whoever owns the ticket now, and where it came from names another
    /// customer of ours.
    /// </summary>
    [Fact]
    public async Task The_move_is_recorded_with_who_and_why()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", "Reported to the wrong address.", Tue(11));

        db.ChangeTracker.Clear();

        TicketEvent move = db.TicketEvents
            .Single(e => e.TicketId == ticket.Id && e.Kind == TicketEventKind.Moved);

        move.Actor.Should().Be("nils");
        move.Detail.Should().Contain("ENTIT").And.Contain("Nordvik Drift");
        move.Detail.Should().Contain("Remissen");
        move.Detail.Should().Contain("Reported to the wrong address.");
        move.CustomerVisible.Should().BeFalse();
    }

    // ---- What correctly does not survive -------------------------------------------------

    /// <summary>
    /// <b>The agreement follows the application.</b> Holding a customer to terms bought by
    /// somebody else is the error a move exists to correct, so the support window, §4.1's
    /// guarantee and §13's call-out are all re-derived at the destination.
    /// </summary>
    [Fact]
    public async Task The_support_window_comes_from_the_destination_agreement()
    {
        Ticket ticket = await Reported();
        ticket.SupportWindow.Should().Be(SupportWindow.S1);

        TicketService.TicketMove? moved = await tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        moved!.Value.Ticket.SupportWindow.Should().Be(SupportWindow.S4);
        moved.Value.WindowChanged.Should().BeTrue();
    }

    /// <summary>
    /// A report that arrived at nine on a Saturday is outside S1 and inside S4. Moving it to
    /// an application covered around the clock therefore starts its clock when it arrived —
    /// while nothing has yet been measured against the old start.
    /// </summary>
    [Fact]
    public async Task The_clock_start_is_re_derived_while_nothing_has_been_measured()
    {
        DateTime saturday = Swedish(2026, 9, 26, 9);

        Ticket ticket = await Reported(saturday);
        ticket.ClockStartsAt.Should().NotBe(saturday, "S1 does not cover a Saturday");

        TicketService.TicketMove? moved = await tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", null, saturday.AddHours(1));

        moved!.Value.Ticket.ClockStartsAt.Should().Be(saturday);
    }

    /// <summary>
    /// <b>Once a response has been given, the clock start is evidence.</b> §14.6 makes these
    /// timestamps the record between the parties, and moving the start under a response
    /// already made would rewrite whether that response was late.
    /// </summary>
    [Fact]
    public async Task A_clock_already_answered_against_is_not_re_derived()
    {
        DateTime saturday = Swedish(2026, 9, 26, 9);

        Ticket ticket = await Reported(saturday);
        DateTime startedAt = ticket.ClockStartsAt;

        await tickets.RecordResponseAsync(ticket.Id, "nils", saturday.AddHours(2));

        await tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", null, saturday.AddHours(3));

        db.ChangeTracker.Clear();
        db.Tickets.Single(t => t.Id == ticket.Id).ClockStartsAt.Should().Be(startedAt);
    }

    // ---- What is refused -------------------------------------------------------------------

    /// <summary>
    /// An application belonging to a different customer would be silently wrong rather than
    /// obviously wrong: the ticket would read as this customer's and be measured against
    /// somebody else's agreement.
    /// </summary>
    [Fact]
    public async Task An_application_belonging_to_somebody_else_is_refused()
    {
        Ticket ticket = await Reported();

        Func<Task> move = () => tickets.MoveAsync(
            ticket.Id, theirCustomer, ourApp, "nils", null, Tue(11));

        await move.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not belong to Regionen*");
    }

    /// <summary>
    /// A number issued before numbering went installation-wide can still be taken on the
    /// other side. Refusing is the honest answer: the alternative is renumbering a ticket
    /// whose number is already in the customer's inbox.
    /// </summary>
    [Fact]
    public async Task A_move_that_would_need_a_new_number_is_refused()
    {
        Ticket ticket = await Reported();

        // A legacy row, numbered under the old per-tenant scheme.
        db.Tickets.Add(new Ticket
        {
            Id = Guid.NewGuid(),
            Number = ticket.Number,
            TenantId = theirs,
            CustomerId = theirCustomer,
            Title = "Något annat",
            Description = "Beskrivning.",
            Priority = TicketPriority.P3,
            ReportedAt = Tue(8),
            ClockStartsAt = Tue(8),
            PriorityEffectiveFrom = Tue(8),
            Channel = TicketChannel.Portal,
        });
        await db.SaveChangesAsync();

        Func<Task> move = () => tickets.MoveAsync(
            ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        await move.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has a ticket*");
    }

    // ---- The reply that comes afterwards ---------------------------------------------------

    /// <summary>
    /// <b>The failure a move would otherwise cause.</b> The reporter goes on writing to the
    /// address they always used, quoting the number we told them to quote. Their mail is
    /// still placed with their own customer, whose open tickets no longer include the one
    /// they are writing about — so without help every reply after a move opens a second
    /// ticket about a fault already being worked, and the reporter has done nothing wrong.
    /// </summary>
    [Fact]
    public async Task A_reply_still_finds_a_ticket_that_has_moved_away()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        // The sender is still recognised as the original customer's contact.
        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            CustomerId = ourCustomer,
            Domain = "kund.example",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = Reporter,
            Subject = $"Re: [#{ticket.Number}] Remisser kommer inte fram",
            Body = "Fortfarande inget.",
            SentAt = Tue(12),
        });

        reply!.Suggestions.Should().Contain(s =>
            s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);
    }

    /// <summary>
    /// The same, threaded rather than quoted, and with the subject gone entirely. The
    /// Message-Id we minted is the strongest evidence there is that this message belongs to
    /// this ticket — it was sent to the people the ticket concerns and carries a random half
    /// nobody can guess — so it reaches the ticket wherever the ticket now lives.
    ///
    /// <para>The sender is registered here for the same reason as above: a message from an
    /// address nobody recognises is flagged for a person before anything else is said about
    /// it, which is older than any of this and not what these tests are about.</para>
    /// </summary>
    [Fact]
    public async Task A_threaded_reply_finds_a_moved_ticket_too()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            CustomerId = ourCustomer,
            Domain = "kund.example",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? reply = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = Reporter,
            Subject = "SV: nagot annat",
            Body = "Fortfarande inget.",
            InReplyTo = SupportMessageId.For(ticket.Number),
            SentAt = Tue(12),
        });

        reply!.Suggestions.Should().Contain(s =>
            s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);
    }

    /// <summary>
    /// <b>And the thing that must not follow from it.</b> A number typed into a subject is
    /// not evidence about who is writing, so it only reaches a ticket whose own reporter is
    /// this sender. Otherwise anybody could attach their message — and their attachments —
    /// to a stranger's ticket by guessing a number.
    /// </summary>
    [Fact]
    public async Task A_stranger_quoting_a_number_does_not_reach_that_ticket()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        db.CustomerEmailDomains.Add(new CustomerEmailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = ours,
            CustomerId = ourCustomer,
            Domain = "nyfiken.example",
        });
        await db.SaveChangesAsync();

        InboundMailMessage? snooper = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "someone@nyfiken.example",
            Subject = $"Re: [#{ticket.Number}] vad händer",
            Body = "Berätta mer.",
            SentAt = Tue(12),
        });

        snooper!.Suggestions.Should().NotContain(s => s.TicketId == ticket.Id);
    }

    /// <summary>
    /// The mail a ticket came in on follows it, so that a later reply is analysed against
    /// the customer who owns it now — their applications, their hour bank, their tickets.
    /// The tenant on the message is left alone: it says which mailbox the message physically
    /// arrived in, which no move changes.
    /// </summary>
    [Fact]
    public async Task The_message_it_arrived_on_follows_the_ticket()
    {
        InboundMailMessage? message = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = Reporter,
            Subject = "Remisser kommer inte fram",
            Body = "Inget syns i listan.",
            SentAt = Tue(10),
            CustomerId = ourCustomer,
        });

        MailSuggestion open = message!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.OpenTicket);

        Ticket? ticket = await mail.AcceptAsync(open.Id, "nils", Tue(10), new AppChoice(ourApp));

        await tickets.MoveAsync(ticket!.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        db.ChangeTracker.Clear();
        InboundMailMessage after = db.InboundMailMessages.Single(m => m.Id == message.Id);

        after.CustomerId.Should().Be(theirCustomer);
        after.TenantId.Should().Be(ours, "it really did arrive in that mailbox");
    }
}
