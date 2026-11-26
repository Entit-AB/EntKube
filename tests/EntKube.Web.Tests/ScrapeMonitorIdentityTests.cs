using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// owned-rule: what makes a monitor the engine's own, and what the engine's monitor for a component or a
/// data service is called, said once, for the planner and for any code outside it that asks (the CNPG and
/// Keycloak handovers). Two definitions disagreed: the planner knew its own by one label, the handover by
/// two, and a Prometheus selector could overwrite one of the two.
/// </summary>
public sealed class ScrapeMonitorIdentityTests
{
    private readonly ScrapeWorld world = new();

    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    [Fact]
    public void Ours_is_a_monitor_labelled_with_this_clusters_id()
    {
        Guid id = world.ClusterId;

        ScrapeMonitorIdentity.IsOurs(ScrapeWorld.Labels(("entkube.io/scrape-monitor", id.ToString())), id)
            .Should().BeTrue();
        ScrapeMonitorIdentity.IsOurs(ScrapeWorld.Labels(
                ("entkube.io/scrape-monitor", id.ToString()), ("app.kubernetes.io/managed-by", "entkube")), id)
            .Should().BeTrue();
        ScrapeMonitorIdentity.IsOurs(ScrapeWorld.Labels(("entkube.io/scrape-monitor", Other.ToString())), id)
            .Should().BeFalse("another registration of the cluster, or another EntKube, wrote it");
        ScrapeMonitorIdentity.IsOurs(ScrapeWorld.Labels(("app.kubernetes.io/managed-by", "entkube")), id)
            .Should().BeFalse("managed-by says who manages it, not for which registration");
        ScrapeMonitorIdentity.IsOurs(ScrapeWorld.Labels(), id).Should().BeFalse();
        ScrapeMonitorIdentity.IsOurs(null, id).Should().BeFalse();
    }

