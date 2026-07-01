namespace EntKube.Web.Services;

/// <summary>
/// Background service that reconciles the stored status of managed messaging
/// clusters (RabbitMQ and Kafka) against their live Kubernetes state on an
/// interval. Without this, a cluster created via the operator can remain stuck in
/// "Creating" in the UI even after it becomes Ready, because status was only
/// reconciled when a user opened the cluster detail.
///
/// Runs every 60 seconds. Both reconcile methods are no-ops when nothing changed,
/// so this does not churn the database.
/// </summary>
public class MessagingStatusPollingService(
    IServiceScopeFactory scopeFactory,
    ILogger<MessagingStatusPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();

                RabbitMQService rabbit = scope.ServiceProvider.GetRequiredService<RabbitMQService>();
                await rabbit.ReconcileAllAsync(stoppingToken);

                KafkaService kafka = scope.ServiceProvider.GetRequiredService<KafkaService>();
                await kafka.ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MessagingStatusPollingService run failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
