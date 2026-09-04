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

/// <summary>An app that a namespace's cost rolls up to.</summary>
public readonly record struct AppRef(Guid AppId, string AppName);

/// <summary>
/// What EntKube knows about who a namespace belongs to.
///
/// <see cref="Apps"/> is a list rather than a single app because nothing stops two apps
/// from deploying into one namespace. Measurement is per namespace, so when that happens
/// the cost genuinely cannot be split between them — saying so is the only honest option,
/// and it is what keeps a shared namespace from being billed entirely to whichever app
/// happened to be enumerated first.
/// </summary>
public sealed record NamespaceOwner
{
    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public IReadOnlyList<AppRef> Apps { get; init; } = [];
    public string? EnvironmentName { get; init; }

    /// <summary>A namespace EntKube has no deployment record for — part of the shared pool.</summary>
    public static readonly NamespaceOwner None = new();
}

/// <summary>What one namespace costs, split by resource so an operator can see what drives it.</summary>
public sealed record NamespaceCost
{
    public required string Namespace { get; init; }
    public required Guid ClusterId { get; init; }
    public required string ClusterName { get; init; }

    public Guid? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? EnvironmentName { get; init; }

    /// <summary>The apps deployed into this namespace. Empty for a shared/platform namespace.</summary>
    public IReadOnlyList<AppRef> Apps { get; init; } = [];

    /// <summary>
    /// The app this namespace's cost rolls up to, or null when it is shared by several —
    /// in which case there is no measurement that could divide it between them.
    /// </summary>
    public Guid? AppId => Apps.Count == 1 ? Apps[0].AppId : null;

    public string? AppName => Apps.Count switch
    {
        0 => null,
        1 => Apps[0].AppName,
        int n => $"{n} apps",
    };

    /// <summary>True when several apps share this namespace, so its cost cannot be attributed to one.</summary>
    public bool IsMultiApp => Apps.Count > 1;

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

    /// <summary>What this namespace consumed in its own right, before any shared cost.</summary>
    public decimal DirectMonthlyCost =>
        CpuMonthlyCost + MemoryMonthlyCost + StorageMonthlyCost + NetworkMonthlyCost;

    /// <summary>
    /// The part of the direct cost that decides this namespace's share of the shared pool:
    /// CPU, memory and storage — the capacity it holds on the cluster.
    ///
    /// Network is excluded. A load balancer is a pass-through charge billed straight to
    /// the namespace that provisioned it; letting it also enlarge that namespace's share
    /// of the platform would charge for the same thing twice.
    /// </summary>
    public decimal ResourceMonthlyCost =>
        CpuMonthlyCost + MemoryMonthlyCost + StorageMonthlyCost;

    /// <summary>
    /// This namespace's share of the cluster's shared pool — the platform namespaces plus
    /// the cluster's fixed monthly fee.
    /// </summary>
    public decimal SharedMonthlyCost { get; init; }

    /// <summary>
    /// How big this namespace is on its cluster: its resource cost over the resource cost
    /// of everything billable there, 0–1. The proportion <see cref="SharedMonthlyCost"/>
    /// was allocated on, surfaced because it is the number an operator will be asked to
    /// justify.
    /// </summary>
    public double ShareOfCluster { get; init; }

    /// <summary>
    /// True when this namespace's own cost was moved into the cluster's shared pool and
    /// charged out to the billable namespaces instead. Its figures are still reported —
    /// the pool has to be auditable — but it must be left out of any total, or the cost
    /// would be counted twice.
    /// </summary>
    public bool IsRedistributed { get; init; }

    public decimal TotalMonthlyCost => DirectMonthlyCost + SharedMonthlyCost;

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

    /// <summary>One namespace after pass one: priced, and with its owner resolved.</summary>
    private sealed record Priced(
        NamespaceConsumption Consumption,
        NamespaceOwner Owner,
        decimal Cpu,
        decimal Memory,
        decimal Storage,
        decimal Network)
    {
        /// <summary>Direct cost, all lines.</summary>
        public decimal Direct => Cpu + Memory + Storage + Network;

        /// <summary>The share basis: capacity held on the cluster, excluding pass-through network charges.</summary>
        public decimal Basis => Cpu + Memory + Storage;
    }

