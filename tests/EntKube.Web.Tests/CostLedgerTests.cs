using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the cost ledger — what was actually incurred over time, as opposed to the
/// run rate, which is a projection of what it would cost if today lasted a month.
///
/// The properties these defend, in order of how expensive they are to get wrong:
/// an hour is never billed twice; an hour that was never measured is reported as
/// unmeasured rather than as free; and a historical amount does not change when the
/// thing it was charged for is renamed, moved or deleted.
/// </summary>
public class CostLedgerTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly TestDbContextFactory factory;
    private readonly CostLedgerWriter writer;
    private readonly CostLedgerService history;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();
    private readonly Guid appId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();

    public CostLedgerTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });

        Data.Environment environment = new() { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Production" };
        db.Environments.Add(environment);

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = environment.Id,
            Name = "prod",
            ApiServerUrl = "https://prod.example.com:6443",
        });
        db.SaveChanges();

        factory = new TestDbContextFactory(connection);
        writer = new CostLedgerWriter(factory, NullLogger<CostLedgerWriter>.Instance);
        history = new CostLedgerService(factory);
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════════
    // The accrual arithmetic — pure, and the part a customer's bill rests on
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void A_first_measurement_bills_nothing()
    {
        AccrualSpan span = CostAccrual.Measure(lastSampleAt: null, Noon);

        // Nothing measured the time before the first sweep. An opening balance derived
        // from it would be indistinguishable from a real one ever afterwards.
        span.BillableHours.Should().Be(0m);
        span.GapHours.Should().Be(0m);
        span.HasAnything.Should().BeFalse();
    }

    [Fact]
    public void A_steady_month_accrues_exactly_the_quoted_monthly_cost()
    {
        // The whole point of using the same 730-hour month as the run rate: a workload
        // held steady for a month must accrue what the dashboard said it would, or the
        // two numbers argue with each other in front of a customer.
        decimal accrued = CostAccrual.Accrue(monthlyCost: 730m, hours: CostAccrual.HoursPerMonth);

        accrued.Should().Be(730m);
    }

    [Fact]
    public void An_hour_of_a_small_namespace_is_not_rounded_away()
    {
        // $0.30/month is a hundredth of a cent an hour. Rounded to money on the way in
        // it would be zero, and a year of it would still be zero — so the ledger keeps
        // six decimals and rounds only when a figure is shown.
        decimal accrued = CostAccrual.Accrue(monthlyCost: 0.30m, hours: 1m);

        accrued.Should().BeGreaterThan(0m);
        accrued.Should().BeApproximately(0.000411m, 0.000001m);
    }

    [Fact]
    public void A_long_outage_is_not_billed_at_the_rate_measured_after_it()
    {
        // Ten hours passed and one measurement came back. It can speak for the hours
        // closest to it, not for the whole outage — pricing all ten at Monday morning's
        // reading would invoice a guess.
        AccrualSpan span = CostAccrual.Measure(Noon.AddHours(-10), Noon);

        span.BillableHours.Should().Be(3m);
        span.GapHours.Should().Be(7m);
        span.End.Should().Be(Noon);
        span.Start.Should().Be(Noon.AddHours(-3));
    }

    [Fact]
    public void A_clock_that_went_backwards_bills_nothing()
    {
        AccrualSpan span = CostAccrual.Measure(Noon.AddHours(1), Noon);

        span.BillableHours.Should().Be(0m);
        span.GapHours.Should().Be(0m);
    }

    [Fact]
    public void A_period_crossing_midnight_is_split_between_both_days()
    {
        IReadOnlyList<DaySlice> slices = CostAccrual.SplitByDay(
            new DateTime(2026, 9, 1, 23, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 2, 1, 0, 0, DateTimeKind.Utc));

        slices.Should().HaveCount(2);
        slices[0].Day.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        slices[0].Hours.Should().Be(1m);
        slices[1].Day.Should().Be(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc));
        slices[1].Hours.Should().Be(1m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Writing the ledger
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task The_first_sweep_opens_the_ledger_without_charging_for_it()
    {
        CostLedgerWriteResult result = await RecordAsync(Noon, Report(cpuCost: 730m));

        result.ClustersStarted.Should().Be(1);
        result.EntriesWritten.Should().Be(0);
        (await db.CostLedgerEntries.CountAsync()).Should().Be(0);

        CostLedgerCursor cursor = await db.CostLedgerCursors.SingleAsync();
        cursor.LastSampleAt.Should().Be(Noon);
    }

    [Fact]
    public async Task The_second_sweep_accrues_the_hour_that_elapsed()
    {
        await RecordAsync(Noon, Report(cpuCost: 730m));
        await RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));

        CostLedgerEntry entry = await db.CostLedgerEntries.SingleAsync();

        entry.Hours.Should().Be(1m);
        // A $730/month run rate is $1 an hour.
        entry.CpuCost.Should().Be(1m);
        entry.Day.Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Hours_accumulate_into_one_row_per_day()
    {
        await RecordAsync(Noon, Report(cpuCost: 730m));
        await RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));
        await RecordAsync(Noon.AddHours(2), Report(cpuCost: 730m));

        CostLedgerEntry entry = await db.CostLedgerEntries.SingleAsync();

        entry.Hours.Should().Be(2m);
        entry.CpuCost.Should().Be(2m);
    }

    [Fact]
    public async Task A_sweep_that_re_runs_for_a_period_already_booked_charges_nothing_more()
    {
        // The failure this guards is the expensive one. A second management-plane
        // instance, or a retried sweep, must not bill the same hour twice — there is
        // nothing on an invoice to distinguish a double-booked hour from a real one.
        await RecordAsync(Noon, Report(cpuCost: 730m));
        await RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));

        CostLedgerWriteResult again = await RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));

        again.EntriesWritten.Should().Be(0);
        (await db.CostLedgerEntries.SingleAsync()).CpuCost.Should().Be(1m);
    }

    [Fact]
    public async Task A_writer_that_loses_the_race_for_a_period_books_nothing()
    {
        await RecordAsync(Noon, Report(cpuCost: 730m));

        // Two writers see the same cursor. The first claims the period; the second's
        // conditional update matches nothing and it books nothing at all — rather than
        // adding a second copy of the same hour.
        Task<CostLedgerWriteResult> first = RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));
        Task<CostLedgerWriteResult> second = RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m));

        await Task.WhenAll(first, second);

        (await db.CostLedgerEntries.SingleAsync()).CpuCost.Should().Be(1m);
    }

    [Fact]
    public async Task A_period_crossing_midnight_lands_on_both_days()
    {
        DateTime elevenPm = new(2026, 9, 1, 23, 0, 0, DateTimeKind.Utc);

        await RecordAsync(elevenPm, Report(cpuCost: 730m));
        await RecordAsync(elevenPm.AddHours(2), Report(cpuCost: 730m));

        List<CostLedgerEntry> entries = await db.CostLedgerEntries.OrderBy(e => e.Day).ToListAsync();

        entries.Should().HaveCount(2);
        entries[0].CpuCost.Should().Be(1m);
        entries[1].CpuCost.Should().Be(1m);
    }

    [Fact]
    public async Task A_cluster_that_returned_no_metrics_records_unmeasured_time_not_a_cheap_day()
    {
        await RecordAsync(Noon, Report(cpuCost: 730m));

        // The cluster is priced but produced nothing this sweep — unreachable, or its
        // metrics stack is down. Reporting a free hour would understate the bill and
        // look like a workload that had been switched off.
        await RecordAsync(Noon.AddHours(1), EmptyReport());

        (await db.CostLedgerEntries.CountAsync()).Should().Be(0);

        CostLedgerCoverage coverage = await db.CostLedgerCoverages.SingleAsync();
        coverage.GapHours.Should().Be(1m);
        coverage.CoveredHours.Should().Be(0m);
    }

    [Fact]
    public async Task Ownership_is_recorded_as_it_stood_and_does_not_follow_a_later_rename()
    {
        await RecordAsync(Noon, Report(cpuCost: 730m));
        await RecordAsync(Noon.AddHours(1), Report(cpuCost: 730m, appName: "checkout"));

        // The app is renamed afterwards. A ledger that re-derived the past from present
        // relationships would restate a statement that has already been sent.
        CostLedgerEntry entry = await db.CostLedgerEntries.SingleAsync();
        entry.AppName.Should().Be("checkout");
        entry.CustomerName.Should().Be("Acme Ltd");

        await RecordAsync(Noon.AddHours(2), Report(cpuCost: 730m, appName: "checkout-v2"));

        List<CostLedgerEntry> all = await db.CostLedgerEntries.AsNoTracking().ToListAsync();
        all.Should().HaveCount(1, "the day's accrual is one row");
        all[0].CpuCost.Should().Be(2m, "both hours are still billed");
    }

    [Fact]
    public async Task Pruning_drops_only_rows_past_the_horizon()
    {
        await SeedDayAsync(Noon.AddDays(-400), cost: 5m);
        await SeedDayAsync(Noon.AddDays(-10), cost: 7m);

        await writer.PruneAsync(retentionDays: 90, Noon);

        List<CostLedgerEntry> left = await db.CostLedgerEntries.AsNoTracking().ToListAsync();
        left.Should().HaveCount(1);
        left[0].CpuCost.Should().Be(7m);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Reading it back
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task History_totals_what_was_incurred_day_by_day()
    {
        await SeedDayAsync(Noon.AddDays(-2), cost: 10m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 20m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-2), Noon, CostGroupBy.App, Noon);

        report.TotalCost.Should().Be(30m);
        report.Daily.Should().HaveCount(3);
        report.Daily[0].Cost.Should().Be(10m);
        report.Daily[1].Cost.Should().Be(20m);
        report.Daily[2].Cost.Should().Be(0m);
    }

    [Fact]
    public async Task History_compares_against_the_preceding_period_of_the_same_length()
    {
        await SeedDayAsync(Noon.AddDays(-3), cost: 10m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 30m);

        // Two-day window against the two days before it.
        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon);

        report.TotalCost.Should().Be(30m);
        report.PreviousTotalCost.Should().Be(10m);
        report.Change.Should().Be(20m);
        report.ChangeFraction.Should().BeApproximately(2d, 0.001d);
    }

    [Fact]
    public async Task A_workload_that_was_switched_off_still_appears_in_the_comparison()
    {
        // Otherwise the saving is invisible: the line simply vanishes from the table,
        // which reads as nothing having happened.
        await SeedDayAsync(Noon.AddDays(-3), cost: 40m, appName: "batch");

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon);

        CostHistoryGroup gone = report.Groups.Should().ContainSingle(g => g.Label == "batch").Subject;
        gone.Cost.Should().Be(0m);
        gone.PreviousCost.Should().Be(40m);
        gone.IsGone.Should().BeTrue();
        gone.ChangeFraction.Should().BeApproximately(-1d, 0.001d);
    }

    [Fact]
    public async Task A_line_that_is_new_this_period_has_no_percentage_change()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 15m, appName: "new-thing");

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon);

        CostHistoryGroup line = report.Groups.Single(g => g.Label == "new-thing");
        line.IsNew.Should().BeTrue();
        // Not +100%, and not infinity: there is nothing to compare against, and inventing
        // a trend from an appearance is how a chart starts lying.
        line.ChangeFraction.Should().BeNull();
    }

    [Fact]
    public async Task Redistributed_platform_rows_are_excluded_from_the_total_and_reported_apart()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 20m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 8m, ns: "monitoring", redistributed: true);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon);

        // Counting both would bill the platform twice: once as itself, once inside the
        // share already charged out to the apps.
        report.TotalCost.Should().Be(20m);
        report.PlatformCost.Should().Be(8m);
    }

    [Fact]
    public async Task An_unmeasured_period_is_stated_rather_than_shown_as_a_cheap_day()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 10m, coveredHours: 12m, gapHours: 12m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon.AddDays(-1), CostGroupBy.App, Noon);

        report.IsComplete.Should().BeFalse();
        report.Completeness.Should().BeApproximately(0.5d, 0.01d);
        report.Warnings.Should().ContainSingle(w => w.Contains("never measured"));
        report.Daily[0].IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Hours_lost_without_even_being_recorded_as_a_gap_still_show_as_missing()
    {
        // The crash-mid-write case: the period was claimed, so nothing will retry it, and
        // no gap was recorded either. The day is simply short — and it must still read as
        // unmeasured rather than as a day the workloads were smaller.
        await SeedDayAsync(Noon.AddDays(-3), cost: 20m, coveredHours: 24m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 5m, coveredHours: 12m, gapHours: 0m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon.AddDays(-1), CostGroupBy.App, Noon);

        // Not the cluster's opening day — it was measured three days earlier — so the day
        // is held to a full 24 hours.
        report.Completeness.Should().BeApproximately(0.5d, 0.01d);
        report.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task A_fully_measured_day_reports_no_warning()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 10m, coveredHours: 24m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon.AddDays(-1), CostGroupBy.App, Noon);

        report.IsComplete.Should().BeTrue();
        report.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Cost_that_reached_no_customer_is_labelled_rather_than_dropped()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 10m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 4m, ns: "orphan", attributed: false);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.Customer, Noon);

        report.TotalCost.Should().Be(14m);
        report.UnattributedCost.Should().Be(4m);
        report.Groups.Should().ContainSingle(g => g.Label == "Unattributed" && g.Cost == 4m);
    }

    [Fact]
    public async Task A_namespace_shared_by_several_apps_is_named_as_such_not_billed_to_one()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 9m, ns: "shared", multiApp: true, appName: null);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon);

        report.MultiAppCost.Should().Be(9m);
        report.Groups.Should().ContainSingle(g => g.Label == "Shared by several apps");
    }

    [Fact]
    public async Task Monthly_totals_group_by_calendar_month()
    {
        await SeedDayAsync(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), cost: 100m);
        await SeedDayAsync(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), cost: 40m);

        IReadOnlyList<CostMonth> months = await history.GetMonthlyAsync(tenantId, months: 3, Noon);

        months.Should().HaveCount(3);
        months[^2].Cost.Should().Be(100m);
        months[^1].Cost.Should().Be(40m);
        months[^1].Month.Should().Be(9);
    }

    [Fact]
    public async Task A_customer_scoped_history_holds_only_that_customers_cost()
    {
        await SeedDayAsync(Noon.AddDays(-1), cost: 10m);
        await SeedDayAsync(Noon.AddDays(-1), cost: 99m, ns: "other-prod", customer: otherCustomerId);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, Noon.AddDays(-1), Noon, CostGroupBy.App, Noon, customerId: customerId);

        // The customer portal renders this. Filtering after the fact would mean the
        // panel briefly holds another customer's figures; the filter is in the query.
        report.TotalCost.Should().Be(10m);
        report.Groups.Should().NotContain(g => g.Label.Contains("Other"));
    }

    [Fact]
    public async Task Monthly_totals_can_be_scoped_to_one_customer()
    {
        await SeedDayAsync(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), cost: 30m);
        await SeedDayAsync(
            new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), cost: 70m,
            ns: "other-prod", customer: otherCustomerId);

        IReadOnlyList<CostMonth> mine =
            await history.GetMonthlyAsync(tenantId, months: 1, Noon, customerId: customerId);

        mine.Single().Cost.Should().Be(30m);
    }

    [Fact]
    public async Task An_empty_ledger_is_distinguishable_from_a_quiet_window()
    {
        (await history.HasAnyAsync(tenantId)).Should().BeFalse();

        await SeedDayAsync(Noon.AddDays(-1), cost: 10m);

        (await history.HasAnyAsync(tenantId)).Should().BeTrue();
    }

    // ── Helpers ──

    private Task<CostLedgerWriteResult> RecordAsync(DateTime now, CostReport report) =>
        writer.RecordAsync(tenantId, report, new Dictionary<Guid, ClusterCostRate>
        {
            [clusterId] = new ClusterCostRate { ClusterId = clusterId, Currency = "EUR", ChargeOnRequests = true },
        }, now);

    private CostReport Report(decimal cpuCost, string appName = "checkout") => new()
    {
        GeneratedAt = Noon,
        Namespaces =
        [
            new NamespaceCost
            {
                Namespace = "acme-prod",
                ClusterId = clusterId,
                ClusterName = "prod",
                CustomerId = customerId,
                CustomerName = "Acme Ltd",
                Apps = [new AppRef(appId, appName)],
                EnvironmentName = "Production",
                CpuMonthlyCost = cpuCost,
            },
        ],
    };

    private static CostReport EmptyReport() => new() { GeneratedAt = Noon, Namespaces = [] };

    /// <summary>Writes a ledger row directly, for tests about reading rather than accruing.</summary>
    private async Task SeedDayAsync(
        DateTime day,
        decimal cost,
        string ns = "acme-prod",
        string? appName = "checkout",
        bool attributed = true,
        bool redistributed = false,
        bool multiApp = false,
        decimal coveredHours = 24m,
        decimal gapHours = 0m,
        Guid? customer = null)
    {
        DateTime utcDay = CostAccrual.DayOf(day);

        db.CostLedgerEntries.Add(new CostLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            ClusterName = "prod",
            Namespace = ns,
            Day = utcDay,
            CustomerId = attributed ? customer ?? customerId : null,
            CustomerName = attributed ? (customer is null ? "Acme Ltd" : "Other Ltd") : null,
            AppId = appName is null ? null : appId,
            AppName = appName,
            EnvironmentName = "Production",
            IsMultiApp = multiApp,
            IsRedistributed = redistributed,
            Hours = coveredHours,
            CpuCost = cost,
            Currency = "EUR",
        });

        if (!await db.CostLedgerCoverages.AnyAsync(c => c.ClusterId == clusterId && c.Day == utcDay))
        {
            db.CostLedgerCoverages.Add(new CostLedgerCoverage
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterId = clusterId,
                ClusterName = "prod",
                Day = utcDay,
                CoveredHours = coveredHours,
                GapHours = gapHours,
                SampleCount = 24,
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
