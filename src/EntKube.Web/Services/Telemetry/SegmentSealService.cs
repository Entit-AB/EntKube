namespace EntKube.Web.Services.Telemetry;

/// <summary>
/// Background driver for one signal's segment engine (logs / spans / rum). Telemetry is tenant-scoped, so
/// on each tick it iterates every live per-tenant manager in the signal's registry: sealing a tenant's
/// active index into an immutable object-storage segment when it has grown past the size or age threshold,
/// and (on a slower cadence) dropping that tenant's segments past the retention window. Single-replica by
/// deployment, so no leader election is needed. A final seal on shutdown flushes whatever each tenant has
/// buffered so it isn't lost on restart.
/// </summary>
public sealed class SegmentSealService(
    ISegmentManagerRegistry registry,
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
                bool runRetention = DateTime.UtcNow - lastRetention >= retentionEvery;
                try
                {
                    foreach (SegmentManagerBase manager in registry.ActiveManagers)
                    {
                        if (manager.ActiveDocCount >= options.RollMaxDocs || manager.ActiveAge >= options.RollMaxAge)
                            await manager.RollAndSealAsync(stoppingToken);
                        if (runRetention)
                            await manager.DropExpiredAsync(stoppingToken);
                    }
                    if (runRetention) lastRetention = DateTime.UtcNow;
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

        // Best-effort final seal per tenant so buffered events survive a restart.
        foreach (SegmentManagerBase manager in registry.ActiveManagers)
        {
            try { await manager.RollAndSealAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Final segment seal on shutdown failed."); }
        }
    }
}
