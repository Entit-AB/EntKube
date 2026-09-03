using EntKube.Web.Data;
using EntKube.Web.Services.Cost;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for cost allocation. These rules end up on a customer invoice, so they are
/// pinned precisely: the arithmetic, how fixed overhead is shared, and what happens
/// to consumption that cannot be attributed to anyone.
/// </summary>
public class CostAllocationTests
{
    private static ClusterCostRate Rate(
        decimal cpu = 0.03m, decimal memory = 0.004m, decimal storage = 0.10m, decimal overhead = 0m) => new()
    {
        Id = Guid.NewGuid(),
        ClusterId = Guid.NewGuid(),
        CpuCoreHourCost = cpu,
        MemoryGiBHourCost = memory,
        StorageGiBMonthCost = storage,
        ClusterMonthlyOverhead = overhead,
        Currency = "USD",
    };

    private static readonly Func<string, (Guid?, string?, string?, string?)> Unattributed =
        _ => (null, null, null, null);

    private static IReadOnlyList<NamespaceCost> Allocate(
        IReadOnlyList<NamespaceConsumption> consumption,
        ClusterCostRate rate,
        Func<string, (Guid?, string?, string?, string?)>? attribute = null) =>
        CostAllocation.Allocate(
            consumption, rate, Guid.NewGuid(), "prod-eu-west-1", attribute ?? Unattributed);

    // ── The arithmetic ──

    [Fact]
    public void Cpu_cost_is_cores_times_hourly_rate_times_a_730_hour_month()
    {
        IReadOnlyList<NamespaceCost> costs = Allocate(
            [new NamespaceConsumption { Namespace = "acme", CpuCores = 2 }],
            Rate(cpu: 0.03m));

        // 2 cores × $0.03/core-hour × 730 h = $43.80
        costs.Single().CpuMonthlyCost.Should().Be(43.80m);
    }

    [Fact]
    public void Memory_cost_is_gib_times_hourly_rate_times_a_730_hour_month()
    {
        IReadOnlyList<NamespaceCost> costs = Allocate(
            [new NamespaceConsumption { Namespace = "acme", MemoryGiB = 8 }],
            Rate(memory: 0.004m));

        // 8 GiB × $0.004/GiB-hour × 730 h = $23.36
        costs.Single().MemoryMonthlyCost.Should().Be(23.36m);
    }

    [Fact]
    public void Storage_is_priced_per_month_directly_not_per_hour()
    {
        // The storage rate is already monthly, so multiplying by 730 would inflate it 730×.
        IReadOnlyList<NamespaceCost> costs = Allocate(
            [new NamespaceConsumption { Namespace = "acme", StorageGiB = 100 }],
            Rate(storage: 0.10m));

        costs.Single().StorageMonthlyCost.Should().Be(10.00m);
    }

    [Fact]
    public void Total_is_the_sum_of_the_resource_lines()
    {
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme", CpuCores = 2, MemoryGiB = 8, StorageGiB = 100 }],
            Rate()).Single();

