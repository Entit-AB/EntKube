using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// Registering and moving tickets: the rules §14 applies at the door, and the history that
/// §14.6 makes the record between the parties.
/// </summary>
public class TicketServiceTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour = 0, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private static DateTime Fri(int hour, int minute = 0) => Swedish(2026, 9, 25, hour, minute);

    private static DateTime Mon(int hour, int minute = 0) => Swedish(2026, 9, 28, hour, minute);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid s1AppId = Guid.NewGuid();
    private readonly Guid s4AppId = Guid.NewGuid();
    private readonly Guid unmanagedAppId = Guid.NewGuid();

    public TicketServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.AddRange(
            new App { Id = s1AppId, CustomerId = customerId, Name = "Bokning" },
            new App { Id = s4AppId, CustomerId = customerId, Name = "Journal" },
            new App { Id = unmanagedAppId, CustomerId = customerId, Name = "Okänd" });

        AddContract(s1AppId, SupportWindow.S1);
        AddContract(s4AppId, SupportWindow.S4);
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        tickets = new TicketService(factory, new ContractService(factory));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void AddContract(Guid appId, SupportWindow window)
    {
        ApplicationContract contract = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AppId = appId,
            Origin = ContractOrigin.ExternallyDeveloped,
            OnboardedAt = Swedish(2026, 1, 1),
        };

        contract.ServiceLevels.Add(new ApplicationServiceLevel
        {
            Id = Guid.NewGuid(),
            ApplicationContractId = contract.Id,
            Level = ManagementLevel.Standard,
            SupportWindow = window,
            EffectiveFrom = Swedish(2026, 1, 1),
            Reason = "On-boarding",
        });

        db.ApplicationContracts.Add(contract);
    }

    private Task<Ticket> Raise(
        Guid appId, TicketPriority priority, DateTime at, TicketChannel channel = TicketChannel.Portal) =>
        tickets.CreateAsync(
            tenantId, customerId, appId, "Tjänsten svarar inte", "Ingen kommer in.",
            channel, priority, at, "Kundens tekniska kontakt", "tech@example.org");

    // ---- Registration ---------------------------------------------------------------------

    /// <summary>
    /// §9.1: a ticket arriving outside the window is registered when it arrives, but its
    /// response time counts from the next opening.
    /// </summary>
    [Fact]
    public async Task A_ticket_raised_after_hours_starts_its_clock_at_the_next_opening()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Fri(18));

        ticket.ReportedAt.Should().Be(Fri(18));
        ticket.ClockStartsAt.Should().Be(Mon(8));
        ticket.SupportWindow.Should().Be(SupportWindow.S1);
    }

    [Fact]
    public async Task A_ticket_raised_inside_the_window_starts_immediately()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(10));

        ticket.ClockStartsAt.Should().Be(Tue(10));
    }

    /// <summary>
    /// §13: a P1 worked outside the bought window is an utryckning — the callout rate, with
    /// a two-hour minimum. The same ticket on an application that bought S4 is not.
    /// </summary>
    [Fact]
    public async Task A_P1_outside_the_bought_window_is_flagged_as_an_utryckning()
    {
        Ticket onS1 = await Raise(s1AppId, TicketPriority.P1, Fri(22));
        Ticket onS4 = await Raise(s4AppId, TicketPriority.P1, Fri(22));

        onS1.IsCallout.Should().BeTrue();
        onS4.IsCallout.Should().BeFalse();
        onS4.ClockStartsAt.Should().Be(Fri(22));
    }

    [Fact]
    public async Task A_P3_outside_the_window_is_not_an_utryckning()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P3, Fri(22));

        ticket.IsCallout.Should().BeFalse();
    }

    /// <summary>
    /// §14.3 asks the customer to propose a priority and gives their view precedence for P1
    /// and P2 until the first assessment. So the proposal is adopted on arrival, and the
    /// confirmation is a separate act with its own timestamp.
    /// </summary>
    [Fact]
    public async Task The_customers_proposed_priority_stands_until_the_first_assessment()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(10));

        ticket.ProposedPriority.Should().Be(TicketPriority.P1);
        ticket.Priority.Should().Be(TicketPriority.P1);
        ticket.PriorityConfirmedAt.Should().BeNull();
    }

    /// <summary>
    /// §14.3: a ticket our own monitoring opened is registered with the time of the alarm as
    /// its reporting time, not the time somebody got round to writing it down.
    /// </summary>
    [Fact]
    public async Task A_monitoring_ticket_is_reported_at_the_time_of_the_alarm()
    {
        Ticket ticket = await Raise(s4AppId, TicketPriority.P1, Fri(3), TicketChannel.Monitoring);

        ticket.ReportedAt.Should().Be(Fri(3));
        ticket.ClockStartsAt.Should().Be(Fri(3));
        ticket.Channel.Should().Be(TicketChannel.Monitoring);
    }

    /// <summary>
    /// An application with no Bilaga A still has to be able to raise a ticket — but the
    /// assumption has to be visible. S1 is the narrowest window, so it is also the
    /// assumption least favourable to us.
    /// </summary>
    [Fact]
    public async Task An_application_with_no_contract_is_assumed_to_be_S1_and_says_so()
    {
        Ticket ticket = await Raise(unmanagedAppId, TicketPriority.P2, Tue(10));
        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        ticket.SupportWindow.Should().Be(SupportWindow.S1);
        loaded!.Events.Should().Contain(e => e.Detail.Contains("No support window in force"));
    }

    [Fact]
    public async Task Tickets_are_numbered_in_sequence_within_a_tenant()
    {
        Ticket first = await Raise(s1AppId, TicketPriority.P3, Tue(9));
        Ticket second = await Raise(s1AppId, TicketPriority.P3, Tue(10));

        first.Number.Should().Be(1);
        second.Number.Should().Be(2);
    }

    // ---- Priority -------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_the_priority_unchanged_leaves_the_clock_where_it_was()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));

        await tickets.ConfirmPriorityAsync(
            ticket.Id, TicketPriority.P1, "Agreed — the service is down for everyone.", "nils", Tue(9, 20));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.PriorityEffectiveFrom.Should().Be(Tue(9));
        loaded.PriorityConfirmedAt.Should().Be(Tue(9, 20));
        loaded.Events.Should().Contain(e => e.Kind == TicketEventKind.PriorityConfirmed);
    }

    /// <summary>
    /// §14.3: a change resets the targets to run from the reprioritisation, and the reason
    /// has to be in writing. A P1 downgraded once a workaround exists should not carry its
    /// P1 resolution target into the following week.
    /// </summary>
    [Fact]
    public async Task Reprioritising_moves_the_targets_and_records_the_reason()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));

        await tickets.ConfirmPriorityAsync(
            ticket.Id, TicketPriority.P3, "Workaround in place; downgraded per §14.3.", "nils", Tue(11));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);
        TicketSlaStatus status = TicketService.StatusOf(loaded!, Tue(12));

        loaded!.Priority.Should().Be(TicketPriority.P3);
        loaded.PriorityEffectiveFrom.Should().Be(Tue(11));
        loaded.Events.Should().Contain(e =>
            e.Kind == TicketEventKind.Reprioritised && e.Detail.Contains("P1 → P3"));

        // A P3 response is due at the end of the next working day, counted from 11:00.
        status.Response.Deadline.Should().Be(Swedish(2026, 9, 23, 17));
    }

    // ---- Response, pauses, resolution ------------------------------------------------------

    [Fact]
    public async Task Only_the_first_response_counts()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));

        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(9, 30));
        await tickets.RecordResponseAsync(ticket.Id, "someone else", Tue(14));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.FirstResponseAt.Should().Be(Tue(9, 30));
        loaded.Status.Should().Be(TicketStatus.InProgress);
    }

    /// <summary>
    /// §14.4 requires the wait to be documented with its time and the party being waited
    /// for. Both end up in the ticket's own history, not in somebody's inbox.
    /// </summary>
    [Fact]
    public async Task A_pause_records_who_is_being_waited_for()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(9, 30));

        await tickets.StartPauseAsync(
            ticket.Id, WaitingOn.CustomerVendor, "Hosting-leverantören",
            "Awaiting a network trace.", "nils", Tue(10));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.Status.Should().Be(TicketStatus.Waiting);
        loaded.Pauses.Should().ContainSingle();
        loaded.Pauses[0].Party.Should().Be("Hosting-leverantören");
        loaded.Pauses[0].EndedAt.Should().BeNull();
        loaded.Events.Should().Contain(e =>
            e.Kind == TicketEventKind.PauseStarted && e.Detail.Contains("Hosting-leverantören"));
    }

    /// <summary>
    /// A second open pause would subtract the same stretch of time twice, so it is refused
    /// rather than quietly accepted.
    /// </summary>
    [Fact]
    public async Task A_ticket_cannot_be_paused_twice_at_once()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.StartPauseAsync(ticket.Id, WaitingOn.Customer, null, "First", "nils", Tue(10));

        TicketPause? second = await tickets.StartPauseAsync(
            ticket.Id, WaitingOn.Customer, null, "Second", "nils", Tue(11));

        second.Should().BeNull();
        (await tickets.GetAsync(ticket.Id))!.Pauses.Should().ContainSingle();
    }

    [Fact]
    public async Task Ending_a_pause_restarts_the_clock()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(9, 30));
        await tickets.StartPauseAsync(ticket.Id, WaitingOn.Customer, null, "Awaiting access.", "nils", Tue(10));
        await tickets.EndPauseAsync(ticket.Id, "nils", Tue(12));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);
        TicketSlaStatus status = TicketService.StatusOf(loaded!, Tue(13));

        loaded!.Status.Should().Be(TicketStatus.InProgress);
        loaded.Pauses[0].EndedAt.Should().Be(Tue(12));
        status.Resolution.Paused.Should().BeFalse();

        // Two of the four elapsed hours were paused.
        status.Resolution.Elapsed.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task Resolving_closes_any_open_pause()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.StartPauseAsync(ticket.Id, WaitingOn.Customer, null, "Awaiting a decision.", "nils", Tue(10));
        await tickets.ResolveAsync(ticket.Id, "Customer accepted the workaround.", "nils", Tue(14));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.Status.Should().Be(TicketStatus.Resolved);
        loaded.ResolvedAt.Should().Be(Tue(14));
        loaded.Pauses.Should().OnlyContain(p => p.EndedAt == Tue(14));
    }

    // ---- Deviations and exemptions -----------------------------------------------------------

    [Fact]
    public async Task A_missed_P1_response_is_a_penalty_deviation()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(14));

        TicketSlaStatus status = TicketService.StatusOf((await tickets.GetAsync(ticket.Id))!, Tue(15));

        status.Response.Breached.Should().BeTrue();
        status.IsPenaltyDeviation.Should().BeTrue();
    }

    [Fact]
    public async Task A_missed_P3_response_is_not()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P3, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Swedish(2026, 9, 25, 9));

        TicketSlaStatus status = TicketService.StatusOf((await tickets.GetAsync(ticket.Id))!, Fri(10));

        status.Response.Breached.Should().BeTrue();
        status.IsPenaltyDeviation.Should().BeFalse();
    }

    /// <summary>
    /// §14.4: the fault was in a system outside the agreement. Time is still billable and
    /// the ticket is not a deviation — but the reason is written down, because §14.6 says a
    /// deviation only stops counting when something explains it.
    /// </summary>
    [Fact]
    public async Task A_rejected_ticket_is_billable_but_not_a_deviation()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));
        await tickets.RejectAsync(
            ticket.Id, "The fault is in the customer's identity provider.", "nils", Tue(16));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);
        TicketSlaStatus status = TicketService.StatusOf(loaded!, Tue(17));

        loaded!.Status.Should().Be(TicketStatus.Rejected);
        loaded.ExcludedFromSla.Should().BeTrue();
        loaded.SlaExclusionReason.Should().Contain("§14.4");
        status.Response.Breached.Should().BeTrue();
        status.IsPenaltyDeviation.Should().BeFalse();
    }

    [Fact]
    public async Task A_documented_exemption_takes_a_ticket_out_of_the_count()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(14));
        await tickets.ExcludeFromSlaAsync(
            ticket.Id, "Force majeure — regional power outage.", "nils", Tue(15));

        TicketSlaStatus status = TicketService.StatusOf((await tickets.GetAsync(ticket.Id))!, Tue(16));

        status.IsPenaltyDeviation.Should().BeFalse();
    }

    /// <summary>§14.6: a written incident report within five working days of closing a P1.</summary>
    [Fact]
    public async Task A_closed_P1_owes_an_incident_report_within_five_working_days()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));
        await tickets.ResolveAsync(ticket.Id, "Restarted the ingress.", "nils", Tue(11));
        await tickets.CloseAsync(ticket.Id, "nils", Tue(12));

        TicketSlaStatus status = TicketService.StatusOf((await tickets.GetAsync(ticket.Id))!, Tue(13));

        // Five working days after Tuesday 22 September is Tuesday 29 September.
        status.IncidentReportDue.Should().Be(Swedish(2026, 9, 29, 17));
    }

    [Fact]
    public async Task A_closed_P3_owes_no_incident_report()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P3, Tue(9));
        await tickets.CloseAsync(ticket.Id, "nils", Tue(12));

        TicketSlaStatus status = TicketService.StatusOf((await tickets.GetAsync(ticket.Id))!, Tue(13));

        status.IncidentReportDue.Should().BeNull();
    }

    /// <summary>
    /// §14.4 resolves a P1 or P2 only when service is restored or the customer accepts a
    /// workaround, so the customer has to be able to say it is not resolved. The clock
    /// resumes from where it stopped: the time already spent was still spent.
    /// </summary>
    [Fact]
    public async Task A_customer_can_reopen_a_ticket_they_do_not_accept_as_resolved()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(9, 30));
        await tickets.ResolveAsync(ticket.Id, "Restarted the pod.", "nils", Tue(11));

        await tickets.ReopenAsync(ticket.Id, "It failed again ten minutes later.", "kund", Tue(12));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);
        TicketSlaStatus status = TicketService.StatusOf(loaded!, Tue(13));

        loaded!.Status.Should().Be(TicketStatus.InProgress);
        loaded.ResolvedAt.Should().BeNull();
        loaded.Events.Should().Contain(e => e.Detail.Contains("does not accept this as resolved"));

        // Four hours of window time have passed since the clock started, none of them paused.
        status.Resolution.Elapsed.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public async Task Reopening_clears_a_close_as_well()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P2, Tue(9));
        await tickets.ResolveAsync(ticket.Id, "Fixed.", "nils", Tue(11));
        await tickets.CloseAsync(ticket.Id, "nils", Tue(11, 30));

        await tickets.ReopenAsync(ticket.Id, "Still broken.", "kund", Tue(12));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.ClosedAt.Should().BeNull();
        loaded.Status.Should().Be(TicketStatus.InProgress);
    }

    // ---- Queues and periods --------------------------------------------------------------------

    [Fact]
    public async Task The_open_queue_is_worst_first_and_excludes_finished_tickets()
    {
        await Raise(s1AppId, TicketPriority.P3, Tue(9));
        Ticket urgent = await Raise(s1AppId, TicketPriority.P1, Tue(10));
        Ticket done = await Raise(s1AppId, TicketPriority.P2, Tue(11));
        await tickets.CloseAsync(done.Id, "nils", Tue(12));

        List<TicketSlaStatus> queue = await tickets.GetOpenQueueAsync(customerId, Tue(13));

        queue.Should().HaveCount(2);
        queue[0].Ticket.Id.Should().Be(urgent.Id);
    }

    [Fact]
    public async Task A_period_report_covers_what_was_reported_in_it()
    {
        await Raise(s1AppId, TicketPriority.P2, Swedish(2026, 8, 20, 10));
        await Raise(s1AppId, TicketPriority.P1, Tue(10));

        List<TicketSlaStatus> september = await tickets.GetForPeriodAsync(
            customerId, Swedish(2026, 9, 1), Swedish(2026, 10, 1), Tue(13));

        september.Should().ContainSingle();
        september[0].Ticket.Priority.Should().Be(TicketPriority.P1);
    }

    /// <summary>
    /// Everything that moves a ticket leaves an entry. §14.6 makes these timestamps the
    /// record between the parties, so a gap in them is a gap in the evidence.
    /// </summary>
    [Fact]
    public async Task Every_step_is_written_to_the_history()
    {
        Ticket ticket = await Raise(s1AppId, TicketPriority.P1, Tue(9));
        await tickets.ConfirmPriorityAsync(ticket.Id, TicketPriority.P1, "Confirmed.", "nils", Tue(9, 10));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(9, 15));
        await tickets.StartPauseAsync(ticket.Id, WaitingOn.Customer, null, "Awaiting access.", "nils", Tue(10));
        await tickets.EndPauseAsync(ticket.Id, "nils", Tue(11));
        await tickets.ResolveAsync(ticket.Id, "Fixed.", "nils", Tue(12));
        await tickets.CloseAsync(ticket.Id, "nils", Tue(13));

        Ticket? loaded = await tickets.GetAsync(ticket.Id);

        loaded!.Events.Select(e => e.Kind).Should().Contain([
            TicketEventKind.Created,
            TicketEventKind.PriorityProposed,
            TicketEventKind.PriorityConfirmed,
            TicketEventKind.Responded,
            TicketEventKind.PauseStarted,
            TicketEventKind.PauseEnded,
            TicketEventKind.Resolved,
            TicketEventKind.Closed,
        ]);
    }
}
