using EntKube.Web.Data;
using EntKube.Web.Services.Contracts;
using EntKube.Web.Services.Support;
using EntKube.Web.Services.Tickets;
using EntKube.Web.Services.Time;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Reporting;

/// <summary>Measured availability for one application over the month (§16.1).</summary>
/// <param name="AppName">The application.</param>
/// <param name="UptimePercent">Fraction of health snapshots that were healthy, as a percentage.</param>
/// <param name="TargetPercent">The agreed SLA target, when one is recorded.</param>
/// <param name="SampleCount">How many snapshots the figure rests on.</param>
public readonly record struct AvailabilityRow(
    string AppName,
    double? UptimePercent,
    double? TargetPercent,
    int SampleCount)
{
    /// <summary>
    /// Whether the target was met. Null when there is no target or no data — reported as
    /// unknown rather than as a pass, on the same principle the cost ledger uses.
    /// </summary>
    public bool? TargetMet =>
        UptimePercent is null || TargetPercent is null ? null : UptimePercent >= TargetPercent;
}

/// <summary>Ticket counts and SLA compliance for one priority (§14.6, §16.1).</summary>
/// <param name="Priority">The priority band.</param>
/// <param name="Raised">Tickets reported in the month.</param>
/// <param name="Resolved">How many of them reached a resolution.</param>
/// <param name="ResponseBreaches">Missed response times, excluding those with a documented exemption.</param>
/// <param name="ResolutionOverruns">Resolution targets overrun — a missed goal, not a breach.</param>
/// <param name="AverageResolution">Mean time to resolution, counted inside the support window.</param>
public readonly record struct PriorityRow(
    TicketPriority Priority,
    int Raised,
    int Resolved,
    int ResponseBreaches,
    int ResolutionOverruns,
    TimeSpan? AverageResolution);

/// <summary>
/// The monthly report §16.1 requires, due by the fifth of the following month.
/// </summary>
/// <param name="CustomerName">Who it is for.</param>
/// <param name="Month">The month it covers, as its first instant.</param>
/// <param name="Availability">Uptime per application.</param>
/// <param name="ByPriority">Tickets and SLA compliance per priority band.</param>
/// <param name="OpenAtMonthEnd">Tickets still open when the month ended.</param>
/// <param name="Timebank">The hour bank as it stood (§11.1 — including what expired).</param>
/// <param name="Hours">Committed hours at the §20 granularity.</param>
/// <param name="PenaltyDeviations">Response breaches on P1 or P2 that carry §14.6's penalty.</param>
/// <param name="PenaltyAmount">What those come to, at 10% of the month's window fee each.</param>
/// <param name="WindowFee">The window fee the penalty is a fraction of.</param>
/// <param name="PenaltiesAlreadyThisYear">
/// Penalty deviations earlier in the same calendar year, against §14.6's cap of three.
/// </param>
/// <param name="ActionPlanRequired">
/// Whether §14.6's action plan is owed — more than two P1/P2 deviations in the quarter.
/// </param>
public readonly record struct MonthlyReport(
    string CustomerName,
    DateTime Month,
    IReadOnlyList<AvailabilityRow> Availability,
    IReadOnlyList<PriorityRow> ByPriority,
    int OpenAtMonthEnd,
    TimebankStatement Timebank,
    CommittedHoursStatement Hours,
    int PenaltyDeviations,
    decimal PenaltyAmount,
    decimal WindowFee,
    int PenaltiesAlreadyThisYear,
    bool ActionPlanRequired)
{
    public int TotalRaised => ByPriority.Sum(p => p.Raised);

    public int TotalResolved => ByPriority.Sum(p => p.Resolved);

    /// <summary>
    /// Deviations left before §14.6's cap of three a year is reached. Once it is, further
    /// breaches cost no money — but §14.6 also lets the customer force the window down to
    /// S1, which costs considerably more.
    /// </summary>
    public int PenaltiesRemainingThisYear =>
        Math.Max(0, TicketSla.MaxPenaltiesPerYear - PenaltiesAlreadyThisYear - PenaltyDeviations);
}

