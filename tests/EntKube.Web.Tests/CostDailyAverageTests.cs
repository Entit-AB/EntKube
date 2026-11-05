using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The "Per day" figure, against windows that end today and clusters that joined mid-day.
///
/// <para><b>What was wrong.</b> The denominator weighted each day by its <c>Completeness</c>, and
/// completeness answers a different question: <em>of the hours we could have watched, how many did
/// we?</em> <c>ExpectedCoverage</c> deliberately reduces that expectation twice — for today, to the
/// hours elapsed, and for a cluster's opening day, to the hours since the ledger started watching —
/// so in both cases a part day scores <b>1.0</b> while holding part of a day's money.</para>
///
/// <list type="bullet">
/// <item><b>Today.</b> Six full days at 100 plus today's elapsed 50 is 650 over a denominator of
/// 7.0: <b>92.86</b> where 100.00 is right. Every Cost tab render hits this, because the window
/// always ends today, and so does the default window of
/// <c>GET /api/v1/cost/history</c>.</item>
/// <item><b>An opening day.</b> A cluster added at 18:00 contributes six hours of money and a whole
/// day to the divisor — and this happens in windows <em>entirely in the past</em>, so it is not the
/// same case in disguise. Three past days with the cluster opened at 18:00 on the first reported
/// <b>75.00</b> against a true 100.00.</item>
/// </list>
///
/// <para>⚠️ <b>Neither was tested, and the existing fixtures are why.</b> No test had a coverage row
/// on today's day read back through <c>GetHistoryAsync</c>, and no test had an opening day with
/// covered below 24 and no gap — every fixture had either a 12-hour gap or a full 24, and both of
/// those make the exemption agree with the nominal day by accident.</para>
///
/// <para>The weight is now <c>MeasuredDayFraction</c>: measured hours over a whole day's 24 per
/// cluster, with no exemption. The exemptions stay where they belong, on completeness and the
/// warnings — <see cref="Completeness_still_answers_its_own_question"/> holds them there.</para>
/// </summary>
public class CostDailyAverageTests : IDisposable
{
    /// <summary>Noon, so "today" is half elapsed — the case the old denominator rounded up to a whole day.</summary>
    private static readonly DateTime Noon = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly CostLedgerService history;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid otherClusterId = Guid.NewGuid();
    private readonly Guid customerId = Guid.NewGuid();

    public CostDailyAverageTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        // ⚠️ Staged, principals first. Foreign keys ARE enforced in this SQLite host and EF's
        // batch ordering can place a dependent ahead of the row it points at, which rejects the
        // whole SaveChanges — the first run of this file failed exactly that way, on
        // "FOREIGN KEY constraint failed", because the ledger rows went in with no cluster.
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Acme", Slug = "acme" });
        db.SaveChanges();

