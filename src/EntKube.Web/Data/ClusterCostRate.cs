namespace EntKube.Web.Data;

/// <summary>
/// The price sheet for one cluster: what a unit of capacity costs, so consumption
/// can be turned into money.
///
/// Per cluster rather than global because that is where prices actually differ — a
/// bare-metal cluster and a hyperscaler cluster have nothing in common — and per
/// cluster rather than per node pool because the extra precision would require
/// modelling pools EntKube does not otherwise track. Where a cluster mixes node
/// types, these are blended rates, and the UI says so rather than implying a
/// precision the model does not have.
/// </summary>
public class ClusterCostRate
{
    public Guid Id { get; set; }

    public Guid ClusterId { get; set; }

    /// <summary>Cost of one CPU core for one hour.</summary>
    public decimal CpuCoreHourCost { get; set; }

    /// <summary>Cost of one GiB of memory for one hour.</summary>
    public decimal MemoryGiBHourCost { get; set; }

    /// <summary>Cost of one GiB of provisioned persistent storage for a 730-hour month.</summary>
    public decimal StorageGiBMonthCost { get; set; }

    /// <summary>
    /// Fixed monthly cost for the cluster itself — control-plane fees, support contracts,
    /// anything billed per cluster rather than per workload. Spread across consumers in
    /// proportion to their compute share rather than hidden, so the reported total matches
    /// the actual bill.
    /// </summary>
    public decimal ClusterMonthlyOverhead { get; set; }

    /// <summary>
    /// Cost of one load balancer for a month. OpenStack clouds bill Octavia per load
    /// balancer, and every Service of type LoadBalancer provisions one — so this is a real
    /// charge caused by a specific namespace, not shared infrastructure. Zero leaves it out.
    /// </summary>
    public decimal LoadBalancerMonthlyCost { get; set; }

    /// <summary>
    /// Cost of one public IPv4 address for a month. Charged per address by most clouds
    /// (Cleura bills IPv4 and gives IPv6 away), and a LoadBalancer Service normally holds
    /// one. Zero leaves it out.
    /// </summary>
    public decimal PublicIpMonthlyCost { get; set; }

    /// <summary>ISO currency code, used for display only — no conversion is performed.</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>
    /// Charge on requests rather than actual usage. Requests are what the scheduler
    /// reserves and therefore what the customer genuinely denies to others, which is the
    /// defensible basis for a chargeback; usage-based billing lets an over-requesting
    /// team push their waste onto everyone else.
    /// </summary>
    public bool ChargeOnRequests { get; set; } = true;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }

    // Navigation
    public KubernetesCluster Cluster { get; set; } = null!;
}
