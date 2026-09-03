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
