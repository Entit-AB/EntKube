using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace EntKube.Web.Services.Cost;

/// <summary>What a cost history is broken down by.</summary>
public enum CostGroupBy
{
    Customer,
    App,
    Cluster,
    Environment,
    Namespace,
}

/// <summary>One day of the history.</summary>
public sealed record CostHistoryPoint
{
    public required DateTime Day { get; init; }
    public decimal Cost { get; init; }

    /// <summary>Cluster-hours the ledger measured and billed on this day.</summary>
    public decimal CoveredHours { get; init; }

    /// <summary>Cluster-hours that elapsed and were expected to be measured.</summary>
    public decimal ExpectedHours { get; init; }

    /// <summary>
    /// 24 hours for every cluster with a record on this day — a whole day, with no exemption.
    ///
    /// <para>⚠️ <b>This is deliberately NOT <see cref="ExpectedHours"/>, and the difference is the
    /// defect this member exists to fix.</b> <c>ExpectedHours</c> answers "of the hours we could
    /// have watched, how many should we have?", so it is correctly reduced on a cluster's opening
    /// day and on today. <c>FullDayHours</c> answers a different question — "how much of a whole
    /// day is this?" — which a figure dividing money by days needs and which
    /// <c>Completeness</c> cannot give, because it saturates at 1 for a part day.</para>
    /// </summary>
    public decimal FullDayHours { get; init; }

    /// <summary>
    /// How much of a whole day's money this day holds, 0–1: measured cluster-hours over a whole
    /// day's worth. A day the ledger ran end to end is 1; today at noon is about 0.5; a day it
    /// never ran on is 0.
    ///
    /// <para>Measured hours rather than elapsed ones, deliberately. The sweep is hourly, so at
    /// noon the ledger holds roughly eleven hours of money, not twelve — and an hour not yet swept
    /// is missing from the numerator too, so this cancels instead of over-reporting.</para>
    ///
    /// <para>Clamped, because a day holding more than a whole day's hours is indefensible on its
    /// face — it means a double-booked hour or a clock that moved, and in that case the money is
    /// inflated as well, so letting the denominator follow it would hide the error rather than
    /// halve it.</para>
    /// </summary>
    public decimal MeasuredDayFraction =>
        FullDayHours > 0m ? Math.Min(1m, CoveredHours / FullDayHours) : 0m;

    /// <summary>0–1. Below 1 the day is cheap partly because it was not fully watched.</summary>
    public double Completeness =>
        ExpectedHours > 0m ? Math.Min(1d, (double)(CoveredHours / ExpectedHours)) : 1d;

    /// <summary>
    /// True when the day was measured essentially end to end. The tolerance exists
    /// because sweeps land seconds apart from where they nominally should, not to
    /// forgive real gaps.
    /// </summary>
    public bool IsComplete => Completeness >= 0.99d;
}

/// <summary>One line of a breakdown, with the same line in the preceding period beside it.</summary>
public sealed record CostHistoryGroup
{
    public Guid? Id { get; init; }
    public required string Label { get; init; }

    /// <summary>Cost in the requested window.</summary>
    public decimal Cost { get; init; }

    /// <summary>Cost in the window of the same length immediately before it.</summary>
    public decimal PreviousCost { get; init; }

    /// <summary>
    /// The difference in money recorded. ⚠️ Recorded, not incurred: the two windows are not
    /// necessarily measured to the same extent, so this is a bookkeeping difference. The question
    /// a reader means — "is this line costing more?" — is <see cref="ChangeFraction"/>.
    /// </summary>
    public decimal Change => Cost - PreviousCost;

    /// <summary>
    /// Whole days' worth of measurement in the requested window, and in the one before it. Both
    /// are properties of the <em>window</em> rather than of this line — coverage is recorded per
    /// cluster-day, not per customer or app — so every line in one report carries the same pair.
    ///
    /// <para>They are here because without them a line cannot tell a cheap window from an
    /// unwatched one, which is the whole of this record's comparison.</para>
    /// </summary>
    public decimal MeasuredDays { get; init; }

    /// <inheritdoc cref="MeasuredDays"/>
    public decimal PreviousMeasuredDays { get; init; }

    /// <summary>What this line cost per measured day in the requested window.</summary>
    public decimal DailyAverage => MeasuredDays > 0m ? Cost / MeasuredDays : 0m;

    /// <summary>What it cost per measured day in the window before it.</summary>
    public decimal PreviousDailyAverage =>
        PreviousMeasuredDays > 0m ? PreviousCost / PreviousMeasuredDays : 0m;

    /// <summary>
    /// Relative change, or null when there is nothing to compare against.
    ///
    /// <para>⚠️ <b>Daily rate against daily rate, not total against total, and that is the
    /// correction.</b> The two windows are not measured to the same extent — the requested one
    /// ends today and so holds a part day, and the earlier one may reach back before the ledger
    /// was running at all — so dividing one total by the other reports the difference in
    /// <em>watching</em> as a difference in <em>spending</em>. A line costing a steady 100 a day
    /// came out as +133% when the earlier window had three measured days of seven, and as a 7%
    /// saving on every ordinary render, because the window always ends mid-day.</para>
    ///
    /// <para>Null when there is no earlier rate to divide by — a line that did not exist last
    /// window, or a window that was never measured. Reporting +100% or ∞ for either would invent
    /// a trend out of an appearance.</para>
    /// </summary>
    public double? ChangeFraction =>
        MeasuredDays > 0m && PreviousDailyAverage > 0m
            ? (double)((DailyAverage - PreviousDailyAverage) / PreviousDailyAverage)
            : null;

