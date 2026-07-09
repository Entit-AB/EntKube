namespace EntKube.Web.Services;

/// <summary>
/// Periodically refreshes this instance's live connections in the presence
/// backend so TTL-based backends (Redis) don't reap circuits that are still up.
/// Only registered when a TTL backend is active; it is a no-op for the in-memory
/// backend.
/// </summary>
public sealed class PresenceHeartbeatService : BackgroundService
{
    private readonly IPresenceTracker _tracker;
    private readonly ILogger<PresenceHeartbeatService> _logger;
    private readonly TimeSpan _interval;

    public PresenceHeartbeatService(
        IPresenceTracker tracker,
        IConfiguration config,
        ILogger<PresenceHeartbeatService> logger)
    {
        _tracker = tracker;
        _logger = logger;
        int seconds = config.GetValue<int?>("Presence:RefreshSeconds") ?? 10;
        _interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _tracker.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Presence heartbeat refresh failed.");
            }
        }
    }
}
