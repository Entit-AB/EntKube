using System.Collections.Concurrent;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Dr;

/// <summary>
/// Holds the most recent DR sweep per tenant. Same rationale as the drift, supply-chain
/// and cost caches: reading Velero state walks every cluster, so a page render must not
/// trigger it. Per-process and not persisted.
/// </summary>
public class DrScanCache
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<ClusterDrStatus>> reports = new();

    public IReadOnlyList<ClusterDrStatus>? Get(Guid tenantId) => reports.GetValueOrDefault(tenantId);

    public void Set(Guid tenantId, IReadOnlyList<ClusterDrStatus> statuses) => reports[tenantId] = statuses;

    public void Clear(Guid tenantId) => reports.TryRemove(tenantId, out _);
}

/// <summary>Periodically refreshes each tenant's disaster-recovery posture.</summary>
public class DrScanService(
    IServiceScopeFactory scopeFactory,
    DrScanCache cache,
    ILogger<DrScanService> logger) : BackgroundService
{
    /// <summary>
    /// Hourly. Backups complete on a schedule measured in hours, and the finding that
    /// matters most — "no usable backup in 36 hours" — cannot become true faster than that.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Offset from the other sweeps so they do not all walk the fleet at once.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(14);

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
                logger.LogWarning(ex, "DR scan cycle failed");
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
        var velero = scope.ServiceProvider.GetRequiredService<VeleroService>();

        List<Guid> tenantIds;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                cache.Set(tenantId, await velero.GetTenantStatusAsync(tenantId, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "DR sweep failed for tenant {TenantId}", tenantId);
            }
        }
    }
}
