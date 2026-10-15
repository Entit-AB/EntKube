using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Services.ClusterChanges;

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
            using IDisposable unattended = scope.DeclareUnattended("uptime-tracking");
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            using ApplicationDbContext db = dbFactory.CreateDbContext();

            DateTime cutoff = DateTime.UtcNow - RetentionPeriod;
            DateTime now = DateTime.UtcNow;

            // ⚠️ The prune stays installation-wide ON PURPOSE, and this is the one place in the
            // sweep conversion (docs/decomposition.md §4.0.1.2) where per-tenant is the wrong
            // answer. It deletes by timestamp alone: no tenant is named, nothing is decided about
            // one, and no cluster is reached. Scoping it would turn one ExecuteDeleteAsync into one
            // per tenant, each joining snapshots -> deployment -> app -> customer to find a tenant
            // it then ignores. That is strictly more work for no authorization gain. Retention is
            // housekeeping on a shared table, not an act performed on a tenant's behalf.
            int deleted = await db.DeploymentHealthSnapshots
                .Where(s => s.SnapshotAt < cutoff)
                .ExecuteDeleteAsync(ct);

            int routeHistDeleted = await db.ExternalRouteHealthHistories
                .Where(h => h.CheckedAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0 || routeHistDeleted > 0)
                logger.LogDebug("Pruned {Snaps} health snapshots and {Routes} route history entries older than {Days} days",
                    deleted, routeHistDeleted, (int)RetentionPeriod.TotalDays);

            // The snapshot half IS per tenant. It used to select every deployment in the
            // installation in one query; nothing was authorized against a tenant then and nothing
            // is now, so what this buys is isolation rather than safety — a tenant whose rows fail
            // to write no longer takes the whole cycle down with it, which is what the worker
            // extraction needs. Said plainly because this sweep and the ones that act AS a tenant
            // are different in kind, and treating them alike invites doing the wrong one first.
            List<Guid> tenants = await db.Tenants.Select(x => x.Id).ToListAsync(ct);

            int recorded = 0;

            foreach (Guid tenantId in tenants)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    recorded += await SnapshotTenantAsync(db, tenantId, now, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "Uptime snapshot failed for tenant {TenantId}; the other tenants continue",
                        tenantId);
                }
            }

            logger.LogDebug("Recorded {Count} health snapshots at {Time:u}", recorded, now);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uptime tracking snapshot failed");
        }
    }

    /// <summary>
    /// Snapshots one tenant's deployment health. Saved per tenant so a failure is contained to the
    /// tenant that caused it.
    ///
    /// <para>Public and static so the scoping can be asserted without running the sweep: the
    /// previous version selected every deployment in the installation, and a test that only counted
    /// the rows written would pass against either. Tenancy reaches a deployment through its app's
    /// customer, which is the long way round and exactly where a hand-written filter goes wrong.</para>
    /// </summary>
    public static IQueryable<AppDeployment> DeploymentsFor(ApplicationDbContext db, Guid tenantId)
        => db.AppDeployments.Where(d => d.App.Customer.TenantId == tenantId);

    private static async Task<int> SnapshotTenantAsync(
        ApplicationDbContext db, Guid tenantId, DateTime now, CancellationToken ct)
    {
        List<(Guid Id, HealthStatus Health, SyncStatus Sync)> deployments =
            await DeploymentsFor(db, tenantId)
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
        return deployments.Count;
    }
}
