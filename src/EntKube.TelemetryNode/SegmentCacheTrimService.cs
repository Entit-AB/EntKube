using EntKube.Telemetry;

namespace EntKube.TelemetryNode;

/// <summary>
/// Keeps the querier's local segment cache inside its bounds.
///
/// A querier never seals and never runs retention — the indexer owns both, and a second process doing
/// either would race it over shared state. But a querier does pull cold segments onto its volume to search
/// them, so without this it accumulates a read-through cache until the disk is full.
///
/// Configure the querier with <c>WarmRetentionDays</c> equal to <c>RetentionDays</c> so the age rule never
/// fires, leaving <c>WarmMaxBytes</c> and LRU to do the work. Ageing a querier's cache by event time would
/// be actively wrong: a query over last month's logs would download those segments and then immediately
/// evict them as "old", so the next identical query pays the download all over again.
/// </summary>
public sealed class SegmentCacheTrimService(
    IReadOnlyList<ISegmentManagerRegistry> registries,
    ILogger<SegmentCacheTrimService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    foreach (ISegmentManagerRegistry registry in registries)
                        foreach (SegmentManagerBase manager in registry.ActiveManagers)
                            await manager.TrimWarmTierAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Segment cache trim cycle failed; will retry next tick.");
                }
            }
        }
        catch (OperationCanceledException) { /* clean shutdown */ }
    }
}
