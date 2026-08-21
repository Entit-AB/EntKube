using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.SupplyChain;

/// <summary>
/// Holds the most recent supply-chain sweep per tenant, for the same reasons as the
/// drift cache: the report is an observation of live cluster and registry state, it is
/// expensive to produce, and the advisor must never pay for it during a page render.
/// Per-process and not persisted — see DriftScanCache for the trade-off this implies.
/// </summary>
public class SupplyChainScanCache
{
    private readonly ConcurrentDictionary<Guid, SupplyChainReport> reports = new();

    public SupplyChainReport? Get(Guid tenantId) => reports.GetValueOrDefault(tenantId);

    public void Set(Guid tenantId, SupplyChainReport report) => reports[tenantId] = report;

    public void Clear(Guid tenantId) => reports.TryRemove(tenantId, out _);
}

/// <summary>
/// Periodically joins running workloads to registry scan data so the advisor and the
/// Supply chain tab have an answer without either crawling every cluster on demand.
/// </summary>
public class SupplyChainScanService(
    IServiceScopeFactory scopeFactory,
    SupplyChainScanCache cache,
    ILogger<SupplyChainScanService> logger) : BackgroundService
{
    /// <summary>
    /// Trivy's vulnerability database updates roughly daily, and workloads change on
    /// deploy. Six hours keeps the picture current without re-walking every cluster and
    /// registry for findings that rarely move between scans.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>Offset from the drift sweep so the two do not walk every cluster at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(8);

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
                logger.LogWarning(ex, "Supply-chain scan cycle failed");
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
        var supplyChain = scope.ServiceProvider.GetRequiredService<SupplyChainService>();

        List<Guid> tenantIds;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                cache.Set(tenantId, await supplyChain.GetTenantReportAsync(tenantId, DateTime.UtcNow, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Supply-chain sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
