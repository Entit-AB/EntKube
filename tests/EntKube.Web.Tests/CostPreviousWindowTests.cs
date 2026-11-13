using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The "vs previous N days" comparison, against windows the ledger measured to different extents.
///
/// <para><b>What was wrong.</b> <c>GetHistoryAsync</c> fetched the ledger rows for both windows in
/// one pass — the comment above that query says so, and gives the reason — but the coverage query
/// beside it ran <c>c.Day &gt;= start</c>. So the earlier window's <em>money</em> was fetched and
/// its <em>measurement</em> was not, and <c>PreviousTotalCost</c> was the one total in
/// <c>CostHistoryReport</c> with no coverage beside it. <c>ChangeFraction</c> then divided one
/// total by the other, which reports the difference in watching as a difference in spending.</para>
///
/// <list type="bullet">
/// <item><b>A fabricated rise.</b> A tenant spending a steady 100 a day, whose ledger started four
/// days into the earlier window, read <b>+133%</b> — in red, on the Cost tab, in the customer
/// portal, and in <c>changeFraction</c> of <c>GET /api/v1/cost/history</c>, which that endpoint
/// documents as the figure a billing system reads.</item>
/// <item><b>A fabricated saving, on every render.</b> The window always ends today, so the
/// requested window holds a part day and the earlier one does not. Seven steady days against six
/// and a half-elapsed today read <b>−7.1%</b>, in green, for a tenant whose spending had not
/// moved. This was the worse of the two: always present, always pointing the same way, and
/// coloured as the thing a reader wants to believe.</item>
/// </list>
///
/// <para>⚠️ <b>Why no existing test saw either.</b> Every fixture that exercised the comparison
/// seeded both windows identically and fully, which is the one arrangement in which totals and
/// rates agree — and <c>CostWireShapeTests</c> cannot see it at all, because its oracle builds
/// both sides of the comparison from the same report object, so any change to the derivation
/// stays green on both. The assertions below are therefore <b>literal numbers</b>, not figures
/// recomputed from the report.</para>
///
/// <para>The comparison is now daily rate against daily rate, over the measured days of each
/// window, and null when either window holds no measurement at all.</para>
/// </summary>
public class CostPreviousWindowTests : IDisposable
{
    /// <summary>Noon, so a window ending today holds half a day — the fabricated-saving case.</summary>
    private static readonly DateTime Noon = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection connection;
    private readonly ApplicationDbContext db;
    private readonly CostLedgerService history;

    private readonly Guid tenantId = Guid.NewGuid();
    private readonly Guid clusterId = Guid.NewGuid();
    private readonly Guid steadyCustomerId = Guid.NewGuid();
    private readonly Guid otherCustomerId = Guid.NewGuid();

    public CostPreviousWindowTests()
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        db = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        // Staged, principals first: foreign keys ARE enforced in this SQLite host and EF's batch
        // ordering can put a dependent ahead of the row it points at.
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

        db.KubernetesClusters.Add(new KubernetesCluster
        {
            Id = clusterId,
            TenantId = tenantId,
            EnvironmentId = environment.Id,
            Name = "prod",
            ApiServerUrl = "https://prod.example.com:6443",
        });
        db.SaveChanges();

        history = new CostLedgerService(new TestDbContextFactory(connection));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// One day for one customer, with the coverage row that says how much of it was watched.
    ///
    /// <para>The coverage row is the point of this fixture: a day seeded without one is money the
    /// ledger holds and cannot account for, which is exactly the arrangement the comparison used
    /// to read as a saving.</para>
    /// </summary>
    private async Task SeedAsync(
        DateTime day,
        decimal cost,
        decimal coveredHours = 24m,
        decimal gapHours = 0m,
        Guid? customer = null,
        bool withCoverage = true)
    {
        DateTime utcDay = CostAccrual.DayOf(day);
        Guid forCustomer = customer ?? steadyCustomerId;

        db.CostLedgerEntries.Add(new CostLedgerEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClusterId = clusterId,
            ClusterName = "prod",
            Namespace = forCustomer == steadyCustomerId ? "steady-prod" : "other-prod",
            Day = utcDay,
            CustomerId = forCustomer,
            CustomerName = forCustomer == steadyCustomerId ? "Steady AB" : "Other AB",
            EnvironmentName = "Production",
            Hours = coveredHours,
            CpuCost = cost,
            Currency = "EUR",
        });