        cost.TotalMonthlyCost.Should().Be(43.80m + 23.36m + 10.00m);
    }

    [Fact]
    public void Hourly_run_rate_is_the_monthly_figure_over_the_same_730_hours()
    {
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme", CpuCores = 2 }],
            Rate(cpu: 0.03m)).Single();

        // Must reconcile with the monthly figure, not be computed independently.
        cost.HourlyCost.Should().BeApproximately(0.06m, 0.0001m);
    }

    [Fact]
    public void Costs_are_rounded_to_cents()
    {
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "a", CpuCores = 0.001 }],
            Rate(cpu: 0.0333m)).Single();

        decimal.Round(cost.CpuMonthlyCost, 2).Should().Be(cost.CpuMonthlyCost);
    }

    [Fact]
    public void A_namespace_consuming_nothing_costs_nothing()
    {
        Allocate([new NamespaceConsumption { Namespace = "idle" }], Rate())
            .Single().TotalMonthlyCost.Should().Be(0m);
    }

    [Fact]
    public void No_consumption_produces_no_rows()
    {
        Allocate([], Rate(overhead: 500m)).Should().BeEmpty();
    }

    // ── Fixed overhead ──

    [Fact]
    public void Overhead_is_shared_in_proportion_to_compute_not_evenly()
    {
        // A namespace running one small pod should not carry the same share of a
        // control-plane fee as one running most of the cluster.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "big", CpuCores = 9 },
            new NamespaceConsumption { Namespace = "small", CpuCores = 1 },
        ], Rate(cpu: 0.03m, overhead: 100m));

        costs.Single(c => c.Namespace == "big").OverheadMonthlyCost.Should().Be(90.00m);
        costs.Single(c => c.Namespace == "small").OverheadMonthlyCost.Should().Be(10.00m);
    }

    [Fact]
    public void Overhead_shares_reconcile_to_the_full_overhead()
    {
        // The point of spreading overhead is that the total matches the real bill.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 3 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 5 },
            new NamespaceConsumption { Namespace = "c", MemoryGiB = 16 },
        ], Rate(overhead: 300m));

        costs.Sum(c => c.OverheadMonthlyCost).Should().BeApproximately(300m, 0.05m);
    }

    [Fact]
    public void Memory_counts_toward_the_overhead_share_as_well_as_cpu()
    {
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "cpu-only", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "mem-only", MemoryGiB = 100 },
        ], Rate(overhead: 100m));

        costs.Single(c => c.Namespace == "mem-only").OverheadMonthlyCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Overhead_is_split_evenly_when_nothing_consumes_compute()
    {
        // Dropping it instead would make the reported total understate the real bill.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", StorageGiB = 10 },
            new NamespaceConsumption { Namespace = "b", StorageGiB = 10 },
        ], Rate(overhead: 100m));

        costs.Should().OnlyContain(c => c.OverheadMonthlyCost == 50.00m);
    }

    [Fact]
    public void No_overhead_configured_means_no_overhead_line()
    {
        Allocate([new NamespaceConsumption { Namespace = "a", CpuCores = 1 }], Rate(overhead: 0m))
            .Single().OverheadMonthlyCost.Should().Be(0m);
    }

    // ── Attribution ──

    [Fact]
    public void An_attributed_namespace_carries_its_customer_app_and_environment()
    {
        Guid customerId = Guid.NewGuid();

        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme-prod", CpuCores = 1 }],
            Rate(),
            _ => (customerId, "Acme", "storefront", "Production")).Single();

        cost.CustomerId.Should().Be(customerId);
        cost.CustomerName.Should().Be("Acme");
        cost.AppName.Should().Be("storefront");
        cost.EnvironmentName.Should().Be("Production");
        cost.IsUnattributed.Should().BeFalse();
    }

    [Fact]
    public void An_unowned_namespace_is_still_costed_but_marked_unattributed()
    {
        // Platform namespaces (ingress, monitoring) cost real money. Dropping them would
        // make the tenant total understate the bill; hiding the fact would misreport
        // whose cost it is.
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "kube-system", CpuCores = 2 }],
            Rate()).Single();

        cost.IsUnattributed.Should().BeTrue();
        cost.TotalMonthlyCost.Should().BeGreaterThan(0m);
    }

    // ── Report roll-ups ──

    private static CostReport Report(params NamespaceCost[] namespaces) =>
        new() { Namespaces = namespaces, GeneratedAt = DateTime.UtcNow };

    private static NamespaceCost Cost(
        string ns, decimal cpuCost, Guid? customerId = null,
        string? customerName = null, string? environment = null) => new()
    {
        Namespace = ns,
        ClusterId = Guid.NewGuid(),
        ClusterName = "c",
        CustomerId = customerId,
        CustomerName = customerName,
        EnvironmentName = environment,
        CpuMonthlyCost = cpuCost,
    };

    [Fact]
    public void Report_groups_cost_by_customer_largest_first()
    {
        Guid acme = Guid.NewGuid();
        Guid globex = Guid.NewGuid();

        CostReport report = Report(
            Cost("a", 10m, acme, "Acme"),
            Cost("b", 5m, acme, "Acme"),
            Cost("c", 100m, globex, "Globex"));

        report.ByCustomer.Should().HaveCount(2);
        report.ByCustomer[0].Should().Be((globex, "Globex", 100m));
        report.ByCustomer[1].Should().Be((acme, "Acme", 15m));
    }

    [Fact]
    public void Report_excludes_unattributed_cost_from_the_per_customer_view_but_not_the_total()
    {
        CostReport report = Report(
            Cost("acme-prod", 60m, Guid.NewGuid(), "Acme"),
            Cost("kube-system", 40m));

        report.ByCustomer.Sum(c => c.MonthlyCost).Should().Be(60m);
        report.UnattributedMonthlyCost.Should().Be(40m);
        report.TotalMonthlyCost.Should().Be(100m);
    }

    [Fact]
    public void Report_groups_cost_by_environment()
    {
        CostReport report = Report(
            Cost("a", 10m, Guid.NewGuid(), "Acme", "Production"),
            Cost("b", 4m, Guid.NewGuid(), "Acme", "Staging"),
            Cost("c", 6m, Guid.NewGuid(), "Globex", "Production"));

        report.ByEnvironment[0].Should().Be(("Production", 16m));
        report.ByEnvironment[1].Should().Be(("Staging", 4m));
    }

    [Fact]
    public void Report_hourly_total_reconciles_with_the_monthly_total()
    {
        CostReport report = Report(Cost("a", 730m));

        report.TotalHourlyCost.Should().Be(1m);
    }

    [Fact]
    public void Bytes_convert_to_gibibytes_not_gigabytes()
    {
        // Kubernetes reports binary units; using 10^9 would under-count memory by ~7%.
        CostAllocation.BytesToGiB(1024d * 1024d * 1024d).Should().Be(1d);
    }
}

/// <summary>
/// Tests for the per-month network charges OpenStack providers bill separately —
/// a load balancer and its public IPv4. Cleura, for one, bills Octavia per load
/// balancer and IPv4 per address, and neither was modelled before.
/// </summary>
public class NetworkCostTests
{
    private static ClusterCostRate Rate(decimal loadBalancer = 0m, decimal publicIp = 0m, decimal overhead = 0m) => new()
    {
        Id = Guid.NewGuid(),
        ClusterId = Guid.NewGuid(),
        LoadBalancerMonthlyCost = loadBalancer,
        PublicIpMonthlyCost = publicIp,
        ClusterMonthlyOverhead = overhead,
        Currency = "SEK",
    };