    /// <summary>
    /// True when this line is new in this window.
    ///
    /// <para>⚠️ Requires the earlier window to have been measured at all. Without that every
    /// line of every report was "new" whenever the window reached back before the ledger's first
    /// day — which is every window wider than the installation is old.</para>
    ///
    /// <para>A residual remains and is not fixable from here: an earlier window measured in part
    /// can hold a line only in its unmeasured days, and this will call that line new. Narrowing it
    /// further would need per-line coverage, which the ledger does not record.</para>
    /// </summary>
    public bool IsNew => PreviousMeasuredDays > 0m && PreviousCost == 0m && Cost > 0m;

    /// <summary>
    /// True when this line was there last window and is gone from this one — and, likewise, only
    /// when this window was measured, or an unwatched window would retire every line in it.
    /// </summary>
    public bool IsGone => MeasuredDays > 0m && Cost == 0m && PreviousCost > 0m;

    public double CpuCoreHours { get; init; }
    public double MemoryGiBHours { get; init; }
    public double StorageGiBHours { get; init; }

    /// <summary>True for cost that reached no customer — platform, or workloads EntKube does not manage.</summary>
    public bool IsUnattributed { get; init; }
}

/// <summary>A tenant's cost over a period, as it was actually incurred.</summary>
public sealed record CostHistoryReport
{
    public required DateTime From { get; init; }
    public required DateTime To { get; init; }
    public required CostGroupBy GroupBy { get; init; }

    /// <summary>Currency of the figures, or "—" when the window mixes several.</summary>
    public string Currency { get; init; } = "USD";

    public IReadOnlyList<CostHistoryPoint> Daily { get; init; } = [];
    public IReadOnlyList<CostHistoryGroup> Groups { get; init; } = [];

    public decimal TotalCost { get; init; }

    /// <summary>The same length of time immediately before <see cref="From"/>.</summary>
    public decimal PreviousTotalCost { get; init; }

    /// <summary>
    /// The difference in money recorded between the two windows.
    ///
    /// <para>⚠️ <b>Recorded, not incurred.</b> The two windows are not necessarily measured to
    /// the same extent, so this subtracts two figures of different coverage and is a bookkeeping
    /// difference rather than a trend. <see cref="ChangeFraction"/> is the one that answers "are
    /// we spending more?", and it is deliberately on a different basis — see its own remarks.</para>
    /// </summary>
    public decimal Change => TotalCost - PreviousTotalCost;

    /// <summary>
    /// How much more or less this window cost than the one before it, as a fraction — or null
    /// when there is nothing sound to compare against.
    ///
    /// <para>⚠️ <b>Daily rate against daily rate, not total against total.</b> This divided
    /// <see cref="TotalCost"/> by <see cref="PreviousTotalCost"/>, and the earlier window's
    /// coverage was never even fetched — the coverage query stopped at <see cref="From"/>. So the
    /// comparison was between two windows measured to unknown and unequal degrees, and reported
    /// the difference in <em>watching</em> as a difference in <em>spending</em>:</para>
    ///
    /// <list type="bullet">
    /// <item><b>A fabricated rise.</b> A tenant spending a steady 100 a day, whose ledger started
    /// four days into the earlier window, had 700 against 300 — <b>+133%</b>, in red, on the Cost
    /// tab and in <c>changeFraction</c> of <c>GET /api/v1/cost/history</c>.</item>
    /// <item><b>A fabricated saving, on every render.</b> The window always ends today, so the
    /// requested window holds a part day and the earlier one does not: seven days at 100 against
    /// six days and a half-elapsed today came out as <b>−7.1%</b>, rendered green, for a tenant
    /// whose spending had not moved at all.</item>
    /// </list>
    ///
    /// <para>The second one was the worse of the two: it was there every time anybody looked, it
    /// always pointed the same way, and the colour said the thing a reader wanted to believe. It
    /// also contradicted this file's own rule, that an hour never measured is reported as
    /// unmeasured rather than as free — and <c>CostCoverageResponse</c>'s own doc, which says
    /// coverage travels beside every total because "a caller that cannot see the shortfall reads
    /// it as a saving".</para>
    ///
    /// <para>Null when <see cref="PreviousDailyAverage"/> is zero — nothing was recorded in the
    /// earlier window, or it was never measured — and null when this window was never measured
    /// either, because 0 over a rate is a 100% saving and an unwatched window is not a free one.
    /// <see cref="ComparableToPrevious"/> is the same condition, named, for a caller that wants to
    /// know before it reads.</para>
    /// </summary>
    public double? ChangeFraction =>
        ComparableToPrevious && PreviousDailyAverage > 0m
            ? (double)((DailyAverage - PreviousDailyAverage) / PreviousDailyAverage)
            : null;

    /// <summary>
    /// True when both windows hold some measurement, so a comparison between them means
    /// something. False for a window the ledger was not running in — at either end.
    /// </summary>
    public bool ComparableToPrevious => MeasuredDays > 0m && PreviousMeasuredDays > 0m;