        if (withCoverage && !await db.CostLedgerCoverages.AnyAsync(c => c.Day == utcDay))
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
    }

    private Task<CostHistoryReport> HistoryAsync(DateTime from, DateTime to) =>
        history.GetHistoryAsync(
            tenantId, CostAccrual.DayOf(from), CostAccrual.DayOf(to), CostGroupBy.Customer, Noon);

    // ═══════════════════════════════════════════════════════════════════
    // The two fabricated trends
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ The fabricated rise. Both windows are entirely in the past, so nothing here depends on
    /// the clock — the ledger simply started four days into the earlier one.
    /// </summary>
    [Fact]
    public async Task A_ledger_that_started_mid_window_is_not_a_doubling_of_cost()
    {
        // The earlier window, Sep 1–7: measured for its last three days only.
        foreach (int d in new[] { 5, 6, 7 }) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        // The requested window, Sep 8–14: measured end to end.
        for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.TotalCost.Should().Be(700m);
        report.PreviousTotalCost.Should().Be(300m);

        // The measurement the earlier window had all along, and that nothing fetched.
        report.PreviousMeasuredDays.Should().Be(3m);
        report.PreviousDays.Should().Be(7);
        report.PreviousUnmeasuredDays.Should().Be(4);
        report.PreviousIsComplete.Should().BeFalse();

        report.DailyAverage.Should().Be(100m);
        report.PreviousDailyAverage.Should().Be(100m);

        // ⚠️ The literal the defect produced: (700 − 300) / 300.
        report.ChangeFraction.Should().Be(
            0d, "spending held at 100 a day, and this read +133% in red");

        // The money difference is still the money difference — it is a bookkeeping subtraction,
        // and the record now says so rather than pretending the two windows are comparable.
        report.Change.Should().Be(400m);
    }

    /// <summary>
    /// ⚠️ The fabricated saving — the one that was on screen every time anybody opened the tab,
    /// because the window always ends today and today is half elapsed at noon.
    /// </summary>
    [Fact]
    public async Task A_window_ending_at_noon_today_is_not_a_seven_percent_saving()
    {
        // The earlier window, Sep 2–8: seven whole days at 100.
        for (int d = 2; d <= 8; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        // The requested window, Sep 9–15: six whole days, then half of today.
        for (int d = 9; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
        await SeedAsync(Noon, cost: 50m, coveredHours: 12m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc), Noon);

        report.TotalCost.Should().Be(650m);
        report.PreviousTotalCost.Should().Be(700m);

        report.MeasuredDays.Should().Be(6.5m, "six whole days and half of today");
        report.PreviousMeasuredDays.Should().Be(7m);

        report.DailyAverage.Should().Be(100m);
        report.PreviousDailyAverage.Should().Be(100m);

        // ⚠️ The literal the defect produced: (650 − 700) / 700 = −0.0714…, rendered green.
        report.ChangeFraction.Should().Be(
            0d, "nothing changed; the window is simply not over yet");
    }

    // ═══════════════════════════════════════════════════════════════════
    // The positive controls — a real change must still be reported
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ THE control that makes the two facts above mean something. Equal coverage on both sides
    /// is the arrangement in which totals and rates agree, so this is the honest case the fix must
    /// not move — and every fixture that existed before was this case, which is why neither defect
    /// was visible.
    /// </summary>
    [Fact]
    public async Task Equal_coverage_reports_the_change_totals_would_have()
    {
        for (int d = 1; d <= 7; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
        for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 120m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.TotalCost.Should().Be(840m);
        report.PreviousTotalCost.Should().Be(700m);
        report.MeasuredDays.Should().Be(7m);
        report.PreviousMeasuredDays.Should().Be(7m);

        // 840/700 − 1 and 120/100 − 1 are the same number, which is the point.
        report.ChangeFraction.Should().BeApproximately(0.2d, 1e-9);
        report.ComparableToPrevious.Should().BeTrue();
    }

    /// <summary>A real saving is still a saving, and still negative.</summary>
    [Fact]
    public async Task A_real_saving_is_still_reported()
    {
        for (int d = 1; d <= 7; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
        for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 80m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.ChangeFraction.Should().BeApproximately(-0.2d, 1e-9);
    }

    // ═══════════════════════════════════════════════════════════════════
    // An unmeasured window is not a cheap one, at either end
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// A window reaching back before the ledger's first day — which is every window wider than the
    /// installation is old. There is no earlier rate, so there is no percentage.
    /// </summary>
    [Fact]
    public async Task An_unmeasured_earlier_window_has_no_percentage()
    {
        for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.PreviousTotalCost.Should().Be(0m);
        report.PreviousMeasuredDays.Should().Be(0m);
        report.PreviousUnmeasuredDays.Should().Be(7);
        report.PreviousCoveredHours.Should().Be(0m);
        report.ComparableToPrevious.Should().BeFalse();
        report.ChangeFraction.Should().BeNull();
    }

    /// <summary>
    /// ⚠️ The mirror image, and it is not symmetrical in the code: spending nothing in a window
    /// the ledger never ran in is 0 over a rate, which is a 100% saving unless the other end is
    /// checked too.
    /// </summary>
    [Fact]
    public async Task An_unmeasured_requested_window_is_not_a_total_saving()
    {
        for (int d = 1; d <= 7; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        // Sep 8–14 has no ledger row and no coverage row at all.
        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.TotalCost.Should().Be(0m);
        report.MeasuredDays.Should().Be(0m);
        report.PreviousDailyAverage.Should().Be(100m);
        report.ComparableToPrevious.Should().BeFalse();
        report.ChangeFraction.Should().BeNull("an unwatched window is not a free one");

        // ⚠️ And the same at line level: an installation whose ledger has simply stopped must
        // not retire every customer it has. This is the only fact pinning IsGone's guard —
        // CostLedgerTests' switched-off case now measures both windows, so it passes either way.
        CostHistoryGroup steady = report.Groups.Single(g => g.Label == "Steady AB");
        steady.Cost.Should().Be(0m);
        steady.PreviousCost.Should().Be(700m);
        steady.IsGone.Should().BeFalse("nothing here was measured, so nothing here is known to be gone");
        steady.ChangeFraction.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════════════
    // The earlier window's coverage, now that it is fetched at all
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The gap hours of the earlier window reach the report, and the requested window's own
    /// figures do not absorb them.
    ///
    /// <para>⚠️ The second half is the assertion that matters: widening the coverage query is only
    /// half the change, and summing the widened result into <c>CoveredHours</c> and
    /// <c>GapHours</c> would have made the requested window look twice as watched as it is.</para>
    /// </summary>
    [Fact]
    public async Task Each_window_reports_its_own_coverage_and_not_the_others()
    {
        // Earlier window: three days, one of them half-missed.
        await SeedAsync(new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), 100m);
        await SeedAsync(new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc), 50m, coveredHours: 12m, gapHours: 12m);
        await SeedAsync(new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc), 100m);

        // Requested window: seven whole days, no gaps.
        for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.CoveredHours.Should().Be(7m * 24m);
        report.ExpectedHours.Should().Be(7m * 24m);
        report.GapHours.Should().Be(0m, "the gap is in the EARLIER window");
        report.IsComplete.Should().BeTrue();

        report.PreviousCoveredHours.Should().Be(24m + 12m + 24m);
        report.PreviousGapHours.Should().Be(12m);
        report.PreviousMeasuredDays.Should().Be(2.5m);
        report.PreviousIsComplete.Should().BeFalse();

        // 250 over two and a half measured days, against 700 over seven.
        report.PreviousDailyAverage.Should().Be(100m);
        report.ChangeFraction.Should().Be(0d);
    }

    /// <summary>
    /// A partly measured earlier window is said out loud, because the rate it is compared against
    /// is extrapolated from the part that was measured.
    ///
    /// <para>⚠️ <b>Run under a Swedish locale on purpose.</b> This warning is the first in the
    /// list to carry a fractional figure, and the list is returned verbatim in <c>warnings</c> of
    /// <c>GET /api/v1/cost/history</c> — so an interpolated <c>:F1</c> said <b>"3,0"</b> on the
    /// very first run of this test, on a machine whose culture is the one this installation's own
    /// servers run under. A client parsing the sentence reads that as thirty.</para>
    /// </summary>
    [Fact]
    public async Task A_partly_measured_earlier_window_is_warned_about()
    {
        System.Globalization.CultureInfo previousCulture =
            System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture =
            System.Globalization.CultureInfo.GetCultureInfo("sv-SE");

        try
        {
            foreach (int d in new[] { 5, 6, 7 }) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
            for (int d = 8; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

            CostHistoryReport report = await HistoryAsync(
                new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

            report.Warnings.Should().Equal(
                [
                    "The 7 days before this window were measured for only 3.0 of them, so the "
                    + "change shown against them extrapolates their daily rate from the part "
                    + "that was measured.",
                ],
                "a decimal point, not a comma, in a sentence an API client reads");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// And NOT warned about when both windows were measured end to end, or the sentence would be
    /// on every page of a healthy installation and mean nothing.
    /// </summary>
    [Fact]
    public async Task A_fully_measured_pair_of_windows_warns_about_nothing()
    {
        for (int d = 1; d <= 14; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.Warnings.Should().BeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════════
    // The breakdown lines carry the same correction
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ⚠️ Every line of every report used to be "new" whenever the window reached back before the
    /// ledger's first day. That is the state of a fresh installation on any range wider than its
    /// own age, so the badge said "new" against everything and meant nothing.
    /// </summary>
    [Fact]
    public async Task No_line_is_new_merely_because_the_earlier_window_was_unwatched()
    {
        for (int d = 8; d <= 14; d++)
        {
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 40m, customer: otherCustomerId);
        }

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        report.Groups.Should().HaveCount(2);
        report.Groups.Should().OnlyContain(g => !g.IsNew);
        report.Groups.Should().OnlyContain(g => g.ChangeFraction == null);
    }

    /// <summary>
    /// ⚠️ The positive control for the line above: a line that genuinely appears in a MEASURED
    /// window is still new. Without this, hard-coding <c>IsNew</c> to false would pass.
    /// </summary>
    [Fact]
    public async Task A_line_that_appears_in_a_measured_window_is_still_new()
    {
        for (int d = 1; d <= 7; d++) await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);

        for (int d = 8; d <= 14; d++)
        {
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 40m, customer: otherCustomerId);
        }

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        CostHistoryGroup steady = report.Groups.Single(g => g.Label == "Steady AB");
        CostHistoryGroup other = report.Groups.Single(g => g.Label == "Other AB");

        steady.IsNew.Should().BeFalse();
        other.IsNew.Should().BeTrue("it was absent from an earlier window that WAS measured");
        other.ChangeFraction.Should().BeNull("there is no earlier rate to divide by");
    }

    /// <summary>
    /// A line's own percentage is a rate comparison too, on the same window-level denominators —
    /// so a steady customer reads 0% in the breakdown as well as in the tile above it.
    /// </summary>
    [Fact]
    public async Task A_lines_percentage_compares_rates_on_the_windows_measured_days()
    {
        // Earlier window measured for three of seven days; both customers steady throughout.
        foreach (int d in new[] { 5, 6, 7 })
        {
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 40m, customer: otherCustomerId);
        }

        for (int d = 8; d <= 14; d++)
        {
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 100m);
            await SeedAsync(new DateTime(2026, 9, d, 0, 0, 0, DateTimeKind.Utc), 40m, customer: otherCustomerId);
        }

        CostHistoryReport report = await HistoryAsync(
            new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc));

        CostHistoryGroup steady = report.Groups.Single(g => g.Label == "Steady AB");
        CostHistoryGroup other = report.Groups.Single(g => g.Label == "Other AB");

        steady.MeasuredDays.Should().Be(7m);
        steady.PreviousMeasuredDays.Should().Be(3m);

        // 700/7 against 300/3, and 280/7 against 120/3.
        steady.ChangeFraction.Should().Be(0d, "and this read +133%");
        other.ChangeFraction.Should().Be(0d, "and this read +133% too");

        steady.DailyAverage.Should().Be(100m);
        other.PreviousDailyAverage.Should().Be(40m);
    }
}
