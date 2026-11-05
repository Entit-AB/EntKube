using System.Text.Json;
using EntKube.Web.Services.Cost;
using EntKube.Web.Services.PublicApi;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// <c>GET /api/v1/cost</c> — the run rate — serialises exactly as the anonymous object it
/// replaced. Fourteen envelope fields over five nested collections, which is why it was the last
/// of the cost routes to be typed.
///
/// <para>⚠️ <b>The oracle is the OLD code, extracted from git rather than retyped.</b> The
/// anonymous literal in the first test was cut out of <c>origin/main</c>'s
/// <c>PublicApiEndpoints.cs</c> — its original indentation and comments included, so a reader can
/// diff it against the commit — which makes it an INDEPENDENT statement of the wire contract. A
/// record whose mapping and whose expected values were both written from one reading of the
/// handler would pass on a consistent misreading of it.</para>
///
/// <para>⚠️ <b>Every figure on this envelope is COMPUTED from the namespace list</b> —
/// <c>TotalMonthlyCost</c> sums the billable namespaces, <c>IdleMonthlyCost</c> the idle ones,
/// <c>SharedPoolMonthlyCost</c> every namespace's pool share, and the three breakdowns are
/// groupings of the same list. So the fixture cannot hand each field a distinct value; it has to
/// be a namespace list from which each figure FALLS OUT distinct and non-zero, and
/// <see cref="The_fixture_makes_every_envelope_figure_distinct_and_non_zero"/> is the measure that
/// it still does. Without that measure a later edit could collapse two figures to the same number
/// and the byte comparison would stop being able to tell them apart — passing while proving
/// less.</para>
/// </summary>
public class CostRunRateWireShapeTests
{
    private static readonly JsonSerializerOptions AsTheApiSerialises = new(JsonSerializerDefaults.Web);

    private static readonly Guid Customer1 = Guid.Parse("c0000001-0000-0000-0000-000000000001");
    private static readonly Guid Customer2 = Guid.Parse("c0000002-0000-0000-0000-000000000002");
    private static readonly Guid App1 = Guid.Parse("a0000001-0000-0000-0000-000000000001");
    private static readonly Guid App2 = Guid.Parse("a0000002-0000-0000-0000-000000000002");
    private static readonly Guid App3 = Guid.Parse("a0000003-0000-0000-0000-000000000003");
    private static readonly Guid Cluster1 = Guid.Parse("50000001-0000-0000-0000-000000000001");

