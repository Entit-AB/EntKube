using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Reporting;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Tests;

/// <summary>
/// The §16.1 monthly report, due by the fifth of the following month.
///
/// <para>§28 makes what it says binding unless the customer disputes it within thirty
/// days, and §14.6 measures SLA compliance from it — so the figures in it are the ones
/// that will be argued about, and the penalty arithmetic in it is real money.</para>
/// </summary>
public class MonthlyReportTests : IDisposable
{
    private static DateTime Swedish(int year, int month, int day, int hour, int minute = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(
            new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified),
            BusinessCalendar.SwedishTime);

    /// <summary>Tuesday 22 September 2026, inside S1's window.</summary>
    private static DateTime Tue(int hour) => Swedish(2026, 9, 22, hour);

    private static readonly DateTime September = new(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly MonthlyReportService reports;
    private readonly TicketService tickets;
    private readonly TimeService time;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();

    public MonthlyReportTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "ENTIT", Slug = "entit" });
        db.Customers.Add(new Customer { Id = customerId, TenantId = tenantId, Name = "Capio" });
        db.Apps.Add(new App { Id = appId, CustomerId = customerId, Name = "Journalportalen" });
        db.PriceLists.Add(StandardPriceList.Create(tenantId, Swedish(2026, 1, 1, 0)));

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
        time = new TimeService(factory, contracts);
        reports = new MonthlyReportService(factory, tickets, time, contracts);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Ticket> Raise(TicketPriority priority, DateTime at) =>
        tickets.CreateAsync(
            tenantId, customerId, appId, "Fel", "Beskrivning",
            TicketChannel.Portal, priority, at);

    /// <summary>A ticket whose response time was missed — the only thing §14.6 pays for.</summary>
    private async Task<Ticket> RaiseWithLateResponse(TicketPriority priority, DateTime at)
    {
        Ticket ticket = await Raise(priority, at);
        await tickets.RecordResponseAsync(ticket.Id, "nils", at.AddHours(6));
        return ticket;
    }

    // ---- When it is due -------------------------------------------------------------------

    /// <summary>§16.1: "senast den 5:e varje månad avseende föregående månad".</summary>
    [Fact]
    public void The_report_is_due_on_the_fifth_of_the_following_month() =>
        MonthlyReportService.DueDate(September).Should().Be(Swedish(2026, 10, 5, 0));

    // ---- The KPI table --------------------------------------------------------------------

    [Fact]
    public async Task Tickets_are_counted_per_priority()
    {
        await Raise(TicketPriority.P1, Tue(9));
        await Raise(TicketPriority.P3, Tue(10));
        await Raise(TicketPriority.P3, Tue(11));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.TotalRaised.Should().Be(3);
        report.ByPriority.Single(p => p.Priority == TicketPriority.P3).Raised.Should().Be(2);
        report.ByPriority.Should().NotContain(p => p.Priority == TicketPriority.P2);
    }

    [Fact]
    public async Task Resolved_tickets_and_their_average_time_are_reported()
    {
        Ticket first = await Raise(TicketPriority.P3, Tue(9));
        Ticket second = await Raise(TicketPriority.P3, Tue(9));
        await tickets.ResolveAsync(first.Id, "Fixed.", "nils", Tue(11));
        await tickets.ResolveAsync(second.Id, "Fixed.", "nils", Tue(13));

        MonthlyReport report = await reports.BuildAsync(customerId, September);
        PriorityRow row = report.ByPriority.Single(p => p.Priority == TicketPriority.P3);

        row.Resolved.Should().Be(2);
        // Two and four hours of window time.
        row.AverageResolution.Should().Be(TimeSpan.FromHours(3));
    }

    [Fact]
    public async Task A_ticket_from_another_month_is_not_counted()
    {
        await Raise(TicketPriority.P1, Swedish(2026, 8, 18, 9));
        await Raise(TicketPriority.P1, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.TotalRaised.Should().Be(1);
    }

    [Fact]
    public async Task Tickets_still_open_at_month_end_are_counted()
    {
        Ticket closed = await Raise(TicketPriority.P3, Tue(9));
        await tickets.CloseAsync(closed.Id, "nils", Tue(12));
        await Raise(TicketPriority.P3, Tue(10));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.OpenAtMonthEnd.Should().Be(1);
    }

    // ---- §14.6's penalty ---------------------------------------------------------------------

    /// <summary>
    /// §14.6 pays 10% of the month's fönsteravgift for each missed P1 or P2 response. At
    /// S1's 6 000 kr that is 600 kr; the report has to show the arithmetic, because the
    /// customer has thirty days to claim it and §28 makes the figure binding if they do not.
    /// </summary>
    [Fact]
    public async Task A_missed_P1_response_is_priced_at_ten_percent_of_the_window_fee()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.WindowFee.Should().Be(6_000m);
        report.PenaltyDeviations.Should().Be(1);
        report.PenaltyAmount.Should().Be(600m);
    }

    [Fact]
    public async Task Several_breaches_in_a_month_each_cost()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Tue(9));
        await RaiseWithLateResponse(TicketPriority.P2, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.PenaltyDeviations.Should().Be(2);
        report.PenaltyAmount.Should().Be(1_200m);
    }

    /// <summary>
    /// §14.6 says an overrun lösningstid is a missed goal rather than a breach, and pays
    /// nothing for it. It is still reported, because the customer is entitled to see it.
    /// </summary>
    [Fact]
    public async Task An_overrun_resolution_time_is_reported_but_costs_nothing()
    {
        Ticket ticket = await Raise(TicketPriority.P1, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Tue(10));
        await tickets.ResolveAsync(ticket.Id, "Took a while.", "nils", Swedish(2026, 9, 24, 16));

        MonthlyReport report = await reports.BuildAsync(customerId, September);
        PriorityRow row = report.ByPriority.Single(p => p.Priority == TicketPriority.P1);

        row.ResolutionOverruns.Should().Be(1);
        report.PenaltyDeviations.Should().Be(0);
        report.PenaltyAmount.Should().Be(0m);
    }

    [Fact]
    public async Task A_missed_P3_response_costs_nothing()
    {
        Ticket ticket = await Raise(TicketPriority.P3, Tue(9));
        await tickets.RecordResponseAsync(ticket.Id, "nils", Swedish(2026, 9, 25, 9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.PenaltyDeviations.Should().Be(0);
    }

    [Fact]
    public async Task A_documented_exemption_removes_the_penalty()
    {
        Ticket ticket = await RaiseWithLateResponse(TicketPriority.P1, Tue(9));
        await tickets.ExcludeFromSlaAsync(
            ticket.Id, "Force majeure — regional power outage.", "nils", Tue(16));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.PenaltyDeviations.Should().Be(0);
        report.PenaltyAmount.Should().Be(0m);
    }

    /// <summary>
    /// §14.6 caps the penalty at three deviations a calendar year, so the report has to
    /// count what has already gone.
    /// </summary>
    [Fact]
    public async Task Earlier_deviations_this_year_count_against_the_cap()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Swedish(2026, 7, 21, 9));
        await RaiseWithLateResponse(TicketPriority.P1, Swedish(2026, 8, 18, 9));
        await RaiseWithLateResponse(TicketPriority.P1, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.PenaltiesAlreadyThisYear.Should().Be(2);
        report.PenaltyDeviations.Should().Be(1);
        report.PenaltiesRemainingThisYear.Should().Be(0);
    }

    [Fact]
    public async Task Last_years_deviations_do_not_count()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Swedish(2025, 11, 18, 9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.PenaltiesAlreadyThisYear.Should().Be(0);
    }

    /// <summary>
    /// §14.6: more than two P1–P2 deviations in a calendar quarter obliges an åtgärdsplan
    /// at the next quarterly meeting.
    /// </summary>
    [Fact]
    public async Task Three_deviations_in_a_quarter_require_an_action_plan()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Swedish(2026, 7, 21, 9));
        await RaiseWithLateResponse(TicketPriority.P2, Swedish(2026, 8, 18, 9));
        await RaiseWithLateResponse(TicketPriority.P1, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.ActionPlanRequired.Should().BeTrue();
    }

    [Fact]
    public async Task Two_deviations_in_a_quarter_do_not()
    {
        await RaiseWithLateResponse(TicketPriority.P1, Swedish(2026, 8, 18, 9));
        await RaiseWithLateResponse(TicketPriority.P1, Tue(9));

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.ActionPlanRequired.Should().BeFalse();
    }

    // ---- Availability -----------------------------------------------------------------------

    [Fact]
    public async Task Uptime_is_measured_from_the_health_snapshots()
    {
        Guid environmentId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();
        Guid deploymentId = Guid.NewGuid();

        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-1", ApiServerUrl = "https://k8s.example.com",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = deploymentId, AppId = appId, Name = "journal",
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = "journal",
        });

        for (int i = 0; i < 10; i++)
        {
            db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                HealthStatus = i < 9 ? HealthStatus.Healthy : HealthStatus.Degraded,
                SnapshotAt = Tue(9).AddMinutes(i * 5),
            });
        }

        db.SlaTargets.Add(new SlaTarget
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
            TargetPercent = 99.5,
        });
        await db.SaveChangesAsync();

        MonthlyReport report = await reports.BuildAsync(customerId, September);
        AvailabilityRow row = report.Availability.Should().ContainSingle().Subject;

        row.AppName.Should().Be("Journalportalen");
        row.UptimePercent.Should().Be(90.0);
        row.TargetPercent.Should().Be(99.5);
        row.SampleCount.Should().Be(10);
        row.TargetMet.Should().BeFalse();
    }

    /// <summary>
    /// An application with no health data is reported as unknown rather than as 100% —
    /// the same principle the cost ledger keeps: an unmeasured hour is not a free one.
    /// </summary>
    [Fact]
    public async Task An_application_with_no_samples_reports_no_uptime()
    {
        Guid environmentId = Guid.NewGuid();
        Guid clusterId = Guid.NewGuid();

        db.Environments.Add(new EntKube.Web.Data.Environment
        {
            Id = environmentId, TenantId = tenantId, Name = "prod",
        });
        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId, TenantId = tenantId, EnvironmentId = environmentId,
            Name = "prod-1", ApiServerUrl = "https://k8s.example.com",
        });
        db.AppDeployments.Add(new AppDeployment
        {
            Id = Guid.NewGuid(), AppId = appId, Name = "journal",
            EnvironmentId = environmentId, ClusterId = clusterId, Namespace = "journal",
        });
        await db.SaveChangesAsync();

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        AvailabilityRow row = report.Availability.Should().ContainSingle().Subject;
        row.UptimePercent.Should().BeNull();
        row.TargetMet.Should().BeNull();
    }

    // ---- Hours ---------------------------------------------------------------------------------

    /// <summary>
    /// §16.1 wants förbrukade timmar in the report: hours drawn, hours that expired, and
    /// hours beyond the bank. All three come from the same statement the Hours view shows.
    /// </summary>
    [Fact]
    public async Task The_report_carries_the_timbank_and_the_hours()
    {
        db.PortfolioAgreements.Add(new PortfolioAgreement
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CustomerId = customerId,
            EffectiveFrom = Swedish(2026, 1, 1, 0),
            PricingModel = PricingModel.HourBank, HourBankHoursPerMonth = 20m,
        });
        await db.SaveChangesAsync();

        await time.LogAsync(
            tenantId, customerId, appId, null, Tue(9), Tue(13),
            WorkKind.Management, "Felsökning", "nils");

        MonthlyReport report = await reports.BuildAsync(customerId, September);

        report.Timebank.EntitledHours.Should().Be(20m);
        report.Timebank.DrawnHours.Should().Be(4m);
        report.Timebank.ExpiringHours.Should().Be(16m);
        report.Hours.TotalHours.Should().Be(4m);
    }

    [Fact]
    public async Task A_customer_that_does_not_exist_reports_nothing()
    {
        MonthlyReport report = await reports.BuildAsync(Guid.NewGuid(), September);

        report.TotalRaised.Should().Be(0);
        report.PenaltyAmount.Should().Be(0m);
    }
}