    /// <summary>Cost that reached no customer.</summary>
    public decimal UnattributedCost { get; init; }

    /// <summary>Cost in namespaces shared by several apps, billable to a customer but not to one app.</summary>
    public decimal MultiAppCost { get; init; }

    /// <summary>
    /// What the platform namespaces themselves cost over the window. Already charged out
    /// inside <see cref="TotalCost"/> through each app's share — reported separately
    /// because "what does the platform cost us" is a question this is the only answer to,
    /// not because it is an extra.
    /// </summary>
    public decimal PlatformCost { get; init; }

    /// <summary>The shared cost that was charged out to apps — platform namespaces plus fixed cluster fees.</summary>
    public decimal SharedChargedOutCost { get; init; }

    public decimal CoveredHours { get; init; }
    public decimal ExpectedHours { get; init; }

    /// <summary>Hours explicitly recorded as unmeasured — the plane was down, or a cluster could not be read.</summary>
    public decimal GapHours { get; init; }

    /// <summary>
    /// The same three figures for the window immediately before this one.
    ///
    /// <para>⚠️ <b>These used not to exist, and that is what made the comparison wrong.</b> The
    /// coverage query ran from <see cref="From"/>, so the earlier window's money was fetched and
    /// its measurement was not — leaving <see cref="PreviousTotalCost"/> as the one total in this
    /// record with no coverage beside it, in a record whose every other total carries some.</para>
    /// </summary>
    public decimal PreviousCoveredHours { get; init; }

    /// <inheritdoc cref="PreviousCoveredHours"/>
    public decimal PreviousExpectedHours { get; init; }

    /// <inheritdoc cref="PreviousCoveredHours"/>
    public decimal PreviousGapHours { get; init; }

    /// <summary>
    /// Whole days' worth of measurement in the earlier window, a part day counting as its
    /// fraction — <see cref="MeasuredDays"/> for the other window.
    ///
    /// <para>Set by the service rather than derived here, because the earlier window's days are
    /// not shipped: it exists only to be compared against, and a second <see cref="Daily"/> would
    /// double the size of every response for a number shown in a corner.</para>
    /// </summary>
    public decimal PreviousMeasuredDays { get; init; }

    /// <summary>Days in the earlier window with no coverage record at all.</summary>
    public int PreviousUnmeasuredDays { get; init; }

    /// <summary>
    /// Days the earlier window spans. Normally the same as this window's, and fewer only when the
    /// subtraction was clamped at the first day <see cref="DateTime"/> can represent.
    /// </summary>
    public int PreviousDays { get; init; }

    /// <summary>How much of the earlier window the ledger accounts for, 0–1.</summary>
    public double PreviousCompleteness =>
        PreviousExpectedHours > 0m
            ? Math.Min(1d, (double)(PreviousCoveredHours / PreviousExpectedHours))
            : 1d;

    /// <summary>
    /// True when the earlier window was measured essentially end to end — both tests, for the
    /// reason <see cref="IsComplete"/> gives: completeness alone is 1.0 for a window that reaches
    /// back before the ledger's first day.
    /// </summary>
    public bool PreviousIsComplete =>
        PreviousCompleteness >= 0.99d && PreviousUnmeasuredDays == 0;

    /// <summary>
    /// What the earlier window cost per measured day — the figure
    /// <see cref="ChangeFraction"/> compares <see cref="DailyAverage"/> against.
    /// </summary>
    public decimal PreviousDailyAverage =>
        PreviousMeasuredDays > 0m ? PreviousTotalCost / PreviousMeasuredDays : 0m;

    /// <summary>
    /// How much of the window the ledger actually accounts for, 0–1. An incomplete window
    /// under-states cost, so this is reported beside every total rather than left for
    /// someone to work out from the chart.
    /// </summary>
    public double Completeness =>
        ExpectedHours > 0m ? Math.Min(1d, (double)(CoveredHours / ExpectedHours)) : 1d;

    /// <summary>
    /// Days in this window with <b>no coverage record at all</b> — the ledger was not running,
    /// rather than running and missing hours.
    ///
    /// <para>⚠️ These are invisible to <see cref="Completeness"/> by construction, because
    /// <see cref="ExpectedHours"/> is summed over recorded days only. Exposed so the denominator
    /// <see cref="DailyAverage"/> uses is checkable rather than implied, and so a caller can tell
    /// a quiet window from an unwatched one.</para>
    /// </summary>
    public int UnmeasuredDays => Daily.Count(d => d.ExpectedHours <= 0m);