        Data.Environment environment = new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = "Production",
        };

        db.Environments.Add(environment);
        db.SaveChanges();

        foreach ((Guid id, string name) in new[] { (clusterId, "prod"), (otherClusterId, "prod-two") })
        {
            db.KubernetesClusters.Add(new KubernetesCluster
            {
                Id = id,
                TenantId = tenantId,
                EnvironmentId = environment.Id,
                Name = name,
                ApiServerUrl = $"https://{name}.example.com:6443",
            });
        }

        db.SaveChanges();

        history = new CostLedgerService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task SeedAsync(
        DateTime day, decimal cost, decimal coveredHours = 24m, decimal gapHours = 0m, Guid? cluster = null)
    {
        Guid onCluster = cluster ?? clusterId;
        DateTime utcDay = CostAccrual.DayOf(day);

        db.CostLedgerEntries.Add(new CostLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = onCluster,
            ClusterName = "prod",
            Namespace = "acme-prod",
            Day = utcDay,
            CustomerId = customerId,
            CustomerName = "Acme Ltd",
            EnvironmentName = "Production",
            Hours = coveredHours,
            CpuCost = cost,
            Currency = "EUR",
        });

        db.CostLedgerCoverages.Add(new CostLedgerCoverage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = onCluster,
            ClusterName = "prod",
            Day = utcDay,
            CoveredHours = coveredHours,
            GapHours = gapHours,
            SampleCount = 24,
        });

        await db.SaveChangesAsync();
    }

    private Task<CostHistoryReport> HistoryAsync(int days) =>
        history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-(days - 1))), CostAccrual.DayOf(Noon),
            CostGroupBy.Customer, Noon);

    /// <summary>
    /// ⚠️ The headline. Today holds half a day's money and must weigh half a day.
    /// </summary>
    [Fact]
    public async Task A_window_ending_at_noon_today_averages_over_six_and_a_half_days()
    {
        for (int d = 6; d >= 1; d--) await SeedAsync(Noon.AddDays(-d), cost: 100m);

        // Today: twelve hours elapsed, twelve measured, half a day's money.
        await SeedAsync(Noon, cost: 50m, coveredHours: 12m);

        CostHistoryReport report = await HistoryAsync(7);

        report.TotalCost.Should().Be(650m);
        report.MeasuredDays.Should().Be(6.5m, "six whole days and half of today");
        report.DailyAverage.Should().Be(100m, "and this reported 92.86 while the window was open");
    }

    /// <summary>
    /// ⚠️ The second trigger, in a window ENTIRELY IN THE PAST — so a fix scoped to today would
    /// have left it, and the data a day arrives with cannot even identify it: a
    /// <c>CostHistoryPoint</c> carries no cluster and no opening date.
    /// </summary>
    [Fact]
    public async Task A_cluster_that_joined_at_six_in_the_evening_weighs_a_quarter_of_that_day()
    {
        // Its first coverage row is six hours — the ledger cannot be missing the hours before it
        // was watching, so Completeness is 1.0 for this day and always was.
        await SeedAsync(Noon.AddDays(-3), cost: 25m, coveredHours: 6m);
        await SeedAsync(Noon.AddDays(-2), cost: 100m);
        await SeedAsync(Noon.AddDays(-1), cost: 100m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-3)), CostAccrual.DayOf(Noon.AddDays(-1)),
            CostGroupBy.Customer, Noon);

        report.TotalCost.Should().Be(225m);
        report.MeasuredDays.Should().Be(2.25m, "a quarter of the opening day plus two whole ones");
        report.DailyAverage.Should().Be(100m, "and this reported 75.00");
    }

    /// <summary>
    /// ⚠️ THE POSITIVE CONTROL for the window case: a window entirely in the past with full
    /// coverage was already right, and the fix must not move it. Without this, weighting every day
    /// by something smaller would pass the two facts above while inflating every honest average.
    /// </summary>
    [Fact]
    public async Task A_window_of_whole_measured_days_is_unchanged()
    {
        for (int d = 7; d >= 1; d--) await SeedAsync(Noon.AddDays(-d), cost: 100m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-7)), CostAccrual.DayOf(Noon.AddDays(-1)),
            CostGroupBy.Customer, Noon);

        report.MeasuredDays.Should().Be(7m);
        report.DailyAverage.Should().Be(100m);
    }

    /// <summary>
    /// A day that lost twelve hours to an outage still weighs half a day — the behaviour the
    /// previous fix to this figure introduced, pinned here because the new denominator reaches it
    /// by a different route and must agree.
    /// </summary>
    [Fact]
    public async Task A_day_half_lost_to_an_outage_still_weighs_half_a_day()
    {
        await SeedAsync(Noon.AddDays(-3), cost: 100m);
        await SeedAsync(Noon.AddDays(-2), cost: 100m);
        await SeedAsync(Noon.AddDays(-1), cost: 50m, coveredHours: 12m, gapHours: 12m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-3)), CostAccrual.DayOf(Noon.AddDays(-1)),
            CostGroupBy.Customer, Noon);

        report.MeasuredDays.Should().Be(2.5m);
        report.DailyAverage.Should().Be(100m, "the money that was measured over the days it covers");
    }

    /// <summary>
    /// A window reaching back before the ledger existed divides by what it measured, not by the
    /// range — the defect the previous fix addressed, re-asserted against the new denominator.
    /// </summary>
    [Fact]
    public async Task A_window_wider_than_the_ledger_still_averages_over_what_it_measured()
    {
        for (int d = 10; d >= 1; d--) await SeedAsync(Noon.AddDays(-d), cost: 100m);

        CostHistoryReport report = await HistoryAsync(30);

        report.MeasuredDays.Should().Be(10m, "twenty days have no coverage row and weigh nothing");
        report.DailyAverage.Should().Be(100m);
        report.UnmeasuredDays.Should().Be(20);
    }

    /// <summary>
    /// ⚠️ A fleet, because a whole day is 24 hours PER CLUSTER. With one mature cluster and one
    /// joining at 18:00, the day holds 30 measured hours against a nominal 48 — and the old
    /// denominator called it a whole day because the exemption made both clusters' expectations
    /// add up to exactly what was covered.
    /// </summary>
    [Fact]
    public async Task A_whole_day_is_twenty_four_hours_for_every_cluster()
    {
        await SeedAsync(Noon.AddDays(-1), cost: 100m);
        await SeedAsync(Noon.AddDays(-1), cost: 25m, coveredHours: 6m, cluster: otherClusterId);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-1)), CostAccrual.DayOf(Noon.AddDays(-1)),
            CostGroupBy.Customer, Noon);

        report.TotalCost.Should().Be(125m);
        report.MeasuredDays.Should().Be(
            30m / 48m, "thirty measured cluster-hours of a nominal forty-eight");
    }

    /// <summary>
    /// ⚠️ The exemptions must STAY on completeness. They are right for the question completeness
    /// asks, and moving them — or "simplifying" the fix by redefining <c>ExpectedHours</c> to the
    /// nominal 24 — would silently change a published wire field's meaning without changing a
    /// single JSON key, so no wire-shape test in the repo could see it.
    /// </summary>
    [Fact]
    public async Task Completeness_still_answers_its_own_question()
    {
        for (int d = 6; d >= 1; d--) await SeedAsync(Noon.AddDays(-d), cost: 100m);
        await SeedAsync(Noon, cost: 50m, coveredHours: 12m);

        CostHistoryReport report = await HistoryAsync(7);

        report.Daily[^1].Completeness.Should().Be(
            1d, "today was measured for every hour it has had, which is what completeness means");
        report.Daily[^1].MeasuredDayFraction.Should().Be(
            0.5m, "while holding half a day's money, which is what the denominator needs");

        report.Completeness.Should().Be(1d, "nothing was missed");
        report.IsComplete.Should().BeTrue();
        report.Warnings.Should().BeEmpty("a window that is merely still open is not under-measured");
    }

    /// <summary>
    /// ⚠️ The measure. Every assertion above reads <c>MeasuredDays</c> or <c>DailyAverage</c> off a
    /// report built from seeded rows, so all of them would pass if the fraction were clamped to a
    /// constant 1 and the money happened to match. This checks the fraction varies with the hours,
    /// including the clamp — a day cannot hold more than a whole day.
    /// </summary>
    [Fact]
    public async Task The_fraction_tracks_the_hours_and_is_clamped_at_one()
    {
        await SeedAsync(Noon.AddDays(-3), cost: 100m, coveredHours: 24m);
        await SeedAsync(Noon.AddDays(-2), cost: 25m, coveredHours: 6m, gapHours: 18m);

        // More hours than a day holds: a double-booked hour or a clock that moved. The money is
        // inflated too, so the denominator must not follow it up.
        await SeedAsync(Noon.AddDays(-1), cost: 200m, coveredHours: 36m);

        CostHistoryReport report = await history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(Noon.AddDays(-3)), CostAccrual.DayOf(Noon.AddDays(-1)),
            CostGroupBy.Customer, Noon);

        report.Daily.Select(d => d.MeasuredDayFraction).Should().Equal([1m, 0.25m, 1m]);
        report.MeasuredDays.Should().Be(2.25m);
    }
}