    /// <summary>
    /// Six namespaces, one of each kind the report distinguishes, chosen so that the six computed
    /// envelope figures all come out different:
    ///
    /// <list type="bullet">
    /// <item><c>app-prod</c> — ordinary: customer 1, one app, Production. Direct 100, shared 7.</item>
    /// <item><c>shared-staging</c> — TWO apps, so no <c>appId</c> and its cost is reported through
    /// <c>multiAppMonthlyCost</c> rather than under any app. Direct 20, shared 3.</item>
    /// <item><c>acme-prod</c> — customer 2, so <c>byCustomer</c> has two rows and the
    /// largest-first ordering is exercised rather than assumed. Direct 2, shared 0.</item>
    /// <item><c>unowned</c> — no customer, so it is unattributed: billed, in the total, reaching
    /// nobody. Direct 5, shared 1.</item>
    /// <item><c>platform</c> — redistributed: its own cost was pooled onto the others, so it must
    /// be OUT of every total while still being reported. Direct 50, shared 2.</item>
    /// <item>the idle row — not a namespace but the cluster's unallocated capacity, named by
    /// <see cref="CostAllocation.IdleNamespace"/>, also redistributed. Direct 60.</item>
    /// </list>
    /// </summary>
    private static CostReport Report() => new()
    {
        GeneratedAt = new DateTime(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc),
        Currency = "SEK",
        Warnings = ["one cluster could not be priced"],
        Capacity =
        [
            new ClusterCapacityCost
            {
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                HasCapacity = true,
                Nodes = 3d,
                CpuCapacity = 24d,
                CpuAllocated = 11d,
                MemoryCapacityGiB = 96d,
                MemoryAllocatedGiB = 41d,
                NodeMonthlyCost = 9000m,
                IdleMonthlyCost = 60m,
                IdleCharged = true,
                TotalMonthlyCost = 8888m,
            },
        ],
        Namespaces =
        [
            new NamespaceCost
            {
                Namespace = "app-prod",
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CustomerId = Customer1,
                CustomerName = "Acme AB",
                EnvironmentName = "Production",
                Apps = [new AppRef(App1, "Billing")],
                CpuCores = 4d,
                MemoryGiB = 8d,
                StorageGiB = 16d,
                LoadBalancers = 1,
                PublicIps = 2,
                CpuMonthlyCost = 10m,
                MemoryMonthlyCost = 20m,
                StorageMonthlyCost = 30m,
                NetworkMonthlyCost = 40m,
                SharedMonthlyCost = 7m,
                ShareOfCluster = 0.5d,
            },
            new NamespaceCost
            {
                Namespace = "shared-staging",
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CustomerId = Customer1,
                CustomerName = "Acme AB",
                EnvironmentName = "Staging",
                Apps = [new AppRef(App1, "Billing"), new AppRef(App3, "Portal")],
                CpuCores = 1d,
                MemoryGiB = 2d,
                StorageGiB = 3d,
                LoadBalancers = 0,
                PublicIps = 0,
                CpuMonthlyCost = 1m,
                MemoryMonthlyCost = 2m,
                StorageMonthlyCost = 3m,
                NetworkMonthlyCost = 14m,
                SharedMonthlyCost = 3m,
                ShareOfCluster = 0.1d,
            },
            new NamespaceCost
            {
                Namespace = "acme-prod",
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CustomerId = Customer2,
                CustomerName = "Beta Industries",
                EnvironmentName = "Production",
                Apps = [new AppRef(App2, "Storefront")],
                CpuCores = 0.25d,
                MemoryGiB = 0.5d,
                StorageGiB = 0d,
                CpuMonthlyCost = 2m,
                ShareOfCluster = 0.01d,
            },
            new NamespaceCost
            {
                Namespace = "unowned",
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CpuCores = 0.5d,
                CpuMonthlyCost = 5m,
                SharedMonthlyCost = 1m,
                ShareOfCluster = 0.02d,
            },
            new NamespaceCost
            {
                Namespace = "platform",
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CpuMonthlyCost = 50m,
                SharedMonthlyCost = 2m,
                IsRedistributed = true,
                ShareOfCluster = 0.2d,
            },
            new NamespaceCost
            {
                Namespace = CostAllocation.IdleNamespace,
                ClusterId = Cluster1,
                ClusterName = "prod-north",
                CpuCores = 6d,
                MemoryGiB = 24d,
                CpuMonthlyCost = 60m,
                IsRedistributed = true,
            },
        ],
    };

    /// <summary>
    /// ⚠️ The measure for the comparison below. The byte comparison can only distinguish two
    /// fields that hold different values, and this fixture's values are not assigned — they are
    /// summed out of the namespace list. If an edit to the list makes two of these figures equal,
    /// a mapping that swapped them would still pass, so the fixture's own adequacy is asserted.
    /// </summary>
    [Fact]
    public void The_fixture_makes_every_envelope_figure_distinct_and_non_zero()
    {
        CostReport report = Report();

        // app-prod 107 + shared-staging 23 + acme-prod 2 + unowned 6. The two redistributed
        // rows are out: their cost was charged onto these, and counting both counts it twice.
        report.TotalMonthlyCost.Should().Be(138m);
        report.UnattributedMonthlyCost.Should().Be(6m);
        report.MultiAppMonthlyCost.Should().Be(23m);
        report.SharedPoolMonthlyCost.Should().Be(13m);
        report.IdleMonthlyCost.Should().Be(60m);

        decimal[] figures =
        [
            report.TotalMonthlyCost,
            report.TotalHourlyCost,
            report.UnattributedMonthlyCost,
            report.MultiAppMonthlyCost,
            report.SharedPoolMonthlyCost,
            report.IdleMonthlyCost,
        ];

        figures.Should().OnlyContain(f => f != 0m, "a zero cannot be told from an unset field");
        figures.Should().OnlyHaveUniqueItems(
            "two equal figures make a transposition between them invisible to the byte comparison");

        // And the breakdowns have more than one row each, so their ordering is exercised.
        report.ByCustomer.Should().HaveCount(2);
        report.ByApp.Should().HaveCount(2);
        report.ByEnvironment.Should().HaveCount(2);
        report.Namespaces.Should().HaveCount(6);
        report.Capacity.Should().HaveCount(1);
    }

