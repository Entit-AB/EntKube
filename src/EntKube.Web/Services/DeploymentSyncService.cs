using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that periodically reconciles live Kubernetes state into
/// the database, keeping DeploymentResource rows and AppDeployment sync/health
/// status current without requiring manual user interaction.
///
/// Runs every 2 minutes. Processes each deployment sequentially with a short
/// delay between them to avoid bursting all cluster API connections at once.
/// Clusters without a kubeconfig are skipped automatically.
/// </summary>
public class DeploymentSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InterDeploymentDelay = TimeSpan.FromMilliseconds(200);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so the app finishes starting before the first sync run.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAllDeploymentsAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task SyncAllDeploymentsAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("deployment-sync");

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DeliveryDbContext>>();
        var appDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var opsService = scope.ServiceProvider.GetRequiredService<KubernetesOperationsService>();
        var deploymentService = scope.ServiceProvider.GetRequiredService<DeploymentService>();

        // One tenant at a time, which is the decided shape for every sweep (docs/decomposition.md
        // §4.0.1.2): the tenant is an INPUT to the query that finds the work, so each live read is
        // authorized as that tenant. This used to select every deployment in the installation whose
        // cluster had a kubeconfig and then read each one's resources by id alone.
        List<(Guid TenantId, Guid DeploymentId)> targets = [];

        using (ApplicationDbContext appDb = appDbFactory.CreateDbContext())
        using (DeliveryDbContext db = dbFactory.CreateDbContext())
        {
            foreach (Guid tenantId in await appDb.Tenants.Select(t => t.Id).ToListAsync(ct))
            {
                List<Guid> ids = await db.AppDeployments
                    .Where(d => d.Cluster.KubeconfigSecretId != null
                        && d.App.Customer.TenantId == tenantId)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                targets.AddRange(ids.Select(id => (tenantId, id)));
            }
        }

        logger.LogDebug("DeploymentSyncService: syncing {Count} deployments", targets.Count);

        int synced = 0, failed = 0;

        foreach ((Guid tenantId, Guid id) in targets)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                KubernetesOperationResult<List<DeploymentResource>> result =
                    await opsService.GetLiveResourcesAsync(tenantId, id, ct);

                if (result.IsSuccess && result.Data is not null)
                {
                    (SyncStatus sync, HealthStatus health) =
                        KubernetesOperationsService.ComputeStatusFromResources(result.Data);

                    await deploymentService.UpdateDeploymentStatusAsync(id, sync, health, null, ct);
                    synced++;
                }
                else
                {
                    await deploymentService.UpdateDeploymentStatusAsync(
                        id, SyncStatus.Unknown, HealthStatus.Unknown, result.Error, ct);
                }
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogWarning(ex, "Failed to sync deployment {DeploymentId}", id);
            }

            await Task.Delay(InterDeploymentDelay, ct);
        }

        if (synced > 0 || failed > 0)
        {
            logger.LogInformation("DeploymentSyncService: synced={Synced} failed={Failed}", synced, failed);
        }
    }
}
