using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace EntKube.Web.Services;

/// <summary>
/// Background service that periodically HTTP-probes each external route hostname
/// and writes the result back to ExternalRoute.IsReachable / LastStatusCode /
/// LastHealthCheckAt. This surfaces availability problems in the portal without
/// requiring operators to manually check every endpoint.
///
/// Runs every 5 minutes. Uses a dedicated HttpClient with a short timeout so
/// an unresponsive host doesn't stall the entire check cycle.
/// </summary>
public class ExternalRouteHealthService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalRouteHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAllRoutesAsync(stoppingToken);
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task CheckAllRoutesAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        List<(Guid Id, string Hostname, string PathPrefix)> routes;

        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            routes = await db.ExternalRoutes
                .Select(r => new ValueTuple<Guid, string, string>(r.Id, r.Hostname, r.PathPrefix))
                .ToListAsync(ct);
        }

        if (routes.Count == 0) return;

        logger.LogDebug("ExternalRouteHealthService: checking {Count} routes", routes.Count);

        HttpClient httpClient = httpClientFactory.CreateClient("RouteHealth");

        foreach ((Guid id, string hostname, string pathPrefix) in routes)
        {
            if (ct.IsCancellationRequested) break;

            bool isReachable = false;
            int? statusCode = null;

            try
            {
                string url = $"https://{hostname}{pathPrefix}";
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);

                using HttpResponseMessage response = await httpClient.GetAsync(url, cts.Token);
                statusCode = (int)response.StatusCode;

                // 2xx, 3xx, and even 401/403 mean the host is reachable — just access-controlled.
                isReachable = (int)response.StatusCode < 500;
            }
            catch (Exception ex) when (ex is not OperationCanceledException { } oce || oce.CancellationToken != ct)
            {
                logger.LogDebug("Health check failed for {Hostname}: {Message}", hostname, ex.Message);
                isReachable = false;
                statusCode = null;
            }

            try
            {
                using ApplicationDbContext db = dbFactory.CreateDbContext();
                ExternalRoute? route = await db.ExternalRoutes.FindAsync([id], ct);
                if (route is null) continue;

                DateTime now = DateTime.UtcNow;
                route.LastHealthCheckAt = now;
                route.LastStatusCode = statusCode;
                route.IsReachable = isReachable;

                // Persist history entry for uptime tracking
                db.ExternalRouteHealthHistories.Add(new ExternalRouteHealthHistory
                {
                    RouteId = id,
                    IsReachable = isReachable,
                    StatusCode = statusCode,
                    CheckedAt = now
                });

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist health check result for route {RouteId}", id);
            }
        }
    }
}