    private static IReadOnlyList<NamespaceCost> Allocate(
        IReadOnlyList<NamespaceConsumption> consumption, ClusterCostRate rate) =>
        CostAllocation.Allocate(consumption, rate, Guid.NewGuid(), "prod",
            _ => (null, null, null, null));

    [Fact]
    public void A_load_balancer_is_charged_by_the_month_not_the_hour()
    {
        // Clouds bill a load balancer per month. Multiplying by the 730-hour month, as
        // the compute rates are, would overstate it by three orders of magnitude.
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme", LoadBalancers = 1 }],
            Rate(loadBalancer: 120m)).Single();

        cost.NetworkMonthlyCost.Should().Be(120m);
    }

    [Fact]
    public void Each_load_balancer_and_public_ip_is_counted()
    {
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme", LoadBalancers = 3, PublicIps = 3 }],
            Rate(loadBalancer: 100m, publicIp: 25m)).Single();

        cost.NetworkMonthlyCost.Should().Be(3 * 100m + 3 * 25m);
    }

    [Fact]
    public void Network_cost_is_attributed_not_spread()
    {
        // A namespace that provisions load balancers causes those charges. Burying them
        // in shared overhead would bill every other customer for them.
        Guid other = Guid.NewGuid();
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "acme", CpuCores = 1, LoadBalancers = 2 },
            new NamespaceConsumption { Namespace = "globex", CpuCores = 1 },
        ], Rate(loadBalancer: 100m));

        costs.Single(c => c.Namespace == "acme").NetworkMonthlyCost.Should().Be(200m);
        costs.Single(c => c.Namespace == "globex").NetworkMonthlyCost.Should().Be(0m);
    }

    [Fact]
    public void Network_cost_counts_toward_the_namespace_total()
    {
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme", LoadBalancers = 1 }],
            Rate(loadBalancer: 120m)).Single();

        cost.TotalMonthlyCost.Should().Be(120m);
    }

    [Fact]
    public void An_unpriced_load_balancer_adds_nothing()
    {
        // Zero means "not billed", which is what an operator who has not entered a rate
        // means — inventing one would put a number they never agreed to on an invoice.
        Allocate([new NamespaceConsumption { Namespace = "acme", LoadBalancers = 5 }], Rate())
            .Single().NetworkMonthlyCost.Should().Be(0m);
    }

    [Fact]
    public void Network_cost_does_not_change_how_fixed_overhead_is_shared()
    {
        // Overhead is spread by COMPUTE share. Letting load balancers influence that split
        // would charge a namespace twice for the same thing.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 1, LoadBalancers = 10 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 1 },
        ], Rate(loadBalancer: 100m, overhead: 200m));

        costs.Single(c => c.Namespace == "a").OverheadMonthlyCost.Should().Be(100m);
        costs.Single(c => c.Namespace == "b").OverheadMonthlyCost.Should().Be(100m);
    }

    // ── Counting them off the cluster ──

    private const string ServicesJson = """
    {
      "items": [
        { "metadata": { "name": "web", "namespace": "acme" },
          "spec": { "type": "LoadBalancer" },
          "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.10" } ] } } },
        { "metadata": { "name": "api", "namespace": "acme" },
          "spec": { "type": "LoadBalancer" },
          "status": { "loadBalancer": { "ingress": [ { "ip": "203.0.113.11" } ] } } },
        { "metadata": { "name": "internal", "namespace": "acme" },
          "spec": { "type": "ClusterIP" } },
        { "metadata": { "name": "edge", "namespace": "globex" },
          "spec": { "type": "NodePort" } },
        { "metadata": { "name": "pending", "namespace": "globex" },
          "spec": { "type": "LoadBalancer" },
          "status": { "loadBalancer": {} } }
      ]
    }
    """;

    [Fact]
    public void Only_load_balancer_services_are_counted()
    {
        var counts = CostReportService.ParseLoadBalancerServices(ServicesJson);

        counts["acme"].LoadBalancers.Should().Be(2);
        counts["globex"].LoadBalancers.Should().Be(1);
        counts.Should().NotContainKey("");
    }

    [Fact]
    public void A_load_balancer_still_awaiting_an_address_is_not_billed_for_an_ip()
    {
        // It is provisioning a load balancer but holds no IP yet, and billing for an
        // address that does not exist would be wrong.
        var counts = CostReportService.ParseLoadBalancerServices(ServicesJson);

        counts["globex"].LoadBalancers.Should().Be(1);
        counts["globex"].PublicIps.Should().Be(0);
        counts["acme"].PublicIps.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void An_unreadable_service_list_yields_no_counts_rather_than_throwing(string? json)
    {
        // Losing the network line is better than losing the whole cost report.
        CostReportService.ParseLoadBalancerServices(json).Should().BeEmpty();
    }
}
