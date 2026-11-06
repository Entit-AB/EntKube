namespace EntKube.Web.Services;

/// <summary>
/// Ages out feedback. A screenshot shows whatever was on somebody's screen and an item can carry a
/// typed address, so neither is kept indefinitely. Singleton work because one instance is enough,
/// though it is idempotent, so two running it would do no harm.
/// </summary>
public sealed class FeedbackRetentionService(
    FeedbackService feedback, ILogger<FeedbackRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        do
        {
            try
            {
                (int screenshots, int items) = await feedback.PruneAsync(DateTime.UtcNow, stoppingToken);

                if (screenshots + items > 0)
                {
                    logger.LogInformation(
                        "Feedback retention removed {Screenshots} screenshots and {Items} items.",
                        screenshots, items);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Feedback retention sweep failed; it runs again in {Interval}.", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
