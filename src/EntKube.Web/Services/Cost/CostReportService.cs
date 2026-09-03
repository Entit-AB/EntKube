using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Cost;

/// <summary>A tenant-wide cost picture at current run rate.</summary>
public sealed record CostReport
{
    public required IReadOnlyList<NamespaceCost> Namespaces { get; init; }
    public required DateTime GeneratedAt { get; init; }

    /// <summary>Currency of the figures. Mixed-currency tenants are reported as "—" (see below).</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Clusters that could not be priced, and why. Surfaced rather than silently
    /// omitted: a total that quietly excludes half the fleet is worse than no total.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public decimal TotalMonthlyCost => Namespaces.Sum(n => n.TotalMonthlyCost);
    public decimal TotalHourlyCost => TotalMonthlyCost / CostAllocation.HoursPerMonth;

    /// <summary>Monthly cost that could not be attributed to a customer — platform overhead.</summary>
    public decimal UnattributedMonthlyCost => Namespaces.Where(n => n.IsUnattributed).Sum(n => n.TotalMonthlyCost);

    /// <summary>Cost per customer, largest first — the chargeback view.</summary>
    public IReadOnlyList<(Guid CustomerId, string CustomerName, decimal MonthlyCost)> ByCustomer =>
        [.. Namespaces
            .Where(n => n.CustomerId is not null)
            .GroupBy(n => (n.CustomerId!.Value, n.CustomerName ?? "—"))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(n => n.TotalMonthlyCost)))
            .OrderByDescending(t => t.Item3)];

    /// <summary>Cost per environment, largest first.</summary>
    public IReadOnlyList<(string Environment, decimal MonthlyCost)> ByEnvironment =>
        [.. Namespaces
            .Where(n => n.EnvironmentName is not null)
            .GroupBy(n => n.EnvironmentName!)
            .Select(g => (g.Key, g.Sum(n => n.TotalMonthlyCost)))
            .OrderByDescending(t => t.Item2)];
}

