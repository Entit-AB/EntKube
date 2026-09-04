using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

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

    public decimal Change => Cost - PreviousCost;

    /// <summary>
    /// Relative change, or null when there is nothing to compare against — a line that
    /// did not exist last period has no percentage, and reporting one as +100% or ∞
    /// would invent a trend out of an appearance.
    /// </summary>
    public double? ChangeFraction =>
        PreviousCost > 0m ? (double)(Change / PreviousCost) : null;

    /// <summary>True when this line is new in this window.</summary>
    public bool IsNew => PreviousCost == 0m && Cost > 0m;

    /// <summary>True when this line was there last window and is gone from this one.</summary>
    public bool IsGone => Cost == 0m && PreviousCost > 0m;

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

    public decimal Change => TotalCost - PreviousTotalCost;

    public double? ChangeFraction =>
        PreviousTotalCost > 0m ? (double)(Change / PreviousTotalCost) : null;

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
    /// How much of the window the ledger actually accounts for, 0–1. An incomplete window
    /// under-states cost, so this is reported beside every total rather than left for
    /// someone to work out from the chart.
    /// </summary>
    public double Completeness =>
        ExpectedHours > 0m ? Math.Min(1d, (double)(CoveredHours / ExpectedHours)) : 1d;

    public bool IsComplete => Completeness >= 0.99d;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Average cost per day over the days actually covered — the basis for a projection.</summary>
    public decimal DailyAverage
    {
        get
        {
            decimal days = CoveredHours > 0m && ExpectedHours > 0m
                ? (decimal)Daily.Count * (decimal)Completeness
                : Daily.Count;

            return days > 0m ? TotalCost / days : 0m;
        }
    }
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
public class CostLedgerService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    /// <summary>
    /// Whether the ledger holds anything at all for this tenant. Distinguishes "nothing
    /// has ever been recorded" — a fleet nobody has priced, or a plane that has not run
    /// long enough — from "nothing happened in the window you asked about", which is a
    /// real answer.
    /// </summary>
    public async Task<bool> HasAnyAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.CostLedgerEntries.AnyAsync(e => e.TenantId == tenantId, ct);
    }

    /// <summary>The first day the ledger recorded anything for this tenant.</summary>
    public async Task<DateTime?> GetLedgerStartAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

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
        DateTime previousStart = start.AddDays(-days);

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

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

        List<CoverageRow> coverage = await db.CostLedgerCoverages
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Day >= start && c.Day <= end)
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

        Dictionary<DateTime, (decimal Covered, decimal Expected)> byDay =
            ExpectedCoverage(coverage, openedOn, now);

        List<CostHistoryPoint> daily = [];
        for (DateTime day = start; day <= end; day = day.AddDays(1))
        {
            (decimal covered, decimal expected) = byDay.GetValueOrDefault(day);

            daily.Add(new CostHistoryPoint
            {
                Day = day,
                Cost = current.Where(r => r.Day == day).Sum(r => r.Cost),
                CoveredHours = covered,
                ExpectedHours = expected,
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
        decimal gapHours = coverage.Sum(c => c.GapHours);

        if (expectedHours > 0m && coveredHours < expectedHours * 0.99m)
        {
            decimal missing = expectedHours - coveredHours;
            warnings.Add(
                $"{missing:F0} of {expectedHours:F0} cluster-hours in this period were never measured, "
                + "so the cost below is lower than what was actually incurred.");
        }

        return new CostHistoryReport
        {
            From = start,
            To = end,
            GroupBy = groupBy,
            Currency = currencies.Count == 1 ? currencies.First() : currencies.Count == 0 ? "USD" : "—",
            Daily = daily,
            Groups = Group(current, previous, groupBy),
            TotalCost = current.Sum(r => r.Cost),
            PreviousTotalCost = previous.Sum(r => r.Cost),
            UnattributedCost = current.Where(r => r.CustomerId is null).Sum(r => r.Cost),
            MultiAppCost = current.Where(r => r.IsMultiApp).Sum(r => r.Cost),
            PlatformCost = platformCost,
            SharedChargedOutCost = current.Sum(r => r.SharedCost),
            CoveredHours = coveredHours,
            ExpectedHours = expectedHours,
            GapHours = gapHours,
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

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

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

        Dictionary<DateTime, (decimal Covered, decimal Expected)> byDay =
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
        ApplicationDbContext db, Guid tenantId, CancellationToken ct)
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
    private static Dictionary<DateTime, (decimal Covered, decimal Expected)> ExpectedCoverage(
        IReadOnlyList<CoverageRow> coverage, IReadOnlyDictionary<Guid, DateTime> openedOn, DateTime now)
    {
        Dictionary<DateTime, (decimal, decimal)> byDay = [];

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

            (decimal covered, decimal existingExpected) = byDay.GetValueOrDefault(row.Day);
            byDay[row.Day] = (covered + row.CoveredHours, existingExpected + expected);
        }

        return byDay;
    }

    private static IReadOnlyList<CostHistoryGroup> Group(
        List<LedgerRow> current, List<LedgerRow> previous, CostGroupBy groupBy)
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
