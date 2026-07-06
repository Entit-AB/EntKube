namespace EntKube.Web.Services;

/// <summary>
/// Background service that keeps the telemetry database's partitions healthy: ensures the
/// schema and upcoming partitions exist at startup, then re-runs every 12 hours to roll new
/// daily partitions forward and drop partitions past the retention window.
///
/// No-op when <see cref="TelemetryStore.IsEnabled"/> is false (no telemetry DB configured),
/// so it is harmless to register on every deployment including local SQLite dev.
/// </summary>
public sealed class TelemetryMaintenanceService(
    TelemetryStore store,
    ILogger<TelemetryMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!store.IsEnabled)
        {
            logger.LogInformation("Telemetry maintenance idle: telemetry store not configured.");
            return;
        }

        // Initial ensure. A failure here (DB not reachable yet) must not crash the host —
        // the periodic cycle retries.
        try
        {
            await store.EnsureSchemaAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Initial telemetry schema ensure failed; will retry on the maintenance cycle.");
        }

        using PeriodicTimer timer = new(TimeSpan.FromHours(12));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await store.EnsureSchemaAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Telemetry partition maintenance cycle failed; will retry next cycle.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* shutting down */ }
    }
}
