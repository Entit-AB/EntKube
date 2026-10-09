using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Reconciles the stored status of managed Elasticsearch clusters against what the ECK operator
/// reports, on an interval.
///
/// <para>Without it a cluster that came up perfectly well keeps saying "Creating" until somebody
/// opens its detail pane, because that was the only place status was ever read. An Elasticsearch
/// cluster takes minutes to form and its health moves on its own afterwards — yellow while shards
/// relocate, green when they land — so waiting for a page view is exactly the wrong cadence.</para>
///
/// <para>Runs every 60 seconds and writes nothing when nothing changed.</para>
/// </summary>
public class SearchStatusPollingService(
    IServiceScopeFactory scopeFactory,
    ILogger<SearchStatusPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromSeconds(50), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("search-status-polling");
                ElasticsearchService elasticsearch =
                    scope.ServiceProvider.GetRequiredService<ElasticsearchService>();
                await elasticsearch.ReconcileAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SearchStatusPollingService run failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
