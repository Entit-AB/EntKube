using System.Text.Json.Nodes;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services;
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

    /// <summary>
    /// The one path rule, for targets and monitors alike: the same pod port is the same series names
    /// whatever the path, except where the scrape asks the endpoint for something else, an exporter's
    /// probe of another address, or a query that picks other families.
    /// </summary>
    [Theory]
    [InlineData(8000, "/metrics", "")]
    [InlineData(9187, "/probe", "")]
    [InlineData(9187, "/metrics", "?target=10.0.0.99%3A5432")]
    [InlineData(9187, "/metrics/cadvisor", "")]
    public void A_target_on_another_port_or_one_that_asks_for_something_else_is_not_coverage(
        int port, string path, string query)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.Scraped("somebody", world.CnpgPod("orders"), port, path, query: query);

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// A job that is not a monitor (a ScrapeConfig, an additionalScrapeConfigs job) on another path of
    /// the same port serves the same names again, as RabbitMQ's per-object path does: ours beside it
    /// would double them.
    /// </summary>
    [Theory]
    [InlineData("scrapeConfig/queues/rabbit-per-object")]
    [InlineData("rabbitmq-per-object")]
    public void A_job_on_another_path_of_the_same_port_covers_it(string pool)
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        V1Pod broker = world.RabbitBroker("events");
        world.Scraped(pool, broker, 15692, "/metrics/per-object");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Detail.Should().Contain(pool);
        plan.Apply.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"port":"prometheus","path":"/probe"}""", "an exporter's probe serves the probed address's metrics")]
    [InlineData("""{"port":"prometheus","path":"/metrics/detailed","params":{"family":["queue_coarse_metrics"]}}""",
        "a query asks for other families")]
    public void A_foreign_monitor_that_asks_the_port_for_something_else_covers_nothing(string endpoint, string because)
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events");
        world.Monitor(MonitorKind.ServiceMonitor, "queues", "detailed",
            $$$"""{"selector":{"matchLabels":{"app.kubernetes.io/component":"rabbitmq"}},"endpoints":[{{{endpoint}}}]}""");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed, because);
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

    /// <summary>
    /// The stack's annotation job, as the catalog installs it, and Prometheus scraping some other pod
    /// through it: the job is there.
    /// </summary>
    private void TheAnnotationJobRuns()
    {
        V1Pod demo = world.Pod("apps", "demo-0", ScrapeWorld.Labels(("app", "demo")),
            ScrapeWorld.Container("demo", ("http", 8080)));
        demo.Metadata.Annotations = ScrapeWorld.Labels(("prometheus.io/scrape", "true"));
        world.Scraped("kubernetes-pods-annotated", demo, 8080);
    }

    private V1Pod StartedAMinuteAgo(V1Pod pod)
    {
        pod.Status.StartTime = (world.Now - TimeSpan.FromMinutes(1)).UtcDateTime;
        return pod;
    }

    /// <summary>
    /// A pod that has only just started, that the annotation job takes by its own rules, and that
    /// Prometheus has not listed yet: ours beside it would be the second job in a moment.
    /// </summary>
    [Theory]
    [InlineData("9187")]
    [InlineData(null)]
    public void A_pod_that_asks_to_be_scraped_on_that_port_needs_no_monitor(string? port)
    {
        TheAnnotationJobRuns();
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = StartedAMinuteAgo(world.CnpgPod("orders"));
        pod.Metadata.Annotations = ScrapeWorld.Labels(("prometheus.io/scrape", "true"));
        if (port is not null) pod.Metadata.Annotations["prometheus.io/port"] = port;
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Annotated);
        plan.Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
        plan.Apply.Should().BeEmpty();
    }

    /// <summary>
    /// An adopted stack with its own additionalScrapeConfigs has no annotation job, and nothing in
    /// Prometheus's list says otherwise: an annotation is then a wish nobody grants.
    /// </summary>
    [Fact]
    public void An_annotation_is_no_evidence_where_no_annotation_job_is_there()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        StartedAMinuteAgo(world.CnpgPod("orders")).Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "9187"));

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
        OnlyRow(plan).Change.Should().Be(MonitorChange.Create);
    }

    /// <summary>The job keeps only the literal true: its regex is anchored and minds case.</summary>
    [Theory]
    [InlineData("True")]
    [InlineData("yes")]
    [InlineData(" true")]
    public void The_annotation_job_takes_only_the_literal_true(string scrape)
    {
        TheAnnotationJobRuns();
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        StartedAMinuteAgo(world.CnpgPod("orders")).Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", scrape), ("prometheus.io/port", "9187"));

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// Without a port annotation the job scrapes the ports the containers declare, and drops a
    /// container that declares none. Tetragon's agent declares none; its Service says 2112 by number.
    /// </summary>
    [Fact]
    public void Without_a_port_annotation_the_job_takes_only_the_ports_a_container_declares()
    {
        TheAnnotationJobRuns();
        world.Component("tetragon", "tetragon", "tetragon");
        Dictionary<string, string> labels = ScrapeWorld.Labels(
            ("app.kubernetes.io/name", "tetragon"), ("app.kubernetes.io/instance", "tetragon"));
        V1Pod pod = StartedAMinuteAgo(world.Pod("tetragon", "tetragon-abcde", labels, ScrapeWorld.Container("tetragon")));
        pod.Metadata.Annotations = ScrapeWorld.Labels(("prometheus.io/scrape", "true"));
        world.Service("tetragon", "tetragon", labels, labels, ("metrics", 2112, "2112"));

        world.Plan().Rows.Single(r => r.Kind == "Tetragon agent").State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// The job takes pods within seconds of their start. One it still has not after ten minutes is
    /// one it does not take, whatever its annotations read like to EntKube.
    /// </summary>
    [Fact]
    public void A_pod_the_annotation_job_has_left_alone_for_a_while_is_not_counted()
    {
        TheAnnotationJobRuns();
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders").Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "9187"));

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
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
        world.Scraped("kubernetes-pods-annotated", pod, 15020, "/stats/prometheus");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// Istio's injector, merging metrics as it does by default: the pod's own prometheus.io
    /// annotations go into the proxy's ISTIO_PROMETHEUS_ANNOTATIONS, the pod's now point at the agent
    /// on 15020, and the agent scrapes the app's port and serves its series there with its own.
    /// </summary>
    private static V1Pod IstioMerged(V1Pod pod, string original, string role = "sidecar")
    {
        pod.Spec.Containers.Add(new V1Container
        {
            Name = "istio-proxy",
            Args = ["proxy", role, "--domain", "$(POD_NAMESPACE).svc.cluster.local"],
            Env = [new V1EnvVar { Name = "ISTIO_PROMETHEUS_ANNOTATIONS", Value = original }],
            Ports = [new V1ContainerPort { Name = "http-envoy-prom", ContainerPort = 15090, Protocol = "TCP" }],
        });
        pod.Metadata.Annotations = ScrapeWorld.Labels(
            ("prometheus.io/scrape", "true"), ("prometheus.io/port", "15020"), ("prometheus.io/path", "/stats/prometheus"));
        return pod;
    }

    [Fact]
    public void Istio_serves_the_apps_metrics_again_on_15020_where_the_annotation_job_scrapes_them()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = IstioMerged(world.CnpgPod("orders"), """{"scrape":"true","path":"/metrics","port":"9187"}""");
        world.Scraped("kubernetes-pods-annotated", pod, 15020, "/stats/prometheus");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Detail.Should().Contain("kubernetes-pods-annotated").And.Contain("15020");
        plan.Apply.Should().BeEmpty();
        plan.Prune.Should().Equal(Pods("databases", "entkube-cnpg-orders"));
    }

    [Fact]
    public void Istio_merging_a_pod_that_just_started_waits_for_the_annotation_job()
    {
        TheAnnotationJobRuns();
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        IstioMerged(StartedAMinuteAgo(world.CnpgPod("orders")), """{"scrape":"true","path":"","port":"9187"}""");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Annotated);
        plan.HasChanges.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"scrape":"true","path":"/metrics","port":"9999"}""", "the agent merges another port")]
    [InlineData("""{"scrape":"","path":"","port":""}""", "with no port the agent merges port 80, not 9187")]
    [InlineData("""{"scrape":"false","path":"/metrics","port":"9187"}""", "the agent merges nothing the pod said false to")]
    [InlineData("""{"scrape":"true","path":"/metrics?family=queue_coarse_metrics","port":"9187"}""",
        "the agent asks the port for something other than its own metrics")]
    [InlineData("""{"scrape":"true","path":"/metrics","port":"9187","targets":[{"port":"9187","path":"/metrics"},{"port":"15090","path":"/metrics"}]}""",
        "an agent handed a port of Istio's own refuses to start, and merges nothing")]
    [InlineData("""{"scrape":"true","path":"/metrics","port":"abc"}""", "a port that is not a number is scraped by nobody")]
    public void Istio_merging_something_else_is_no_coverage(string original, string because)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = IstioMerged(world.CnpgPod("orders"), original);
        world.Scraped("kubernetes-pods-annotated", pod, 15020, "/stats/prometheus");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed, because);
    }

    /// <summary>
    /// What pilot-agent merges, as upstream has it (pilot/cmd/pilot-agent/status/server.go, NewServer):
    /// anything the record does not say "false" to, in so many words, and the injector writes the
    /// record for a pod with no scrape annotation, or one ParseBool reads as true
    /// (pkg/kube/inject/webhook.go, getPrometheusScrape). With scrape targets, each of them.
    /// </summary>
    [Theory]
    [InlineData("""{"scrape":"","path":"","port":"9187"}""", "a pod with only a port annotation is merged")]
    [InlineData("""{"scrape":"True","path":"/metrics","port":"9187"}""", "ParseBool reads True as true, and the agent minds only false")]
    [InlineData("""{"scrape":"1","path":"/metrics","port":"9187"}""", "ParseBool reads 1 as true")]
    [InlineData("""{"port":"9187"}""", "an absent scrape is not false")]
    [InlineData("""{"scrape":"true","path":"/metrics","port":"8000","targets":[{"port":"8000","path":"/metrics"},{"port":"9187","path":"/metrics"}]}""",
        "with more than one scrape target the agent scrapes every one")]
    [InlineData("""{"scrape":"true","path":"","port":"","targets":[{"port":"9187","path":"/metrics"}]}""",
        "with one target and no port the agent takes the target's")]
    public void Istio_merges_what_its_agent_merges(string original, string because)
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = IstioMerged(world.CnpgPod("orders"), original);
        world.Scraped("kubernetes-pods-annotated", pod, 15020, "/stats/prometheus");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere, because);
        plan.Prune.Should().Equal([Pods("databases", "entkube-cnpg-orders")], because);
        plan.Apply.Should().BeEmpty(because);
    }

    /// <summary>A gateway's agent runs as a router, and reads no scrape record: only a sidecar's merges.</summary>
    [Fact]
    public void A_gateways_agent_merges_nothing()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = IstioMerged(world.CnpgPod("orders"), """{"scrape":"true","path":"/metrics","port":"9187"}""", "router");
        world.Scraped("kubernetes-pods-annotated", pod, 15020, "/stats/prometheus");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>The agent serves the merged series on /stats/prometheus alone; its other paths are probes.</summary>
    [Fact]
    public void Only_the_agents_merged_path_serves_the_apps_metrics_again()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = IstioMerged(world.CnpgPod("orders"), """{"scrape":"true","path":"/metrics","port":"9187"}""");
        world.Scraped("istio-agent-health", pod, 15020, "/healthz/ready");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// Merging doubles nothing by itself: only a job that scrapes the agent does. Where none does, ours
    /// is the one scrape of the database, and stays.
    /// </summary>
    [Fact]
    public void Istio_merging_that_nothing_scrapes_on_the_agents_port_doubles_nothing()
    {
        TheAnnotationJobRuns();
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        IstioMerged(world.CnpgPod("orders"), """{"scrape":"true","path":"/metrics","port":"9187"}""");
        world.Apply(world.Plan());

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
        plan.HasChanges.Should().BeFalse();
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
        Filer().Detail.Should().Contain("Re-apply SeaweedFS", "that is what adds the rule; the chart undoes an edit by hand")
            .And.NotContain("Allow it in");

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

    /// <summary>
    /// Traefik's chart annotates its pods and EntKube makes no monitor for it. Whether anything takes
    /// them is the annotation job's to do, and a row says only what Prometheus has shown of that job.
    /// </summary>
    [Theory]
    [InlineData("traefik")]
    [InlineData("cert-manager")]
    [InlineData("velero")]
    public void A_component_left_to_the_annotation_job_claims_no_job_Prometheus_does_not_list(string chart)
    {
        world.Component(chart, chart, chart);

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.Info);
        row.Detail.Should().Contain("lists no target of a kubernetes-pods-annotated job")
            .And.NotContain("already scrapes");
    }

    [Fact]
    public void A_component_left_to_the_annotation_job_names_the_job_where_Prometheus_lists_it()
    {
        TheAnnotationJobRuns();
        world.Component("traefik", "traefik", "traefik");

        MonitorPlanRow row = world.Plan().Rows.Single(r => r.Kind == "Traefik");

        row.Detail.Should().Contain("kubernetes-pods-annotated job").And.Contain("Scrape targets")
            .And.NotContain("lists no target").And.NotContain("already scrapes");
    }

    [Fact]
    public void A_component_left_to_the_annotation_job_says_nothing_of_it_while_Prometheus_is_starting()
    {
        world.PrometheusStarted(world.Now - TimeSpan.FromSeconds(30));
        world.Component("traefik", "traefik", "traefik");

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.Info);
        row.Detail.Should().Contain("cannot be told yet").And.NotContain("lists no target");
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

    /// <summary>
    /// Prometheus lets go of a pool when it next reloads, about a minute after the monitor is
    /// deleted. Until then the pool of a monitor of ours that was just removed (pruned while its
    /// row was briefly missing, or deleted by hand) is still in the target list, and it is the pool
    /// of the very monitor about to be written: the same name is the same job, so writing it again
    /// scrapes nothing twice. Seen on kind, where it held the monitor back for a whole pass.
    /// </summary>
    [Fact]
    public void The_pool_our_removed_monitor_left_behind_does_not_keep_it_from_being_made_again()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        world.Scraped("podMonitor/databases/entkube-cnpg-orders/0", pod, 9187);

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
        OnlyRow(plan).Change.Should().Be(MonitorChange.Create);
        plan.Apply.Should().ContainSingle().Which.Identity.Should().Be(Pods("databases", "entkube-cnpg-orders"));
    }

    /// <summary>Only our own name: the pool another deleted monitor left behind still scrapes the pod.</summary>
    [Fact]
    public void The_pool_somebody_elses_removed_monitor_left_behind_still_covers_it()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        world.Scraped("podMonitor/databases/orders/0", pod, 9187);

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.CoveredElsewhere);
        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Only while nothing of our name is live: a live one that is somebody else's owns its pool,
    /// and Prometheus saying it scrapes the pod outweighs a selector that reads as if it did not.
    /// </summary>
    [Fact]
    public void The_pool_of_somebody_elses_live_monitor_under_our_name_is_theirs()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod pod = world.CnpgPod("orders");
        world.Monitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders", "{}",
            (ScrapeMonitorManifest.OwnershipLabel, Guid.NewGuid().ToString()));
        world.Scraped("podMonitor/databases/entkube-cnpg-orders/0", pod, 9187);

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Detail.Should().Contain("podMonitor/databases/entkube-cnpg-orders/0");
        plan.HasChanges.Should().BeFalse();
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
        V1Pod broker = world.RabbitBroker("events");
        broker.Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "15692"));
        world.OldRabbitMonitor("events");
        world.Scraped("kubernetes-pods-annotated", broker, 15692);

        world.Plan().Prune.Should().Equal(Services("queues", "events-metrics"));
    }

    /// <summary>
    /// The same broker on a stack whose own additionalScrapeConfigs have no annotation job: the old
    /// monitor is the only scrape there is, and it is taken over, not removed.
    /// </summary>
    [Fact]
    public void RabbitMqs_old_monitor_stays_where_nothing_honours_the_brokers_annotation()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "queues", "events");
        world.RabbitBroker("events").Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "15692"));
        world.OldRabbitMonitor("events");

        ScrapeMonitorPlan plan = world.Plan();

        plan.Prune.Should().BeEmpty();
        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
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
    /// Kyverno's admission controller behind kyverno-svc-metrics and a second Service with the same
    /// labels and port name, as a chart upgrade or a hand-made Service puts it. A ServiceMonitor gets
    /// one target per Service, each with its own job label, so every sum over those pods is twice
    /// what it is. Seen on kind: 9 targets for the 6 endpoints the row claimed.
    /// </summary>
    private V1Pod KyvernoAdmissionBehindTwoServices()
    {
        world.Component("kyverno", "kyverno", "kyverno");
        Dictionary<string, string> labels = ScrapeWorld.Labels(
            ("app.kubernetes.io/instance", "kyverno"), ("app.kubernetes.io/part-of", "kyverno"),
            ("app.kubernetes.io/component", "admission-controller"));
        V1Pod pod = world.Pod("kyverno", "kyverno-admission-controller-0", labels,
            ScrapeWorld.Container("kyverno", ("https", 9443), ("metrics-port", 8000)));
        world.Service("kyverno", "kyverno-svc-metrics", labels, labels, ("metrics-port", 8000, "8000"));
        world.Service("kyverno", "kyverno-svc-metrics-extra", labels, labels, ("metrics-port", 8000, "8000"));
        return pod;
    }

    [Fact]
    public void A_pod_two_Services_lead_to_gets_no_ServiceMonitor_that_would_scrape_it_twice()
    {
        KyvernoAdmissionBehindTwoServices();

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.NotSelectable);
        OnlyRow(plan).Change.Should().Be(MonitorChange.None);
        OnlyRow(plan).Detail.Should().Contain("kyverno-svc-metrics").And.Contain("kyverno-svc-metrics-extra")
            .And.Contain("twice");
        plan.HasChanges.Should().BeFalse();
    }

    /// <summary>Ours is what counts it twice, so ours goes: a visible gap rather than a silent double.</summary>
    [Fact]
    public void Our_ServiceMonitor_goes_when_a_second_Service_makes_it_scrape_a_pod_twice()
    {
        KyvernoAdmissionBehindTwoServices();
        world.OurMonitor(MonitorKind.ServiceMonitor, "kyverno", "entkube-kyverno-kyverno");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Change.Should().Be(MonitorChange.Remove);
        plan.Prune.Should().Equal(Services("kyverno", "entkube-kyverno-kyverno"));
        plan.Apply.Should().BeEmpty();
    }

    /// <summary>Two Services in front of different pods are two endpoints, not one twice.</summary>
    [Fact]
    public void Two_Services_in_front_of_different_pods_are_scraped_once_each()
    {
        world.Component("kyverno", "kyverno", "kyverno");
        foreach (string controller in (string[])["admission-controller", "reports-controller"])
        {
            Dictionary<string, string> labels = ScrapeWorld.Labels(
                ("app.kubernetes.io/instance", "kyverno"), ("app.kubernetes.io/part-of", "kyverno"),
                ("app.kubernetes.io/component", controller));
            world.Pod("kyverno", $"kyverno-{controller}-0", labels, ScrapeWorld.Container("kyverno", ("metrics", 8000)));
            world.Service("kyverno", $"kyverno-{controller}-metrics", labels, labels, ("metrics-port", 8000, "8000"));
        }

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.Managed);
        OnlyRow(plan).Endpoints.Should().HaveCount(2);
    }

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

    // ── Coverage is a question about every endpoint ───────────────────────────

    /// <summary>
    /// Argo CD's application controller, server and repo-server, which one PodMonitor of ours selects.
    /// The chart can give the controller a ServiceMonitor of its own and leave the others without.
    /// </summary>
    private V1Pod ArgoCd()
    {
        world.Component("argo-cd", "argocd", "argocd");
        V1Pod Part(string component, int port) => world.Pod("argocd", $"argocd-{component}-0", ScrapeWorld.Labels(
            ("app.kubernetes.io/part-of", "argocd"), ("app.kubernetes.io/instance", "argocd"),
            ("app.kubernetes.io/component", component)), ScrapeWorld.Container(component, ("metrics", port)));

        V1Pod controller = Part("application-controller", 8082);
        Part("server", 8083);
        Part("repo-server", 8084);
        return controller;
    }

    [Fact]
    public void A_job_that_scrapes_one_part_of_a_target_does_not_take_the_rest_with_it()
    {
        V1Pod controller = ArgoCd();
        world.Apply(world.Plan());
        world.Scraped("serviceMonitor/argocd/argocd-application-controller/0", controller, 8082);

        ScrapeMonitorPlan plan = world.Plan();

        MonitorPlanRow row = OnlyRow(plan);
        row.State.Should().Be(MonitorState.PartlyCovered);
        row.Change.Should().Be(MonitorChange.Remove, "ours scrapes the controller a second time");
        plan.Prune.Should().Equal(Pods("argocd", "entkube-argocd-argocd"));
        plan.Apply.Should().BeEmpty();
        row.Endpoints.Should().HaveCount(3);
        row.Detail.Should().Contain("serviceMonitor/argocd/argocd-application-controller/0")
            .And.Contain("argocd-server-0").And.Contain("argocd-repo-server-0")
            .And.NotContain("already scrapes it");
    }

    [Fact]
    public void A_target_scraped_only_in_part_gets_no_monitor_of_ours_either()
    {
        V1Pod controller = ArgoCd();
        world.Scraped("serviceMonitor/argocd/argocd-application-controller/0", controller, 8082);

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.PartlyCovered);
        OnlyRow(plan).Change.Should().Be(MonitorChange.None);
        plan.HasChanges.Should().BeFalse("a monitor for the rest would scrape the controller twice");
    }

    [Fact]
    public void A_monitor_over_one_part_of_a_target_does_not_cover_the_rest()
    {
        ArgoCd();
        world.Monitor(MonitorKind.PodMonitor, "argocd", "argocd-application-controller",
            """{"selector":{"matchLabels":{"app.kubernetes.io/component":"application-controller"}},"podMetricsEndpoints":[{"port":"metrics"}]}""");

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.PartlyCovered);
        row.Detail.Should().Contain("PodMonitor argocd/argocd-application-controller").And.Contain("argocd-server-0");
    }

    /// <summary>
    /// The part that is covered is covered by a monitor Prometheus has not loaded yet. Ours given up now
    /// would leave every part unscraped until it has, and all of it for good if it never does: ours stays
    /// until Prometheus lists that monitor's target, and only then goes.
    /// </summary>
    [Fact]
    public void A_part_covered_only_by_a_monitor_still_loading_keeps_ours()
    {
        V1Pod controller = ArgoCd();
        world.Apply(world.Plan());
        world.Monitor(MonitorKind.PodMonitor, "argocd", "argocd-application-controller",
            """{"selector":{"matchLabels":{"app.kubernetes.io/component":"application-controller"}},"podMetricsEndpoints":[{"port":"metrics"}]}""");

        ScrapeMonitorPlan plan = world.Plan();

        MonitorPlanRow row = OnlyRow(plan);
        row.State.Should().Be(MonitorState.PartlyCovered);
        row.Change.Should().Be(MonitorChange.None);
        row.Detail.Should().Contain("PodMonitor argocd/argocd-application-controller").And.Contain("argocd-server-0");
        plan.HasChanges.Should().BeFalse();

        world.Scraped("podMonitor/argocd/argocd-application-controller/0", controller, 8082);
        world.Plan().Prune.Should().Equal([Pods("argocd", "entkube-argocd-argocd")],
            "once Prometheus lists it, ours scrapes the controller a second time");
    }

    [Fact]
    public void A_part_covered_only_by_a_pod_that_has_just_asked_the_annotation_job_keeps_ours()
    {
        V1Pod controller = ArgoCd();
        world.Apply(world.Plan());
        TheAnnotationJobRuns();
        StartedAMinuteAgo(controller).Metadata.Annotations =
            ScrapeWorld.Labels(("prometheus.io/scrape", "true"), ("prometheus.io/port", "8082"));

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.PartlyCovered);
        OnlyRow(plan).Change.Should().Be(MonitorChange.None);
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void A_target_every_endpoint_of_which_is_scraped_elsewhere_is_covered()
    {
        V1Pod controller = ArgoCd();
        world.Scraped("serviceMonitor/argocd/argocd-application-controller/0", controller, 8082);
        foreach (V1Pod other in world.Workloads().Namespaces["argocd"].Pods.Where(p => p != controller))
        {
            world.Scraped("argocd-by-hand", other, ScrapedEndpoints.DeclaredPorts(other).Single().ContainerPort);
        }

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.CoveredElsewhere);
        row.Detail.Should().Contain("serviceMonitor/argocd/argocd-application-controller/0").And.Contain("argocd-by-hand");
    }

    // ── Foreign monitors: which count, and for how long without a target ──────

    /// <summary>
    /// With ignoreNamespaceSelectors the operator ignores every monitor's namespaceSelector, and a
    /// monitor discovers only its own namespace: Strimzi's example in monitoring finds no broker.
    /// </summary>
    [Fact]
    public void With_ignoreNamespaceSelectors_a_monitor_looks_only_in_its_own_namespace()
    {
        world.Prometheus("""{"ignoreNamespaceSelectors":true,"serviceMonitorSelector":{},"podMonitorSelector":{},"serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{}}""");
        world.DataService(ManagedServiceKind.Kafka, "kafka", "events");
        world.Pod("kafka", "events-broker-0", ScrapeWorld.Labels(("strimzi.io/cluster", "events"), ("strimzi.io/kind", "Kafka")),
            ScrapeWorld.Container("kafka", ("tcp-prometheus", 9404)));
        world.Monitor(MonitorKind.PodMonitor, "monitoring", "kafka-resources-metrics",
            """{"namespaceSelector":{"any":true},"selector":{"matchExpressions":[{"key":"strimzi.io/kind","operator":"In","values":["Kafka"]}]},"podMetricsEndpoints":[{"port":"tcp-prometheus"}]}""");

        world.Plan().Rows.Single(r => r.Kind == "Kafka").State.Should().Be(MonitorState.Managed);
    }

    /// <summary>
    /// A monitor the operator rejected, or whose own relabelling drops the pod, is selected and scrapes
    /// nothing. Prometheus loads a new monitor in a minute or two; one it has listed no target of
    /// after ten is one it scrapes nothing through.
    /// </summary>
    [Fact]
    public void A_selected_monitor_Prometheus_has_scraped_nothing_through_for_a_while_is_not_coverage()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders",
            """{"selector":{"matchLabels":{"cnpg.io/cluster":"orders","cnpg.io/podRole":"instance"}},"podMetricsEndpoints":[{"port":"metrics"}]}""");

        MonitorPlanRow row = OnlyRow(world.Plan());

        row.State.Should().Be(MonitorState.Managed);
        row.Change.Should().Be(MonitorChange.Create);
        row.Detail.Should().Contain("PodMonitor databases/orders").And.Contain("nothing through it");
    }

    [Fact]
    public void A_selected_monitor_of_no_known_age_changes_nothing()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorMade(null, MonitorKind.PodMonitor, "databases", "orders",
            """{"selector":{"matchLabels":{"cnpg.io/cluster":"orders","cnpg.io/podRole":"instance"}},"podMetricsEndpoints":[{"port":"metrics"}]}""");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.NotSelectable);
        plan.HasChanges.Should().BeFalse();
    }

    private const string CnpgOperatorsSpec =
        """{"selector":{"matchLabels":{"cnpg.io/cluster":"orders","cnpg.io/podRole":"instance"}},"podMetricsEndpoints":[{"port":"metrics"}]}""";

    /// <summary>
    /// An old monitor that Prometheus scrapes through, and a pod that has only just started: its target
    /// is not listed yet because discovery has not come round to it, not because the monitor scrapes
    /// nothing. Ours made now would be the second job by the next pass.
    /// </summary>
    [Fact]
    public void An_old_monitor_and_a_pod_that_has_only_just_started_change_nothing()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        StartedAMinuteAgo(world.CnpgPod("orders"));
        world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec);

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Change.Should().Be(MonitorChange.None);
        OnlyRow(plan).Detail.Should().Contain("PodMonitor databases/orders").And.Contain("orders-1");
        plan.HasChanges.Should().BeFalse("whether that monitor scrapes the new pod cannot be told yet");
    }

    [Fact]
    public void An_old_monitor_and_a_pod_that_has_only_just_started_do_not_take_ours_away_either()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        StartedAMinuteAgo(world.CnpgPod("orders"));
        world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec);
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>The same where the database has a second instance the monitor is already scraped through.</summary>
    [Fact]
    public void An_old_monitor_listed_on_one_pod_and_not_yet_on_a_new_one_makes_nothing_of_ours()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        V1Pod first = world.CnpgPod("orders");
        StartedAMinuteAgo(world.CnpgPod("orders", instance: 2));
        world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec);
        world.Scraped("podMonitor/databases/orders/0", first, 9187);

        ScrapeMonitorPlan plan = world.Plan();

        plan.Apply.Should().BeEmpty("ours would scrape orders-1 a second time");
        plan.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// A monitor edited a moment ago (a selector widened, a relabelling taken out) may scrape what it
    /// did not, once Prometheus has taken the edit in. Until then that cannot be told.
    /// </summary>
    [Fact]
    public void An_old_monitor_edited_a_moment_ago_changes_nothing()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorEdited(
            world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec),
            world.Now - TimeSpan.FromMinutes(2));

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).Detail.Should().Contain("PodMonitor databases/orders");
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void An_old_monitor_edited_a_moment_ago_does_not_take_ours_away_either()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorEdited(
            world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec),
            world.Now - TimeSpan.FromMinutes(2));
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-orders");

        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>Edited, by its generation, at a time the API server keeps no record of: cannot be told.</summary>
    [Fact]
    public void A_monitor_edited_at_a_time_nobody_says_changes_nothing()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorEdited(
            world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec),
            at: null, generation: 3);

        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>An edit an hour ago is long taken in: a monitor with no target since scrapes nothing here.</summary>
    [Fact]
    public void A_monitor_edited_long_ago_and_still_without_a_target_is_not_coverage()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.MonitorEdited(
            world.MonitorMade(world.Now - TimeSpan.FromDays(3), MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec),
            world.Now - ScrapeWorld.AnHour);

        OnlyRow(world.Plan()).Change.Should().Be(MonitorChange.Create);
    }

    [Fact]
    public void A_pod_whose_start_the_kubelet_does_not_give_changes_nothing_beside_an_old_monitor()
    {
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders").Status.StartTime = null;
        world.MonitorMade(world.Now - ScrapeWorld.AnHour, MonitorKind.PodMonitor, "databases", "orders", CnpgOperatorsSpec);

        world.Plan().HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Loki's chart makes a ServiceMonitor where the catalog makes a PodMonitor. Both end at the same
    /// pod port, so the chart's covers it from the moment it is there, not only once Prometheus lists it.
    /// </summary>
    [Fact]
    public void A_selected_monitor_of_the_other_kind_covers_it_as_well()
    {
        world.Component("loki", "loki", "loki");
        Dictionary<string, string> labels = ScrapeWorld.Labels(("app.kubernetes.io/name", "loki"),
            ("app.kubernetes.io/instance", "loki"), ("app.kubernetes.io/component", "single-binary"));
        world.Pod("loki", "loki-0", labels, ScrapeWorld.Container("loki", ("http-metrics", 3100)));
        world.Service("loki", "loki", labels, labels, ("http-metrics", 3100, "http-metrics"));
        world.Apply(world.Plan());
        world.Monitor(MonitorKind.ServiceMonitor, "loki", "loki",
            """{"selector":{"matchLabels":{"app.kubernetes.io/name":"loki","app.kubernetes.io/instance":"loki"}},"endpoints":[{"port":"http-metrics"}]}""");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.CoveredElsewhere);
        OnlyRow(plan).Detail.Should().Contain("ServiceMonitor loki/loki");
        plan.Prune.Should().Equal(Pods("loki", "entkube-loki-loki"));
    }

    // ── A Prometheus that has only just started ───────────────────────────────

    /// <summary>
    /// Ready is not settled: discovery fills the target list over the first moments, so an empty or
    /// short list right after a start says nothing about who scrapes what.
    /// </summary>
    [Fact]
    public void A_Prometheus_that_started_a_moment_ago_changes_nothing()
    {
        world.PrometheusStarted(world.Now - TimeSpan.FromSeconds(40));
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");
        world.DataService(ManagedServiceKind.Postgres, "databases", "billing");
        V1Pod billing = world.CnpgPod("billing");
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-billing");
        world.Scraped("additional-job", billing, 9187);
        world.OurMonitor(MonitorKind.PodMonitor, "databases", "entkube-cnpg-gone");

        ScrapeMonitorPlan plan = world.Plan();

        plan.Rows.Select(r => r.State).Should().AllBeEquivalentTo(MonitorState.PrometheusStarting);
        plan.Rows[0].Detail.Should().Contain("40 seconds");
        plan.HasChanges.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "a Prometheus whose container is not ready answered nothing")]
    [InlineData(true, "the kubelet says nothing of when it started")]
    public void A_Prometheus_whose_start_cannot_be_told_changes_nothing(bool ready, string because)
    {
        world.PrometheusStarted(world.Now - ScrapeWorld.AnHour);
        V1ContainerStatus status = world.PrometheusPod.Status.ContainerStatuses.Single();
        status.Ready = ready;
        if (ready) status.State.Running = null;
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        ScrapeMonitorPlan plan = world.Plan();

        OnlyRow(plan).State.Should().Be(MonitorState.PrometheusStarting, because);
        plan.HasChanges.Should().BeFalse(because);
    }

    [Fact]
    public void A_Prometheus_that_has_run_a_few_minutes_is_believed()
    {
        world.PrometheusStarted(world.Now - TimeSpan.FromMinutes(4));
        world.DataService(ManagedServiceKind.Postgres, "databases", "orders");
        world.CnpgPod("orders");

        OnlyRow(world.Plan()).State.Should().Be(MonitorState.Managed);
    }

    // ── What the panel says ───────────────────────────────────────────────────

    /// <summary>
    /// "Scraped" is what Prometheus scrapes: EntKube's own, or a job it lists or has just been given.
    /// An annotation it has not acted on, a target scraped in part, a Prometheus just started: no.
    /// </summary>
    [Theory]
    [InlineData(MonitorState.Managed, "success")]
    [InlineData(MonitorState.CoveredElsewhere, "success")]
    [InlineData(MonitorState.Annotated, "warning")]
    [InlineData(MonitorState.PartlyCovered, "warning")]
    [InlineData(MonitorState.PrometheusStarting, "warning")]
    [InlineData(MonitorState.NotSelectable, "warning")]
    public void The_panel_calls_scraped_only_what_Prometheus_scrapes(MonitorState state, string tone)
    {
        ScrapeMonitorPlan plan = new(
            [new MonitorPlanRow("cnpg/databases/orders", "orders", "PostgreSQL", "databases", null, state,
                MonitorChange.None, "What is so.", [])],
            [], []);

        MonitorsApplied.From(KubernetesOperationResult<ScrapeMonitorPlan>.Success(plan), "cnpg/databases/orders")
            .Tone.Should().Be(tone);
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
