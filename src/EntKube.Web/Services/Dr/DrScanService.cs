using System.Collections.Concurrent;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;
using EntKube.Web.Services.Outbound;

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
        using IDisposable unattended = scope.DeclareUnattended("dr-scan");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleetDbContext>>();
        var velero = scope.ServiceProvider.GetRequiredService<VeleroService>();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        List<Guid> tenantIds;
        await using (FleetDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            try
            {
                // Read before the write, as the drift sweep does: the cached statuses are the
                // previous sweep, and a gap that was already open is not news.
                IReadOnlyList<ClusterDrStatus>? previous = cache.Get(tenantId);

                List<ClusterDrStatus> statuses = await velero.GetTenantStatusAsync(tenantId, ct);
                cache.Set(tenantId, statuses);

                await NotifyNewGapsAsync(notifications, tenantId, previous, statuses, ct);
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

    /// <summary>
    /// Tells the tenant's channels about backup gaps that opened <em>since the last sweep</em>. The
    /// rule, including why an Info gap is left to the digest, is on <see cref="ScanTransitions"/>.
    /// </summary>
    private async Task NotifyNewGapsAsync(
        NotificationService notifications, Guid tenantId,
        IReadOnlyList<ClusterDrStatus>? previous, IReadOnlyList<ClusterDrStatus> current,
        CancellationToken ct)
    {
        if (ScanTransitions.DrMessage(ScanTransitions.NewlyOpenedGaps(previous, current, DateTime.UtcNow))
            is not (string title, string body, string severity))
        {
            return;
        }

        try
        {
            await notifications.DispatchDigestAsync(tenantId, title, body, severity, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The statuses are cached either way, so a failed notification costs the alert and not
            // the finding.
            logger.LogWarning(ex, "Could not notify on new DR gaps for tenant {TenantId}", tenantId);
        }
    }
}