    [Fact]
    public void The_run_rate_serialises_exactly_as_the_anonymous_object_it_replaced()
    {
        CostReport report = Report();

        // Copied out of origin/main's handler — the independent oracle, original indentation
        // and comments kept so it can be diffed against the commit it came from.
        object asItWas =
new
            {
                generatedAt = report.GeneratedAt,
                currency = report.Currency,
                totalMonthlyCost = report.TotalMonthlyCost,
                totalHourlyCost = report.TotalHourlyCost,
                unattributedMonthlyCost = report.UnattributedMonthlyCost,
                sharedPoolMonthlyCost = report.SharedPoolMonthlyCost,
                // Node capacity nothing holds, priced. Inside the shared pool and the
                // total; reported apart because it is what makes the total approach
                // the invoice rather than the sum of what was requested.
                idleMonthlyCost = report.IdleMonthlyCost,
                multiAppMonthlyCost = report.MultiAppMonthlyCost,
                warnings = report.Warnings,
                capacity = report.Capacity.Select(c => new
                {
                    clusterId = c.ClusterId,
                    cluster = c.ClusterName,
                    hasCapacity = c.HasCapacity,
                    nodes = c.Nodes,
                    cpuCapacity = c.CpuCapacity,
                    cpuAllocated = c.CpuAllocated,
                    memoryCapacityGiB = c.MemoryCapacityGiB,
                    memoryAllocatedGiB = c.MemoryAllocatedGiB,
                    // What the nodes cost at the compute rates whatever runs on them —
                    // the figure to hold up against the provider's invoice.
                    nodeMonthlyCost = c.NodeMonthlyCost,
                    idleMonthlyCost = c.IdleMonthlyCost,
                    idleCharged = c.IdleCharged,
                    monthlyCost = c.TotalMonthlyCost,
                }),
                byCustomer = report.ByCustomer.Select(c => new
                {
                    customerId = c.CustomerId,
                    customer = c.CustomerName,
                    monthlyCost = c.MonthlyCost,
                }),
                byApp = report.ByApp.Select(a => new
                {
                    appId = a.AppId,
                    app = a.AppName,
                    customerId = a.CustomerId,
                    customer = a.CustomerName,
                    environments = a.Environments,
                    namespaces = a.Namespaces,
                    cpuCores = a.CpuCores,
                    memoryGiB = a.MemoryGiB,
                    storageGiB = a.StorageGiB,
                    directMonthlyCost = a.DirectMonthlyCost,
                    sharedMonthlyCost = a.SharedMonthlyCost,
                    monthlyCost = a.TotalMonthlyCost,
                    shareOfBillable = a.ShareOfBillable,
                }),
                byEnvironment = report.ByEnvironment.Select(e => new
                {
                    environment = e.Environment,
                    monthlyCost = e.MonthlyCost,
                }),
                namespaces = report.Namespaces.Select(n => new
                {
                    ns = n.Namespace,
                    cluster = n.ClusterName,
                    customer = n.CustomerName,
                    app = n.AppName,
                    appId = n.AppId,
                    environment = n.EnvironmentName,
                    cpuCores = n.CpuCores,
                    memoryGiB = n.MemoryGiB,
                    storageGiB = n.StorageGiB,
                    loadBalancers = n.LoadBalancers,
                    publicIps = n.PublicIps,
                    directMonthlyCost = n.DirectMonthlyCost,
                    sharedMonthlyCost = n.SharedMonthlyCost,
                    shareOfCluster = n.ShareOfCluster,
                    monthlyCost = n.TotalMonthlyCost,
                    unattributed = n.IsUnattributed,
                    // Its cost was pooled onto the billable namespaces; it is reported so
                    // the pool can be audited, but it is not part of any total.
                    redistributed = n.IsRedistributed,
                    // Not a namespace: the cluster's unallocated node capacity, carried
                    // as a line so it can be pooled and audited like one.
                    idle = n.IsIdle,
                }),
            };

        JsonSerializer.Serialize(CostRunRateResponse.From(report), AsTheApiSerialises)
            .Should().Be(JsonSerializer.Serialize(asItWas, AsTheApiSerialises));
    }

    /// <summary>
    /// Named as well as compared, so a failure says WHICH key moved instead of printing two very
    /// long strings. Every nested shape is checked: a client reading <c>namespaces[].ns</c> breaks
    /// just as hard as one reading <c>totalMonthlyCost</c>.
    /// </summary>
    [Fact]
    public void The_run_rate_keys_are_the_ones_clients_already_read()
    {
        System.Text.Json.Nodes.JsonObject root =
            JsonSerializer.SerializeToNode(
                CostRunRateResponse.From(Report()), AsTheApiSerialises)!.AsObject();

        root.Select(p => p.Key).Should().Equal(
            "generatedAt", "currency", "totalMonthlyCost", "totalHourlyCost",
            "unattributedMonthlyCost", "sharedPoolMonthlyCost", "idleMonthlyCost",
            "multiAppMonthlyCost", "warnings", "capacity", "byCustomer", "byApp",
            "byEnvironment", "namespaces");

        root["capacity"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "clusterId", "cluster", "hasCapacity", "nodes", "cpuCapacity", "cpuAllocated",
            "memoryCapacityGiB", "memoryAllocatedGiB", "nodeMonthlyCost", "idleMonthlyCost",
            "idleCharged", "monthlyCost");

        root["byCustomer"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "customerId", "customer", "monthlyCost");

        root["byApp"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "appId", "app", "customerId", "customer", "environments", "namespaces", "cpuCores",
            "memoryGiB", "storageGiB", "directMonthlyCost", "sharedMonthlyCost", "monthlyCost",
            "shareOfBillable");

        root["byEnvironment"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "environment", "monthlyCost");

        root["namespaces"]!.AsArray()[0]!.AsObject().Select(p => p.Key).Should().Equal(
            "ns", "cluster", "customer", "app", "appId", "environment", "cpuCores", "memoryGiB",
            "storageGiB", "loadBalancers", "publicIps", "directMonthlyCost", "sharedMonthlyCost",
            "shareOfCluster", "monthlyCost", "unattributed", "redistributed", "idle");
    }

