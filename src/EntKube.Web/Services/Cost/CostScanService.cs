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

        List<Guid> tenantIds;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                cache.Set(tenantId, await costs.GetTenantReportAsync(tenantId, DateTime.UtcNow, ct));
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