/// <summary>
/// Computes what the fleet costs and who is consuming it, by joining live resource
/// consumption from each cluster's Prometheus to the cluster's price sheet and to
/// EntKube's own namespace ownership model.
///
/// Charging is on <em>requests</em> by default rather than actual usage: requests are
/// what the scheduler reserves and therefore what a customer genuinely denies to
/// everyone else. Billing on usage would let an over-requesting team push the cost of
/// their own waste onto their neighbours.
/// </summary>
public class CostReportService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    PrometheusService prometheus,
    ILogger<CostReportService> logger)
{
    /// <summary>
    /// Averaged over 15 minutes rather than read instantly, so a single scrape landing
    /// mid-rollout does not double-count a workload that is briefly running two replica sets.
    /// </summary>
    private static readonly TimeSpan SampleWindow = TimeSpan.FromMinutes(15);

    // Standard kube-state-metrics series. Requests and usage are both available so the
    // charging basis is a configuration choice, not a code change.
    private const string CpuRequestsQuery =
        "sum by (namespace) (kube_pod_container_resource_requests{resource=\"cpu\"})";
    private const string CpuUsageQuery =
        "sum by (namespace) (rate(container_cpu_usage_seconds_total{container!=\"\"}[5m]))";
    private const string MemoryRequestsQuery =
        "sum by (namespace) (kube_pod_container_resource_requests{resource=\"memory\"})";
    private const string MemoryUsageQuery =
        "sum by (namespace) (container_memory_working_set_bytes{container!=\"\"})";
    private const string StorageQuery =
        "sum by (namespace) (kubelet_volume_stats_capacity_bytes)";

    public async Task<CostReport> GetTenantReportAsync(
        Guid tenantId, DateTime now, CancellationToken ct = default)
    {
        List<string> warnings = [];

        await using ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct);

        var clusters = await db.KubernetesClusters
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new { c.Id, c.Name })
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        Dictionary<Guid, ClusterCostRate> rates = await db.ClusterCostRates
            .AsNoTracking()
            .Where(r => r.Cluster.TenantId == tenantId)
            .ToDictionaryAsync(r => r.ClusterId, ct);

        // Namespace ownership comes from EntKube's own deployment records: the same
        // (cluster, namespace) pair that the platform deploys into is the one being billed.
        var ownership = await db.AppDeployments
            .AsNoTracking()
            .Where(d => d.App.Customer.TenantId == tenantId)
            .Select(d => new
            {
                d.ClusterId,
                d.Namespace,
                CustomerId = (Guid?)d.App.CustomerId,
                CustomerName = d.App.Customer.Name,
                AppName = d.App.Name,
                EnvironmentName = d.Environment.Name,
            })
            .ToListAsync(ct);

        Dictionary<(Guid, string), (Guid?, string?, string?, string?)> owners = [];
        foreach (var row in ownership)
        {
            // First writer wins: several deployments can share a namespace, and any of
            // them identifies the owning customer, which is what billing needs.
            owners.TryAdd(
                (row.ClusterId, row.Namespace),
                (row.CustomerId, row.CustomerName, row.AppName, row.EnvironmentName));
        }

        List<NamespaceCost> allCosts = [];
        HashSet<string> currencies = [];

        foreach (var cluster in clusters)
        {
            if (!rates.TryGetValue(cluster.Id, out ClusterCostRate? rate))
            {
                warnings.Add($"“{cluster.Name}” has no price sheet, so its cost is not included.");
                continue;
            }

            currencies.Add(rate.Currency);

            IReadOnlyList<NamespaceConsumption> consumption;
            try
            {
                consumption = await MeasureAsync(cluster.Id, rate.ChargeOnRequests, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Cost measurement failed for cluster {ClusterId}", cluster.Id);
                warnings.Add($"Could not read metrics from “{cluster.Name}”: {ex.Message}");
                continue;
            }

            if (consumption.Count == 0)
            {
                warnings.Add(
                    $"“{cluster.Name}” returned no resource metrics — is kube-prometheus-stack installed?");
                continue;
            }

            allCosts.AddRange(CostAllocation.Allocate(
                consumption, rate, cluster.Id, cluster.Name,
                ns => owners.TryGetValue((cluster.Id, ns), out var owner)
                    ? owner
                    : (null, null, null, null)));
        }

        List<NamespaceCost> ordered = [.. allCosts.OrderByDescending(c => c.TotalMonthlyCost)];

        return new CostReport
        {
            Namespaces = ordered,
            GeneratedAt = now,
            // Mixing currencies would make the totals meaningless, so say so rather than
            // silently adding euros to dollars.
            Currency = currencies.Count == 1 ? currencies.First() : "—",
            Warnings = currencies.Count > 1
                ? [.. warnings, "Clusters use different currencies; totals are not meaningful."]
                : warnings,
        };
    }

    /// <summary>Reads per-namespace consumption from a cluster's Prometheus.</summary>
    private async Task<IReadOnlyList<NamespaceConsumption>> MeasureAsync(
        Guid clusterId, bool chargeOnRequests, CancellationToken ct)
    {
        Dictionary<string, double> cpu = await SumByNamespaceAsync(
            clusterId, chargeOnRequests ? CpuRequestsQuery : CpuUsageQuery, ct);

        Dictionary<string, double> memory = await SumByNamespaceAsync(
            clusterId, chargeOnRequests ? MemoryRequestsQuery : MemoryUsageQuery, ct);

        // Storage is always charged on provisioned capacity, whatever the compute basis:
        // a half-empty volume still denies its full size to everyone else.
        Dictionary<string, double> storage = await SumByNamespaceAsync(clusterId, StorageQuery, ct);

        HashSet<string> namespaces = [.. cpu.Keys, .. memory.Keys, .. storage.Keys];

        return [.. namespaces.Select(ns => new NamespaceConsumption
        {
            Namespace = ns,
            CpuCores = cpu.GetValueOrDefault(ns),
            MemoryGiB = CostAllocation.BytesToGiB(memory.GetValueOrDefault(ns)),
            StorageGiB = CostAllocation.BytesToGiB(storage.GetValueOrDefault(ns)),
        })];
    }

    private async Task<Dictionary<string, double>> SumByNamespaceAsync(
        Guid clusterId, string query, CancellationToken ct)
    {
        KubernetesOperationResult<List<PrometheusTimeSeries>> result =
            await prometheus.GetMetricRangeAsync(clusterId, query, SampleWindow, ct);

        Dictionary<string, double> values = [];
        if (!result.IsSuccess || result.Data is null)
        {
            return values;
        }

        foreach (PrometheusTimeSeries series in result.Data)
        {
            string ns = series.Labels.GetValueOrDefault("namespace", "");
            if (ns.Length == 0 || series.DataPoints.Count == 0)
            {
                continue;
            }

            // Mean over the window, not the latest point: the average is what was actually
            // reserved over the period being billed.
            double mean = series.DataPoints
                .Select(p => p.Value)
                .Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0)
                .DefaultIfEmpty(0)
                .Average();

            if (mean > 0)
            {
                values[ns] = mean;
            }
        }

        return values;
    }
}
