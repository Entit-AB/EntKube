using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Cost;

/// <summary>What one accrual run booked. Returned for logging and so the rules can be tested.</summary>
public sealed record CostLedgerWriteResult
{
    /// <summary>Clusters that accrued cost this run.</summary>
    public int ClustersAccrued { get; init; }

    /// <summary>
    /// Clusters seen for the first time. They set a cursor and bill nothing — there is no
    /// measurement covering the time before them.
    /// </summary>
    public int ClustersStarted { get; init; }

    /// <summary>
    /// Clusters whose period had already been claimed by another writer. Not an error:
    /// it is the mechanism working.
    /// </summary>
    public int ClustersAlreadyClaimed { get; init; }

    /// <summary>Ledger rows created or added to.</summary>
    public int EntriesWritten { get; init; }

    /// <summary>Hours billed across all clusters.</summary>
    public decimal BilledHours { get; init; }

    /// <summary>Hours that elapsed without a measurement close enough to price them.</summary>
    public decimal GapHours { get; init; }
}

/// <summary>
/// Books each cost sweep into the ledger.
///
/// <para>The run rate is a projection; this is the record of what was actually incurred.
/// Every sweep bills the hours that have elapsed since the previous one at the rate it
/// just measured, into the row for the UTC day they fell in.</para>
///
/// <para>The one thing this must never do is bill the same hour twice, so every cluster's
/// period is <em>claimed</em> before it is written — see <see cref="CostLedgerCursor"/>.
/// The opposite failure, an hour lost, is left visible in
/// <see cref="CostLedgerCoverage"/> rather than papered over.</para>
/// </summary>
public class CostLedgerWriter(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<CostLedgerWriter> logger)
{
    /// <summary>
    /// Records a completed sweep. <paramref name="pricedClusterIds"/> is every cluster the
    /// tenant could have been billed for — clusters with a price sheet — so that one which
    /// produced no measurement is recorded as an unmeasured period rather than vanishing
    /// from the history as though it had not existed.
    /// </summary>
    public async Task<CostLedgerWriteResult> RecordAsync(
        Guid tenantId,
        CostReport report,
        IReadOnlyDictionary<Guid, ClusterCostRate> pricedClusters,
        DateTime now,
        CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        Dictionary<Guid, List<NamespaceCost>> measured = report.Namespaces
            .GroupBy(n => n.ClusterId)
            .ToDictionary(g => g.Key, g => g.ToList());

        int accrued = 0, started = 0, claimedAway = 0, entries = 0;
        decimal billedHours = 0m, gapHours = 0m;

        foreach ((Guid clusterId, ClusterCostRate rate) in pricedClusters)
        {
            CostLedgerCursor? cursor = await db.CostLedgerCursors
                .FirstOrDefaultAsync(c => c.ClusterId == clusterId, ct);

            if (cursor is null)
            {
                // First sight of this cluster. Open the ledger at now and bill nothing:
                // an opening balance derived from the first reading would look exactly
                // like a measured one in every report thereafter.
                db.CostLedgerCursors.Add(new CostLedgerCursor
                {
                    Id = Guid.NewGuid(),
                    ClusterId = clusterId,
                    LastSampleAt = now,
                });

                try
                {
                    await db.SaveChangesAsync(ct);
                    started++;
                }
                catch (DbUpdateException)
                {
                    // Another writer opened it in the meantime; theirs is as good as ours.
                    db.ChangeTracker.Clear();
                }

                continue;
            }

            DateTime seen = cursor.LastSampleAt;
            AccrualSpan span = CostAccrual.Measure(seen, now);

            if (!span.HasAnything)
            {
                continue;
            }

            // Claim the period before writing anything. A writer that loses this race
            // books nothing at all, which is the only safe direction to fail in: the
            // hour can be seen missing from coverage, where a double-booking would just
            // be a wrong number on an invoice with nothing to distinguish it from a
            // right one.
            int claimed = await db.CostLedgerCursors
                .Where(c => c.ClusterId == clusterId && c.LastSampleAt == seen)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSampleAt, now), ct);

