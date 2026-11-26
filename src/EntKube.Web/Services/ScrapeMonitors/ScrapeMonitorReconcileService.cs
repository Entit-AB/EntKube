using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Keeps every cluster's Prometheus monitors in step with what runs on it, on a timer.
///
/// <para><b>Why a timer and not the actions that change things.</b> A database is created in one
/// service, adopted in another, deleted by a cascade when its tenant goes, and a component is
/// uninstalled from a page; and on an installation with several instances the one that handles the
/// click is not the one that sweeps. This sees all of it the same way, within five minutes.</para>
///
/// <para>Each run is declared unattended: it changes clusters with nobody to acknowledge, and the
/// audit trail records it under that name. What a cluster that refuses its monitors costs there is
/// why the backoff lives here, for as long as this process does.</para>
///
/// <para><b>On every instance.</b> Registered as singleton work, which is placement and not a
/// lease, so with the default configuration each instance runs this. Each cluster is claimed for
/// its read and its write (<see cref="ScrapeMonitorClaim"/>), and an instance that finds it claimed
/// leaves it for that pass; the backoff is still this instance's own.</para>
/// </summary>
public class ScrapeMonitorReconcileService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScrapeMonitorReconcileService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ScrapeMonitorBackoff backoff = new(TimeProvider.System);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first run.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                using IDisposable unattended = scope.DeclareUnattended("scrape-monitor-reconcile");

                ScrapeMonitorService monitors = scope.ServiceProvider.GetRequiredService<ScrapeMonitorService>();
                await monitors.ReconcileAllAsync(backoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nothing may escape: an exception out of a background service stops the application.
                logger.LogWarning(ex, "ScrapeMonitorReconcileService run failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
