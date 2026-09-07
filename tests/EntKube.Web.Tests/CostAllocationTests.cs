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
        decimal cpu = 0.03m, decimal memory = 0.004m, decimal storage = 0.10m,
        decimal overhead = 0m, decimal loadBalancer = 0m) => new()
    {
        Id = Guid.NewGuid(),
        ClusterId = Guid.NewGuid(),
        CpuCoreHourCost = cpu,
        MemoryGiBHourCost = memory,
        StorageGiBMonthCost = storage,
        ClusterMonthlyOverhead = overhead,
        LoadBalancerMonthlyCost = loadBalancer,
        Currency = "USD",
    };

    private static readonly Func<string, NamespaceOwner> Unattributed = _ => NamespaceOwner.None;

    /// <summary>An owner for a namespace that belongs to one customer and one app.</summary>
    private static NamespaceOwner Owned(
        string app = "storefront", string customer = "Acme", string environment = "Production") =>
        new()
        {
            CustomerId = Guid.NewGuid(),
            CustomerName = customer,
            Apps = [new AppRef(Guid.NewGuid(), app)],
            EnvironmentName = environment,
        };

    /// <summary>Attributes every namespace to its own customer, so nothing is pooled.</summary>
    private static readonly Func<string, NamespaceOwner> AllOwned = ns => Owned(app: ns);

    private static IReadOnlyList<NamespaceCost> Allocate(
        IReadOnlyList<NamespaceConsumption> consumption,
        ClusterCostRate rate,
        Func<string, NamespaceOwner>? attribute = null) =>
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

    // ── Sharing the pool ──
    //
    // Everything EntKube has no deployment record for — platform namespaces plus the
    // cluster's fixed fee — is pooled and charged to the namespaces that are attributed,
    // in proportion to the capacity each holds. These tests pin that split, because it is
    // the part a customer will ask to have justified.

    [Fact]
    public void The_pool_is_shared_in_proportion_to_consumption_not_evenly()
    {
        // A namespace running one small pod should not carry the same share of a
        // control-plane fee as one running most of the cluster.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "big", CpuCores = 9 },
            new NamespaceConsumption { Namespace = "small", CpuCores = 1 },
        ], Rate(cpu: 0.03m, overhead: 100m), AllOwned);

        costs.Single(c => c.Namespace == "big").SharedMonthlyCost.Should().Be(90.00m);
        costs.Single(c => c.Namespace == "small").SharedMonthlyCost.Should().Be(10.00m);
    }

    [Fact]
    public void Shares_reconcile_to_the_full_pool_to_the_cent()
    {
        // The point of spreading the pool is that the total matches the real bill. Rounding
        // each share independently would leave pennies unbilled, so the residual is assigned.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 3 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 5 },
            new NamespaceConsumption { Namespace = "c", MemoryGiB = 16 },
        ], Rate(overhead: 300m), AllOwned);

        costs.Sum(c => c.SharedMonthlyCost).Should().Be(300m);
    }

    [Fact]
    public void An_awkward_split_still_reconciles_exactly()
    {
        // Three equal namespaces over $100 is $33.33 each, which loses a cent.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "c", CpuCores = 1 },
        ], Rate(overhead: 100m), AllOwned);

        costs.Sum(c => c.SharedMonthlyCost).Should().Be(100m);
    }

    [Fact]
    public void Memory_counts_toward_the_share_as_well_as_cpu()
    {
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "cpu-only", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "mem-only", MemoryGiB = 100 },
        ], Rate(overhead: 100m), AllOwned);

        costs.Single(c => c.Namespace == "mem-only").SharedMonthlyCost.Should().BeGreaterThan(0m);
    }

    [Fact]
    public void Storage_counts_toward_the_share_too()
    {
        // A storage-heavy app is a large tenant of the cluster even when it barely computes,
        // and the shared services backing it — backups, monitoring, the log stack — scale
        // with what it stores.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "compute", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "archive", StorageGiB = 5000 },
        ], Rate(cpu: 0.03m, storage: 0.10m, overhead: 100m), AllOwned);

        // archive: 5000 × $0.10 = $500. compute: 1 × $0.03 × 730 = $21.90.
        costs.Single(c => c.Namespace == "archive").SharedMonthlyCost
            .Should().BeApproximately(500m / 521.90m * 100m, 0.01m);
    }

    [Fact]
    public void Network_charges_do_not_enlarge_a_share()
    {
        // A load balancer is billed straight to the namespace that provisioned it. Letting
        // it also grow that namespace's share of the platform would charge for it twice.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 1, LoadBalancers = 10 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 1 },
        ], Rate(overhead: 200m, loadBalancer: 100m), AllOwned);

        costs.Single(c => c.Namespace == "a").SharedMonthlyCost.Should().Be(100m);
        costs.Single(c => c.Namespace == "b").SharedMonthlyCost.Should().Be(100m);
    }

    [Fact]
    public void The_pool_is_split_evenly_when_nothing_holds_any_capacity()
    {
        // Dropping it instead would make the reported total understate the real bill.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a" },
            new NamespaceConsumption { Namespace = "b" },
        ], Rate(overhead: 100m), AllOwned);

        costs.Should().OnlyContain(c => c.SharedMonthlyCost == 50.00m);
    }

    [Fact]
    public void Nothing_to_share_means_no_shared_line()
    {
        Allocate([new NamespaceConsumption { Namespace = "a", CpuCores = 1 }], Rate(overhead: 0m), AllOwned)
            .Single().SharedMonthlyCost.Should().Be(0m);
    }

    // ── What goes into the pool ──

    [Fact]
    public void A_namespace_with_no_deployment_record_is_pooled_onto_the_ones_that_have_them()
    {
        // The platform exists to serve the workloads, so the workloads pay for it. Leaving
        // it charged to nobody means the operator silently absorbs it.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "acme", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "monitoring", CpuCores = 3 },
        ], Rate(cpu: 0.03m), ns => ns == "acme" ? Owned() : NamespaceOwner.None);

        NamespaceCost acme = costs.Single(c => c.Namespace == "acme");
        NamespaceCost monitoring = costs.Single(c => c.Namespace == "monitoring");

        // monitoring's whole cost lands on acme, the only namespace there is to bill.
        monitoring.IsRedistributed.Should().BeTrue();
        acme.SharedMonthlyCost.Should().Be(monitoring.DirectMonthlyCost);
        acme.TotalMonthlyCost.Should().Be(acme.DirectMonthlyCost + monitoring.DirectMonthlyCost);
    }

    [Fact]
    public void A_pooled_namespace_keeps_its_own_figures_so_the_pool_can_be_audited()
    {
        // Zeroing it would hide what is being charged out, and an unexpected workload in
        // the pool is billed to every customer — exactly the thing worth being able to see.
        NamespaceCost pooled = Allocate(
        [
            new NamespaceConsumption { Namespace = "acme", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "rogue", CpuCores = 4 },
        ], Rate(cpu: 0.03m), ns => ns == "acme" ? Owned() : NamespaceOwner.None)
            .Single(c => c.Namespace == "rogue");

        pooled.DirectMonthlyCost.Should().BeGreaterThan(0m);
        pooled.SharedMonthlyCost.Should().Be(0m);
        pooled.IsRedistributed.Should().BeTrue();
    }

    [Fact]
    public void Storage_in_the_pool_is_shared_even_when_one_app_caused_it()
    {
        // The log stack's 5 TiB is charged by capacity share, not to whoever generated the
        // logs: EntKube cannot attribute a shared volume's contents, and guessing would put
        // an unfounded number on an invoice.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "chatty", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "quiet", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "logging", StorageGiB = 5000 },
        ], Rate(cpu: 0.03m, storage: 0.10m),
            ns => ns is "chatty" or "quiet" ? Owned(app: ns) : NamespaceOwner.None);

        costs.Single(c => c.Namespace == "chatty").SharedMonthlyCost
            .Should().Be(costs.Single(c => c.Namespace == "quiet").SharedMonthlyCost);
    }

    [Fact]
    public void The_pool_stays_unattributed_when_there_is_nobody_to_charge_it_to()
    {
        // A cluster running only platform components has no app to absorb the fixed fee.
        // Spreading it over the platform namespaces keeps it in the total; inventing a
        // recipient would not.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "kube-system", CpuCores = 1 },
            new NamespaceConsumption { Namespace = "monitoring", CpuCores = 1 },
        ], Rate(overhead: 100m));

        costs.Should().OnlyContain(c => !c.IsRedistributed);
        costs.Sum(c => c.SharedMonthlyCost).Should().Be(100m);
    }

    // ── Attribution ──

    [Fact]
    public void An_attributed_namespace_carries_its_customer_app_and_environment()
    {
        Guid customerId = Guid.NewGuid();
        Guid appId = Guid.NewGuid();

        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "acme-prod", CpuCores = 1 }],
            Rate(),
            _ => new NamespaceOwner
            {
                CustomerId = customerId,
                CustomerName = "Acme",
                Apps = [new AppRef(appId, "storefront")],
                EnvironmentName = "Production",
            }).Single();

        cost.CustomerId.Should().Be(customerId);
        cost.CustomerName.Should().Be("Acme");
        cost.AppId.Should().Be(appId);
        cost.AppName.Should().Be("storefront");
        cost.EnvironmentName.Should().Be("Production");
        cost.IsUnattributed.Should().BeFalse();
        cost.IsMultiApp.Should().BeFalse();
    }

    [Fact]
    public void A_namespace_shared_by_two_apps_is_attributed_to_neither()
    {
        // Consumption is measured per namespace. Handing the whole figure to one of the
        // two apps would put a number on an invoice that nothing measured.
        NamespaceCost cost = Allocate(
            [new NamespaceConsumption { Namespace = "shared", CpuCores = 1 }],
            Rate(),
            _ => new NamespaceOwner
            {
                CustomerId = Guid.NewGuid(),
                CustomerName = "Acme",
                Apps = [new AppRef(Guid.NewGuid(), "api"), new AppRef(Guid.NewGuid(), "worker")],
            }).Single();

        cost.IsMultiApp.Should().BeTrue();
        cost.AppId.Should().BeNull();
        cost.AppName.Should().Be("2 apps");

        // Still billed — it belongs to a customer, just not to one app.
        cost.IsUnattributed.Should().BeFalse();
        cost.IsRedistributed.Should().BeFalse();
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

    [Fact]
    public void The_share_of_a_cluster_is_reported_alongside_the_money()
    {
        // It is the number an operator gets asked to justify, so it is not left implicit
        // in the arithmetic.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "big", CpuCores = 3 },
            new NamespaceConsumption { Namespace = "small", CpuCores = 1 },
        ], Rate(overhead: 100m), AllOwned);

        costs.Single(c => c.Namespace == "big").ShareOfCluster.Should().BeApproximately(0.75d, 0.0001d);
        costs.Single(c => c.Namespace == "small").ShareOfCluster.Should().BeApproximately(0.25d, 0.0001d);
    }

    // ── Report roll-ups ──

    private static CostReport Report(params NamespaceCost[] namespaces) =>
        new() { Namespaces = namespaces, GeneratedAt = DateTime.UtcNow };

    private static NamespaceCost Cost(
        string ns, decimal cpuCost, Guid? customerId = null,
        string? customerName = null, string? environment = null,
        decimal sharedCost = 0m, bool redistributed = false,
        params AppRef[] apps) => new()
    {
        Namespace = ns,
        ClusterId = Guid.NewGuid(),
        ClusterName = "c",
        CustomerId = customerId,
        CustomerName = customerName,
        EnvironmentName = environment,
        Apps = apps,
        CpuMonthlyCost = cpuCost,
        SharedMonthlyCost = sharedCost,
        IsRedistributed = redistributed,
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
        // Nothing was pooled here, so the platform namespace still stands on its own.
        CostReport report = Report(
            Cost("acme-prod", 60m, Guid.NewGuid(), "Acme"),
            Cost("kube-system", 40m));

        report.ByCustomer.Sum(c => c.MonthlyCost).Should().Be(60m);
        report.UnattributedMonthlyCost.Should().Be(40m);
        report.TotalMonthlyCost.Should().Be(100m);
    }

    [Fact]
    public void A_pooled_namespace_is_not_counted_again_in_the_total()
    {
        // Its $40 was charged out to acme-prod. Adding both would report $140 for a $100
        // cluster — the failure mode the redistributed flag exists to prevent.
        CostReport report = Report(
            Cost("acme-prod", 60m, Guid.NewGuid(), "Acme", sharedCost: 40m),
            Cost("kube-system", 40m, redistributed: true));

        report.TotalMonthlyCost.Should().Be(100m);
        report.UnattributedMonthlyCost.Should().Be(0m);
        report.SharedPoolMonthlyCost.Should().Be(40m);
        report.SharedPool.Should().ContainSingle(n => n.Namespace == "kube-system");
    }

    // ── Per-app roll-up ──

    [Fact]
    public void Report_groups_cost_by_app_including_each_apps_share_of_the_platform()
    {
        Guid acme = Guid.NewGuid();
        AppRef storefront = new(Guid.NewGuid(), "storefront");
        AppRef billing = new(Guid.NewGuid(), "billing");

        CostReport report = Report(
            Cost("sf-prod", 60m, acme, "Acme", "Production", sharedCost: 30m, apps: [storefront]),
            Cost("sf-test", 20m, acme, "Acme", "Staging", sharedCost: 10m, apps: [storefront]),
            Cost("bill-prod", 10m, acme, "Acme", "Production", sharedCost: 5m, apps: [billing]),
            Cost("monitoring", 45m, redistributed: true));

        report.ByApp.Should().HaveCount(2);

        AppCost first = report.ByApp[0];
        first.AppId.Should().Be(storefront.AppId);
        first.AppName.Should().Be("storefront");
        first.DirectMonthlyCost.Should().Be(80m);
        first.SharedMonthlyCost.Should().Be(40m);
        first.TotalMonthlyCost.Should().Be(120m);
        first.Environments.Should().Equal("Production", "Staging");
        first.Namespaces.Should().Equal("sf-prod", "sf-test");

        report.ByApp[1].AppName.Should().Be("billing");
        report.ByApp[1].TotalMonthlyCost.Should().Be(15m);
    }

    [Fact]
    public void Per_app_totals_reconcile_to_the_whole_bill()
    {
        // Every attributed namespace belongs to exactly one app here, so the app roll-up
        // must add up to the same number the tenant total reports.
        Guid acme = Guid.NewGuid();

        CostReport report = Report(
            Cost("a", 60m, acme, "Acme", sharedCost: 30m, apps: [new AppRef(Guid.NewGuid(), "a")]),
            Cost("b", 10m, acme, "Acme", sharedCost: 5m, apps: [new AppRef(Guid.NewGuid(), "b")]),
            Cost("platform", 35m, redistributed: true));

        report.ByApp.Sum(a => a.TotalMonthlyCost).Should().Be(report.TotalMonthlyCost);
        report.ByApp.Sum(a => a.ShareOfBillable).Should().BeApproximately(1d, 0.0001d);
    }

    [Fact]
    public void A_namespace_shared_by_apps_is_reported_beside_the_per_app_view_not_inside_it()
    {
        Guid acme = Guid.NewGuid();

        CostReport report = Report(
            Cost("solo", 60m, acme, "Acme", sharedCost: 0m, apps: [new AppRef(Guid.NewGuid(), "solo")]),
            Cost("shared", 40m, acme, "Acme", apps:
                [new AppRef(Guid.NewGuid(), "api"), new AppRef(Guid.NewGuid(), "worker")]));

        report.ByApp.Should().ContainSingle();
        report.MultiAppMonthlyCost.Should().Be(40m);

        // Still the customer's money, just not resolvable to one app.
        report.ByCustomer.Single().MonthlyCost.Should().Be(100m);
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
            _ => NamespaceOwner.None);

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
    public void Network_cost_does_not_change_how_the_fixed_fee_is_shared()
    {
        // The pool is spread by the capacity a namespace holds. Letting load balancers
        // influence that split would charge a namespace twice for the same thing.
        IReadOnlyList<NamespaceCost> costs = Allocate(
        [
            new NamespaceConsumption { Namespace = "a", CpuCores = 1, LoadBalancers = 10 },
            new NamespaceConsumption { Namespace = "b", CpuCores = 1 },
        ], Rate(loadBalancer: 100m, overhead: 200m));

        costs.Single(c => c.Namespace == "a").SharedMonthlyCost.Should().Be(100m);
        costs.Single(c => c.Namespace == "b").SharedMonthlyCost.Should().Be(100m);
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
