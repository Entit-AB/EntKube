using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;

namespace EntKube.Web.Services;

public class UptimeTrackingService(
    IServiceScopeFactory scopeFactory,
    ILogger<UptimeTrackingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TakeSnapshotsAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task TakeSnapshotsAsync(CancellationToken ct)
    {
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            DateTime cutoff = DateTime.UtcNow - RetentionPeriod;
            DateTime now = DateTime.UtcNow;

            // Prune old snapshots and route history to control table growth
            int deleted = await db.DeploymentHealthSnapshots
                .Where(s => s.SnapshotAt < cutoff)
                .ExecuteDeleteAsync(ct);

            int routeHistDeleted = await db.ExternalRouteHealthHistories
                .Where(h => h.CheckedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0 || routeHistDeleted > 0)
                logger.LogDebug("Pruned {Snaps} health snapshots and {Routes} route history entries older than {Days} days",
                    deleted, routeHistDeleted, (int)RetentionPeriod.TotalDays);

            // Read current deployment health state from DB (no K8s call needed)
            List<(Guid Id, HealthStatus Health, SyncStatus Sync)> deployments = await db.AppDeployments
                .Select(d => new ValueTuple<Guid, HealthStatus, SyncStatus>(d.Id, d.HealthStatus, d.SyncStatus))
                .ToListAsync(ct);

            foreach ((Guid id, HealthStatus health, SyncStatus sync) in deployments)
            {
                db.DeploymentHealthSnapshots.Add(new DeploymentHealthSnapshot
                {
                    DeploymentId = id,
                    HealthStatus = health,
                    SyncStatus = sync,
                    SnapshotAt = now
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogDebug("Recorded {Count} health snapshots at {Time:u}", deployments.Count, now);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uptime tracking snapshot failed");
        }
    }
}
