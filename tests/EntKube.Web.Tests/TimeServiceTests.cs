using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The two reports the agreement needs and the billing system consumes: how much of the
/// hour bank is gone (§11), and how many hours have been committed (§12, §20).
///
/// <para>Invoicing happens elsewhere. What is defended here is that the numbers handed
/// over are right and broken down the way §20 requires — per application, ticket, day and
/// time category.</para>
/// </summary>
public class TimeServiceTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    private static DateTime Tue(int hour, int minute = 0) => Swedish(2026, 9, 22, hour, minute);

    private static DateTime Sat(int hour, int minute = 0) => Swedish(2026, 9, 26, hour, minute);

    private static readonly DateTime September = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TimeService time;
    private readonly ContractService contracts;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public TimeServiceTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journal" });
        db.SaveChanges();

        TestDbContextFactory factory = new(connection);
        contracts = new ContractService(factory);
        time = new TimeService(factory, contracts);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void UseHourBank(decimal hours)
    {
        db.PortfolioAgreements.Add(new PortfolioAgreement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingModel = PricingModel.HourBank,
            HourBankHoursPerMonth = hours,
        });
        db.SaveChanges();
    }

    private void UseTimeAndMaterials()
    {
        db.PortfolioAgreements.Add(new PortfolioAgreement
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PricingModel = PricingModel.TimeAndMaterials,
        });
        db.SaveChanges();
    }

    private Guid AddTicket(TicketPriority priority = TicketPriority.P3, bool callout = false)
    {
        Guid id = Guid.NewGuid();

        db.Tickets.Add(new Ticket
        {
            Id = id,
            TenantId = tenantId,
            CustomerId = customerId,
            AppId = appId,
            Number = db.Tickets.Count() + 1,
            Title = "Export is failing",
            Priority = priority,
            PriorityEffectiveFrom = Tue(9),
            ReportedAt = Tue(9),
            ClockStartsAt = Tue(9),
            SupportWindow = SupportWindow.S1,
            IsCallout = callout,
        });
        db.SaveChanges();

        return id;
    }

    private Task<TimeEntry> Log(
        DateTime from, DateTime to, Guid? ticketId = null, WorkKind kind = WorkKind.Management) =>
        time.LogAsync(tenantId, customerId, appId, ticketId, from, to, kind, "Investigation", "nils");

    // ---- Logging ---------------------------------------------------------------------------

    [Fact]
    public async Task An_entry_that_ends_before_it_starts_is_rejected()
    {
        Func<Task> act = async () => await Log(Tue(11), Tue(9));

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ---- The hour bank (§11) --------------------------------------------------------------------

    [Fact]
    public async Task The_bank_is_drawn_down_by_the_hours_worked()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket();
        await Log(Tue(9), Tue(12), ticket);

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.EntitledHours.Should().Be(20m);
        statement.DrawnHours.Should().Be(3m);
        statement.RemainingHours.Should().Be(17m);
        statement.IsExhausted.Should().BeFalse();
    }

    /// <summary>§13: one hour of weekend work takes 1.5 hours out of the bank.</summary>
    [Fact]
    public async Task Out_of_hours_work_costs_the_bank_more_than_an_hour()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket();
        await Log(Sat(10), Sat(12), ticket);

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.DrawnHours.Should().Be(3m);
        statement.ByCategory[SupportTimeCategory.WeekendOrRedDay].Should().Be(3m);
    }

    /// <summary>
    /// §11.1 makes the bank non-rolling: each month starts again at the agreed number,
    /// whatever last month did. Deriving the balance from the month's own entries is what
    /// makes that true without anyone having to remember to reset a counter.
    /// </summary>
    [Fact]
    public async Task Each_month_starts_again_at_the_agreed_number()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket();
        await Log(Swedish(2026, 8, 18, 9), Swedish(2026, 8, 18, 17), ticket);

        TimebankStatement august = await time.GetTimebankAsync(
            customerId, new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc));
        TimebankStatement september = await time.GetTimebankAsync(customerId, September);

        august.DrawnHours.Should().Be(8m);
        september.DrawnHours.Should().Be(0m);
        september.RemainingHours.Should().Be(20m);
    }

    /// <summary>
    /// §11.1: hours not used by month end expire. Showing what is about to be lost is the
    /// only warning anybody gets.
    /// </summary>
    [Fact]
    public async Task Unused_hours_are_reported_as_expiring()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket();
        await Log(Tue(9), Tue(14), ticket);

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.ExpiringHours.Should().Be(15m);
    }

    [Fact]
    public async Task Work_beyond_the_bank_is_reported_as_overspill()
    {
        UseHourBank(2m);
        Guid ticket = AddTicket();
        await Log(Tue(9), Tue(14), ticket);

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.DrawnHours.Should().Be(5m);
        statement.OverspillHours.Should().Be(3m);
        statement.RemainingHours.Should().Be(0m);
        statement.IsExhausted.Should().BeTrue();
    }

    [Fact]
    public async Task Under_Modell_B_there_is_no_bank()
    {
        UseTimeAndMaterials();
        Guid ticket = AddTicket();
        await Log(Tue(9), Tue(12), ticket);

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.EntitledHours.Should().BeNull();
        statement.OverspillHours.Should().Be(0m);
    }

    /// <summary>
    /// §11.1 keeps development assignments out of the hour bank. Development hours are still
    /// committed and still billed — they simply do not come out of the bank.
    /// </summary>
    [Fact]
    public async Task Development_work_does_not_touch_the_bank()
    {
        UseHourBank(20m);
        await Log(Tue(9), Tue(13), null, WorkKind.Development);

        TimebankStatement bank = await time.GetTimebankAsync(customerId, September);
        CommittedHoursStatement committed = await time.GetCommittedHoursAsync(
            customerId, TimeService.MonthBounds(September).From, TimeService.MonthBounds(September).To);

        bank.DrawnHours.Should().Be(0m);
        committed.TotalHours.Should().Be(4m);
        committed.Lines.Should().OnlyContain(l => l.BankHours == 0m);
    }

    // ---- Committed hours and the §20 breakdown -------------------------------------------------

    /// <summary>
    /// §20 requires the specification to be per application, ticket, day and time category
    /// so the customer can validate it. That obligation lands on this export, not on the
    /// billing system, which can only pass on what it is given.
    /// </summary>
    [Fact]
    public async Task The_specification_is_broken_down_the_way_the_agreement_requires()
    {
        UseTimeAndMaterials();
        Guid ticket = AddTicket();
        await Log(Tue(16), Tue(18), ticket);

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.Lines.Should().HaveCount(2);
        statement.Lines.Should().OnlyContain(l =>
            l.AppId == appId
            && l.AppName == "Journal"
            && l.TicketId == ticket
            && l.TicketNumber == 1
            && l.Day == new DateOnly(2026, 9, 22)
            && l.Description == "Investigation");

        statement.Lines.Select(l => l.Category).Should().BeEquivalentTo([
            SupportTimeCategory.Ordinary, SupportTimeCategory.EveningMorning,
        ]);
        statement.TotalHours.Should().Be(2m);
    }

    [Fact]
    public async Task A_period_only_contains_what_was_worked_in_it()
    {
        UseTimeAndMaterials();
        await Log(Swedish(2026, 8, 18, 9), Swedish(2026, 8, 18, 11));
        await Log(Tue(9), Tue(11));

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.TotalHours.Should().Be(2m);
    }

    /// <summary>
    /// §11.1 counts the bank per calendar month. A Swedish calendar month starts at
    /// midnight in Stockholm, which in summer is 22:00 UTC the day before — work at 07:00
    /// on the first must land in the right month.
    /// </summary>
    [Fact]
    public void A_month_runs_on_the_Swedish_calendar()
    {
        (DateTime from, DateTime to) = TimeService.MonthBounds(September);

        from.Should().Be(Swedish(2026, 9, 1, 0));
        to.Should().Be(Swedish(2026, 10, 1, 0));
    }

    /// <summary>
    /// Every DateTime read back from the database arrives with an unspecified Kind — that
    /// is what SQLite, <c>timestamp without time zone</c> and <c>datetime2</c> all return.
    /// Anything that then calls <c>ToUniversalTime()</c> on it reinterprets a stored UTC
    /// instant in the <em>server's</em> time zone and shifts it by that offset. That bug
    /// was here, and on a machine set to Swedish time it turned three worked hours into a
    /// negative span and a drawdown of 2.75.
    ///
    /// <para>This test only catches it on a machine whose local zone is not UTC, which is
    /// most developer laptops and no CI runner — so the assertion is written to be exact
    /// rather than approximate, and the comment is the other half of the defence.</para>
    /// </summary>
    [Fact]
    public async Task An_entry_round_tripped_through_the_database_keeps_its_hours()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket();
        await Log(Tue(9), Tue(12), ticket);

        TimeEntry stored = db.TimeEntries.AsNoTracking().Single();
        stored.StartedAt.Kind.Should().Be(DateTimeKind.Unspecified,
            "the database does not preserve Kind — the calendar has to assume UTC");

        TimebankStatement statement = await time.GetTimebankAsync(customerId, September);

        statement.DrawnHours.Should().Be(3m);
        statement.ByCategory.Should().ContainKey(SupportTimeCategory.Ordinary);
        statement.ByCategory.Should().NotContainKey(SupportTimeCategory.EveningMorning);
    }

    // ---- The approval gate (§11.1, §12) ---------------------------------------------------------

    /// <summary>
    /// §11.1: once the bank is spent, further work needs the customer's approval. Work that
    /// went ahead without one is surfaced here rather than discovered on the invoice.
    /// </summary>
    [Fact]
    public async Task Work_beyond_the_bank_with_no_approval_is_flagged()
    {
        UseHourBank(2m);
        Guid ticket = AddTicket(TicketPriority.P3);
        await Log(Tue(9), Tue(14), ticket);

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.UnauthorisedHours.Should().Be(3m);
    }

    /// <summary>
    /// §11.1 excepts P1 and P2: they are worked without delay and billed without separate
    /// approval, so they can never appear as unauthorised.
    /// </summary>
    [Theory]
    [InlineData(TicketPriority.P1)]
    [InlineData(TicketPriority.P2)]
    public async Task Urgent_work_beyond_the_bank_needs_no_approval(TicketPriority priority)
    {
        UseHourBank(2m);
        Guid ticket = AddTicket(priority);
        await Log(Tue(9), Tue(14), ticket);

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.UnauthorisedHours.Should().Be(0m);
    }

    [Fact]
    public async Task An_approval_for_the_ticket_clears_the_flag()
    {
        UseHourBank(2m);
        Guid ticket = AddTicket(TicketPriority.P3);
        await Log(Tue(9), Tue(14), ticket);

        await time.AuthoriseAsync(new WorkAuthorisation
        {
            TenantId = tenantId,
            CustomerId = customerId,
            TicketId = ticket,
            Month = TimeService.MonthBounds(September).From,
            ApprovedBy = "Kundens tekniska kontakt",
        });

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.UnauthorisedHours.Should().Be(0m);
    }

    [Fact]
    public async Task A_blanket_approval_for_the_month_clears_everything()
    {
        UseHourBank(2m);
        Guid ticket = AddTicket(TicketPriority.P3);
        await Log(Tue(9), Tue(14), ticket);

        await time.AuthoriseAsync(new WorkAuthorisation
        {
            TenantId = tenantId,
            CustomerId = customerId,
            Month = TimeService.MonthBounds(September).From,
            ApprovedBy = "Kundens tekniska kontakt",
            Note = "Go ahead for the rest of September.",
        });

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.UnauthorisedHours.Should().Be(0m);
    }

    [Fact]
    public async Task Under_Modell_B_nothing_is_measured_against_a_bank()
    {
        UseTimeAndMaterials();
        Guid ticket = AddTicket(TicketPriority.P3);
        await Log(Tue(9), Tue(17), ticket);

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.UnauthorisedHours.Should().Be(0m);
        statement.TotalHours.Should().Be(8m);
    }

    // ---- Call-out ------------------------------------------------------------------------------

    /// <summary>
    /// A ticket the agreement made a call-out bills its work at the callout rate with a
    /// two-hour floor, whatever the clock says (§13).
    /// </summary>
    [Fact]
    public async Task Work_on_a_callout_ticket_bills_at_the_callout_rate()
    {
        UseHourBank(20m);
        Guid ticket = AddTicket(TicketPriority.P1, callout: true);
        await Log(Sat(23), Sat(23, 30), ticket);

        (DateTime from, DateTime to) = TimeService.MonthBounds(September);
        CommittedHoursStatement statement = await time.GetCommittedHoursAsync(customerId, from, to);

        statement.Lines.Should().ContainSingle()
            .Which.Category.Should().Be(SupportTimeCategory.Callout);
        statement.TotalHours.Should().Be(2m);

        TimebankStatement bank = await time.GetTimebankAsync(customerId, September);
        bank.DrawnHours.Should().Be(4m);
    }
}
