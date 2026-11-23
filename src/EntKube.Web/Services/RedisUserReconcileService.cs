using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Keeps every managed Redis's per-app users in step with its bindings, on a timer.
///
/// <para><b>Why a timer and not only the binding actions.</b> Deleting a deployment, an app or a
/// tenant removes the cache bindings by database cascade and touches no cluster, so the app's Redis
/// user would keep working until something else happened to reconcile that Redis. This closes that
/// window to one interval. It also finishes a switch-over to per-app users whose syncs failed while
/// the Redis pods were restarting, and puts users back on a node that lost them.</para>
///
/// <para>Each run acts as each tenant in turn and is declared unattended: it changes clusters with
/// nobody to acknowledge, and the audit trail records it under that name.</para>
/// </summary>
public class RedisUserReconcileService(
    IServiceScopeFactory scopeFactory,
    ILogger<RedisUserReconcileService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("redis-per-app-users");

                RedisService redis = scope.ServiceProvider.GetRequiredService<RedisService>();
                await redis.ReconcileAllUsersAsync(logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RedisUserReconcileService run failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
