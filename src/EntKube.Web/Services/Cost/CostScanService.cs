using System.Collections.Concurrent;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

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
        using IDisposable unattended = scope.DeclareUnattended("cost-scan");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CostDbContext>>();
        var costs = scope.ServiceProvider.GetRequiredService<CostReportService>();
        var rates = scope.ServiceProvider.GetRequiredService<CostRateService>();
        var ledger = scope.ServiceProvider.GetRequiredService<CostLedgerWriter>();

        List<Guid> tenantIds;
        await using (CostDbContext db = await dbFactory.CreateDbContextAsync(ct))
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
/// <summary>
/// A price sheet as the public API's PUT describes one: four required prices, and the rest
/// optional so that <b>omitting a field keeps what is stored</b> rather than overwriting it.
///
/// <para>⚠️ Every optional member must be nullable, and that is the whole point of the type. A
/// <c>bool</c> cannot express "not supplied" — <c>false</c> and absent are the same value — which
/// is why <c>ChargeIdleCapacity</c> was silently reset to the entity default on every API write
/// while being neither readable nor writable through the API at all.</para>
/// </summary>
public sealed record CostRatePatch(
    decimal CpuCoreHourCost,
    decimal MemoryGiBHourCost,
    decimal StorageGiBMonthCost,
    decimal ClusterMonthlyOverhead,
    string? Currency = null,
    bool? ChargeOnRequests = null,
    bool? ChargeIdleCapacity = null,
    decimal? LoadBalancerMonthlyCost = null,
    decimal? PublicIpMonthlyCost = null);

public class CostRateService(IDbContextFactory<CostDbContext> dbFactory)
{
    public async Task<List<ClusterCostRate>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ClusterCostRates
            .AsNoTracking()
            .Where(r => r.Cluster.TenantId == tenantId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// One cluster's price sheet, or null when it has none.
    ///
    /// <para>⚠️ Tenant-scoped through the cluster, like <see cref="ListAsync"/>: a cluster id on
    /// its own is not authorisation. The predicate is applied in the database rather than by
    /// filtering <see cref="ListAsync"/>'s result, which is what the public API route did — it
    /// fetched every rate the tenant had in order to return one.</para>
    /// </summary>
    public async Task<ClusterCostRate?> GetAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        return await db.ClusterCostRates
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClusterId == clusterId && r.Cluster.TenantId == tenantId, ct);
    }

    /// <summary>
    /// Creates or updates a cluster's price sheet. Scoped by tenant as well as cluster so a
    /// tenant cannot price another tenant's cluster.
    /// </summary>
    public async Task<ClusterCostRate?> SaveAsync(
        Guid tenantId, Guid clusterId, ClusterCostRate values, string? updatedBy,
        CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

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

    /// <summary>
    /// Creates or updates a price sheet from a <b>partial</b> set of values: a null optional field
    /// keeps whatever is stored, and on a row that does not exist yet keeps the entity's own
    /// documented default.
    ///
    /// <para><b>Why this exists beside <see cref="SaveAsync"/>.</b> That one takes a whole
    /// <c>ClusterCostRate</c> and writes every field, which is right for the settings page: the
    /// form shows all nine, so the operator is replacing a sheet they can see. The public API's PUT
    /// is not that — its body type carries four required prices and the rest optional, for clients
    /// written before a field existed — and expressing "leave it alone" through a full entity is
    /// impossible, because an unset property is indistinguishable from one deliberately set to its
    /// default. That is exactly how <c>ChargeIdleCapacity</c> came to be reset by every API write.
    /// </para>
    ///
    /// <para>⚠️ <b>The merge happens here, in the context that loads the row</b>, not in the
    /// caller. A caller that read the sheet and then wrote it back would leave a window between
    /// the two in which two concurrent PUTs, each omitting a different field, each write back the
    /// other's stale value.</para>
    ///
    /// <para>New rows need no special case: <c>Currency</c>, <c>ChargeOnRequests</c> and
    /// <c>ChargeIdleCapacity</c> carry initializers on the entity, so "keep what is there" already
    /// means "take the documented default" for a sheet that does not exist yet.</para>
    /// </summary>
    public async Task<ClusterCostRate?> PatchAsync(
        Guid tenantId, Guid clusterId, CostRatePatch patch, string? updatedBy,
        CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        // Same refusal as SaveAsync, for the same reason: a crafted cluster id must not let one
        // tenant price another tenant's cluster.
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

        // Required, so always written. Clamped for the reason SaveAsync gives: a negative price
        // produces credits that corrupt the proportional overhead split.
        rate.CpuCoreHourCost = Math.Max(0m, patch.CpuCoreHourCost);
        rate.MemoryGiBHourCost = Math.Max(0m, patch.MemoryGiBHourCost);
        rate.StorageGiBMonthCost = Math.Max(0m, patch.StorageGiBMonthCost);
        rate.ClusterMonthlyOverhead = Math.Max(0m, patch.ClusterMonthlyOverhead);

        // Optional: absent means keep. Never "absent means zero", which is what the PUT handler
        // did and what the input type's own comment said it did not.
        if (patch.LoadBalancerMonthlyCost is decimal lb) rate.LoadBalancerMonthlyCost = Math.Max(0m, lb);
        if (patch.PublicIpMonthlyCost is decimal ip) rate.PublicIpMonthlyCost = Math.Max(0m, ip);
        if (patch.ChargeOnRequests is bool onRequests) rate.ChargeOnRequests = onRequests;
        if (patch.ChargeIdleCapacity is bool idle) rate.ChargeIdleCapacity = idle;

        if (!string.IsNullOrWhiteSpace(patch.Currency))
        {
            rate.Currency = patch.Currency.Trim().ToUpperInvariant();
        }

        rate.UpdatedAt = DateTime.UtcNow;
        rate.UpdatedBy = updatedBy;

        await db.SaveChangesAsync(ct);
        return rate;
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        await using CostDbContext db = await dbFactory.CreateDbContextAsync(ct);

        ClusterCostRate? rate = await db.ClusterCostRates
            .FirstOrDefaultAsync(r => r.ClusterId == clusterId && r.Cluster.TenantId == tenantId, ct);

        if (rate is null) return false;

        db.ClusterCostRates.Remove(rate);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
