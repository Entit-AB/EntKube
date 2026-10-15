using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using Microsoft.EntityFrameworkCore;
using System.Net;
using EntKube.Web.Services.ClusterChanges;

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

    /// <summary>
    /// The external routes belonging to a set of components — the tenant's, when
    /// <paramref name="componentIds"/> came from <c>ICatalogApi.GetComponentsForTenantAsync</c>.
    ///
    /// <para>This used to be <c>Where(r =&gt; r.Component.Cluster.TenantId == tenantId)</c>: a
    /// correct predicate, and a two-hop read out of Connectivity's own table into Catalog's and
    /// then Fleet's. The tenant check has not been weakened — it has moved into Catalog's
    /// tenant-first lookup, which is the one place that answers "whose components are these?".
    /// Still public and static so the scoping is assertable without an HTTP server.</para>
    /// </summary>
    public static IQueryable<ExternalRoute> RoutesFor(
        ConnectivityDbContext db, IReadOnlyCollection<Guid> componentIds)
        => db.ExternalRoutes.Where(r => componentIds.Contains(r.ComponentId));

    private async Task CheckAllRoutesAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("external-route-health");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConnectivityDbContext>>();
        var catalog = scope.ServiceProvider.GetRequiredService<EntKube.Contracts.Catalog.ICatalogApi>();

        // One tenant at a time (docs/decomposition.md §4.0.1.2). ⚠️ Unlike most of that work, this
        // one buys isolation rather than safety, and it is worth saying which: the probe is an
        // outbound HTTPS GET to a hostname and the write is history on the row it just probed, so
        // no tenant-scoped decision is made either way. What it buys is a cycle per tenant — one
        // tenant's unreachable hosts cannot delay or abort another's checks, and the worker that
        // will run these sweeps needs them separable.
        List<(Guid Id, string Hostname, string PathPrefix)> routes = [];

        using (ConnectivityDbContext db = dbFactory.CreateDbContext())
        {
            foreach (Guid tenantId in await db.Tenants.Select(x => x.Id).ToListAsync(ct))
            {
                // Asked of Catalog as each tenant in turn — the tenant is the input, never read
                // back off a row this sweep happens to have loaded.
                List<Guid> componentIds =
                [
                    .. (await catalog.GetComponentsForTenantAsync(tenantId, ct)).Select(c => c.Id),
                ];
                if (componentIds.Count == 0) continue;

                routes.AddRange(await RoutesFor(db, componentIds)
                    .Select(r => new ValueTuple<Guid, string, string>(r.Id, r.Hostname, r.PathPrefix))
                    .ToListAsync(ct));
            }
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
                using ConnectivityDbContext db = dbFactory.CreateDbContext();
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