    /// <summary>
    /// True when the window was measured essentially end to end.
    ///
    /// <para>⚠️ <c>Completeness >= 0.99</c> alone was not enough, and said the opposite of the
    /// truth in the case that matters: for a window reaching back before the ledger's first day
    /// it is 1.0, so a report two thirds of which was never watched claimed to be complete. The
    /// day count has to be checked as well as the hours.</para>
    /// </summary>
    public bool IsComplete => Completeness >= 0.99d && UnmeasuredDays == 0;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Average cost per day over the days actually covered — the basis for a projection.
    ///
    /// <para>⚠️ <b>This used to divide by the whole range, scaled by
    /// <see cref="Completeness"/>, and that correction could not work.</b> It was
    /// <c>Daily.Count * Completeness</c>, and <see cref="ExpectedHours"/> is summed only over days
    /// that HAVE a coverage record — <c>ExpectedCoverage</c> iterates the coverage rows, so a day
    /// the ledger never ran on contributes to neither side of the ratio. Completeness therefore
    /// answers "of the days we recorded, how fully did we record them", not "how much of the
    /// window did we record", and for a window reaching back before the ledger existed it is
    /// 1.0. The scale factor was 1 exactly when it was needed most.</para>
    ///
    /// <para>So a 30-day view of a ten-day-old ledger divided ten days of cost by thirty and
    /// reported a third of the true daily rate — on the Cost tab's "Per day" tile and in the
    /// <c>dailyAverage</c> field of <c>GET /api/v1/cost/history</c>, which that endpoint documents
    /// as the figure a billing system reads.</para>
    ///
    /// <para>Now it divides by the days actually measured, counting a partly measured day as the
    /// fraction of it that was. A day with no coverage record contributes <b>0</b>: it is not a
    /// cheap day, it is an absent one — which is this file's own rule, that an hour never
    /// measured must be reported as unmeasured rather than as free.</para>
    ///
    /// <para>⚠️ <b>And that correction was itself wrong, through <see cref="Completeness"/>, for
    /// the same reason one level down.</b> It weighted each day by its completeness, and
    /// completeness answers "of the hours we COULD have watched, how many did we?" — a question
    /// whose answer is 1.0 for two kinds of part day, because
    /// <c>ExpectedCoverage</c> deliberately reduces the expectation for both:</para>
    ///
    /// <list type="bullet">
    /// <item><b>today</b>, expected against the hours elapsed so far, so at noon it is 12/12 = 1.0
    /// while holding half a day's money. Every Cost tab render hits this, because the window
    /// always ends today: six full days at 100 plus today's 50 gave 650 over a denominator of
    /// <b>7.0</b> — 92.86 reported where 100.00 is right;</item>
    /// <item>a cluster's <b>opening day</b>, expected against the hours since the ledger started
    /// watching, which is 1.0 whenever it ran cleanly from then. A cluster added at 18:00
    /// contributes six hours of money and a whole day to the divisor. This one is in windows
    /// <em>entirely in the past</em>, so it is not the same bug wearing a hat — three past days
    /// with a cluster opened at 18:00 on the first reported 75.00 against a true 100.00.</item>
    /// </list>
    ///
    /// <para>So the weight is <see cref="CostHistoryPoint.MeasuredDayFraction"/> — measured hours
    /// over a whole day's 24 per cluster, with no exemption — and the exemptions stay exactly where
    /// they belong, on completeness and the warnings. ⚠️ A fix scoped to today would not have been
    /// enough and could not have been written here anyway: this record carries no clock, and
    /// <see cref="CostHistoryPoint"/> carries no cluster identity or opening date, so the second
    /// case is not detectable from the data a day arrives with.</para>
    ///
    /// <para>⚠️ <b>The assumption this rests on, stated.</b> Weighting by hours assumes cost per
    /// cluster-hour is roughly uniform across the clusters in the window. It is exact for one
    /// cluster and biased for a fleet of very unlike ones. The error it replaces was
    /// one-directional — always understating whenever any day was partial, whatever the fleet —
    /// and the honest alternative needs per-cluster day weights, which is a larger change than
    /// this.</para>
    /// </summary>
    public decimal DailyAverage => MeasuredDays > 0m ? TotalCost / MeasuredDays : 0m;

    /// <summary>
    /// The denominator <see cref="DailyAverage"/> divides by: whole days' worth of measurement in
    /// this window, where a part day counts as its fraction.
    ///
    /// <para>⚠️ Exposed because <see cref="UnmeasuredDays"/>' own doc says it exists "so the
    /// denominator <c>DailyAverage</c> uses is checkable rather than implied" — and once the
    /// weighting stopped being the day count, it no longer was. This is the number itself, so a
    /// reader can divide by hand.</para>
    ///
    /// <para>Below 1 the daily rate is an extrapolation from less than a day, which needs a
    /// ledger younger than the window rather than merely a window ending today. The figure is
    /// still returned rather than suppressed — a caller can see the denominator and decide — but
    /// it is not a projection anyone should lean on.</para>
    /// </summary>
    public decimal MeasuredDays => Daily.Sum(d => d.MeasuredDayFraction);
}

/// <summary>One calendar month of accrued cost.</summary>
public sealed record CostMonth
{
    public required int Year { get; init; }
    public required int Month { get; init; }
    public decimal Cost { get; init; }
    public decimal CoveredHours { get; init; }
    public decimal ExpectedHours { get; init; }
    public string Currency { get; init; } = "USD";

    public DateTime Start => new(Year, Month, 1, 0, 0, 0, DateTimeKind.Utc);
    public string Label => Start.ToString("MMM yyyy");

    public double Completeness =>
        ExpectedHours > 0m ? Math.Min(1d, (double)(CoveredHours / ExpectedHours)) : 1d;

    public bool IsComplete => Completeness >= 0.99d;
}

