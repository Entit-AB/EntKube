using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Cost;

/// <summary>
/// Holds the most recent cost report per tenant. Same rationale as the drift and
/// supply-chain caches: the report queries every cluster's Prometheus, so a page
/// render must not trigger one. Per-process and not persisted.
/// </summary>
public class CostScanCache
{
    private readonly ConcurrentDictionary<Guid, CostReport> reports = new();

    public CostReport? Get(Guid tenantId) => reports.GetValueOrDefault(tenantId);

    public void Set(Guid tenantId, CostReport report) => reports[tenantId] = report;

    public void Clear(Guid tenantId) => reports.TryRemove(tenantId, out _);
}

/// <summary>Periodically recomputes each tenant's cost run rate.</summary>
public class CostScanService(
    IServiceScopeFactory scopeFactory,
    CostScanCache cache,
    IConfiguration configuration,
    ILogger<CostScanService> logger) : BackgroundService
{
    /// <summary>
    /// Hourly: this is a run-rate figure, and requests change on deploy rather than
    /// second to second. More often would spend Prometheus queries to redraw the same
    /// number.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Offset from the drift and supply-chain sweeps so they do not all fire at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(11);

    /// <summary>
    /// How long the ledger is kept. Generous by default — a daily grain makes even a
    /// large fleet's year a modest table, and comparing a month against the same month
    /// last year is the reason anyone keeps cost history at all.
    /// </summary>
    private const int DefaultRetentionDays = 800;

    /// <summary>When the ledger was last pruned. Pruning is a daily job riding the hourly sweep.</summary>
    private DateTime lastPrune = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cost scan cycle failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var costs = scope.ServiceProvider.GetRequiredService<CostReportService>();
        var rates = scope.ServiceProvider.GetRequiredService<CostRateService>();
        var ledger = scope.ServiceProvider.GetRequiredService<CostLedgerWriter>();

        List<Guid> tenantIds;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                CostReport report = await costs.GetTenantReportAsync(tenantId, now, ct);
                cache.Set(tenantId, report);

                // The run rate is what this sweep measured; the ledger is what it means
                // over the hours since the last one. Booked here rather than inside the
                // report service so that a page's "Recalculate" button — which produces
                // the same report on demand — cannot accrue cost by being clicked.
                Dictionary<Guid, ClusterCostRate> priced =
                    (await rates.ListAsync(tenantId, ct)).ToDictionary(r => r.ClusterId);

                CostLedgerWriteResult written =
                    await ledger.RecordAsync(tenantId, report, priced, now, ct);

                if (written.GapHours > 0m)
                {
                    logger.LogInformation(
                        "Cost ledger for tenant {TenantId}: billed {Billed:F2} h, {Gap:F2} h unmeasured",
                        tenantId, written.BilledHours, written.GapHours);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cost sweep failed for tenant {TenantId}", tenantId);
            }
        }

        await PruneAsync(ledger, ct);
    }

    /// <summary>
    /// Drops ledger rows past the retention horizon, once a day. Failure is logged and
    /// swallowed: an unpruned ledger is a table that is larger than it needs to be, which
    /// is not a reason to fail the sweep that produces the figures.
    /// </summary>
    private async Task PruneAsync(CostLedgerWriter ledger, CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        if (now - lastPrune < TimeSpan.FromHours(24))
        {
            return;
        }

        try
        {
            int retentionDays = configuration.GetValue("Cost:LedgerRetentionDays", DefaultRetentionDays);
            await ledger.PruneAsync(retentionDays, now, ct);
            lastPrune = now;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cost ledger prune failed");
        }
    }
}

/// <summary>Reads and writes the per-cluster price sheets.</summary>
public class CostRateService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<List<ClusterCostRate>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ClusterCostRates
            .AsNoTracking()
            .Where(r => r.Cluster.TenantId == tenantId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Creates or updates a cluster's price sheet. Scoped by tenant as well as cluster so a
    /// tenant cannot price another tenant's cluster.
    /// </summary>
    public async Task<ClusterCostRate?> SaveAsync(
        Guid tenantId, Guid clusterId, ClusterCostRate values, string? updatedBy,
        CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        bool owned = await db.KubernetesClusters.AnyAsync(c => c.Id == clusterId && c.TenantId == tenantId, ct);
        if (!owned)
        {
            return null;
        }

        ClusterCostRate? rate = await db.ClusterCostRates.FirstOrDefaultAsync(r => r.ClusterId == clusterId, ct);
        if (rate is null)
        {
            rate = new ClusterCostRate { Id = Guid.NewGuid(), ClusterId = clusterId };
            db.ClusterCostRates.Add(rate);
        }

        // Negative prices would produce credits that make no sense and would corrupt the
        // proportional overhead split, so they are clamped rather than trusted.
        rate.CpuCoreHourCost = Math.Max(0m, values.CpuCoreHourCost);
        rate.MemoryGiBHourCost = Math.Max(0m, values.MemoryGiBHourCost);
        rate.StorageGiBMonthCost = Math.Max(0m, values.StorageGiBMonthCost);
        rate.ClusterMonthlyOverhead = Math.Max(0m, values.ClusterMonthlyOverhead);
        rate.LoadBalancerMonthlyCost = Math.Max(0m, values.LoadBalancerMonthlyCost);
        rate.PublicIpMonthlyCost = Math.Max(0m, values.PublicIpMonthlyCost);
        rate.Currency = string.IsNullOrWhiteSpace(values.Currency) ? "USD" : values.Currency.Trim().ToUpperInvariant();
        rate.ChargeOnRequests = values.ChargeOnRequests;
        rate.ChargeIdleCapacity = values.ChargeIdleCapacity;
        rate.UpdatedAt = DateTime.UtcNow;
        rate.UpdatedBy = updatedBy;

        await db.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ClusterCostRate? rate = await db.ClusterCostRates
            .FirstOrDefaultAsync(r => r.ClusterId == clusterId && r.Cluster.TenantId == tenantId, ct);

        if (rate is null) return false;

        db.ClusterCostRates.Remove(rate);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
