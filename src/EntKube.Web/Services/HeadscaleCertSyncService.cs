using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using EntKube.Web.Services.ClusterChanges;

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
        using IDisposable unattended = scope.DeclareUnattended("headscale-cert-sync");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var headscaleService = scope.ServiceProvider.GetRequiredService<HeadscaleService>();

        var catalog = scope.ServiceProvider.GetRequiredService<EntKube.Contracts.Catalog.ICatalogApi>();

        // One tenant at a time, which is the decided shape for every sweep: the tenant is an
        // INPUT to each lookup rather than something read off the cluster the sweep found. This
        // used to select cluster ids across the whole installation and then act on them, so every
        // check downstream compared a row against itself. CostScanService is the precedent.
        List<Guid> tenantIds;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            tenantIds = await db.Tenants.Select(t => t.Id).ToListAsync(ct);
        }

        foreach (Guid tenantId in tenantIds)
        {
            List<Guid> clusterIds;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                // The headscale installs this tenant owns, from Catalog; then the ones published
                // with TLS passthrough, which is the only reason this sweep exists. The alias is
                // asymmetric (Name or HelmChartName) so that half stays in memory.
                List<Guid> componentIds =
                [
                    .. (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                        .Where(c => c.HelmChartName == "headscale" || c.Name == "headscale")
                        .Select(c => c.Id),
                ];

                clusterIds = await db.ExternalRoutes
                    .Where(r => componentIds.Contains(r.ComponentId)
                        && r.TlsMode == TlsMode.Passthrough)
                    .Select(r => r.Component.ClusterId)
                    .Distinct()
                    .ToListAsync(ct);
            }

            foreach (Guid clusterId in clusterIds)
            {
                try
                {
                    await headscaleService.SyncCertIfChangedAsync(tenantId, clusterId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Headscale cert sync failed for cluster {ClusterId} of tenant {TenantId}",
                        clusterId, tenantId);
                }
            }
        }
    }
}