/// <summary>
/// Reads the cost ledger: what was actually spent, when, and on whose behalf.
///
/// <para>Distinct from <see cref="CostReportService"/>, which answers "what is this
/// costing right now". This answers "what did it cost", which is the only one of the two
/// a bill can be built from and the only one that can say whether something got more
/// expensive.</para>
///
/// <para>Every query excludes redistributed rows, exactly as <see cref="CostReport"/>
/// does: their cost was pooled onto the billable namespaces and counting both would
/// double it. That exclusion is applied here, once, rather than left to each caller —
/// this is the class where forgetting it would silently double an invoice.</para>
/// </summary>
public class CostLedgerService(IDbContextFactory<CostDbContext> dbFactory)
{
    /// <summary>
    /// Whether the ledger holds anything at all for this tenant. Distinguishes "nothing
    /// has ever been recorded" — a fleet nobody has priced, or a plane that has not run
    /// long enough — from "nothing happened in the window you asked about", which is a
    /// real answer.
    /// </summary>
    public async Task<bool> HasAnyAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CostLedgerEntries.AnyAsync(e => e.TenantId == tenantId, ct);
    }

    /// <summary>The first day the ledger recorded anything for this tenant.</summary>
    public async Task<DateTime?> GetLedgerStartAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.CostLedgerEntries
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Day)
            .Select(e => (DateTime?)e.Day)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Cost over a window, day by day and broken down, with the preceding window of the
    /// same length alongside for comparison.
    /// </summary>
    /// <param name="from">First day of the window, inclusive.</param>
    /// <param name="to">Last day of the window, inclusive.</param>
    public async Task<CostHistoryReport> GetHistoryAsync(
        Guid tenantId,
        DateTime from,
        DateTime to,
        CostGroupBy groupBy,
        DateTime now,
        Guid? customerId = null,
        Guid? appId = null,
        Guid? clusterId = null,
        CancellationToken ct = default)
    {
        DateTime start = CostAccrual.DayOf(from);
        DateTime end = CostAccrual.DayOf(to);

        if (end < start)
        {
            (start, end) = (end, start);
        }

        int days = (int)(end - start).TotalDays + 1;

        // ⚠️ Clamped, not AddDays: `start` can be the first day DateTime can represent, because
        // the route parses `from` out of the query string. The previous window is only ever a
        // comparison, so a clamp costs nothing — there is no cost data before year one.
        DateTime previousStart = CostAccrual.ShiftDays(start, -days);

        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Both windows in one pass — the previous period is only ever used as a
        // comparison, and a second round trip to fetch it would double the cost of every
        // page render for a number shown in a corner.
        IQueryable<CostLedgerEntry> all = db.CostLedgerEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Day >= previousStart && e.Day <= end)
            .Where(e => !e.IsRedistributed);

        if (customerId is Guid cid) all = all.Where(e => e.CustomerId == cid);
        if (appId is Guid aid) all = all.Where(e => e.AppId == aid);
        if (clusterId is Guid clid) all = all.Where(e => e.ClusterId == clid);

        List<LedgerRow> rows = await all
            .Select(e => new LedgerRow
            {
                Day = e.Day,
                Cost = e.CpuCost + e.MemoryCost + e.StorageCost + e.NetworkCost + e.SharedCost,
                SharedCost = e.SharedCost,
                CustomerId = e.CustomerId,
                CustomerName = e.CustomerName,
                AppId = e.AppId,
                AppName = e.AppName,
                ClusterId = e.ClusterId,
                ClusterName = e.ClusterName,
                EnvironmentName = e.EnvironmentName,
                Namespace = e.Namespace,
                IsMultiApp = e.IsMultiApp,
                Currency = e.Currency,
                CpuCoreHours = e.CpuCoreHours,
                MemoryGiBHours = e.MemoryGiBHours,
                StorageGiBHours = e.StorageGiBHours,
            })
            .ToListAsync(ct);

        // The platform's own cost is the one figure that comes from the rows the rest of
        // this query excludes.
        decimal platformCost = await db.CostLedgerEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Day >= start && e.Day <= end && e.IsRedistributed)
            .Where(e => clusterId == null || e.ClusterId == clusterId)
            .SumAsync(e => e.CpuCost + e.MemoryCost + e.StorageCost + e.NetworkCost, ct);

        // ⚠️ `previousStart`, not `start`. This query used to begin where the window does, which
        // left the earlier window's money fetched and its measurement not — so the comparison
        // divided two totals of unequal coverage and reported the difference in watching as a
        // difference in spending. The ledger rows above already span both windows in one pass for
        // exactly this reason; the coverage rows had been left behind.
        List<CoverageRow> coverage = await db.CostLedgerCoverages
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Day >= previousStart && c.Day <= end)
            .Where(c => clusterId == null || c.ClusterId == clusterId)
            .Select(c => new CoverageRow
            {
                ClusterId = c.ClusterId,
                Day = c.Day,
                CoveredHours = c.CoveredHours,
                GapHours = c.GapHours,
            })
            .ToListAsync(ct);

        Dictionary<Guid, DateTime> openedOn = await OpeningDaysAsync(db, tenantId, ct);

        List<LedgerRow> current = [.. rows.Where(r => r.Day >= start)];
        List<LedgerRow> previous = [.. rows.Where(r => r.Day < start)];

        List<CoverageRow> currentCoverage = [.. coverage.Where(c => c.Day >= start)];
        List<CoverageRow> previousCoverage = [.. coverage.Where(c => c.Day < start)];

        // `openedOn` spans the whole ledger, so it answers for both windows; `now` is passed to
        // both because a window placed in the future puts today in the EARLIER one.
        Dictionary<DateTime, (decimal Covered, decimal Expected, decimal Nominal)> byDay =
            ExpectedCoverage(currentCoverage, openedOn, now);

        Dictionary<DateTime, (decimal Covered, decimal Expected, decimal Nominal)> previousByDay =
            ExpectedCoverage(previousCoverage, openedOn, now);

        List<CostHistoryPoint> daily = [];
        // ⚠️ Indexed rather than `for (day = start; day <= end; day = day.AddDays(1))`. That form
        // increments PAST `end` before re-testing the condition, so a window ending on
        // 9999-12-31 — an ordinary thirty-day request with `?to=9999-12-31` — threw on its last
        // iteration. Walking by offset never leaves the window at all.
        for (int offset = 0; offset < days; offset++)
        {
            DateTime day = CostAccrual.ShiftDays(start, offset);

            (decimal covered, decimal expected, decimal nominal) = byDay.GetValueOrDefault(day);

            daily.Add(new CostHistoryPoint
            {
                Day = day,
                Cost = current.Where(r => r.Day == day).Sum(r => r.Cost),
                CoveredHours = covered,
                ExpectedHours = expected,
                FullDayHours = nominal,
            });
        }

        HashSet<string> currencies = [.. current.Select(r => r.Currency).Distinct()];

        List<string> warnings = [];
        if (currencies.Count > 1)
        {
            warnings.Add("Clusters use different currencies; totals are not meaningful.");
        }

        decimal coveredHours = byDay.Values.Sum(v => v.Covered);
        decimal expectedHours = byDay.Values.Sum(v => v.Expected);
        decimal gapHours = currentCoverage.Sum(c => c.GapHours);

        // The earlier window, on the same construction as the requested one. MeasuredDayFraction
        // is inlined rather than borrowed from a CostHistoryPoint because the earlier window's
        // days are not built: nothing ships them, and nothing needs them one at a time.
        int previousDays = (int)(start - previousStart).TotalDays;
        decimal previousCoveredHours = previousByDay.Values.Sum(v => v.Covered);
        decimal previousExpectedHours = previousByDay.Values.Sum(v => v.Expected);
        decimal previousGapHours = previousCoverage.Sum(c => c.GapHours);
        decimal previousMeasuredDays = previousByDay.Values
            .Sum(v => v.Nominal > 0m ? Math.Min(1m, v.Covered / v.Nominal) : 0m);
        int previousUnmeasuredDays =
            previousDays - previousByDay.Count(e => e.Value.Expected > 0m);

        if (expectedHours > 0m && coveredHours < expectedHours * 0.99m)
        {
            decimal missing = expectedHours - coveredHours;
            warnings.Add(
                $"{missing:F0} of {expectedHours:F0} cluster-hours in this period were never measured, "
                + "so the cost below is lower than what was actually incurred.");
        }

        // ⚠️ A separate warning, because the one above CANNOT see these days. Both of its terms
        // are summed over days that have a coverage record, so a day the ledger never ran on is
        // absent from each and the comparison stays satisfied. Without this the report says
        // nothing at all about a window that reaches back before the ledger's first day — which
        // is every window wider than the installation is old.
        int unmeasuredDays = daily.Count(d => d.ExpectedHours <= 0m);
        if (unmeasuredDays > 0)
        {
            warnings.Add(
                $"{unmeasuredDays} of {daily.Count} days in this period have no coverage record at "
                + "all — the ledger was not yet running for them. Totals cover the remaining "
                + $"{daily.Count - unmeasuredDays} days.");
        }

        // ⚠️ Said only when a percentage is actually shown. With nothing measured in the
        // earlier window ChangeFraction is null and the page prints a dash, which needs no
        // explaining; with it measured in PART, a rate is extrapolated from that part and the
        // reader is entitled to know how much of it there was.
        if (previousMeasuredDays > 0m && previousMeasuredDays < previousDays * 0.99m)
        {
            // ⚠️ Formatted invariantly, unlike the two warnings above, and the difference is not
            // cosmetic: this is the first of them to carry a FRACTIONAL figure, and the list it
            // joins is returned verbatim in `warnings` of GET /api/v1/cost/history. On a host
            // running under a Swedish locale — which this installation's own machines do — the
            // first run of this said "3,0", which a client parsing the sentence reads as thirty.
            // The integer warnings above are separator-free and so were never exposed to it.
            warnings.Add(
                $"The {previousDays} days before this window were measured for only "
                + previousMeasuredDays.ToString("F1", CultureInfo.InvariantCulture)
                + " of them, so the change shown against them extrapolates their daily rate "
                + "from the part that was measured.");
        }

        decimal measuredDays = daily.Sum(d => d.MeasuredDayFraction);

        return new CostHistoryReport
        {
            From = start,
            To = end,
            GroupBy = groupBy,
            Currency = currencies.Count == 1 ? currencies.First() : currencies.Count == 0 ? "USD" : "—",
            Daily = daily,
            Groups = Group(current, previous, groupBy, measuredDays, previousMeasuredDays),
            TotalCost = current.Sum(r => r.Cost),
            PreviousTotalCost = previous.Sum(r => r.Cost),
            UnattributedCost = current.Where(r => r.CustomerId is null).Sum(r => r.Cost),
            MultiAppCost = current.Where(r => r.IsMultiApp).Sum(r => r.Cost),
            PlatformCost = platformCost,
            SharedChargedOutCost = current.Sum(r => r.SharedCost),
            CoveredHours = coveredHours,
            ExpectedHours = expectedHours,
            GapHours = gapHours,
            PreviousCoveredHours = previousCoveredHours,
            PreviousExpectedHours = previousExpectedHours,
            PreviousGapHours = previousGapHours,
            PreviousMeasuredDays = previousMeasuredDays,
            PreviousUnmeasuredDays = previousUnmeasuredDays,
            PreviousDays = previousDays,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Accrued cost per calendar month, most recent last. Pass
    /// <paramref name="customerId"/> to restrict it to one customer — the customer-facing
    /// portal shows these, so the filter belongs in the query rather than being applied
    /// to a tenant-wide result afterwards.
    /// </summary>
    public async Task<IReadOnlyList<CostMonth>> GetMonthlyAsync(
        Guid tenantId, int months, DateTime now, Guid? customerId = null,
        CancellationToken ct = default)
    {
        DateTime firstMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-(Math.Max(1, months) - 1));

        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var costs = await db.CostLedgerEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Day >= firstMonth && !e.IsRedistributed)
            .Where(e => customerId == null || e.CustomerId == customerId)
            .GroupBy(e => new { e.Day.Year, e.Day.Month })
            .Select(g => new
            {
                g.Key.Year,
                g.Key.Month,
                Cost = g.Sum(e => e.CpuCost + e.MemoryCost + e.StorageCost + e.NetworkCost + e.SharedCost),
            })
            .ToListAsync(ct);

        List<CoverageRow> coverage = await db.CostLedgerCoverages
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Day >= firstMonth)
            .Select(c => new CoverageRow
            {
                ClusterId = c.ClusterId,
                Day = c.Day,
                CoveredHours = c.CoveredHours,
                GapHours = c.GapHours,
            })
            .ToListAsync(ct);

        Dictionary<Guid, DateTime> openedOn = await OpeningDaysAsync(db, tenantId, ct);

        Dictionary<DateTime, (decimal Covered, decimal Expected, decimal Nominal)> byDay =
            ExpectedCoverage(coverage, openedOn, now);

        HashSet<string> currencies = [.. await db.CostLedgerEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.Day >= firstMonth)
            .Select(e => e.Currency)
            .Distinct()
            .ToListAsync(ct)];

        List<CostMonth> result = [];
        for (DateTime m = firstMonth; m <= now; m = m.AddMonths(1))
        {
            var cost = costs.FirstOrDefault(c => c.Year == m.Year && c.Month == m.Month);

            var monthDays = byDay.Where(kv => kv.Key.Year == m.Year && kv.Key.Month == m.Month).ToList();

            result.Add(new CostMonth
            {
                Year = m.Year,
                Month = m.Month,
                Cost = cost?.Cost ?? 0m,
                CoveredHours = monthDays.Sum(kv => kv.Value.Covered),
                ExpectedHours = monthDays.Sum(kv => kv.Value.Expected),
                Currency = currencies.Count == 1 ? currencies.First() : currencies.Count == 0 ? "USD" : "—",
            });
        }

        return result;
    }

    /// <summary>
    /// The day each cluster's ledger opened, across the whole ledger rather than any
    /// window. Read separately because a window's own earliest row says nothing about
    /// whether the cluster existed before it — treating it as an opening day would mark
    /// every window's first day complete regardless of what was actually measured.
    /// </summary>
    private static async Task<Dictionary<Guid, DateTime>> OpeningDaysAsync(
        CostDbContext db, Guid tenantId, CancellationToken ct)
    {
        var rows = await db.CostLedgerCoverages
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .GroupBy(c => c.ClusterId)
            .Select(g => new { ClusterId = g.Key, OpenedOn = g.Min(c => c.Day) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.ClusterId, r => r.OpenedOn);
    }

    /// <summary>
    /// Expected against covered cluster-hours per day.
    ///
    /// <para>A day is expected to hold 24 hours for every cluster in the ledger — except
    /// two, which would otherwise be reported as incomplete forever: a cluster's first
    /// day, where the ledger cannot claim to be missing hours from before it was
    /// watching, and today, which has only run as far as it has.</para>
    ///
    /// <para>The opening day is checked first. A cluster added this morning is both, and
    /// measuring it against the whole of today elapsed would report a hole where there
    /// is none.</para>
    /// </summary>
    private static Dictionary<DateTime, (decimal Covered, decimal Expected, decimal Nominal)> ExpectedCoverage(
        IReadOnlyList<CoverageRow> coverage, IReadOnlyDictionary<Guid, DateTime> openedOn, DateTime now)
    {
        Dictionary<DateTime, (decimal, decimal, decimal)> byDay = [];

        DateTime today = CostAccrual.DayOf(now);

        foreach (CoverageRow row in coverage)
        {
            decimal expected;

            if (openedOn.TryGetValue(row.ClusterId, out DateTime opened) && opened == row.Day)
            {
                expected = row.CoveredHours + row.GapHours;
            }
            else if (row.Day == today)
            {
                expected = CostAccrual.HoursBetween(today, now);
            }
            else
            {
                expected = 24m;
            }

            (decimal covered, decimal existingExpected, decimal nominal) =
                byDay.GetValueOrDefault(row.Day);

            // ⚠️ Outside every branch above, and that is the point. The two exemptions are right
            // for completeness and wrong for a denominator: both reduce `expected`, so a part day
            // reports Completeness 1.0 while holding part of a day's money. A whole day is 24
            // hours per cluster whatever the ledger managed to watch of it.
            byDay[row.Day] = (
                covered + row.CoveredHours,
                existingExpected + expected,
                nominal + 24m);
        }

        return byDay;
    }

    /// <summary>
    /// The breakdown, each line carrying the window-level measured-day counts.
    ///
    /// <para>⚠️ The two denominators are passed in rather than derived per line: coverage is
    /// recorded per cluster-day, so there is no such thing as a customer's or an app's share of
    /// it. Every line gets the same pair, and uses it to compare rates instead of totals.</para>
    /// </summary>
    private static IReadOnlyList<CostHistoryGroup> Group(
        List<LedgerRow> current,
        List<LedgerRow> previous,
        CostGroupBy groupBy,
        decimal measuredDays,
        decimal previousMeasuredDays)
    {
        // A line present in only one of the two windows still has to appear, or a
        // workload that was switched off would simply disappear from the comparison
        // instead of showing as the saving it was.
        Dictionary<GroupKey, decimal> previousCosts = previous
            .GroupBy(r => KeyOf(r, groupBy))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Cost));

        List<CostHistoryGroup> groups = [.. current
            .GroupBy(r => KeyOf(r, groupBy))
            .Select(g => new CostHistoryGroup
            {
                Id = g.Key.Id,
                Label = g.Key.Label,
                Cost = g.Sum(r => r.Cost),
                PreviousCost = previousCosts.GetValueOrDefault(g.Key),
                MeasuredDays = measuredDays,
                PreviousMeasuredDays = previousMeasuredDays,
                CpuCoreHours = g.Sum(r => r.CpuCoreHours),
                MemoryGiBHours = g.Sum(r => r.MemoryGiBHours),
                StorageGiBHours = g.Sum(r => r.StorageGiBHours),
                IsUnattributed = g.Key.IsUnattributed,
            })];

        foreach ((GroupKey key, decimal cost) in previousCosts)
        {
            if (!groups.Any(g => g.Id == key.Id && g.Label == key.Label))
            {
                groups.Add(new CostHistoryGroup
                {
                    Id = key.Id,
                    Label = key.Label,
                    Cost = 0m,
                    PreviousCost = cost,
                    MeasuredDays = measuredDays,
                    PreviousMeasuredDays = previousMeasuredDays,
                    IsUnattributed = key.IsUnattributed,
                });
            }
        }

        return [.. groups.OrderByDescending(g => g.Cost).ThenByDescending(g => g.PreviousCost)];
    }

    private readonly record struct GroupKey(Guid? Id, string Label, bool IsUnattributed);

    private static GroupKey KeyOf(LedgerRow row, CostGroupBy groupBy) => groupBy switch
    {
        CostGroupBy.Customer => new GroupKey(
            row.CustomerId, row.CustomerName ?? "Unattributed", row.CustomerId is null),

        // A namespace shared by several apps is labelled as such rather than being
        // folded in with the platform: it is billable to a customer, just not to one app.
        CostGroupBy.App => new GroupKey(
            row.AppId,
            row.AppName ?? (row.IsMultiApp ? "Shared by several apps" : "Unattributed"),
            row.CustomerId is null),

        CostGroupBy.Cluster => new GroupKey(row.ClusterId, row.ClusterName, false),

        CostGroupBy.Environment => new GroupKey(
            null, row.EnvironmentName ?? "—", row.CustomerId is null),

        _ => new GroupKey(null, $"{row.ClusterName}/{row.Namespace}", row.CustomerId is null),
    };

    /// <summary>A ledger row flattened for in-memory grouping.</summary>
    private sealed record LedgerRow
    {
        public DateTime Day { get; init; }
        public decimal Cost { get; init; }
        public decimal SharedCost { get; init; }
        public Guid? CustomerId { get; init; }
        public string? CustomerName { get; init; }
        public Guid? AppId { get; init; }
        public string? AppName { get; init; }
        public Guid ClusterId { get; init; }
        public string ClusterName { get; init; } = "";
        public string? EnvironmentName { get; init; }
        public string Namespace { get; init; } = "";
        public bool IsMultiApp { get; init; }
        public string Currency { get; init; } = "USD";
        public double CpuCoreHours { get; init; }
        public double MemoryGiBHours { get; init; }
        public double StorageGiBHours { get; init; }
    }

    private sealed record CoverageRow
    {
        public Guid ClusterId { get; init; }
        public DateTime Day { get; init; }
        public decimal CoveredHours { get; init; }
        public decimal GapHours { get; init; }
    }
}