    /// <summary>The planner prunes by the same rule: what is ours and has no target any more goes, nothing else.</summary>
    [Fact]
    public void The_planner_prunes_exactly_the_monitors_the_rule_calls_ours()
    {
        LiveMonitor ours = world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-gone");
        LiveMonitor anothers = world.Monitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-elsewhere", "{}",
            ("entkube.io/scrape-monitor", Other.ToString()), ("app.kubernetes.io/managed-by", "entkube"));
        LiveMonitor managedOnly = world.Monitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-unclaimed", "{}",
            ("app.kubernetes.io/managed-by", "entkube"));

        ScrapeMonitorPlan plan = world.Plan();

        plan.Prune.Should().Equal(ours.Identity);
        foreach (LiveMonitor monitor in new[] { ours, anothers, managedOnly })
        {
            plan.Prune.Contains(monitor.Identity).Should().Be(ScrapeMonitorIdentity.IsOurs(monitor.Labels, world.ClusterId));
        }
    }

    [Fact]
    public void Ours_carry_both_labels_and_are_listed_by_the_one_that_makes_them_ours()
    {
        ScrapeMonitorIdentity.Labels(world.ClusterId).Should().Equal(new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "entkube",
            ["entkube.io/scrape-monitor"] = world.ClusterId.ToString(),
        });
        ScrapeMonitorIdentity.ListSelector(world.ClusterId).Should().Be($"entkube.io/scrape-monitor={world.ClusterId}");
    }

    [Theory]
    [InlineData("""{"matchLabels":{"app.kubernetes.io/managed-by":"helm"}}""", "app.kubernetes.io/managed-by")]
    [InlineData("""{"matchLabels":{"entkube.io/scrape-monitor":"x"}}""", "entkube.io/scrape-monitor")]
    [InlineData("""{"matchExpressions":[{"key":"app.kubernetes.io/managed-by","operator":"NotIn","values":["entkube"]}]}""",
        "app.kubernetes.io/managed-by")]
    [InlineData("""{"matchExpressions":[{"key":"entkube.io/scrape-monitor","operator":"DoesNotExist"}]}""", "entkube.io/scrape-monitor")]
    [InlineData("""{"matchLabels":{"release":"prom","app.kubernetes.io/managed-by":"entkube"}}""", null)]
    [InlineData("""{"matchExpressions":[{"key":"app.kubernetes.io/managed-by","operator":"Exists"}]}""", null)]
    [InlineData("""{"matchExpressions":[{"key":"team","operator":"In","values":["a"]}]}""", null)]
    [InlineData("{}", null)]
    public void A_selector_that_demands_another_value_of_an_ownership_label_is_a_conflict(string selector, string? label)
    {
        ScrapeMonitorIdentity.ConflictWith(LabelSelector.From(ScrapeWorld.Json(selector)), world.ClusterId)
            .Should().Be(label);
    }

    [Fact]
    public void No_selector_demands_anything()
        => ScrapeMonitorIdentity.ConflictWith(null, world.ClusterId).Should().BeNull();

    /// <summary>
    /// The name of the engine's monitor for a component is the catalog's, and does not depend on the status:
    /// an apply runs while the component is Installing, before it has ever been installed.
    /// </summary>
    [Theory]
    [InlineData("keycloakx", "iam", "sso")]
    [InlineData("seaweedfs", "storage", "seaweedfs")]
    [InlineData("tetragon", "tetragon", "tetragon")]
    [InlineData("loki", "logging", "loki")]
    public void A_components_monitors_are_named_as_the_catalog_names_them_whatever_its_status(
        string chart, string ns, string release)
    {
        InstalledComponent installed = world.InstalledComponent(chart, ns, release);
        IReadOnlyList<MonitorIdentity> catalog =
            [.. ScrapeTargetCatalog.For([installed], []).Select(t => t.Monitor?.Identity).OfType<MonitorIdentity>()];

        catalog.Should().NotBeEmpty();
        ScrapeMonitorIdentity.For(installed).Should().Equal(catalog);
        ScrapeMonitorIdentity.For(world.InstalledComponent(chart, ns, release, status: ComponentStatus.Installing))
            .Should().Equal(catalog);
        ScrapeMonitorIdentity.For(world.InstalledComponent(chart, ns, release, status: ComponentStatus.Failed))
            .Should().Equal(catalog);
    }

    [Fact]
    public void A_component_the_engine_makes_no_monitor_for_has_none()
    {
        ScrapeMonitorIdentity.For(world.InstalledComponent("keycloakx", null, "sso")).Should().BeEmpty("no namespace is recorded");
        ScrapeMonitorIdentity.For(world.InstalledComponent("traefik", "ingress", "traefik")).Should().BeEmpty();
        ScrapeMonitorIdentity.For(world.InstalledComponent("metrics-server", "kube-system",
            type: ClusterProvidedComponents.ComponentType)).Should().BeEmpty("the cluster's own, under a name EntKube recorded");
    }

    [Theory]
    [InlineData(ManagedServiceStatus.Running)]
    [InlineData(ManagedServiceStatus.Creating)]
    [InlineData(ManagedServiceStatus.Deleting)]
    public void A_data_services_monitor_is_named_as_the_catalog_names_it_whatever_its_status(ManagedServiceStatus status)
    {
        ManagedServiceSummary orders = new(Guid.NewGuid(), world.TenantId, world.ClusterId, ManagedServiceKind.Postgres,
            "orders", "databases", status, IsOperatorManaged: true, IsInCluster: true, HasExporter: false);

        ScrapeMonitorIdentity.For(orders).Should().Equal(new MonitorIdentity(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders"));
        ScrapeMonitorIdentity.For(orders with { Kind = ManagedServiceKind.RabbitMq })
            .Should().Equal(new MonitorIdentity(MonitorKind.ServiceMonitor, "databases", "orders-metrics"));
        ScrapeMonitorIdentity.For(orders with { Kind = ManagedServiceKind.RabbitMq, IsOperatorManaged = false })
            .Should().BeEmpty("a broker somebody else installed gets no monitor of EntKube's");
    }
}
