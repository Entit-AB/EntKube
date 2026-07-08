namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Background driver for the log segment engine, modelled on <see cref="TelemetryMaintenanceService"/>:
/// on a short cadence it seals the active index into an immutable object-storage segment when it has grown
/// past the size or age threshold, and on a slower cadence it drops segments past the retention window.
/// One instance runs per signal (logs / spans / rum). Single-replica by deployment, so no leader election
/// is needed. A final seal on shutdown flushes whatever is buffered so it isn't lost on restart.
/// </summary>
public sealed class SegmentSealService(
    SegmentManagerBase manager,
    SegmentEngineOptions options,
    ILogger<SegmentSealService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retention is far cheaper than sealing and needn't run every tick; run it every Nth cycle.
        var retentionEvery = TimeSpan.FromHours(1);
        DateTime lastRetention = DateTime.UtcNow;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (manager.ActiveDocCount >= options.RollMaxDocs || manager.ActiveAge >= options.RollMaxAge)
                        await manager.RollAndSealAsync(stoppingToken);

                    if (DateTime.UtcNow - lastRetention >= retentionEvery)
                    {
                        await manager.DropExpiredAsync(stoppingToken);
                        lastRetention = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Segment seal/retention cycle failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }

        // Best-effort final seal so buffered logs survive a restart (bounded by the last commit otherwise).
        try { await manager.RollAndSealAsync(CancellationToken.None); }
        catch (Exception ex) { logger.LogWarning(ex, "Final segment seal on shutdown failed."); }
    }
}
