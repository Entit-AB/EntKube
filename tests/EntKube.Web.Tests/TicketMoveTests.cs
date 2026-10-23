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

        TicketService.TicketMove? moved = await tickets.MoveAsync(ours, 
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

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

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

        await tickets.MoveAsync(ours, 
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

        TicketService.TicketMove? moved = await tickets.MoveAsync(ours, 
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

        TicketService.TicketMove? moved = await tickets.MoveAsync(ours, 
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

        await tickets.RecordResponseAsync(ours, ticket.Id, "nils", saturday.AddHours(2));

        await tickets.MoveAsync(ours, 
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

        Func<Task> move = () => tickets.MoveAsync(ours, 
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

        Func<Task> move = () => tickets.MoveAsync(ours, 
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

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

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
    /// ⭐ The reply does not merely find the moved ticket — it is written to it.
    ///
    /// <para>The test above stops at the suggestion, and that is the gap this one closes.
    /// Accepting an <c>AppendToTicket</c> went through <c>TicketService.AddEventAsync</c> with the
    /// arriving mailbox's tenant, while the ticket now belonged to another one. That method takes
    /// a tenant and <b>returns silently</b> when the ticket is not that tenant's, so nothing was
    /// written — and <c>AcceptAsync</c> marked the suggestion Accepted and the message Handled
    /// regardless, because nothing after the switch inspects whether the branch did anything.</para>
    ///
    /// <para>So the customer's reply left the triage queue and landed nowhere. ⚠️ Worse than
    /// losing it: the inbox then states "Added to its ticket as it arrived… it went onto that
    /// history rather than waiting here", so the one place an operator would look to notice the
    /// loss actively asserts the write happened.</para>
    /// </summary>
    [Fact]
    public async Task A_reply_to_a_moved_ticket_is_written_to_its_history_not_just_matched()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

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

        MailSuggestion append = reply!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);

        await mail.AcceptAsync(ours, append.Id, "nils", Tue(13));

        // The assertion the suggestion-only test could not make: the words the customer sent are
        // on the ticket. Asserting on the BODY rather than on a count, because a count would also
        // be satisfied by the Moved event already sitting there.
        List<string> history = await db.TicketEvents
            .Where(e => e.TicketId == ticket.Id)
            .Select(e => e.Detail)
            .ToListAsync();

        history.Should().Contain(d => d.Contains("Fortfarande inget."),
            "a reply that the product matched to this ticket, told the customer to expect on this "
            + "ticket, and then marked handled, has to actually be on this ticket");
    }

    /// <summary>
    /// ⚠️ The boundary the fix above had to be careful not to open.
    ///
    /// <para>Making the write succeed with the ticket's own tenant is what stops a moved
    /// ticket's reply being lost — but a suggestion can point at a ticket merely because the
    /// mail THREADS to it, and that header is forgeable by anyone who knows the number
    /// (<c>SupportMessageId</c> says so). Before the fix a forged thread naming another
    /// tenant's ticket was harmless only by accident, because the write refused. Making it
    /// succeed without asking what justified the crossing would have converted a silent bug
    /// into a way to put words on another tenant's ticket — customer-visible, on a history
    /// §14.6 makes evidence, with an operator clicking accept and nothing on screen saying a
    /// boundary was crossed.</para>
    ///
    /// <para>So a crossing needs something an outsider cannot produce: a token we minted, or
    /// the message coming from the address the ticket records as its reporter. This sender is
    /// a recorded contact of the customer — so the suggestion forms, which is the point — but
    /// is <b>not</b> that reporter, and the subject carries no token.</para>
    /// </summary>
    [Fact]
    public async Task A_forged_thread_from_someone_who_is_not_the_reporter_does_not_cross_tenants()
    {
        Ticket ticket = await Reported();

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        // Both domains are recognised for our customer, so neither sender is a stranger to the
        // product — the only difference that matters is which one the ticket names as reporter.
        foreach (string domain in new[] { "kund.example", "annan.example" })
        {
            db.CustomerEmailDomains.Add(new CustomerEmailDomain
            {
                Id = Guid.NewGuid(),
                TenantId = ours,
                CustomerId = ourCustomer,
                Domain = domain,
            });
        }

        await db.SaveChangesAsync();

        InboundMailMessage? forged = await mail.IngestAsync(new InboundMailMessage
        {
            TenantId = ours,
            MessageId = Guid.NewGuid().ToString("N"),
            FromAddress = "bo@annan.example",
            Subject = "Komplettering",
            Body = "Lagg till det har.",
            SentAt = Tue(12),

            // Anyone who knows the number can mint this; nothing about it is checked.
            InReplyTo = SupportMessageId.For(ticket.Number),
        });

        // ⚠️ Asserted, not tolerated. Measured by reverting the rule under test: the suggestion
        // DOES form here, and without the rule the text crosses. Had this been written as "skip
        // if no suggestion", the test would go on passing if placement ever stopped happening —
        // green for the wrong reason, and the boundary never exercised.
        MailSuggestion append = forged!.Suggestions
            .Should().ContainSingle(s => s.Kind == MailSuggestionKind.AppendToTicket,
                "the thread places the message on the ticket — that is the precondition for "
                + "there being a boundary to refuse to cross")
            .Subject;

        await mail.AcceptAsync(ours, append.Id, "nils", Tue(13));

        db.ChangeTracker.Clear();

        (await db.TicketEvents.Where(e => e.TicketId == ticket.Id).Select(e => e.Detail)
            .ToListAsync())
            .Should().NotContain(d => d.Contains("Lagg till det har."),
                "a threaded header is forgeable, so on its own it must not carry text across a "
                + "tenant boundary onto a §14.6 history");

        (await db.InboundMailMessages.AsNoTracking().FirstAsync(m => m.Id == forged.Id))
            .State.Should().NotBe(MailTriageState.Handled,
                "nothing was written, so it stays where somebody can look at both sides");
    }

    /// <summary>
    /// ⚠️ The companion invariant, which is the one that generalises: if a branch could not do
    /// what it claims, the message stays in the queue.
    ///
    /// <para>Fixing the tenant makes the case above work, but it does not stop the next cause —
    /// a ticket closed or deleted between triage and acceptance — from being swallowed the same
    /// way. "Handled" is a claim about what happened, so it is now only made when something did.
    /// This is asserted separately from the write itself so that a regression in either is
    /// attributable: the two have different causes and different fixes.</para>
    /// </summary>
    [Fact]
    public async Task A_suggestion_that_wrote_nothing_leaves_the_message_in_the_queue()
    {
        Ticket ticket = await Reported();

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
            Body = "Har ni hunnit titta?",
            SentAt = Tue(12),
        });

        MailSuggestion append = reply!.Suggestions
            .Single(s => s.Kind == MailSuggestionKind.AppendToTicket && s.TicketId == ticket.Id);

        // The ticket goes away between the analyst's suggestion and the operator accepting it,
        // which is the general shape of "the branch could not do what it said".
        db.Tickets.Remove(await db.Tickets.FirstAsync(t => t.Id == ticket.Id));
        await db.SaveChangesAsync();

        await mail.AcceptAsync(ours, append.Id, "nils", Tue(13));

        InboundMailMessage after = await db.InboundMailMessages
            .AsNoTracking().FirstAsync(m => m.Id == reply.Id);

        after.State.Should().NotBe(MailTriageState.Handled,
            "nothing was written, so saying it was handled hides a lost customer message behind "
            + "a state that reads as success");
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

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

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

        await tickets.MoveAsync(ours, ticket.Id, theirCustomer, theirApp, "nils", null, Tue(11));

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

        Ticket? ticket = await mail.AcceptAsync(ours, open.Id, "nils", Tue(10), new AppChoice(ourApp));

        await tickets.MoveAsync(ours, ticket!.Id, theirCustomer, theirApp, "nils", null, Tue(11));

        db.ChangeTracker.Clear();
        InboundMailMessage after = db.InboundMailMessages.Single(m => m.Id == message.Id);

        after.CustomerId.Should().Be(theirCustomer);
        after.TenantId.Should().Be(ours, "it really did arrive in that mailbox");
    }
    // ---- Whose ticket, and whose destination ---------------------------------------------

    /// <summary>
    /// <b>The destination stays deliberately cross-tenant.</b> Moving a misfiled report to the
    /// right customer in another tenant is the whole point of the feature, and this pins it so
    /// a later tightening cannot quietly remove it: the one test below that refuses a move must
    /// not be read as "moves across tenants are refused".
    /// </summary>
    [Fact]
    public async Task A_ticket_still_moves_to_another_tenants_customer()
    {
        Ticket ticket = await Reported();

        TicketService.TicketMove? moved = await tickets.MoveAsync(
            ours, ticket.Id, theirCustomer, theirApp, "nils", "Misfiled", Tue(11));

        moved.Should().NotBeNull();

        db.ChangeTracker.Clear();
        Ticket stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);

        stored.CustomerId.Should().Be(theirCustomer);
        stored.TenantId.Should().Be(theirs);
    }

    /// <summary>
    /// What is scoped is the <b>source</b>. An operator notices a misfiled report because it
    /// arrived in their own queue; moving a ticket out of a queue they cannot see is a
    /// different act, and not one the remark on MoveAsync covers.
    /// </summary>
    [Fact]
    public async Task A_ticket_we_were_not_sent_cannot_be_moved()
    {
        Ticket ticket = await Reported();

        // The same move, asked for by the other tenant.
        (await tickets.MoveAsync(
            theirs, ticket.Id, theirCustomer, theirApp, "anna", "Mine now", Tue(11)))
            .Should().BeNull();

        db.ChangeTracker.Clear();
        Ticket stored = await db.Tickets.AsNoTracking().FirstAsync(t => t.Id == ticket.Id);

        stored.CustomerId.Should().Be(ourCustomer);
        stored.TenantId.Should().Be(ours);
    }

    /// <summary>The queue reads describe what was worked and when it is due.</summary>
    [Fact]
    public async Task Another_tenants_queue_is_not_readable()
    {
        await Reported();

        // A positive first, so the empty below is a refusal rather than an empty period.
        (await tickets.GetOpenQueueAsync(ours, ourCustomer, Tue(12))).Should().NotBeEmpty();

        (await tickets.GetOpenQueueAsync(theirs, ourCustomer, Tue(12))).Should().BeEmpty();
        (await tickets.GetForPeriodAsync(theirs, ourCustomer, Tue(0), Tue(23), Tue(23)))
            .Should().BeEmpty();
    }
}