    /// <summary>
    /// Allocates a cluster's whole bill across the namespaces that caused it.
    ///
    /// Two passes. First every namespace is priced on what it consumed in its own right.
    /// Then everything EntKube has no deployment record for — the ingress controller,
    /// monitoring, the telemetry stack, the cluster's fixed monthly fee — is pooled and
    /// charged out to the namespaces that <em>are</em> attributed, in proportion to the
    /// capacity each holds (CPU, memory and storage).
    ///
    /// That proportional split is the point: the platform exists to serve the workloads,
    /// so an app occupying a third of the cluster should carry a third of what the
    /// platform costs to run — including a third of the storage the log stack consumes,
    /// whichever app happens to be generating the logs. Charging the platform to nobody,
    /// as a bare "overhead" line, leaves the operator absorbing it silently.
    ///
    /// On a cluster where nothing is attributed there is nobody to bill, so only the fixed
    /// fee is spread — across everything — and the result stays unattributed rather than
    /// disappearing from the total.
    /// </summary>
    public static IReadOnlyList<NamespaceCost> Allocate(
        IReadOnlyList<NamespaceConsumption> consumption,
        Data.ClusterCostRate rate,
        Guid clusterId,
        string clusterName,
        Func<string, NamespaceOwner> attribute)
    {
        if (consumption.Count == 0)
        {
            return [];
        }

        // ── Pass one: what each namespace consumed in its own right ──

        List<Priced> priced = [];

        foreach (NamespaceConsumption ns in consumption)
        {
            decimal cpu = (decimal)ns.CpuCores * rate.CpuCoreHourCost * HoursPerMonth;
            decimal memory = (decimal)ns.MemoryGiB * rate.MemoryGiBHourCost * HoursPerMonth;
            decimal storage = (decimal)ns.StorageGiB * rate.StorageGiBMonthCost;

            // Already monthly: clouds bill a load balancer and an IP by the month, not by
            // the resource-hour, so these must not be multiplied by HoursPerMonth.
            decimal network = ns.LoadBalancers * rate.LoadBalancerMonthlyCost
                            + ns.PublicIps * rate.PublicIpMonthlyCost;

            priced.Add(new Priced(
                ns, attribute(ns.Namespace) ?? NamespaceOwner.None,
                Round(cpu), Round(memory), Round(storage), Round(network)));
        }

        // ── Pass two: pool the shared cost and charge it out ──

        bool anyBillable = priced.Any(p => p.Owner.CustomerId is not null);

        // A namespace is redistributed when it has no deployment record AND there is
        // somebody to charge it to. Recipients are exactly the rest.
        bool[] redistributed = [.. priced.Select(p => anyBillable && p.Owner.CustomerId is null)];

        decimal pool = Round(rate.ClusterMonthlyOverhead);
        for (int i = 0; i < priced.Count; i++)
        {
            if (redistributed[i])
            {
                pool += priced[i].Direct;
            }
        }

        List<int> recipients = [.. Enumerable.Range(0, priced.Count).Where(i => !redistributed[i])];
        decimal totalBasis = recipients.Sum(i => priced[i].Basis);

        decimal[] shared = new decimal[priced.Count];
        double[] share = new double[priced.Count];

        if (pool > 0m || totalBasis > 0m)
        {
            foreach (int i in recipients)
            {
                // When nothing holds any capacity — everything scaled to zero — an even
                // split is the only division left, and dropping the pool instead would
                // make the reported total understate the real bill.
                decimal fraction = totalBasis > 0m
                    ? priced[i].Basis / totalBasis
                    : 1m / recipients.Count;

                share[i] = (double)fraction;
                shared[i] = Round(pool * fraction);
            }

            // Rounding each share to cents independently leaves a few pennies over or
            // short. They go to the largest recipient, so the allocated total is exactly
            // the pool rather than approximately it.
            decimal residual = pool - recipients.Sum(i => shared[i]);
            if (residual != 0m && recipients.Count > 0)
            {
                int largest = recipients.OrderByDescending(i => shared[i]).ThenBy(i => i).First();
                shared[largest] += residual;
            }
        }

        List<NamespaceCost> result = [];

        for (int i = 0; i < priced.Count; i++)
        {
            Priced p = priced[i];

            result.Add(new NamespaceCost
            {
                Namespace = p.Consumption.Namespace,
                ClusterId = clusterId,
                ClusterName = clusterName,
                CustomerId = p.Owner.CustomerId,
                CustomerName = p.Owner.CustomerName,
                Apps = p.Owner.Apps,
                EnvironmentName = p.Owner.EnvironmentName,
                CpuCores = p.Consumption.CpuCores,
                MemoryGiB = p.Consumption.MemoryGiB,
                StorageGiB = p.Consumption.StorageGiB,
                LoadBalancers = p.Consumption.LoadBalancers,
                PublicIps = p.Consumption.PublicIps,
                CpuMonthlyCost = p.Cpu,
                MemoryMonthlyCost = p.Memory,
                StorageMonthlyCost = p.Storage,
                NetworkMonthlyCost = p.Network,
                SharedMonthlyCost = shared[i],
                ShareOfCluster = share[i],
                IsRedistributed = redistributed[i],
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
