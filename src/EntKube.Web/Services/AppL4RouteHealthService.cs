using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that periodically TCP-dials each applied <b>TCP</b> L4 route's external endpoint
/// (the dedicated Istio L4 gateway's LoadBalancer address : ExternalPort) and writes the result back
/// to AppL4Route.IsReachable / LastHealthCheckAt. A completed TCP handshake is the reachability signal.
///
/// UDP routes are intentionally skipped: UDP is connectionless, so a socket connect does not verify
/// the datagram path actually works and would report false positives. Their IsReachable stays null
/// (rendered as "Applied" rather than reachable/unreachable).
///
/// Runs every 5 minutes. Only checks enabled, managed routes that EntKube has actually applied, so
/// unpublished or observe-only rows don't produce spurious results. The gateway address is resolved
/// once per cluster per cycle to avoid re-shelling kubectl for every route.
/// </summary>
public class AppL4RouteHealthService(
    IServiceScopeFactory scopeFactory,
    ILogger<AppL4RouteHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(75), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAllRoutesAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "AppL4RouteHealthService cycle failed");
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task CheckAllRoutesAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("app-l4-route-health");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConnectivityDbContext>>();
        var appDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var k8sOps = scope.ServiceProvider.GetRequiredService<KubernetesOperationsService>();

        // Only TCP routes are actively probed (UDP is connectionless — see class remarks).
        //
        // One tenant at a time (docs/decomposition.md §4.0.1.2): the gateway address is read from
        // the cluster, so the read is authorized as the tenant whose route is being probed. The
        // tenant travels with each route rather than being recovered from it later.
        List<(Guid TenantId, Guid Id, Guid ClusterId, int Port)> routes = [];
        using (ApplicationDbContext appDb = appDbFactory.CreateDbContext())
        using (ConnectivityDbContext db = dbFactory.CreateDbContext())
        {
            foreach (Guid tenantId in await appDb.Tenants.Select(t => t.Id).ToListAsync(ct))
            {
                List<(Guid Id, Guid ClusterId, int Port)> forTenant = await db.AppL4Routes
                    .Where(r => r.IsEnabled && r.IsManaged && r.ClusterAppliedAt != null
                        && r.Protocol == L4Protocol.Tcp
                        && r.AppDeployment.App.Customer.TenantId == tenantId)
                    .Select(r => new ValueTuple<Guid, Guid, int>(r.Id, r.AppDeployment.ClusterId, r.ExternalPort))
                    .ToListAsync(ct);

                routes.AddRange(forTenant.Select(r => (tenantId, r.Id, r.ClusterId, r.Port)));
            }
        }

        if (routes.Count == 0) return;

        logger.LogDebug("AppL4RouteHealthService: checking {Count} TCP routes", routes.Count);

        // Resolve each cluster's L4 gateway external address once per cycle, as the tenant whose
        // route needs it. Keyed on the pair: a tenant who may not reach a cluster gets null, and
        // under a cluster-only key that null would be served to its owner next.
        Dictionary<(Guid TenantId, Guid ClusterId), string?> addressByCluster = new();
        foreach ((Guid tenantId, Guid clusterId) in routes.Select(r => (r.TenantId, r.ClusterId)).Distinct())
            addressByCluster[(tenantId, clusterId)] =
                await k8sOps.GetL4EndpointAddressAsync(tenantId, clusterId, ct);

        foreach ((Guid tenantId, Guid id, Guid clusterId, int port) in routes)
        {
            if (ct.IsCancellationRequested) break;

            string? address = addressByCluster.GetValueOrDefault((tenantId, clusterId));
            bool reachable = address is not null && await CanConnectAsync(address, port, ct);

            try
            {
                using ConnectivityDbContext db = dbFactory.CreateDbContext();
                AppL4Route? route = await db.AppL4Routes.FindAsync([id], ct);
                if (route is null) continue;

                route.LastHealthCheckAt = DateTime.UtcNow;
                route.IsReachable = reachable;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist L4 health result for route {RouteId}", id);
            }
        }
    }

    private async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using TcpClient client = new();
            using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(DialTimeout);
            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch (Exception ex) when (ex is not OperationCanceledException oce || oce.CancellationToken != ct)
        {
            logger.LogDebug("TCP dial failed for {Host}:{Port}: {Message}", host, port, ex.Message);
            return false;
        }
    }
}
