using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that automatically syncs the headscale TLS certificate whenever
/// cert-manager renews it. cert-manager renews the gateway-level cert in cert-manager
/// namespace; headscale reads a copy in headscale namespace. Without this service,
/// that copy becomes stale after every renewal (~every 60 days for Let's Encrypt certs)
/// and headscale would start serving an expired cert.
///
/// Every 6 hours: compares tls.crt in cert-manager/vpn-*-tls against headscale/headscale-tls.
/// If they differ, re-copies the cert and restarts the headscale Deployment.
/// </summary>
public class HeadscaleCertSyncService(
    IServiceScopeFactory scopeFactory,
    ILogger<HeadscaleCertSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app fully start before the first check.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncAllAsync(stoppingToken);
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task SyncAllAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var headscaleService = scope.ServiceProvider.GetRequiredService<HeadscaleService>();

        List<Guid> clusterIds;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            clusterIds = await db.ClusterComponents
                .Where(c => (c.HelmChartName == "headscale" || c.Name == "headscale")
                    && c.ExternalRoutes.Any(r => r.TlsMode == TlsMode.Passthrough))
                .Select(c => c.ClusterId)
                .Distinct()
                .ToListAsync(ct);
        }

        foreach (Guid clusterId in clusterIds)
        {
            try
            {
                await headscaleService.SyncCertIfChangedAsync(clusterId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Headscale cert sync failed for cluster {ClusterId}", clusterId);
            }
        }
    }
}
