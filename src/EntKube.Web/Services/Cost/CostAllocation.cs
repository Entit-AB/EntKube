namespace EntKube.Web.Services.Cost;

/// <summary>Measured consumption for one namespace on one cluster, averaged over the sample window.</summary>
public sealed record NamespaceConsumption
{
    public required string Namespace { get; init; }

    /// <summary>CPU cores reserved (or used, depending on the rate's charging basis).</summary>
    public double CpuCores { get; init; }

    /// <summary>Memory in GiB reserved (or used).</summary>
    public double MemoryGiB { get; init; }

    /// <summary>Provisioned persistent storage in GiB. Always charged on provisioned size —
    /// a half-empty volume still denies its full capacity to everyone else.</summary>
    public double StorageGiB { get; init; }

    /// <summary>Services of type LoadBalancer in this namespace. Each provisions one cloud load balancer.</summary>
    public int LoadBalancers { get; init; }

    /// <summary>Public IPv4 addresses held by those load balancers.</summary>
    public int PublicIps { get; init; }
}

/// <summary>What one namespace costs, split by resource so an operator can see what drives it.</summary>
public sealed record NamespaceCost
{
    public required string Namespace { get; init; }
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }

    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? AppName { get; init; }
    public string? EnvironmentName { get; init; }

    public double CpuCores { get; init; }
    public double MemoryGiB { get; init; }
    public double StorageGiB { get; init; }
    public int LoadBalancers { get; init; }
    public int PublicIps { get; init; }

    public decimal CpuMonthlyCost { get; init; }
    public decimal MemoryMonthlyCost { get; init; }
    public decimal StorageMonthlyCost { get; init; }

    /// <summary>
    /// Load balancers and public IPs this namespace caused. Directly attributed rather
    /// than spread: a namespace that provisions three LoadBalancer Services causes three
    /// real charges, and burying them in shared overhead would bill everyone else for it.
    /// </summary>
    public decimal NetworkMonthlyCost { get; init; }

    /// <summary>This namespace's share of the cluster's fixed monthly overhead.</summary>
    public decimal OverheadMonthlyCost { get; init; }

    public decimal TotalMonthlyCost =>
        CpuMonthlyCost + MemoryMonthlyCost + StorageMonthlyCost
        + NetworkMonthlyCost + OverheadMonthlyCost;

    /// <summary>Run-rate per hour, derived from the monthly figure by the same 730-hour month.</summary>
    public decimal HourlyCost => TotalMonthlyCost / CostAllocation.HoursPerMonth;

    /// <summary>
    /// True when this namespace could not be attributed to a customer — platform
    /// components, or a workload EntKube does not manage.
    /// </summary>
    public bool IsUnattributed => CustomerId is null;
}

/// <summary>
/// Turns measured consumption into money.
///
/// Kept pure — no database, no Prometheus — because the allocation rules are the part
/// that has to be defensible to a customer looking at an invoice, and they should be
/// checkable without standing up a cluster.
/// </summary>
public static class CostAllocation
{
    /// <summary>
    /// Hours in a billing month. 730 (365×24/12) rather than the actual length of the
    /// current month, so a run-rate figure does not jump 10% between February and March
    /// for reasons that have nothing to do with consumption. This is the same convention
    /// cloud providers publish their monthly prices on.
    /// </summary>
    public const decimal HoursPerMonth = 730m;

    /// <summary>
    /// Allocates cluster costs across namespaces.
    ///
    /// Fixed cluster overhead is spread in proportion to each namespace's compute cost
    /// (CPU + memory), not evenly: a namespace running one small pod should not carry the
    /// same share of a control-plane fee as one running half the cluster. When nothing is
    /// consuming compute, overhead is split evenly rather than dropped, so the total
    /// always reconciles to the real bill.
    /// </summary>
    public static IReadOnlyList<NamespaceCost> Allocate(
        IReadOnlyList<NamespaceConsumption> consumption,
        Data.ClusterCostRate rate,
        Guid clusterId,
        string clusterName,
        Func<string, (Guid? CustomerId, string? CustomerName, string? AppName, string? EnvironmentName)> attribute)
    {
        if (consumption.Count == 0)
        {
            return [];
        }

        List<(NamespaceConsumption Consumption, decimal Cpu, decimal Memory, decimal Storage, decimal Network)> priced = [];

        foreach (NamespaceConsumption ns in consumption)
        {
            decimal cpu = (decimal)ns.CpuCores * rate.CpuCoreHourCost * HoursPerMonth;
            decimal memory = (decimal)ns.MemoryGiB * rate.MemoryGiBHourCost * HoursPerMonth;
            decimal storage = (decimal)ns.StorageGiB * rate.StorageGiBMonthCost;

            // Already monthly: clouds bill a load balancer and an IP by the month, not by
            // the resource-hour, so these must not be multiplied by HoursPerMonth.
            decimal network = ns.LoadBalancers * rate.LoadBalancerMonthlyCost
                            + ns.PublicIps * rate.PublicIpMonthlyCost;

            priced.Add((ns, cpu, memory, storage, network));
        }

        decimal totalCompute = priced.Sum(p => p.Cpu + p.Memory);
        List<NamespaceCost> result = [];

        for (int i = 0; i < priced.Count; i++)
        {
            (NamespaceConsumption ns, decimal cpu, decimal memory, decimal storage, decimal network) = priced[i];

            decimal overhead = rate.ClusterMonthlyOverhead == 0m
                ? 0m
                : totalCompute > 0m
                    ? rate.ClusterMonthlyOverhead * ((cpu + memory) / totalCompute)
                    : rate.ClusterMonthlyOverhead / priced.Count;

            (Guid? customerId, string? customerName, string? appName, string? environmentName) =
                attribute(ns.Namespace);

            result.Add(new NamespaceCost
            {
                Namespace = ns.Namespace,
                ClusterId = clusterId,
                ClusterName = clusterName,
                CustomerId = customerId,
                CustomerName = customerName,
                AppName = appName,
                EnvironmentName = environmentName,
                CpuCores = ns.CpuCores,
                MemoryGiB = ns.MemoryGiB,
                StorageGiB = ns.StorageGiB,
                LoadBalancers = ns.LoadBalancers,
                PublicIps = ns.PublicIps,
                CpuMonthlyCost = Round(cpu),
                MemoryMonthlyCost = Round(memory),
                StorageMonthlyCost = Round(storage),
                NetworkMonthlyCost = Round(network),
                OverheadMonthlyCost = Round(overhead),
            });
        }

        return result;
    }

    /// <summary>
    /// Rounds to cents, away from zero — the convention for money, and the one that
    /// avoids banker's rounding quietly under-billing across many small line items.
    /// </summary>
    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Converts bytes to GiB, the unit the rates are quoted in.</summary>
    public static double BytesToGiB(double bytes) => bytes / (1024d * 1024d * 1024d);
}