/// <summary>
/// Assembles the §16.1 monthly report.
///
/// <para>Every figure here already exists somewhere — uptime in the health snapshots,
/// tickets and their clocks in the ticket store, hours in the time entries, the
/// window fee in Annex C. What this does is put them in the shape the agreement asks
/// for, on the date it asks for, so producing the report is not a monthly exercise in
/// remembering where everything lives.</para>
///
/// <para><b>Computed, never stored.</b> A report re-run for March must say what March said.
/// Everything it rests on is already immutable or dated, so recomputing is safer than
/// snapshotting something that could drift from its own sources.</para>
/// </summary>
public class MonthlyReportService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TicketService tickets,
    TimeService time,
    ContractService contracts)
{
    /// <summary>The day of the following month by which §16.1 requires the report.</summary>
    public const int DueOnDayOfMonth = 5;

    /// <summary>When the report for a month is due — the fifth of the month after it.</summary>
    public static DateTime DueDate(DateTime month)
    {
        (DateTime from, _) = TimeService.MonthBounds(month);
        return from.AddMonths(1).AddDays(DueOnDayOfMonth - 1);
    }

    public async Task<MonthlyReport> BuildAsync(
        Guid customerId, DateTime month, CancellationToken ct = default)
    {
        (DateTime from, DateTime to) = TimeService.MonthBounds(month);

        using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Customer? customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

        if (customer is null)
        {
            return new MonthlyReport(
                "", from, [], [], 0, default, default, 0, 0m, 0m, 0, false);
        }

        List<TicketSlaStatus> inMonth = await tickets.GetForPeriodAsync(customerId, from, to, to, ct);

        List<PriorityRow> byPriority = [];

        foreach (TicketPriority priority in Enum.GetValues<TicketPriority>())
        {
            List<TicketSlaStatus> band = [.. inMonth.Where(t => t.Ticket.Priority == priority)];

            if (band.Count == 0)
            {
                continue;
            }

            List<TicketSlaStatus> resolved = [.. band.Where(t => t.Ticket.ResolvedAt is not null)];

            TimeSpan? average = resolved.Count == 0
                ? null
                : TimeSpan.FromTicks((long)resolved.Average(t => t.Resolution.Elapsed.Ticks));

            byPriority.Add(new PriorityRow(
                priority,
                band.Count,
                resolved.Count,
                band.Count(t => t.IsPenaltyDeviation),
                band.Count(t => t.Resolution.Breached && !t.Ticket.ExcludedFromSla),
                average));
        }

        int penaltyDeviations = inMonth.Count(t => t.IsPenaltyDeviation);

        // §14.6 prices the penalty at 10% of the month's window fee, which comes from the
        // portfolio's widest window and the price list in force then.
        BaseFeeBreakdown fees = await contracts.CalculateBaseFeeAsync(customerId, from, ct);

        decimal penalty = fees.WindowFee * TicketSla.PenaltyFractionOfWindowFee * penaltyDeviations;

        return new MonthlyReport(
            customer.Name,
            from,
            await AvailabilityAsync(db, customerId, from, to, ct),
            byPriority,
            await db.Tickets.CountAsync(t =>
                t.CustomerId == customerId
                && t.ReportedAt < to
                && (t.ClosedAt == null || t.ClosedAt >= to), ct),
            await time.GetTimebankAsync(customerId, from, ct),
            await time.GetCommittedHoursAsync(customerId, from, to, ct),
            penaltyDeviations,
            penalty,
            fees.WindowFee,
            await PenaltiesEarlierThisYearAsync(customerId, from, ct),
            await ActionPlanRequiredAsync(customerId, from, ct));
    }

    /// <summary>
    /// Uptime per application, from the health snapshots. §14.1 samples every five minutes,
    /// so a month is around 8,600 samples per deployment; the count is reported alongside
    /// so a figure resting on twelve of them is visibly not worth much.
    /// </summary>
    private static async Task<List<AvailabilityRow>> AvailabilityAsync(
        ApplicationDbContext db, Guid customerId, DateTime from, DateTime to, CancellationToken ct)
    {
        var deployments = await db.AppDeployments.AsNoTracking()
            .Where(d => d.App.CustomerId == customerId)
            .Select(d => new { d.Id, d.AppId, AppName = d.App.Name })
            .ToListAsync(ct);

        if (deployments.Count == 0)
        {
            return [];
        }

        HashSet<Guid> ids = [.. deployments.Select(d => d.Id)];

        var samples = await db.DeploymentHealthSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.DeploymentId) && s.SnapshotAt >= from && s.SnapshotAt < to)
            .Select(s => new { s.DeploymentId, s.HealthStatus })
            .ToListAsync(ct);

        List<SlaTarget> targets = await db.SlaTargets.AsNoTracking()
            .Where(t => t.CustomerId == customerId || t.CustomerId == null)
            .ToListAsync(ct);

        List<AvailabilityRow> rows = [];

        foreach (var group in deployments.GroupBy(d => new { d.AppId, d.AppName }))
        {
            HashSet<Guid> groupIds = [.. group.Select(d => d.Id)];
            var taken = samples.Where(s => groupIds.Contains(s.DeploymentId)).ToList();

            double? uptime = taken.Count == 0
                ? null
                : 100.0 * taken.Count(s => s.HealthStatus == HealthStatus.Healthy) / taken.Count;

            // The most specific target wins: one for the application, else the customer's.
            SlaTarget? target = targets.FirstOrDefault(t => t.AppId == group.Key.AppId)
                                ?? targets.FirstOrDefault(t => t.AppId == null && t.CustomerId == customerId)
                                ?? targets.FirstOrDefault(t => t.AppId == null && t.CustomerId == null);

            rows.Add(new AvailabilityRow(
                group.Key.AppName, uptime, target?.TargetPercent, taken.Count));
        }

        return [.. rows.OrderBy(r => r.AppName)];
    }

    /// <summary>
    /// Penalty deviations earlier in the same calendar year. §14.6 caps them at three a
    /// year, so the report has to know what has already been paid out.
    /// </summary>
    private async Task<int> PenaltiesEarlierThisYearAsync(
        Guid customerId, DateTime month, CancellationToken ct)
    {
        DateTime yearStart = new(month.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        if (yearStart >= month)
        {
            return 0;
        }

        List<TicketSlaStatus> earlier = await tickets.GetForPeriodAsync(
            customerId, yearStart, month, month, ct);

        return earlier.Count(t => t.IsPenaltyDeviation);
    }

    /// <summary>
    /// Whether §14.6's action plan is owed: more than two P1–P2 deviations in the calendar
    /// quarter the month falls in, presented at the next quarterly meeting.
    /// </summary>
    private async Task<bool> ActionPlanRequiredAsync(
        Guid customerId, DateTime month, CancellationToken ct)
    {
        int quarterFirstMonth = (((month.Month - 1) / 3) * 3) + 1;
        DateTime quarterStart = new(month.Year, quarterFirstMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime quarterEnd = quarterStart.AddMonths(3);

        List<TicketSlaStatus> quarter = await tickets.GetForPeriodAsync(
            customerId, quarterStart, quarterEnd, quarterEnd, ct);

        return quarter.Count(t => t.IsPenaltyDeviation) > TicketSla.DeviationsBeforeActionPlan;
    }
}
