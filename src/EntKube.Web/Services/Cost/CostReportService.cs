using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services.Cost;

/// <summary>
/// What one app costs: what it consumes in its own right, plus its share of the shared
/// platform it runs on.
/// </summary>
public sealed record AppCost
{
    public required Guid AppId { get; init; }
    public required string AppName { get; init; }
    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>Environments this app is deployed to, in name order.</summary>
    public IReadOnlyList<string> Environments { get; init; } = [];

    /// <summary>The namespaces the figures were measured from, so a total can be traced back.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];

    public double CpuCores { get; init; }
    public double MemoryGiB { get; init; }
    public double StorageGiB { get; init; }

    /// <summary>What this app's own workloads consumed.</summary>
    public decimal DirectMonthlyCost { get; init; }

    /// <summary>Its share of the platform namespaces and fixed cluster fees it runs on.</summary>
    public decimal SharedMonthlyCost { get; init; }

    public decimal TotalMonthlyCost => DirectMonthlyCost + SharedMonthlyCost;

    /// <summary>
    /// How big this app is across the fleet: its share of everything billable, 0–1.
    /// Not the same as the per-cluster share the allocation used — an app on a cheap
    /// cluster can be large there and small here.
    /// </summary>
    public double ShareOfBillable { get; init; }
}

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

    /// <summary>
    /// Namespaces that carry a cost of their own. Excludes the ones whose cost was pooled
    /// and charged out to the others — counting both would count that money twice.
    /// </summary>
    public IEnumerable<NamespaceCost> Billed => Namespaces.Where(n => !n.IsRedistributed);

    /// <summary>The platform namespaces whose cost was pooled and charged out, largest first.</summary>
    public IReadOnlyList<NamespaceCost> SharedPool =>
        [.. Namespaces.Where(n => n.IsRedistributed).OrderByDescending(n => n.DirectMonthlyCost)];

    /// <summary>
    /// The shared cost charged out to apps this run — the pooled platform namespaces plus
    /// every cluster's fixed monthly fee. Read off the allocations rather than recomputed,
    /// so it is exactly what was billed.
    /// </summary>
    public decimal SharedPoolMonthlyCost => Namespaces.Sum(n => n.SharedMonthlyCost);

    public decimal TotalMonthlyCost => Billed.Sum(n => n.TotalMonthlyCost);
    public decimal TotalHourlyCost => TotalMonthlyCost / CostAllocation.HoursPerMonth;

    /// <summary>
    /// Cost that reached no customer. Only non-zero on a cluster where nothing at all is
    /// attributed — there being nobody to charge the platform to, it stays here rather
    /// than dropping out of the total.
    /// </summary>
    public decimal UnattributedMonthlyCost =>
        Billed.Where(n => n.IsUnattributed).Sum(n => n.TotalMonthlyCost);

    /// <summary>
    /// Cost in namespaces shared by more than one app. Billed to the customer, but not
    /// resolvable to a single app, so it is reported beside <see cref="ByApp"/> rather
    /// than being assigned to one of them arbitrarily.
    /// </summary>
    public decimal MultiAppMonthlyCost =>
        Billed.Where(n => n.IsMultiApp).Sum(n => n.TotalMonthlyCost);

    /// <summary>Cost per customer, largest first — the chargeback view.</summary>
    public IReadOnlyList<(Guid CustomerId, string CustomerName, decimal MonthlyCost)> ByCustomer =>
        [.. Billed
            .Where(n => n.CustomerId is not null)
            .GroupBy(n => (n.CustomerId!.Value, n.CustomerName ?? "—"))
            .Select(g => (g.Key.Item1, g.Key.Item2, g.Sum(n => n.TotalMonthlyCost)))
            .OrderByDescending(t => t.Item3)];

    /// <summary>Cost per environment, largest first.</summary>
    public IReadOnlyList<(string Environment, decimal MonthlyCost)> ByEnvironment =>
        [.. Billed
            .Where(n => n.EnvironmentName is not null)
            .GroupBy(n => n.EnvironmentName!)
            .Select(g => (g.Key, g.Sum(n => n.TotalMonthlyCost)))
            .OrderByDescending(t => t.Item2)];

    /// <summary>
    /// Cost per app, largest first — direct consumption plus the app's share of the
    /// platform. Namespaces shared by several apps are left out and reported through
    /// <see cref="MultiAppMonthlyCost"/> instead: measurement is per namespace, so there
    /// is nothing to divide such a namespace by.
    /// </summary>
    public IReadOnlyList<AppCost> ByApp
    {
        get
        {
            decimal billable = TotalMonthlyCost;

            return
            [.. Billed
                .Where(n => n.AppId is not null)
                .GroupBy(n => n.AppId!.Value)
                .Select(g =>
                {
                    NamespaceCost first = g.First();
                    decimal direct = g.Sum(n => n.DirectMonthlyCost);
                    decimal shared = g.Sum(n => n.SharedMonthlyCost);

                    return new AppCost
                    {
                        AppId = g.Key,
                        AppName = first.Apps[0].AppName,
                        CustomerId = first.CustomerId,
                        CustomerName = first.CustomerName,
                        Environments =
                            [.. g.Select(n => n.EnvironmentName)
                                 .Where(e => e is not null)
                                 .Select(e => e!)
                                 .Distinct()
                                 .Order()],
                        Namespaces = [.. g.Select(n => n.Namespace).Distinct().Order()],
                        CpuCores = g.Sum(n => n.CpuCores),
                        MemoryGiB = g.Sum(n => n.MemoryGiB),
                        StorageGiB = g.Sum(n => n.StorageGiB),
                        DirectMonthlyCost = direct,
                        SharedMonthlyCost = shared,
                        ShareOfBillable = billable > 0m ? (double)((direct + shared) / billable) : 0d,
                    };
                })
                .OrderByDescending(a => a.TotalMonthlyCost)];
        }
    }
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
    IKubernetesClientFactory k8s,
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
                d.AppId,
                CustomerId = (Guid?)d.App.CustomerId,
                CustomerName = d.App.Customer.Name,
                AppName = d.App.Name,
                EnvironmentName = d.Environment.Name,
            })
            .ToListAsync(ct);

        // Several deployments can target one namespace. The customer is taken from the
        // first — any of them identifies it, and billing needs only that — but every
        // distinct app is kept, because assigning a shared namespace's whole cost to
        // whichever app was enumerated first would put a made-up number on an invoice.
        Dictionary<(Guid, string), NamespaceOwner> owners = [];
        foreach (var group in ownership.GroupBy(r => (r.ClusterId, r.Namespace)))
        {
            var first = group.First();

            List<AppRef> apps =
                [.. group
                    .Select(r => new AppRef(r.AppId, r.AppName))
                    .Distinct()
                    .OrderBy(a => a.AppName)];

            // Only name an environment when the namespace has exactly one. A namespace
            // serving two environments belongs to neither for reporting purposes.
            List<string> environments =
                [.. group.Select(r => r.EnvironmentName).Distinct()];

            owners[group.Key] = new NamespaceOwner
            {
                CustomerId = first.CustomerId,
                CustomerName = first.CustomerName,
                Apps = apps,
                EnvironmentName = environments.Count == 1 ? environments[0] : null,
            };
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
                ns => owners.GetValueOrDefault((cluster.Id, ns), NamespaceOwner.None)));
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

        // Load balancers and public IPs come from the API server rather than Prometheus:
        // they are objects, not a metric, and counting them is exact where a metric would
        // be a scrape-interval approximation of something billed by the month.
        Dictionary<string, (int LoadBalancers, int PublicIps)> network =
            await CountLoadBalancersAsync(clusterId, ct);

        HashSet<string> namespaces = [.. cpu.Keys, .. memory.Keys, .. storage.Keys, .. network.Keys];

        return [.. namespaces.Select(ns => new NamespaceConsumption
        {
            Namespace = ns,
            CpuCores = cpu.GetValueOrDefault(ns),
            MemoryGiB = CostAllocation.BytesToGiB(memory.GetValueOrDefault(ns)),
            StorageGiB = CostAllocation.BytesToGiB(storage.GetValueOrDefault(ns)),
            LoadBalancers = network.GetValueOrDefault(ns).LoadBalancers,
            PublicIps = network.GetValueOrDefault(ns).PublicIps,
        })];
    }

    /// <summary>
    /// Counts LoadBalancer Services and the public addresses they hold, per namespace.
    ///
    /// Every Service of type LoadBalancer provisions a cloud load balancer that is billed
    /// per month, and normally one public IPv4 with it. Those are charges a specific
    /// namespace caused, so they are attributed to it rather than buried in shared
    /// overhead — a team that provisions three of them should see three of them.
    ///
    /// A failure here yields no counts rather than throwing: the compute and storage
    /// figures are still worth reporting, and a cost report that vanishes because one
    /// API call failed is less useful than one that is missing a line.
    /// </summary>
    private async Task<Dictionary<string, (int LoadBalancers, int PublicIps)>> CountLoadBalancersAsync(
        Guid clusterId, CancellationToken ct)
    {
        Dictionary<string, (int, int)> counts = [];

        string? kubeconfig;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            kubeconfig = await db.KubernetesClusters
                .Where(c => c.Id == clusterId)
                .Select(c => c.Kubeconfig)
                .FirstOrDefaultAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(kubeconfig))
        {
            return counts;
        }

        try
        {
            string json = await k8s.GetJsonAllNamespacesAsync("services", kubeconfig, "", ct);
            return ParseLoadBalancerServices(json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not count load balancers on cluster {ClusterId}", clusterId);
            return counts;
        }
    }

    /// <summary>
    /// Parses a Service list into per-namespace load balancer and public IP counts.
    /// Public and static so the counting rules can be checked without a cluster.
    /// </summary>
    public static Dictionary<string, (int LoadBalancers, int PublicIps)> ParseLoadBalancerServices(string? json)
    {
        Dictionary<string, (int LoadBalancers, int PublicIps)> counts = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(json))
        {
            return counts;
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out System.Text.Json.JsonElement items)
                || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return counts;
            }

            foreach (System.Text.Json.JsonElement item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("spec", out System.Text.Json.JsonElement spec)
                    || !spec.TryGetProperty("type", out System.Text.Json.JsonElement type)
                    || type.GetString() != "LoadBalancer")
                {
                    continue;
                }

                string ns = item.TryGetProperty("metadata", out System.Text.Json.JsonElement meta)
                    && meta.TryGetProperty("namespace", out System.Text.Json.JsonElement nsEl)
                    ? nsEl.GetString() ?? ""
                    : "";

                if (ns.Length == 0)
                {
                    continue;
                }

                // Count only addresses the cloud actually assigned. A Service still
                // pending an address is provisioning a load balancer but does not yet
                // hold an IP, and billing for one that does not exist would be wrong.
                int ips = 0;
                if (item.TryGetProperty("status", out System.Text.Json.JsonElement status)
                    && status.TryGetProperty("loadBalancer", out System.Text.Json.JsonElement lb)
                    && lb.TryGetProperty("ingress", out System.Text.Json.JsonElement ingress)
                    && ingress.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    ips = ingress.EnumerateArray()
                        .Count(i => i.TryGetProperty("ip", out System.Text.Json.JsonElement ip)
                                 && !string.IsNullOrWhiteSpace(ip.GetString()));
                }

                (int existingLbs, int existingIps) = counts.GetValueOrDefault(ns);
                counts[ns] = (existingLbs + 1, existingIps + ips);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return counts;
        }

        return counts;
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