            if (claimed == 0)
            {
                claimedAway++;
                continue;
            }

            db.ChangeTracker.Clear();

            IReadOnlyList<DaySlice> billable = CostAccrual.SplitByDay(span.Start, span.End);
            IReadOnlyList<DaySlice> gaps = CostAccrual.SplitByDay(span.GapStart, span.Start);

            List<NamespaceCost> namespaces = measured.GetValueOrDefault(clusterId, []);
            string clusterName = namespaces.Count > 0
                ? namespaces[0].ClusterName
                : await db.KubernetesClusters
                    .Where(c => c.Id == clusterId)
                    .Select(c => c.Name)
                    .FirstOrDefaultAsync(ct) ?? "—";

            if (namespaces.Count == 0)
            {
                // Priced, but nothing came back from it this run — unreachable, or its
                // metrics stack is down. The period is recorded as unmeasured in full:
                // reporting it as a cheap day would be a lie in the direction of an
                // under-stated bill, and reporting nothing at all would hide the outage.
                foreach (DaySlice slice in billable.Concat(gaps))
                {
                    CostLedgerCoverage coverage =
                        await GetOrCreateCoverageAsync(db, tenantId, clusterId, clusterName, slice.Day, now, ct);
                    coverage.GapHours += slice.Hours;
                    coverage.UpdatedAt = now;
                    gapHours += slice.Hours;
                }

                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                continue;
            }

            List<DateTime> days = [.. billable.Concat(gaps).Select(s => s.Day).Distinct()];

            Dictionary<(string Namespace, DateTime Day), CostLedgerEntry> existing =
                await db.CostLedgerEntries
                    .Where(e => e.ClusterId == clusterId && days.Contains(e.Day))
                    .ToDictionaryAsync(e => (e.Namespace, e.Day), ct);

            foreach (DaySlice slice in billable)
            {
                foreach (NamespaceCost ns in namespaces)
                {
                    if (!existing.TryGetValue((ns.Namespace, slice.Day), out CostLedgerEntry? entry))
                    {
                        entry = new CostLedgerEntry
                        {
                            Id = Guid.NewGuid(),
                            TenantId = tenantId,
                            ClusterId = clusterId,
                            Namespace = ns.Namespace,
                            Day = slice.Day,
                        };

                        db.CostLedgerEntries.Add(entry);
                        existing[(ns.Namespace, slice.Day)] = entry;
                        entries++;
                    }

                    // Ownership is refreshed from the latest sample of the day. A
                    // namespace that changed hands mid-day is labelled with where it
                    // ended up — the money is the same either way, since it is the same
                    // namespace, and splitting a day at the moment a record changed would
                    // put more precision on the label than the daily grain supports.
                    entry.ClusterName = ns.ClusterName;
                    entry.CustomerId = ns.CustomerId;
                    entry.CustomerName = ns.CustomerName;
                    entry.AppId = ns.AppId;
                    entry.AppName = ns.Apps.Count == 1 ? ns.Apps[0].AppName : null;
                    entry.EnvironmentName = ns.EnvironmentName;
                    entry.IsMultiApp = ns.IsMultiApp;
                    entry.IsRedistributed = ns.IsRedistributed;
                    entry.Currency = rate.Currency;
                    entry.ChargedOnRequests = rate.ChargeOnRequests;
                    entry.UpdatedAt = now;

                    entry.Hours += slice.Hours;

                    entry.CpuCoreHours += CostAccrual.AccrueQuantity(ns.CpuCores, slice.Hours);
                    entry.MemoryGiBHours += CostAccrual.AccrueQuantity(ns.MemoryGiB, slice.Hours);
                    entry.StorageGiBHours += CostAccrual.AccrueQuantity(ns.StorageGiB, slice.Hours);
                    entry.LoadBalancerHours += CostAccrual.AccrueQuantity(ns.LoadBalancers, slice.Hours);
                    entry.PublicIpHours += CostAccrual.AccrueQuantity(ns.PublicIps, slice.Hours);

                    entry.CpuCost += CostAccrual.Accrue(ns.CpuMonthlyCost, slice.Hours);
                    entry.MemoryCost += CostAccrual.Accrue(ns.MemoryMonthlyCost, slice.Hours);
                    entry.StorageCost += CostAccrual.Accrue(ns.StorageMonthlyCost, slice.Hours);
                    entry.NetworkCost += CostAccrual.Accrue(ns.NetworkMonthlyCost, slice.Hours);
                    entry.SharedCost += CostAccrual.Accrue(ns.SharedMonthlyCost, slice.Hours);
                }

                CostLedgerCoverage coverage =
                    await GetOrCreateCoverageAsync(db, tenantId, clusterId, clusterName, slice.Day, now, ct);
                coverage.CoveredHours += slice.Hours;
                coverage.SampleCount++;
                coverage.UpdatedAt = now;
                billedHours += slice.Hours;
            }

