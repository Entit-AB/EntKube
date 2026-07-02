using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

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

        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var opsService = scope.ServiceProvider.GetRequiredService<KubernetesOperationsService>();
        var deploymentService = scope.ServiceProvider.GetRequiredService<DeploymentService>();

        List<Guid> deploymentIds;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            deploymentIds = await db.AppDeployments
                .Include(d => d.Cluster)
                .Where(d => d.Cluster.KubeconfigSecretId != null)
                .Select(d => d.Id)
                .ToListAsync(ct);
        }

        logger.LogDebug("DeploymentSyncService: syncing {Count} deployments", deploymentIds.Count);

        int synced = 0, failed = 0;

        foreach (Guid id in deploymentIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                KubernetesOperationResult<List<DeploymentResource>> result =
                    await opsService.GetLiveResourcesAsync(id, ct);

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
