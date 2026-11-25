using System.Text.Json.Nodes;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;
using k8s.Models;

namespace EntKube.Web.Tests;

/// <summary>
/// The two rules the monitors live by: a pod endpoint is scraped by exactly one job, and a question
/// that cannot be answered changes nothing. Every row of the state table is here, with what happens
/// to a monitor of ours that already exists, over the labels the charts and operators really render.
/// </summary>
public class ScrapeMonitorPlannerTests
{
    private readonly ScrapeWorld world = new();

    private MonitorPlanRow OnlyRow(ScrapeMonitorPlan plan) => plan.Rows.Should().ContainSingle().Subject;

    private static MonitorIdentity Pods(string ns, string name) => new(MonitorKind.PodMonitor, ns, name);

    private static MonitorIdentity Services(string ns, string name) => new(MonitorKind.ServiceMonitor, ns, name);

    // ── Managed ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_running_database_that_nothing_scrapes_gets_a_monitor()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");

        ScrapeMonitorPlan plan = world.Plan();

        MonitorPlanRow row = OnlyRow(plan);
        row.State.Should().Be(MonitorState.Managed);
        row.Change.Should().Be(MonitorChange.Create);
        row.Monitor.Should().Be(Pods("databases", "entkube-cnpg-orders"));
        row.Endpoints.Should().Equal(new PlannedEndpoint("orders-1", $"{pod.Status.PodIP}:9187", "/metrics"));
        plan.Apply.Should().ContainSingle().Which.Identity.Should().Be(row.Monitor);
        plan.Prune.Should().BeEmpty();
    }

    [Fact]
    public void A_monitor_that_is_already_right_is_left_alone()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Apply(world.Plan());

        ScrapeMonitorPlan plan = world.Plan();

        plan.HasChanges.Should().BeFalse();
        OnlyRow(plan).Change.Should().Be(MonitorChange.None);
        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
    }

    [Fact]
    public void A_monitor_that_has_drifted_is_written_again()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Apply(world.Plan());
        world.Monitors.Single().Spec["podMetricsEndpoints"]![0]!["port"] = "status";

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Change.Should().Be(MonitorChange.Update);
        plan.Apply.Should().ContainSingle();
        plan.Delete.Should().BeEmpty();
    }

    /// <summary>
    /// kubectl removes only what it remembers writing, so a key added to the selector by hand stays
    /// through every apply while the monitor selects nothing.
    /// </summary>
    [Fact]
    public void A_monitor_whose_selector_was_narrowed_by_hand_is_deleted_and_made_again()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Apply(world.Plan());
        world.Monitors.Single().Spec["selector"]!["matchLabels"]!["team"] = "payments";

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Change.Should().Be(MonitorChange.Replace);
        plan.Delete.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
        plan.Apply.Should().ContainSingle();
        plan.Prune.Should().BeEmpty("the summary should not say the monitor is removed");
    }

    [Fact]
    public void A_managed_row_says_how_many_of_its_targets_are_up()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod first = world.CnpgPod("orders");
        V1Pod second = world.CnpgPod("orders", instance: 2);
        world.Apply(world.Plan());
        world.Scraped("podMonitor/databases/entkube-cnpg-orders/0", first, 9187);
        world.Scraped("podMonitor/databases/entkube-cnpg-orders/0", second, 9187, up: false);
        world.Scraped("serviceMonitor/databases/entkube-cnpg-orders/0", second, 9999);

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.Managed, "our own pool on our own endpoint is not somebody else's scrape");
        (row.TargetsUp, row.TargetsTotal).Should().Be((1, 2));
    }

    [Fact]
    public void Every_monitor_carries_whose_it_is_and_what_this_Prometheus_selects_by()
    {
        world.Prometheus("""
            {"serviceMonitorSelector":{},"serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{},
             "podMonitorSelector":{"matchLabels":{"release":"prom","tier":"infra"}}}
            """);
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        JsonObject metadata = world.Plan().Apply.Should().ContainSingle().Subject.Manifest["metadata"]!.AsObject();

        metadata["labels"]!.AsObject().Select(l => $"{l.Key}={l.Value}").Should().BeEquivalentTo(
            "app.kubernetes.io/managed-by=entkube",
            $"entkube.io/scrape-monitor={world.ClusterId}",
            // The selector's value, not the release Catalog recorded: an adopted stack's is the catalog's guess.
            "release=prom",
            "tier=infra");
        metadata["annotations"]!["entkube.io/scrape-owner"]!.GetValue<string>().Should().Be("cnpg/databases/orders");
    }

    [Fact]
    public void Each_kind_of_monitor_gets_its_own_selectors_labels()
    {
        world.Prometheus("""
            {"serviceMonitorSelector":{"matchLabels":{"pick":"services"}},"podMonitorSelector":{"matchLabels":{"pick":"pods"}},
             "serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{}}
            """);
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");

        world.Plan().Apply.ToDictionary(m => m.Identity.Kind, m => m.Manifest["metadata"]!["labels"]!["pick"]!.GetValue<string>())
            .Should().BeEquivalentTo(new Dictionary<MonitorKind, string>
            {
                [MonitorKind.PodMonitor] = "pods",
                [MonitorKind.ServiceMonitor] = "services",
            });
    }

    [Theory]
    [InlineData(ManagedServiceStatus.Creating)]
    [InlineData(ManagedServiceStatus.Updating)]
    [InlineData(ManagedServiceStatus.Failed)]
    public void A_database_is_scraped_whatever_EntKubes_last_attempt_at_it_came_to(ManagedServiceStatus status)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders", status);
        world.CnpgPod("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    // ── CoveredElsewhere ──────────────────────────────────────────────────────

    /// <summary>
    /// The CNPG hand-over needs no code of its own. While the operator's PodMonitor is there, ours
    /// is not; when a re-apply turns it off, the next pass makes ours.
    /// </summary>
    [Fact]
    public void The_CNPG_operators_own_monitor_is_enough()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.CnpgOperatorsPodMonitor("orders");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Change.Should().Be(MonitorChange.Remove);
        OnlyRow(plan).Detail.Should().Contain("PodMonitor databases/orders");
        plan.Apply.Should().BeEmpty();
        plan.Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));

        world.Apply(plan);
        world.Monitors.RemoveAll(m => m.Identity.Name == "orders");
        world.Plan().Apply.Should().ContainSingle().Which.Identity.Name.Should().Be("entkube-cnpg-orders");
    }

    [Fact]
    public void An_older_stack_that_does_not_select_the_operators_monitor_gets_ours()
    {
        world.Prometheus("""
            {"serviceMonitorSelector":{"matchLabels":{"release":"kube-prometheus-stack"}},
             "podMonitorSelector":{"matchLabels":{"release":"kube-prometheus-stack"}},
             "serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{}}
            """);
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.CnpgOperatorsPodMonitor("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed,
            "the operator's PodMonitor carries no release label, so this Prometheus does not scrape it");
    }

    [Fact]
    public void Keycloaks_chart_monitor_is_enough_where_Prometheus_selects_it()
    {
        world.Component("keycloakx", "keycloak", "keycloak");
        world.KeycloakPod();
        world.KeycloakChartsMonitor();

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Keycloaks_chart_monitor_is_nothing_where_Prometheus_does_not_select_it()
    {
        world.Prometheus("""
            {"serviceMonitorSelector":{"matchLabels":{"release":"kube-prometheus-stack"}},"podMonitorSelector":{},
             "serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{}}
            """);
        world.Component("keycloakx", "keycloak", "keycloak");
        world.KeycloakPod();
        world.KeycloakChartsMonitor();

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// What Prometheus itself says it scrapes, for what no selector shows: a scrape config, a monitor
    /// of the other kind, an operator's own.
    /// </summary>
    [Theory]
    [InlineData("some-scrape-config")]
    [InlineData("serviceMonitor/elsewhere/everything/0")]
    [InlineData("podMonitor/databases/a-users-own/0")]
    public void A_target_in_somebody_elses_pool_is_enough(string pool)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        world.Scraped(pool, pod, 9187, up: false);
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Detail.Should().Contain(pool);
        plan.Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
        plan.Apply.Should().BeEmpty();
    }

    [Theory]
    [InlineData(8000, "/metrics")]
    [InlineData(9187, "/probe")]
    public void A_target_on_another_port_or_path_is_another_endpoint(int port, string path)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.Scraped("somebody", world.CnpgPod("orders"), port, path);

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// RabbitMQ's published monitor also scrapes the per-object path of the same port, which serves
    /// the same series names again.
    /// </summary>
    [Fact]
    public void A_foreign_monitor_on_the_same_port_covers_it_whatever_path_it_scrapes()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        world.Monitor(MonitorKind.ServiceMonitor, "observability", "rabbitmq",
            """{"namespaceSelector":{"any":true},"selector":{"matchLabels":{"app.kubernetes.io/component":"rabbitmq"}},"endpoints":[{"port":"prometheus","path":"/metrics/per-object"}]}""");
        world.Namespace("observability");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.CoveredElsewhere);
    }

    [Theory]
    [InlineData("""{"selector":{"matchLabels":{"app.kubernetes.io/component":"rabbitmq"}},"endpoints":[{"port":"prometheus"}]}""",
        "a monitor without a namespace selector looks in its own namespace only")]
    [InlineData("""{"namespaceSelector":{"matchNames":["elsewhere"]},"selector":{},"endpoints":[{"port":"prometheus"}]}""",
        "it looks in another namespace")]
    [InlineData("""{"namespaceSelector":{"any":true},"endpoints":[{"port":"prometheus"}]}""",
        "a monitor with no selector selects nothing")]
    [InlineData("""{"namespaceSelector":{"any":true},"selector":{},"endpoints":[{"port":"management"}]}""",
        "it scrapes another port")]
    public void A_foreign_monitor_that_does_not_reach_the_endpoint_covers_nothing(string spec, string because)
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        world.Namespace("observability");
        world.Monitor(MonitorKind.ServiceMonitor, "observability", "somebodys", spec);

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed, because);
    }

    /// <summary>The upstream Redis sample selects every cluster's pods by number rather than by name.</summary>
    [Theory]
    [InlineData("""{"portNumber":9121}""")]
    [InlineData("""{"targetPort":9121}""")]
    [InlineData("""{"targetPort":"redis-exporter"}""")]
    [InlineData("{}")]
    public void A_foreign_pod_monitor_is_recognised_however_it_names_the_port(string endpoint)
    {
        world.DataService(ManagedServiceKind.Redis, "caches", "sessions");
        world.Pod("caches", "sessions-leader-0", ScrapeWorld.Labels(
            ("app", "sessions-leader"), ("redis_setup_type", "cluster")),
            ScrapeWorld.Container("redis"), ScrapeWorld.Container("redis-exporter", ("redis-exporter", 9121)));
        world.Monitor(MonitorKind.PodMonitor, "caches", "redis",
            $$$"""{"selector":{"matchLabels":{"redis_setup_type":"cluster"}},"podMetricsEndpoints":[{{{endpoint}}}]}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.CoveredElsewhere);
    }

    // ── Annotated ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("9187")]
    [InlineData(null)]
    public void A_pod_that_asks_to_be_scraped_on_that_port_needs_no_monitor(string? port)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        pod.Metadata.Annotations = ScrapeWorld.Labels(("prometheus.io/scrape", "true"));
        if (port is not null) pod.Metadata.Annotations["prometheus.io/port"] = port;
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Annotated);
        plan.Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
        plan.Apply.Should().BeEmpty();
    }

    /// <summary>
    /// Istio's injection annotates every pod for its own proxy on 15020. The database's exporter is
    /// on another port, which that job never visits.
    /// </summary>
    [Fact]
    public void A_pod_annotated_for_another_port_still_needs_a_monitor()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        pod.Metadata.Annotations = ScrapeWorld.Labels(
            ("prometheus.io/scrape", "true"), ("prometheus.io/port", "15020"), ("prometheus.io/path", "/stats/prometheus"));

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    [Fact]
    public void A_pod_that_asks_not_to_be_scraped_is_not_annotated()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders").Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "false"), ("prometheus.io/port", "9187"));

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    // ── NotRunning, NotExposed ────────────────────────────────────────────────

    [Fact]
    public void A_service_with_no_running_pods_keeps_the_monitor_it_has_and_gets_none()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.Namespace("databases");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotRunning);
        world.Plan().Apply.Should().BeEmpty();

        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");
        world.Plan().HasChanges.Should().BeFalse("a database that is down for a night is still scraped when it is back");
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Succeeded")]
    [InlineData("Failed")]
    public void A_pod_that_is_not_running_is_not_an_endpoint(string phase)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders").Status.Phase = phase;

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotRunning);
    }

    [Fact]
    public void A_service_whose_namespace_is_gone_has_nothing_applied_for_it()
    {
        world.DataService(ManagedServiceKind.Postgres, "nowhere", "orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.NotRunning);
        OnlyRow(plan).Detail.Should().Contain("nowhere does not exist");
        plan.HasChanges.Should().BeFalse();
    }

    /// <summary>EntKube's Kafka has no metricsConfig, so the brokers declare no metrics port.</summary>
    [Fact]
    public void A_service_that_runs_without_exposing_metrics_keeps_the_monitor_it_has_and_gets_none()
    {
        world.DataService(ManagedServiceKind.Kafka, "streams", "events");
        world.Pod("streams", "events-pool-0", ScrapeWorld.Labels(
            ("strimzi.io/cluster", "events"), ("strimzi.io/kind", "Kafka")),
            ScrapeWorld.Container("kafka", ("tcp-ctrlplane", 9090), ("tcp-replication", 9091)));
        world.Pod("streams", "events-entity-operator-0", ScrapeWorld.Labels(
            ("strimzi.io/cluster", "events"), ("strimzi.io/kind", "Kafka"), ("app.kubernetes.io/name", "entity-operator")),
            ScrapeWorld.Container("topic-operator", ("healthcheck-to", 8080)),
            ScrapeWorld.Container("user-operator", ("healthcheck-uo", 8081)));
        world.OurMonitor(MonitorKind.PodMonitor, "streams", "entkube-kafka-events");

        ScrapeMonitorPlan plan = world.Plan();

        MonitorPlanRow brokers = plan.Rows.Single(r => r.Kind == "Kafka");
        brokers.State.Should().Be(MonitorState.NotExposed);
        brokers.Detail.Should().Contain("tcp-prometheus");
        plan.Prune.Should().BeEmpty();
        plan.Apply.Should().ContainSingle().Which.Identity.Name.Should().Be("entkube-kafka-entity-events");
        plan.Rows.Single(r => r.Kind == "Kafka entity operator").Endpoints.Should().HaveCount(2,
            "one for each container, and none for the name they had before Strimzi 1.1.0");
    }

    // ── BlockedByNetworkPolicy ────────────────────────────────────────────────

    [Fact]
    public void A_pod_Prometheus_is_kept_out_of_keeps_the_monitor_it_has_and_gets_none()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Policy("databases", """{"podSelector":{},"policyTypes":["Ingress"]}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.BlockedByNetworkPolicy);
        OnlyRow(world.Plan()).Detail.Should().Contain("9187").And.Contain("monitoring");
        world.Plan().Apply.Should().BeEmpty();

        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");
        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>SeaweedFS's chart admits 9327 only from the release's own pods.</summary>
    [Fact]
    public void SeaweedFs_own_policy_keeps_Prometheus_out_until_it_is_opened()
    {
        world.Component("seaweedfs", "seaweedfs", "seaweedfs");
        world.SeaweedFiler();
        world.Policy("seaweedfs", """
            {"podSelector":{"matchLabels":{"app.kubernetes.io/component":"filer"}},"policyTypes":["Ingress"],
             "ingress":[{"from":[{"podSelector":{"matchLabels":{"app.kubernetes.io/name":"seaweedfs"}}}],
                         "ports":[{"port":8888,"protocol":"TCP"},{"port":9327,"protocol":"TCP"}]}]}
            """);

        MonitorPlanRow Filer() => world.Plan().Rows.Single(r => r.Kind == "SeaweedFS filer");
        Filer().State.Should().Be(MonitorState.BlockedByNetworkPolicy);

        world.Policy("seaweedfs", """
            {"podSelector":{"matchLabels":{"app.kubernetes.io/component":"filer"}},"policyTypes":["Ingress"],
             "ingress":[{"from":[{"namespaceSelector":{"matchLabels":{"kubernetes.io/metadata.name":"monitoring"}},
                                  "podSelector":{"matchLabels":{"app.kubernetes.io/name":"prometheus"}}}],
                         "ports":[{"port":9327,"protocol":"TCP"}]}]}
            """);
        Filer().State.Should().Be(MonitorState.Managed, "what one policy opens, no other closes");
    }

    [Theory]
    [InlineData("""{"podSelector":{}}""", false, "no policyTypes means ingress")]
    [InlineData("""{"podSelector":{},"policyTypes":["Egress"]}""", true, "it says nothing about who comes in")]
    [InlineData("""{"podSelector":{"matchLabels":{"app":"other"}},"policyTypes":["Ingress"]}""", true, "it is about other pods")]
    [InlineData("""{"podSelector":{},"ingress":[{}]}""", true, "a rule with no peers and no ports lets everything in")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":9187}]}]}""", true, "the port is open to all")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":"metrics"}]}]}""", true, "a name is looked up on the pod")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":9000,"endPort":9200}]}]}""", true, "the port is in the range")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":9000,"endPort":9100}]}]}""", false, "the port is past the range")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":5432}]}]}""", false, "another port is open")]
    [InlineData("""{"podSelector":{},"ingress":[{"ports":[{"port":9187,"protocol":"UDP"}]}]}""", false, "another protocol is")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"podSelector":{}}]}]}""", false, "pods without a namespace are the policy's own")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"namespaceSelector":{}}]}]}""", true, "every namespace")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"namespaceSelector":{"matchLabels":{"kubernetes.io/metadata.name":"monitoring"}}}]}]}""", true, "Prometheus's namespace")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"namespaceSelector":{"matchLabels":{"kubernetes.io/metadata.name":"apps"}}}]}]}""", false, "another namespace")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"namespaceSelector":{},"podSelector":{"matchLabels":{"app":"ingress"}}}]}]}""", false, "other pods, in any namespace")]
    [InlineData("""{"podSelector":{},"ingress":[{"from":[{"ipBlock":{"cidr":"192.168.0.0/16"}}]}]}""", true, "addresses cannot be told, and a down target is visible")]
    public void A_policy_is_read_the_way_Kubernetes_reads_it(string policy, bool admitted, string because)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Policy("databases", policy);

        OnlyRow(world.Plan()).State.Should().Be(
            admitted ? MonitorState.Managed : MonitorState.BlockedByNetworkPolicy, because);
    }

    [Fact]
    public void A_pod_on_the_hosts_network_is_outside_what_a_policy_governs()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders").Spec.HostNetwork = true;
        world.Policy("databases", """{"podSelector":{}}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    [Fact]
    public void One_pod_of_two_that_is_shut_does_not_take_the_other_one_with_it()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.CnpgPod("orders", instance: 2).Metadata.Labels["quarantine"] = "true";
        world.Policy("databases", """{"podSelector":{"matchLabels":{"quarantine":"true"}}}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
        OnlyRow(world.Plan()).Detail.Should().Contain("out of 1 of them");
    }

    // ── NotSelectable ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"podMonitorNamespaceSelector":{}}""", "an absent selector selects nothing")]
    [InlineData("""{"podMonitorSelector":{}}""", "an absent namespace selector means its own namespace only")]
    [InlineData("""{"podMonitorSelector":{},"podMonitorNamespaceSelector":{"matchLabels":{"monitored":"yes"}}}""", "the namespace does not carry the label")]
    [InlineData("""{"podMonitorNamespaceSelector":{},"podMonitorSelector":{"matchExpressions":[{"key":"team","operator":"Exists"}]}}""", "an expression is not something EntKube can satisfy")]
    [InlineData("""{"podMonitorNamespaceSelector":{},"podMonitorSelector":{"matchExpressions":[{"key":"team","operator":"Gt","values":["1"]}]}}""", "an operator nobody knows cannot be told")]
    [InlineData("""{"podMonitorNamespaceSelector":{},"podMonitorSelector":{},"shards":2}""", "one shard does not say what the others scrape")]
    public void A_monitor_Prometheus_would_not_or_might_not_select_is_neither_made_nor_removed(string spec, string because)
    {
        world.Prometheus(spec);
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotSelectable, because);
        world.Plan().HasChanges.Should().BeFalse(because);

        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");
        world.Plan().HasChanges.Should().BeFalse(because);
    }

    [Fact]
    public void A_namespace_selector_the_namespace_satisfies_is_no_obstacle()
    {
        world.Prometheus("""{"podMonitorSelector":{},"podMonitorNamespaceSelector":{"matchLabels":{"monitored":"yes"}}}""");
        world.Namespace("databases", ("monitored", "yes"));
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    [Fact]
    public void Two_Prometheuses_of_which_neither_is_clearly_ours_means_nothing_is_done()
    {
        const string everything = """{"podMonitorSelector":{},"podMonitorNamespaceSelector":{}}""";
        world.Prometheus(everything, name: "one", release: "one");
        world.Prometheus(everything, name: "two", release: "two", another: true);
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotSelectable);
        OnlyRow(world.Plan()).Detail.Should().Contain("2 Prometheus objects");
        world.Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void The_Prometheus_of_the_release_is_chosen_over_another_in_the_same_namespace()
    {
        world.Prometheus("""{"podMonitorSelector":{"matchLabels":{"team":"x"}},"podMonitorNamespaceSelector":{}}""",
            name: "a-teams-own", release: "a-teams-own");
        world.Prometheus("""{"podMonitorSelector":{},"podMonitorNamespaceSelector":{}}""", another: true);

        ScrapeMonitorPlanner.ChoosePrometheus(world.Live())!.Name.Should().Be("kube-prometheus-stack-prometheus");
    }

    [Fact]
    public void An_adopted_stack_under_another_release_name_is_found_by_being_the_only_one()
    {
        world.Prometheus("{}", name: "prom", release: "my-own-name");

        ScrapeMonitorPlanner.ChoosePrometheus(world.Live())!.Name.Should().Be("prom");
    }

    /// <summary>
    /// "Cannot tell whether it is scraped already" is not "it is not": the monitor is neither made
    /// nor, if it exists, removed.
    /// </summary>
    [Fact]
    public void A_foreign_monitor_that_cannot_be_read_means_nothing_is_done()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Monitor(MonitorKind.PodMonitor, "databases", "odd",
            """{"selector":{"matchExpressions":[{"key":"cnpg.io/cluster","operator":"Gt","values":["1"]}]},"podMetricsEndpoints":[{"port":"metrics"}]}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotSelectable);
        OnlyRow(world.Plan()).Detail.Should().Contain("databases/odd");
        world.Plan().HasChanges.Should().BeFalse();

        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");
        world.Plan().HasChanges.Should().BeFalse();
    }

    // ── Info ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_service_with_nothing_to_create_gets_a_row_and_loses_a_monitor_of_ours()
    {
        world.DataService(ManagedServiceKind.MongoDb, "databases", "documents");
        world.OurMonitor(MonitorKind.ServiceMonitor, "databases", "entkube-mongodb-documents");

        ScrapeMonitorPlan plan = world.Plan();

        MonitorPlanRow row = OnlyRow(plan);
        row.State.Should().Be(MonitorState.Info);
        row.MonitorName.Should().BeNull();
        row.Detail.Should().Contain("password");
        plan.Apply.Should().BeEmpty();
        plan.Prune.Should().Equal(Services("databases", "entkube-mongodb-documents"));
    }

    // ── Whose monitor it is ───────────────────────────────────────────────────

    [Fact]
    public void A_monitor_whose_database_is_gone_is_removed()
    {
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-old");

        world.Plan().Prune.Should().Equal(Pods("databases", "entkube-cnpg-old"));
    }

    [Fact]
    public void A_database_being_deleted_takes_its_monitor_with_it()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders", ManagedServiceStatus.Deleting);
        world.CnpgPod("orders");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        world.Plan().Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
        world.Plan().Apply.Should().BeEmpty();
    }

    /// <summary>One physical cluster registered twice, under two tenants or in two installations.</summary>
    [Fact]
    public void A_monitor_another_registration_of_this_cluster_made_is_never_touched()
    {
        world.Monitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-theirs", "{}",
            (ScrapeMonitorManifest.OwnershipLabel, Guid.NewGuid().ToString()));

        world.Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_monitor_of_the_same_name_from_another_registration_is_somebody_elses()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        world.Apply(world.Plan());
        LiveMonitor theirs = world.Monitors.Single();
        world.Monitors[0] = theirs with
        {
            Labels = new Dictionary<string, string>(theirs.Labels)
            {
                [ScrapeMonitorManifest.OwnershipLabel] = Guid.NewGuid().ToString(),
            },
        };
        world.Scraped("podMonitor/databases/entkube-cnpg-orders/0", pod, 9187);

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.CoveredElsewhere);
        world.Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_monitor_somebody_made_under_our_name_is_not_overwritten()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Monitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders",
            """{"selector":{"matchLabels":{"app":"something-else"}},"podMetricsEndpoints":[{"port":"http"}]}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotSelectable);
        OnlyRow(world.Plan()).Detail.Should().Contain("not EntKube's");
        world.Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void RabbitMqs_old_monitor_is_taken_over_rather_than_doubled()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        V1Pod pod = world.RabbitBroker("events");
        world.OldRabbitMonitor("events");
        world.Scraped("serviceMonitor/queues/events-metrics/0", pod, 15692);

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Managed, "its own pool is not somebody else's scrape");
        OnlyRow(plan).Change.Should().Be(MonitorChange.Update);
        (OnlyRow(plan).TargetsUp, OnlyRow(plan).TargetsTotal).Should().Be((1, 1));
        PlannedMonitor planned = plan.Apply.Should().ContainSingle().Subject;
        planned.Identity.Should().Be(Services("queues", "events-metrics"));
        planned.Manifest["spec"]!["endpoints"]!.AsArray().Should().HaveCount(2, "a broker with TLS serves the other port");
        planned.Manifest["metadata"]!["labels"]![ScrapeMonitorManifest.OwnershipLabel]!.GetValue<string>()
            .Should().Be(world.ClusterId.ToString());
        plan.Delete.Should().BeEmpty();

        world.Targets.Clear();
        world.Apply(plan).Plan().HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_monitor_of_the_old_RabbitMq_name_that_selects_something_else_is_a_persons_own()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        LiveMonitor old = world.OldRabbitMonitor("events");
        old.Spec["selector"]!["matchLabels"]!["team"] = "payments";

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotSelectable);
        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Brokers first made by an operator up to 1.13 keep its scrape annotations, so the annotation
    /// job and the old button's monitor double every one of their numbers.
    /// </summary>
    [Fact]
    public void RabbitMqs_old_monitor_goes_where_the_broker_is_annotated()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events").Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "15692"));
        world.OldRabbitMonitor("events");

        world.Plan().Prune.Should().Equal(Services("queues", "events-metrics"));
    }

    [Fact]
    public void Two_services_that_work_out_to_one_monitor_name_do_not_overwrite_each_other()
    {
        world.DataService(ManagedServiceKind.Kafka, "streams", "x");
        world.DataService(ManagedServiceKind.Kafka, "streams", "entity-x");
        world.Namespace("streams");

        ScrapeMonitorPlan plan = world.Plan();

        plan.Rows.Where(r => r.MonitorName == "entkube-kafka-entity-x").Select(r => r.State)
            .Should().Equal(MonitorState.NotRunning, MonitorState.NotSelectable);
    }

    // ── One pod, once ─────────────────────────────────────────────────────────

    /// <summary>
    /// Two headless Services in front of the same filer pods, both with the port. Only one carries
    /// monitoring=true, and a monitor matching both scrapes every filer twice.
    /// </summary>
    [Fact]
    public void SeaweedFs_filer_is_scraped_through_one_of_its_two_Services()
    {
        world.Component("seaweedfs", "seaweedfs", "seaweedfs");
        V1Pod pod = world.SeaweedFiler();

        ScrapeMonitorPlan plan = world.Plan();

        plan.Rows.Single(r => r.Kind == "SeaweedFS filer").Endpoints.Should()
            .Equal(new PlannedEndpoint("seaweedfs-filer-0", $"{pod.Status.PodIP}:9327", "/metrics"));

        LabelSelector selector = LabelSelector.From(plan.Apply.Should().ContainSingle().Subject.Manifest["spec"]!["selector"])!;
        world.Workloads().Namespaces["seaweedfs"].Services
            .Where(s => selector.Matches(new Dictionary<string, string>(s.Metadata.Labels)) is true)
            .Select(s => s.Metadata.Name).Should().Equal("seaweedfs-filer");
    }

    /// <summary>
    /// The leader and leader-metrics Services carry the same labels and the same port name. A monitor
    /// on pods sees each pod once.
    /// </summary>
    [Fact]
    public void Redis_is_scraped_by_pod_because_its_Services_come_in_pairs()
    {
        world.DataService(ManagedServiceKind.Redis, "caches", "sessions");
        foreach (string role in (string[])["leader", "follower"])
        {
            Dictionary<string, string> labels = ScrapeWorld.Labels(
                ("app", $"sessions-{role}"), ("redis_setup_type", "cluster"), ("role", role), ("cluster", "sessions"));
            world.Pod("caches", $"sessions-{role}-0", labels, ScrapeWorld.Container("redis"),
                ScrapeWorld.Container("redis-exporter", ("redis-exporter", 9121)));
            world.Service("caches", $"sessions-{role}", labels, labels, ("redis-exporter", 9121, null));
            world.Service("caches", $"sessions-{role}-metrics", labels, labels, ("redis-exporter", 9121, null));
        }
        world.Pod("caches", "other-leader-0", ScrapeWorld.Labels(("app", "other-leader"), ("redis_setup_type", "cluster")),
            ScrapeWorld.Container("redis-exporter", ("redis-exporter", 9121)));

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Monitor!.Kind.Should().Be(MonitorKind.PodMonitor);
        OnlyRow(plan).Endpoints.Select(e => e.Pod).Should().Equal("sessions-leader-0", "sessions-follower-0");
    }

    /// <summary>PgBouncer poolers carry the database's cluster label and a port of the same name.</summary>
    [Fact]
    public void A_CNPG_pooler_is_not_the_database()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.Pod("databases", "orders-pooler-rw-0", ScrapeWorld.Labels(
            ("cnpg.io/cluster", "orders"), ("cnpg.io/podRole", "pooler"), ("cnpg.io/poolerName", "orders-pooler-rw")),
            ScrapeWorld.Container("pgbouncer", ("pgbouncer", 5432), ("metrics", 9127)));

        OnlyRow(world.Plan()).Endpoints.Select(e => e.Pod).Should().Equal("orders-1");
    }

    /// <summary>
    /// Loki's and Mimir's nginx gateways have a port CALLED http-metrics and serve no metrics: a
    /// monitor that took them in would be a target that is down for ever.
    /// </summary>
    [Theory]
    [InlineData("loki", "loki", "loki", "single-binary")]
    [InlineData("mimir-distributed", "mimir", "mimir", "ingester")]
    public void A_gateway_whose_port_is_only_named_metrics_is_left_out(
        string chart, string name, string release, string component)
    {
        world.Component(chart, "monitoring", release);
        foreach (string of in (string[])[component, "gateway"])
        {
            world.Pod("monitoring", $"{release}-{of}-0", ScrapeWorld.Labels(("app.kubernetes.io/name", name),
                ("app.kubernetes.io/instance", release), ("app.kubernetes.io/component", of)),
                ScrapeWorld.Container(of, ("http-metrics", 8080)));
        }

        world.Plan().Rows[0].Endpoints.Select(e => e.Pod).Should().Equal($"{release}-{component}-0");
    }

    // ── Following a Service ───────────────────────────────────────────────────

    /// <summary>Keycloak's Service names the port, and only its -http Service has it.</summary>
    [Fact]
    public void A_named_target_port_is_looked_up_on_the_pod()
    {
        world.Component("keycloakx", "keycloak", "keycloak");
        V1Pod pod = world.KeycloakPod();

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.Endpoints.Should().Equal(new PlannedEndpoint("keycloak-keycloakx-0", $"{pod.Status.PodIP}:9000", "/auth/metrics"));

        pod.Spec.Containers[0].Ports = [new V1ContainerPort { Name = "http", ContainerPort = 8080 }];
        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotExposed, "the pod declares no port of that name");
    }

    [Fact]
    public void A_Service_that_sends_to_hand_written_addresses_resolves_to_nothing()
    {
        world.Component("trust-manager", "cert-manager", "trust-manager");
        world.Pod("cert-manager", "trust-manager-0", ScrapeWorld.Labels(("app", "trust-manager")),
            ScrapeWorld.Container("trust-manager", ("metrics", 9402)));
        world.Service("cert-manager", "trust-manager-metrics", ScrapeWorld.Labels(
            ("app", "trust-manager"), ("app.kubernetes.io/component", "metrics")), selector: null, ("metrics", 9402, null));

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.NotRunning);
    }

    /// <summary>Tetragon's containers declare no ports at all; its Service says the number.</summary>
    [Fact]
    public void A_numbered_target_port_needs_no_declaration()
    {
        world.Component("tetragon", "tetragon", "tetragon");
        Dictionary<string, string> labels = ScrapeWorld.Labels(
            ("app.kubernetes.io/name", "tetragon"), ("app.kubernetes.io/instance", "tetragon"));
        V1Pod pod = world.Pod("tetragon", "tetragon-abcde", labels, ScrapeWorld.Container("tetragon"));
        world.Service("tetragon", "tetragon", labels, labels, ("metrics", 2112, "2112"));

        world.Plan().Rows.Single(r => r.Kind == "Tetragon agent").Endpoints
            .Should().Equal(new PlannedEndpoint("tetragon-abcde", $"{pod.Status.PodIP}:2112", "/metrics"));
    }

    [Theory]
    [InlineData("Always", MonitorState.Managed)]
    [InlineData(null, MonitorState.NotExposed)]
    public void An_init_container_counts_when_it_is_a_sidecar(string? restartPolicy, MonitorState expected)
    {
        world.DataService(ManagedServiceKind.Redis, "caches", "sessions");
        V1Pod pod = world.Pod("caches", "sessions-leader-0", ScrapeWorld.Labels(
            ("app", "sessions-leader"), ("redis_setup_type", "cluster")), ScrapeWorld.Container("redis"));
        V1Container exporter = ScrapeWorld.Container("redis-exporter", ("redis-exporter", 9121));
        exporter.RestartPolicy = restartPolicy;
        pod.Spec.InitContainers = [exporter];

        OnlyRow(world.Plan()).State.Should().Be(expected);
    }

    /// <summary>CNPG serves metrics in plain text until a Cluster asks for TLS, and its pods say which.</summary>
    [Fact]
    public void A_database_is_scraped_over_TLS_where_its_pods_serve_it()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        JsonNode Endpoint() => world.Plan().Apply.Single().Manifest["spec"]!["podMetricsEndpoints"]![0]!;

        Endpoint()["scheme"]!.GetValue<string>().Should().Be("http");
        Endpoint()["tlsConfig"].Should().BeNull();

        world.CnpgPod("orders", instance: 2, moreArguments: "--metrics-port-tls");

        Endpoint()["scheme"]!.GetValue<string>().Should().Be("https");
        Endpoint()["tlsConfig"]!["insecureSkipVerify"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Every_row_says_what_is_going_on_in_a_whole_sentence()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.DataService(ManagedServiceKind.Postgres, "databases", "asleep");
        world.DataService(ManagedServiceKind.Elasticsearch, "search", "logs", exporter: true);
        world.Component("traefik", "traefik", "traefik");
        world.Component("keycloakx", "keycloak", "keycloak");
        world.KeycloakPod();
        world.KeycloakChartsMonitor();

        world.Plan().Rows.Should().HaveCount(5).And.OnlyContain(r =>
            r.Title.Length > 0 && r.Kind.Length > 0 && r.Detail.Length > 20 && r.Detail.EndsWith('.'));
    }
}