            foreach (DaySlice slice in gaps)
            {
                CostLedgerCoverage coverage =
                    await GetOrCreateCoverageAsync(db, tenantId, clusterId, clusterName, slice.Day, now, ct);
                coverage.GapHours += slice.Hours;
                coverage.UpdatedAt = now;
                gapHours += slice.Hours;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                accrued++;
            }
            catch (DbUpdateException ex)
            {
                // The period is already claimed, so it will not be retried. That hour is
                // simply absent from the ledger, and coverage for the day will be short
                // by it — which is the visible, under-billing failure, not the invisible,
                // over-billing one.
                logger.LogWarning(ex,
                    "Cost ledger write failed for cluster {ClusterId}; {Hours:F2} h are not accounted for",
                    clusterId, span.BillableHours);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        return new CostLedgerWriteResult
        {
            ClustersAccrued = accrued,
            ClustersStarted = started,
            ClustersAlreadyClaimed = claimedAway,
            EntriesWritten = entries,
            BilledHours = billedHours,
            GapHours = gapHours,
        };
    }

    /// <summary>
    /// Deletes ledger rows past the retention horizon.
    ///
    /// The ledger is daily, so a year of a large fleet is tens of thousands of rows and
    /// the default horizon is generous enough to compare a month against the same month
    /// last year. Deleting is unconditional and irreversible: this is the one place the
    /// history is destroyed, so it is a single explicit call rather than a side effect of
    /// writing.
    /// </summary>
    public async Task<int> PruneAsync(int retentionDays, DateTime now, CancellationToken ct = default)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        DateTime cutoff = CostAccrual.DayOf(now).AddDays(-retentionDays);

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        int removed = await db.CostLedgerEntries.Where(e => e.Day < cutoff).ExecuteDeleteAsync(ct);
        removed += await db.CostLedgerCoverages.Where(c => c.Day < cutoff).ExecuteDeleteAsync(ct);

        if (removed > 0)
        {
            logger.LogInformation(
                "Pruned {Count} cost ledger rows older than {Cutoff:yyyy-MM-dd}", removed, cutoff);
        }

        return removed;
    }

    private static async Task<CostLedgerCoverage> GetOrCreateCoverageAsync(
        ApplicationDbContext db, Guid tenantId, Guid clusterId, string clusterName,
        DateTime day, DateTime now, CancellationToken ct)
    {
        CostLedgerCoverage? coverage = db.ChangeTracker
            .Entries<CostLedgerCoverage>()
            .Select(e => e.Entity)
            .FirstOrDefault(c => c.ClusterId == clusterId && c.Day == day);

        coverage ??= await db.CostLedgerCoverages
            .FirstOrDefaultAsync(c => c.ClusterId == clusterId && c.Day == day, ct);

        if (coverage is null)
        {
            coverage = new CostLedgerCoverage
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ClusterId = clusterId,
                Day = day,
                UpdatedAt = now,
            };

            db.CostLedgerCoverages.Add(coverage);
        }

        coverage.ClusterName = clusterName;
        return coverage;
    }
}
