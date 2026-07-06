using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;

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
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        var k8sOps = scope.ServiceProvider.GetRequiredService<KubernetesOperationsService>();

        // Only TCP routes are actively probed (UDP is connectionless — see class remarks).
        List<(Guid Id, Guid ClusterId, int Port)> routes;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            routes = await db.AppL4Routes
                .Where(r => r.IsEnabled && r.IsManaged && r.ClusterAppliedAt != null && r.Protocol == L4Protocol.Tcp)
                .Select(r => new ValueTuple<Guid, Guid, int>(r.Id, r.AppDeployment.ClusterId, r.ExternalPort))
                .ToListAsync(ct);
        }

        if (routes.Count == 0) return;

        logger.LogDebug("AppL4RouteHealthService: checking {Count} TCP routes", routes.Count);

        // Resolve each cluster's L4 gateway external address once per cycle.
        Dictionary<Guid, string?> addressByCluster = new();
        foreach (Guid clusterId in routes.Select(r => r.ClusterId).Distinct())
            addressByCluster[clusterId] = await k8sOps.GetL4EndpointAddressAsync(clusterId, ct);

        foreach ((Guid id, Guid clusterId, int port) in routes)
        {
            if (ct.IsCancellationRequested) break;

            string? address = addressByCluster.GetValueOrDefault(clusterId);
            bool reachable = address is not null && await CanConnectAsync(address, port, ct);

            try
            {
                using ApplicationDbContext db = dbFactory.CreateDbContext();
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
