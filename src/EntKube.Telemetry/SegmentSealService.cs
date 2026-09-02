namespace EntKube.Telemetry;

/// <summary>
/// Background driver for one signal's segment engine (logs / spans / rum). Telemetry is tenant-scoped, so
/// on each tick it iterates every live per-tenant manager in the signal's registry: sealing a tenant's
/// active index into an immutable object-storage segment when it has grown past the size or age threshold,
/// and (on a slower cadence) dropping that tenant's segments past the retention window and evicting
/// local copies that have aged out of the warm tier. Single-replica by
/// deployment, so no leader election is needed. A final seal on shutdown flushes whatever each tenant has
/// buffered so it isn't lost on restart.
/// </summary>
public sealed class SegmentSealService(
    ISegmentManagerRegistry registry,
    SegmentEngineOptions options,
    ILogger<SegmentSealService> logger) : BackgroundService
{
    /// <summary>
    /// How long the shutdown seal may take before the process gives up and exits.
    ///
    /// Sized to sit inside two deadlines at once: the host's own 30-second <c>ShutdownTimeout</c>, and the
    /// chart's <c>terminationGracePeriodSeconds</c>. Five signals each get this budget in sequence, so it
    /// must be a small fraction of the grace period rather than most of it.
    /// </summary>
    private static readonly TimeSpan ShutdownSealBudget = TimeSpan.FromSeconds(15);

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
                        {
                            await manager.DropExpiredAsync(stoppingToken);
                            // Then age/size the warm tier down. Runs after retention so segments dropped
                            // outright are already gone and aren't measured as warm-tier pressure.
                            await manager.TrimWarmTierAsync(stoppingToken);
                        }
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

        // Best-effort final seal per tenant so buffered events survive a restart — emphasis on BOUNDED.
        //
        // This used to run with CancellationToken.None, which made it unbounded in a place that has a hard
        // deadline: the kubelet sends SIGTERM, waits terminationGracePeriodSeconds, then SIGKILLs. A seal
        // compresses the whole active segment and uploads it, so if object storage is slow or unreachable —
        // or the pod is CPU-starved, which is exactly when it is being restarted — it cannot finish, the
        // process never exits, and the container dies on exit code 137 having logged nothing at all. That
        // reads as an unexplained kill and is one of the hardest states to diagnose from the outside.
        //
        // Sealing is an optimisation, not a correctness requirement: the active index is on a
        // PersistentVolume and is recovered on the next start. Losing the race costs a re-read, not data,
        // so it is right to give up and exit cleanly.
        using CancellationTokenSource shutdown = new(ShutdownSealBudget);
        logger.LogInformation("Sealing active segments before shutdown (budget {Budget}).", ShutdownSealBudget);

        foreach (SegmentManagerBase manager in registry.ActiveManagers)
        {
            try
            {
                await manager.RollAndSealAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Ran out of time sealing on shutdown; the unsealed index stays on the volume and is "
                    + "recovered on the next start.");
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Final segment seal on shutdown failed.");
            }
        }

        logger.LogInformation("Shutdown seal complete.");
    }
}
