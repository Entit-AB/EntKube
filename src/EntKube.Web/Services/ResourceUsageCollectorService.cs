using Microsoft.EntityFrameworkCore;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using EntKube.Web.Services.ClusterChanges;

namespace EntKube.Web.Services;

/// <summary>
/// Periodically snapshots resource-saturation ratios (PVC fill, pod memory-vs-limit)
/// from each cluster's Prometheus into <see cref="ResourceUsageSnapshot"/>. The advisor
/// then reads a recent window of these to project when something will hit its ceiling —
/// capacity forecasting can't run live on the compute-on-read path, so it's cached here.
/// Only clusters with a kube-prometheus-stack component installed are polled.
/// </summary>
public class ResourceUsageCollectorService(
    IServiceScopeFactory scopeFactory,
    ILogger<ResourceUsageCollectorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(15);

    // Standard kube-prometheus-stack / kube-state-metrics queries. Each series carries
    // `namespace` + the named label; the value is already a 0..1 ratio.
    private static readonly (ResourceUsageKind Kind, string Query, string NameLabel)[] Queries =
    [
        (ResourceUsageKind.PvcFill,
         "kubelet_volume_stats_used_bytes / kubelet_volume_stats_capacity_bytes",
         "persistentvolumeclaim"),
        (ResourceUsageKind.MemoryVsLimit,
         "sum by (namespace, pod) (container_memory_working_set_bytes{container!=\"\"}) " +
         "/ sum by (namespace, pod) (kube_pod_container_resource_limits{resource=\"memory\"})",
         "pod"),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CollectAsync(stoppingToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Resource-usage collection cycle failed"); }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        using IDisposable unattended = scope.DeclareUnattended("resource-usage-collector");
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CostDbContext>>();
        var prometheus = scope.ServiceProvider.GetRequiredService<PrometheusService>();

        var catalog = scope.ServiceProvider.GetRequiredService<EntKube.Contracts.Catalog.ICatalogApi>();

        // One tenant at a time (docs/decomposition.md §4.0.1.2), so the cluster list is each
        // tenant's own rather than every installation running kube-prometheus-stack.
        //
        // The pair is kept, not flattened to cluster ids: the query below is authorized as the
        // tenant that was asked, so discarding it here and recovering it there is precisely the
        // move that turns a check into a tautology. This was half converted for three steps while
        // PrometheusService still took a cluster id and no tenant.
        List<(Guid TenantId, Guid ClusterId)> targets = [];
        using (CostDbContext db = dbFactory.CreateDbContext())
        {
            foreach (Guid tenantId in await db.Tenants.Select(t => t.Id).ToListAsync(ct))
            {
                targets.AddRange(
                    (await catalog.GetComponentsForTenantAsync(tenantId, ct))
                        .Where(EntKube.Contracts.Catalog.PrometheusPresence.Answers)
                        .Select(c => (tenantId, c.ClusterId)));
            }
        }
        targets = [.. targets.Distinct()];
        if (targets.Count == 0) return;

        DateTime now = DateTime.UtcNow;
        int written = 0;

        foreach ((Guid tenantId, Guid clusterId) in targets)
        {
            var batch = new List<ResourceUsageSnapshot>();
            foreach ((ResourceUsageKind kind, string query, string nameLabel) in Queries)
            {
                try
                {
                    KubernetesOperationResult<List<PrometheusTimeSeries>> res =
                        await prometheus.GetMetricRangeAsync(tenantId, clusterId, query, SampleWindow, ct);
                    if (!res.IsSuccess || res.Data is null) continue;

                    foreach (PrometheusTimeSeries series in res.Data)
                    {
                        TimeSeriesDataPoint? latest = series.DataPoints
                            .OrderByDescending(p => p.Timestamp).FirstOrDefault();
                        if (latest is null) continue;

                        double frac = latest.Value;
                        if (double.IsNaN(frac) || double.IsInfinity(frac) || frac <= 0) continue;

                        string ns = series.Labels.GetValueOrDefault("namespace", "");
                        string name = series.Labels.GetValueOrDefault(nameLabel, "");
                        if (ns.Length == 0 || name.Length == 0) continue;

                        batch.Add(new ResourceUsageSnapshot
                        {
                            ClusterId = clusterId,
                            Kind = kind,
                            Namespace = ns,
                            Name = name,
                            Fraction = Math.Min(frac, 2.0),
                            SnapshotAt = now,
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Resource-usage query {Kind} failed for cluster {Cluster}", kind, clusterId);
                }
            }

            if (batch.Count == 0) continue;
            using CostDbContext db = dbFactory.CreateDbContext();
            db.ResourceUsageSnapshots.AddRange(batch);
            await db.SaveChangesAsync(ct);
            written += batch.Count;
        }

        // Prune history so the table stays bounded.
        using (CostDbContext db = dbFactory.CreateDbContext())
        {
            int pruned = await db.ResourceUsageSnapshots
                .Where(s => s.SnapshotAt < now - Retention)
                .ExecuteDeleteAsync(ct);
            if (written > 0 || pruned > 0)
                logger.LogDebug("Resource usage: wrote {W} samples, pruned {P}", written, pruned);
        }
    }
}
