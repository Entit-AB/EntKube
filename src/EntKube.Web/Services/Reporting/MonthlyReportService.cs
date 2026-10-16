using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Contracts.Delivery;
using EntKube.Contracts.Fleet;
using ContractHealthStatus = EntKube.Contracts.Delivery.HealthStatus;
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
/// <param name="ExcludedSamples">
/// Snapshots dropped because a maintenance window covered them. Reported rather than
/// quietly removed: taking downtime out of a number is exactly the operation that has to
/// be visible, both because the customer is owed the exclusion and because nothing else
/// would stop a window being drawn around an outage after the fact.
/// </param>
public readonly record struct AvailabilityRow(
    string AppName,
    double? UptimePercent,
    double? TargetPercent,
    int SampleCount,
    int ExcludedSamples = 0)
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

/// <summary>A P1 whose §14.6 written report has not been delivered.</summary>
/// <param name="Number">The ticket, as the customer refers to it.</param>
/// <param name="Title">What it was about.</param>
/// <param name="ClosedAt">When it was closed, which is what the five working days run from.</param>
/// <param name="Due">When the report was owed.</param>
public readonly record struct OutstandingIncidentReport(
    int Number,
    string Title,
    DateTime ClosedAt,
    DateTime Due);

/// <summary>Planned maintenance that went ahead on short notice.</summary>
/// <param name="Title">What the window was called.</param>
/// <param name="StartsAt">When it began.</param>
/// <param name="Notice">How much notice was given, and how much was owed.</param>
/// <param name="ScheduledBy">Who scheduled it.</param>
public readonly record struct ShortNoticeWindow(
    string Title,
    DateTime StartsAt,
    NoticeGiven Notice,
    string ScheduledBy);

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
/// <param name="OutstandingReports">
/// P1s closed by the end of the month whose §14.6 written report is still owed. A closed
/// ticket leaves the queue, so without this the obligation has nowhere left to be seen.
/// </param>
/// <param name="ShortNoticeMaintenance">
/// Planned maintenance in the month that went ahead on less than the agreed notice. Its
/// downtime is excluded from availability like any other window — the exclusion is about
/// what the customer is owed, not about whether we behaved — but the shortfall belongs in
/// the report rather than only in whatever mail announced it.
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
    bool ActionPlanRequired,
    IReadOnlyList<ShortNoticeWindow> ShortNoticeMaintenance,
    IReadOnlyList<OutstandingIncidentReport> OutstandingReports)
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
    IDbContextFactory<SupportDbContext> dbFactory,
    TicketService tickets,
    TimeService time,
    ContractService contracts,
    EntKube.Contracts.Delivery.IDeliveryApi delivery,
    EntKube.Contracts.Fleet.IFleetApi fleet)
{
    /// <summary>
    /// The tenant's deployments that belong to one customer.
    ///
    /// <para><c>DeploymentSummary</c> carries no customer — and deliberately does not grow one —
    /// so the customer comes from Delivery's app list, which already has <c>CustomerId</c>. Same
    /// join <c>ErrorBudgetService</c> and <c>MessagingTab</c> make, for the same reason.</para>
    /// </summary>
    private async Task<IReadOnlyList<DeploymentSummary>> CustomerDeploymentsAsync(
        Guid tenantId, Guid customerId, CancellationToken ct)
    {
        HashSet<Guid> customerApps =
        [
            .. (await delivery.GetAppsAsync(tenantId, ct))
                .Where(a => a.CustomerId == customerId)
                .Select(a => a.Id)
        ];

        return
        [
            .. (await delivery.GetDeploymentsForTenantAsync(tenantId, ct))
                .Where(d => customerApps.Contains(d.AppId))
        ];
    }

    /// <summary>The day of the following month by which §16.1 requires the report.</summary>
    public const int DueOnDayOfMonth = 5;

    /// <summary>When the report for a month is due — the fifth of the month after it.</summary>
    public static DateTime DueDate(DateTime month)
    {
        (DateTime from, _) = TimeService.MonthBounds(month);
        return from.AddMonths(1).AddDays(DueOnDayOfMonth - 1);
    }

    /// <param name="tenantId">
    /// Whose customer. An input, not <c>customer.TenantId</c> read back off the row this method
    /// loads by id — which is what it used to do, so a customer id from anywhere in the
    /// installation produced that customer's full report: availability, ticket counts, timebank,
    /// penalties and the maintenance that touched their clusters. Both call sites have the tenant
    /// (each renders from a <c>selectedCustomer</c>), so nothing had to be plumbed to supply it.
    /// </param>
    public async Task<MonthlyReport> BuildAsync(
        Guid tenantId, Guid customerId, DateTime month, CancellationToken ct = default)
    {
        (DateTime from, DateTime to) = TimeService.MonthBounds(month);

        using SupportDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Customer? customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && c.TenantId == tenantId, ct);

        if (customer is null)
        {
            return new MonthlyReport(
                "", from, [], [], 0, default, default, 0, 0m, 0m, 0, false, [], []);
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
                // §4.1 again: a resolution target that did not apply was not overrun.
                band.Count(t => t.Ticket.SlaApplied
                                && t.Resolution.Breached
                                && !t.Ticket.ExcludedFromSla),
                average));
        }

        int penaltyDeviations = inMonth.Count(t => t.IsPenaltyDeviation);

        // §14.6 prices the penalty at 10% of the month's window fee, which comes from the
        // portfolio's widest window and the price list in force then.
        BaseFeeBreakdown fees = await contracts.CalculateBaseFeeAsync(customerId, from, ct);

        decimal penalty = fees.WindowFee * TicketSla.PenaltyFractionOfWindowFee * penaltyDeviations;

        // The clusters this customer actually runs on. A tenant hosts several customers,
        // and this report goes to one of them — so the windows below are narrowed to the
        // ones that could have touched them, or a customer's own monthly report would
        // name maintenance on somebody else's cluster.
        IReadOnlyList<DeploymentSummary> customerDeployments =
            await CustomerDeploymentsAsync(tenantId, customerId, ct);

        HashSet<Guid> clusters = [.. customerDeployments.Select(d => d.ClusterId).Distinct()];

        // Loaded once: availability needs them to drop covered samples, and the report
        // needs the planned ones that went ahead on short notice. A window with no
        // cluster is tenant-wide and reaches everyone.
        //
        // Asked of Fleet over the INTERVAL. The two older contract methods answer about an
        // instant, which a month cannot be assembled from: a window that opened in August and
        // closed in September is active at no instant this report names, and it still excludes
        // the downtime it covered. Narrowing to this customer's clusters stays here — the
        // contract hands over the tenant's windows and the null-cluster ones belong to everybody.
        List<MaintenanceWindowSummary> maintenance =
        [
            .. (await fleet.GetMaintenanceWindowsOverlappingAsync(tenantId, from, to, ct))
                .Where(w => w.ClusterId == null || clusters.Contains(w.ClusterId.Value))
        ];

        return new MonthlyReport(
            customer.Name,
            from,
            await AvailabilityAsync(db, tenantId, customerId, customerDeployments, from, to, maintenance, ct),
            byPriority,
            await db.Tickets.CountAsync(t =>
                t.CustomerId == customerId
                && t.ReportedAt < to
                && (t.ClosedAt == null || t.ClosedAt >= to), ct),
            await time.GetTimebankAsync(tenantId, customerId, from, ct),
            await time.GetCommittedHoursAsync(tenantId, customerId, from, to, ct),
            penaltyDeviations,
            penalty,
            fees.WindowFee,
            await PenaltiesEarlierThisYearAsync(customerId, from, ct),
            await ActionPlanRequiredAsync(customerId, from, ct),
            ShortNotice(maintenance),
            await OutstandingReportsAsync(db, customerId, to, ct));
    }

    /// <summary>
    /// P1s closed by the end of the month whose §14.6 report is still owed.
    ///
    /// <para>Counted against the whole history rather than the month, because an
    /// undelivered report does not stop being owed when the month turns over — and a
    /// closed ticket is out of the queue, so this is the last place it can be noticed.</para>
    /// </summary>
    private static async Task<List<OutstandingIncidentReport>> OutstandingReportsAsync(
        SupportDbContext db, Guid customerId, DateTime to, CancellationToken ct)
    {
        List<Ticket> closed = await db.Tickets.AsNoTracking()
            .Where(t => t.CustomerId == customerId
                        && t.Priority == TicketPriority.P1
                        && t.ClosedAt != null
                        && t.ClosedAt < to
                        && t.IncidentReportDeliveredAt == null)
            .OrderBy(t => t.ClosedAt)
            .ToListAsync(ct);

        return
        [
            .. closed.Select(t => new OutstandingIncidentReport(
                t.Number,
                t.Title,
                t.ClosedAt!.Value,
                BusinessCalendar.WorkingDaysDeadline(
                    t.ClosedAt.Value, TicketSla.IncidentReportWorkingDays)))
        ];
    }

    /// <summary>
    /// Planned maintenance in the month that went ahead on less than the agreed notice.
    /// Emergency windows are not listed: they owe none.
    /// </summary>
    private static List<ShortNoticeWindow> ShortNotice(List<MaintenanceWindowSummary> windows)
    {
        List<ShortNoticeWindow> rows = [];

        foreach (MaintenanceWindowSummary window in windows)
        {
            NoticeGiven notice = MaintenanceNotice.Assess(window);

            if (!notice.IsSufficient)
            {
                rows.Add(new ShortNoticeWindow(
                    window.Title, window.StartsAt, notice, window.CreatedBy));
            }
        }

        return rows;
    }

    /// <summary>
    /// Uptime per application, from the health snapshots. §14.1 samples every five minutes,
    /// so a month is around 8,600 samples per deployment; the count is reported alongside
    /// so a figure resting on twelve of them is visibly not worth much.
    ///
    /// <para><b>Maintenance is excluded.</b> A sample taken while a window covered the
    /// deployment's cluster is dropped from both halves of the fraction, because agreed
    /// downtime is not unavailability and counting it as such reports the customer a worse
    /// figure than they are entitled to. How many were dropped is carried alongside, so a
    /// month where most of the samples were excluded cannot present itself as a clean
    /// hundred percent.</para>
    /// </summary>
    private async Task<List<AvailabilityRow>> AvailabilityAsync(
        SupportDbContext db, Guid tenantId, Guid customerId,
        IReadOnlyList<DeploymentSummary> deployments, DateTime from, DateTime to,
        List<MaintenanceWindowSummary> maintenance, CancellationToken ct)
    {
        if (deployments.Count == 0)
        {
            return [];
        }

        List<Guid> ids = [.. deployments.Select(d => d.Id)];

        // Delivery's samples, over the half-open month: >= from and < to, which is what the
        // contract's optional exclusive upper bound exists for. §14.1 samples every five minutes,
        // so this is the caller the row-shaped method was written for — it re-slices per
        // application below and drops the ones a maintenance window covered.
        IReadOnlyList<DeploymentHealthSample> samples =
            await delivery.GetHealthSamplesAsync(tenantId, ids, from, to, ct);

        Dictionary<Guid, Guid> clusterOf = deployments.ToDictionary(d => d.Id, d => d.ClusterId);

        // A window with no cluster covers every cluster in the tenant.
        bool UnderMaintenance(Guid deploymentId, DateTime at) =>
            clusterOf.TryGetValue(deploymentId, out Guid cluster)
            && maintenance.Any(w =>
                (w.ClusterId is null || w.ClusterId == cluster)
                && w.StartsAt <= at
                && w.EndsAt >= at);

        List<SlaTarget> targets = await db.SlaTargets.AsNoTracking()
            .Where(t => t.CustomerId == customerId || t.CustomerId == null)
            .ToListAsync(ct);

        List<AvailabilityRow> rows = [];

        foreach (var group in deployments.GroupBy(d => new { d.AppId, d.AppName }))
        {
            HashSet<Guid> groupIds = [.. group.Select(d => d.Id)];
            var all = samples.Where(s => groupIds.Contains(s.DeploymentId)).ToList();

            var taken = all.Where(s => !UnderMaintenance(s.DeploymentId, s.SnapshotAt)).ToList();
            int excluded = all.Count - taken.Count;

            double? uptime = taken.Count == 0
                ? null
                : 100.0 * taken.Count(s => s.Health == ContractHealthStatus.Healthy) / taken.Count;

            // The most specific target wins: one for the application, else the customer's.
            SlaTarget? target = targets.FirstOrDefault(t => t.AppId == group.Key.AppId)
                                ?? targets.FirstOrDefault(t => t.AppId == null && t.CustomerId == customerId)
                                ?? targets.FirstOrDefault(t => t.AppId == null && t.CustomerId == null);

            rows.Add(new AvailabilityRow(
                group.Key.AppName, uptime, target?.TargetPercent, taken.Count, excluded));
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