    /// <summary>
    /// ⚠️ The renames, asserted as VALUES. The key lists above would be satisfied by a mapping
    /// that put the right names on the wrong figures; these pin the ones whose wire name is
    /// spelled differently from the member they come from, which is where a transcription goes
    /// wrong.
    /// </summary>
    [Fact]
    public void The_renamed_fields_carry_the_value_their_source_member_holds()
    {
        CostReport report = Report();
        CostRunRateResponse subject = CostRunRateResponse.From(report);

        CostNamespaceResponse ordinary = subject.Namespaces.First();
        ordinary.Ns.Should().Be("app-prod", "`ns` is Namespace");
        ordinary.Cluster.Should().Be("prod-north", "`cluster` is ClusterName");
        ordinary.Customer.Should().Be("Acme AB", "`customer` is CustomerName");
        ordinary.App.Should().Be("Billing", "`app` is AppName");
        ordinary.Environment.Should().Be("Production", "`environment` is EnvironmentName");
        ordinary.MonthlyCost.Should().Be(107m, "`monthlyCost` is TotalMonthlyCost, not DirectMonthlyCost");
        ordinary.DirectMonthlyCost.Should().Be(100m);
        ordinary.Unattributed.Should().BeFalse();
        ordinary.Redistributed.Should().BeFalse();
        ordinary.Idle.Should().BeFalse();

        // The three booleans a caller must not confuse, each true on exactly its own row.
        subject.Namespaces.Where(n => n.Unattributed).Select(n => n.Ns)
            .Should().BeEquivalentTo(["unowned", "platform", CostAllocation.IdleNamespace],
                "`unattributed` is IsUnattributed, which is CustomerId being null — true of the "
                + "platform and idle rows as well, and they are reported that way");

        subject.Namespaces.Where(n => n.Redistributed).Select(n => n.Ns)
            .Should().BeEquivalentTo(["platform", CostAllocation.IdleNamespace],
                "`redistributed` is IsRedistributed");

        subject.Namespaces.Where(n => n.Idle).Select(n => n.Ns)
            .Should().Equal([CostAllocation.IdleNamespace], "`idle` is IsIdle");

        // A namespace two apps share has no app to attribute to, and says so rather than
        // naming one of them.
        CostNamespaceResponse multi = subject.Namespaces.Single(n => n.Ns == "shared-staging");
        multi.AppId.Should().BeNull();
        multi.App.Should().Be("2 apps");

        CostAppResponse app = subject.ByApp.First();
        app.App.Should().Be("Billing", "`app` is AppName");
        app.MonthlyCost.Should().Be(107m, "`monthlyCost` is TotalMonthlyCost");
        app.Namespaces.Should().Equal(["app-prod"],
            "the multi-app namespace is not this app's, even though this app is one of its two");

        CostCapacityResponse capacity = subject.Capacity.Single();
        capacity.Cluster.Should().Be("prod-north", "`cluster` is ClusterName");
        capacity.MonthlyCost.Should().Be(8888m, "`monthlyCost` is TotalMonthlyCost");
        capacity.NodeMonthlyCost.Should().Be(9000m, "and `nodeMonthlyCost` is the separate figure");

        // Largest first, in all three breakdowns — asserted, because a client renders them in
        // the order they arrive.
        subject.ByCustomer.Select(c => c.MonthlyCost).Should().Equal([130m, 2m]);
        subject.ByEnvironment.Select(e => e.Environment).Should().Equal(["Production", "Staging"]);
        subject.ByApp.Select(a => a.MonthlyCost).Should().Equal([107m, 2m]);

        subject.TotalHourlyCost.Should().Be(138m / 730m, "the run rate is the monthly over a 730-hour month");
    }
}
