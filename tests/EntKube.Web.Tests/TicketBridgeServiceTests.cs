using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Tickets.Bridge;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Taking in a ticket raised in a customer's own service desk.
///
/// <para><b>What is defended here is a boundary, not a feature.</b> §14.6 makes our
/// timestamps the record between the parties and §28 makes the monthly report binding
/// unless disputed within thirty days. A bridge that let another system confirm a priority,
/// pause a clock or close a ticket would make that record a copy of somebody else's
/// database — and a dispute would become an argument about whose row was right. Every test
/// below is one half of that boundary.</para>
/// </summary>
public class TicketBridgeServiceTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>A Tuesday, inside every support window.</summary>
    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TicketBridgeService bridge;
    private readonly TicketService tickets;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid otherAppId = Guid.NewGuid();
    private readonly Guid connectionId = Guid.NewGuid();

    public TicketBridgeServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Entit AB" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.Apps.Add(new App { Id = otherAppId, CustomerId = customerId, Name = "Tidboken" });

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

        db.TicketBridgeConnections.Add(new TicketBridgeConnection
        {
            Id = connectionId,
            TenantId = tenantId,
            CustomerId = customerId,
            System = TicketSystem.ServiceNow,
            Instance = "entit.service-now.com",
            IsEnabled = true,
            DefaultAppId = appId,
            PriorityMap = "1=P1\n2=P2\n3=P3",
        });

        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        ContractService contracts = new(factory);
        tickets = new TicketService(factory, contracts, SilentTicketNotifier.For(factory));

        bridge = new TicketBridgeService(
            factory, tickets, [new ServiceNowAdapter(), new JiraAdapter()],
            NullLogger<TicketBridgeService>.Instance);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Incident(
        string sysId = "abc", string title = "Journalen svarar inte",
        string? priority = "1", string? state = "2", string? openedAt = null,
        string? comment = null, string? ci = null) =>
        $$"""
        {
          "sys_id": "{{sysId}}",
          "number": "INC0012345",
          "short_description": "{{title}}",
          "description": "Ingen kommer in.",
          {{(priority is null ? "" : $"\"priority\": \"{priority}\",")}}
          {{(openedAt is null ? "" : $"\"opened_at\": \"{openedAt}\",")}}
          {{(comment is null ? "" : $"\"work_notes\": \"{comment}\",")}}
          {{(ci is null ? "" : $"\"cmdb_ci\": \"{ci}\",")}}
          "caller_id": "Karin Karlsson",
          "state": "{{state}}"
        }
        """;

    private Task<BridgeDelivery> Deliver(string payload, DateTime? at = null) =>
        bridge.DeliverAsync(connectionId, payload, at ?? Tue(10));

    private async Task<Ticket> TicketNumber(int number) =>
        await db.Tickets.AsNoTracking().FirstAsync(t => t.Number == number);

    // ---- Taking one in -----------------------------------------------------------------

    [Fact]
    public async Task An_incident_becomes_a_ticket()
    {
        BridgeDelivery delivery = await Deliver(Incident());

        delivery.Accepted.Should().BeTrue();
        delivery.Created.Should().BeTrue();

        Ticket ticket = await TicketNumber(delivery.TicketNumber!.Value);

        ticket.CustomerId.Should().Be(customerId);
        ticket.AppId.Should().Be(appId);
        ticket.Title.Should().Be("Journalen svarar inte");
        ticket.Channel.Should().Be(TicketChannel.Integration);
        ticket.RequestedBy.Should().Be("Karin Karlsson");
    }

    /// <summary>
    /// Where the ticket came from is written onto its history rather than left to be
    /// inferred from the channel — it is the first question asked when a ticket's terms are
    /// argued about.
    /// </summary>
    [Fact]
    public async Task The_ticket_says_where_it_came_from()
    {
        BridgeDelivery delivery = await Deliver(Incident());

        Ticket ticket = await TicketNumber(delivery.TicketNumber!.Value);

        List<TicketEvent> events = await db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticket.Id).ToListAsync();

        events.Should().Contain(e => e.Detail.Contains("entit.service-now.com")
                                     && e.Detail.Contains("INC0012345"));
    }

    // ---- The boundary ------------------------------------------------------------------

    /// <summary>
    /// <b>A priority that arrives is a proposal.</b> §14.3 makes confirming one a written,
    /// reasoned act with a person's name on it. A mapped P1 carries a two-hour response, a
    /// call-out rate and 10% of the window fee if missed — none of which another system's
    /// dropdown may settle on our behalf.
    /// </summary>
    [Fact]
    public async Task An_arriving_priority_is_proposed_and_never_confirmed()
    {
        BridgeDelivery delivery = await Deliver(Incident(priority: "1"));

        Ticket ticket = await TicketNumber(delivery.TicketNumber!.Value);

        ticket.ProposedPriority.Should().Be(TicketPriority.P1);
        ticket.Priority.Should().Be(TicketPriority.P1, "§14.3 lets their view stand until we assess");
        ticket.PriorityConfirmedAt.Should().BeNull();
        ticket.PriorityConfirmedBy.Should().BeNull();
    }

    /// <summary>
    /// A priority nobody mapped proposes nothing, and the ticket says so where whoever
    /// makes the first assessment will read it.
    /// </summary>
    [Fact]
    public async Task An_unmapped_priority_proposes_nothing_and_says_so()
    {
        BridgeDelivery delivery = await Deliver(Incident(priority: "4"));

        Ticket ticket = await TicketNumber(delivery.TicketNumber!.Value);

        ticket.ProposedPriority.Should().BeNull();

        List<TicketEvent> events = await db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticket.Id).ToListAsync();

        events.Should().Contain(e => e.Detail.Contains("not mapped"));
    }

    /// <summary>
    /// <b>The most important test here.</b> §14.4 makes resolution something we record and
    /// the customer accepts. A service desk closing its own copy is not that act — and a
    /// bridge that closed ours would stop the resolution clock early, shrinking measured
    /// breach time and with it our own §14.6 penalties. An integration that quietly reduces
    /// our liability is one no customer should ever be asked to believe.
    /// </summary>
    [Fact]
    public async Task Their_closure_is_recorded_and_closes_nothing()
    {
        BridgeDelivery raised = await Deliver(Incident());

        BridgeDelivery closed = await Deliver(Incident(state: "7"), Tue(14));

        closed.Accepted.Should().BeTrue();
        closed.Created.Should().BeFalse();

        Ticket ticket = await TicketNumber(raised.TicketNumber!.Value);

        ticket.Status.Should().Be(TicketStatus.New);
        ticket.ResolvedAt.Should().BeNull();
        ticket.ClosedAt.Should().BeNull();

        List<TicketEvent> events = await db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticket.Id).ToListAsync();

        events.Should().Contain(e => e.Detail.Contains("stays open"));
    }

    /// <summary>
    /// The other half of the same boundary: nothing arriving may pause a clock. A pause
    /// stops the resolution clock, which is the same lever on the same penalty.
    /// </summary>
    [Fact]
    public async Task Nothing_that_arrives_pauses_a_clock()
    {
        BridgeDelivery raised = await Deliver(Incident());

        await Deliver(Incident(comment: "Väntar på leverantören."), Tue(14));

        Ticket ticket = await TicketNumber(raised.TicketNumber!.Value);

        ticket.Status.Should().Be(TicketStatus.New);

        List<TicketPause> pauses = await db.TicketPauses.AsNoTracking()
            .Where(p => p.TicketId == ticket.Id).ToListAsync();

        pauses.Should().BeEmpty();
    }

    // ---- Not doing it twice ---------------------------------------------------------------

    /// <summary>
    /// A service desk resends on every field change and retries what it thinks failed. The
    /// same incident must not become a second ticket.
    /// </summary>
    [Fact]
    public async Task The_same_incident_delivered_twice_is_one_ticket()
    {
        BridgeDelivery first = await Deliver(Incident());
        BridgeDelivery again = await Deliver(Incident(comment: "Fortfarande nere."), Tue(11));

        again.Created.Should().BeFalse();
        again.TicketNumber.Should().Be(first.TicketNumber);

        (await db.Tickets.CountAsync()).Should().Be(1);
    }

    /// <summary>A different incident from the same system is a different ticket.</summary>
    [Fact]
    public async Task A_different_incident_is_a_different_ticket()
    {
        await Deliver(Incident(sysId: "one"));
        await Deliver(Incident(sysId: "two"), Tue(11));

        (await db.Tickets.CountAsync()).Should().Be(2);
    }

    /// <summary>
    /// The update lands on the ticket's history, which is where somebody working it will
    /// see it — a comment that arrived nowhere is the integration not working.
    /// </summary>
    [Fact]
    public async Task An_update_lands_on_the_ticket_it_belongs_to()
    {
        BridgeDelivery raised = await Deliver(Incident());
        await Deliver(Incident(comment: "Fortfarande nere."), Tue(11));

        Ticket ticket = await TicketNumber(raised.TicketNumber!.Value);

        List<TicketEvent> events = await db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticket.Id).ToListAsync();

        events.Should().Contain(e => e.Detail.Contains("Fortfarande nere."));
    }

    // ---- When it happened -------------------------------------------------------------------

    /// <summary>
    /// §14.3 counts from when the fault was reported, and for a ticket raised in the
    /// customer's own system that moment happened there — not when their integration got
    /// round to telling us.
    /// </summary>
    [Fact]
    public async Task The_clock_counts_from_when_they_recorded_it()
    {
        BridgeDelivery delivery = await Deliver(
            Incident(openedAt: "2026-09-22 06:00:00"), at: Tue(10));

        Ticket ticket = await TicketNumber(delivery.TicketNumber!.Value);

        ticket.ReportedAt.Should().Be(new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// A payload with no time falls back to now — later than the truth, and therefore never
    /// inventing SLA time we did not actually have.
    /// </summary>
    [Fact]
    public async Task A_delivery_with_no_time_counts_from_now()
    {
        BridgeDelivery delivery = await Deliver(Incident(), at: Tue(10));

        (await TicketNumber(delivery.TicketNumber!.Value)).ReportedAt.Should().Be(Tue(10));
    }

    /// <summary>
    /// A sending system whose clock runs fast would otherwise start a clock in the future,
    /// and every target would be measured from a moment that had not happened.
    /// </summary>
    [Fact]
    public async Task A_time_in_the_future_is_not_believed()
    {
        BridgeDelivery delivery = await Deliver(
            Incident(openedAt: "2027-01-01 00:00:00"), at: Tue(10));

        (await TicketNumber(delivery.TicketNumber!.Value)).ReportedAt.Should().Be(Tue(10));
    }

    // ---- Which application ---------------------------------------------------------------------

    [Fact]
    public async Task A_named_application_is_matched_by_name()
    {
        BridgeDelivery delivery = await Deliver(Incident(ci: "Tidboken"));

        (await TicketNumber(delivery.TicketNumber!.Value)).AppId.Should().Be(otherAppId);
    }

    /// <summary>
    /// A configuration item we do not recognise falls back to the connection's default
    /// rather than being matched approximately: a ticket on the wrong application is
    /// measured against the wrong support window and looks entirely fine.
    /// </summary>
    [Fact]
    public async Task An_unrecognised_application_falls_back_to_the_default()
    {
        BridgeDelivery delivery = await Deliver(Incident(ci: "Something else entirely"));

        (await TicketNumber(delivery.TicketNumber!.Value)).AppId.Should().Be(appId);
    }

    // ---- Refusing ------------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_that_is_switched_off_takes_nothing_in()
    {
        TicketBridgeConnection off = await db.TicketBridgeConnections.FirstAsync();
        off.IsEnabled = false;
        await db.SaveChangesAsync();

        BridgeDelivery delivery = await Deliver(Incident());

        delivery.Accepted.Should().BeFalse();
        (await db.Tickets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_delivery_for_no_connection_is_refused() =>
        (await bridge.DeliverAsync(Guid.NewGuid(), Incident(), Tue(10)))
            .Accepted.Should().BeFalse();

    /// <summary>
    /// A service desk sending us rubbish should be visible here, not only in their own
    /// logs — a quiet connection and a broken one look identical from the queue.
    /// </summary>
    [Fact]
    public async Task An_unreadable_delivery_is_recorded_on_the_connection()
    {
        BridgeDelivery delivery = await Deliver("not json at all");

        delivery.Accepted.Should().BeFalse();

        TicketBridgeConnection stored = await db.TicketBridgeConnections
            .AsNoTracking().FirstAsync();

        stored.LastError.Should().NotBeNullOrWhiteSpace();
        stored.ConsecutiveFailures.Should().Be(1);
    }

    /// <summary>And a delivery that works clears it, or the warning never goes away.</summary>
    [Fact]
    public async Task A_delivery_that_works_clears_the_last_failure()
    {
        await Deliver("not json at all");
        await Deliver(Incident(), Tue(11));

        TicketBridgeConnection stored = await db.TicketBridgeConnections
            .AsNoTracking().FirstAsync();

        stored.LastError.Should().BeNull();
        stored.ConsecutiveFailures.Should().Be(0);
    }
}
