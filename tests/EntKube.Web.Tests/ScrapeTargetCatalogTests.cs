using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services;
using EntKube.Web.Services.ScrapeMonitors;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// What EntKube knows how to have scraped, pinned as it stands: the kind of monitor, its name, the
/// selector and the ports. Literal on purpose. Each selector was read off a rendered chart, and one
/// that loses a label scrapes something twice or a port that never answers.
/// </summary>
public class ScrapeTargetCatalogTests
{
    private readonly ScrapeWorld world = new();

    /// <summary>A target's monitor in one line: kind, namespace/name, selector, and each port as port path scheme.</summary>
    private static string Describe(ScrapeTargetSpec target)
        => target.Monitor is not { } monitor
            ? "info"
            : $"{monitor.Identity.Kind} {monitor.Identity.Namespace}/{monitor.Identity.Name} [{monitor.Selector.Describe()}] "
              + string.Join(" + ", monitor.Endpoints.Select(e =>
                  $"{e.PortLabel}{e.Path}{(e.Https ? " https" : "")}{(e.HonorLabels ? " honor" : "")}"
                  + $"{(e.BearerTokenFile is null ? "" : " token")}{(e.Relabelings is null ? "" : " relabel")}"));

    private const string N = "app.kubernetes.io/name";
    private const string I = "app.kubernetes.io/instance";
    private const string C = "app.kubernetes.io/component";

    [Theory]
    [InlineData(ManagedServiceKind.Postgres, "cnpg.io/cluster=x, cnpg.io/podRole=instance", "PodMonitor", "entkube-cnpg-x", "metrics/metrics relabel")]
    [InlineData(ManagedServiceKind.Redis, "redis_setup_type=cluster, app in (x-leader, x-follower)", "PodMonitor", "entkube-redis-x", "redis-exporter/metrics")]
    [InlineData(ManagedServiceKind.RabbitMq, $"{N}=x, {C}=rabbitmq", "ServiceMonitor", "x-metrics", "prometheus/metrics + prometheus-tls/metrics https")]
    [InlineData(ManagedServiceKind.Kafka, "strimzi.io/cluster=x, strimzi.io/kind=Kafka", "PodMonitor", "entkube-kafka-x", "tcp-prometheus/metrics relabel")]
    public void A_data_service_is_scraped_like_this(
        ManagedServiceKind kind, string selector, string monitorKind, string name, string ports)
    {
        world.DataService(kind, "data", "x");

        Describe(world.Specs()[0]).Should().Be($"{monitorKind} data/{name} [{selector}] {ports}");
    }

    [Fact]
    public void Kafkas_entity_operator_is_scraped_beside_its_brokers()
    {
        world.DataService(ManagedServiceKind.Kafka, "data", "x");

        Describe(world.Specs()[1]).Should().Be(
            $"PodMonitor data/entkube-kafka-entity-x [strimzi.io/cluster=x, {N}=entity-operator] "
            + "healthcheck-to/metrics + healthcheck-uo/metrics + healthcheck/metrics");
    }

    [Fact]
    public void Kafkas_pods_keep_their_strimzi_labels_on_their_series()
    {
        world.DataService(ManagedServiceKind.Kafka, "data", "x");

        ScrapeRelabeling rule = world.Specs()[0].Monitor!.Endpoints.Single().Relabelings.Should().ContainSingle().Subject;
        rule.Should().Be(new ScrapeRelabeling(Regex: "__meta_kubernetes_pod_label_(strimzi_io_.+)", Action: "labelmap"));
    }

    [Theory]
    [InlineData("cloudnative-pg", $"PodMonitor ns/entkube-cloudnative-pg-rel [{N}=cloudnative-pg, {I}=rel] metrics/metrics")]
    [InlineData("redis-operator", "PodMonitor ns/entkube-redis-operator-rel [name=redis-operator] metrics/metrics")]
    [InlineData("strimzi-kafka-operator", "PodMonitor ns/entkube-strimzi-rel [strimzi.io/kind=cluster-operator] http/metrics")]
    [InlineData("reloader", $"PodMonitor ns/entkube-reloader-rel [{N}=reloader, {I}=rel] http/metrics")]
    [InlineData("descheduler", $"PodMonitor ns/entkube-descheduler-rel [{N}=descheduler, {I}=rel] 10258/metrics https")]
    [InlineData("eck-operator", $"PodMonitor ns/entkube-eck-operator-rel [{N}=elastic-operator, {I}=rel] metrics/metrics")]
    [InlineData("alloy", $"PodMonitor ns/entkube-alloy-rel [{N}=alloy, {I}=rel] http-metrics/metrics")]
    [InlineData("loki", $"PodMonitor ns/entkube-loki-rel [{N}=loki, {I}=rel, {C} not in (gateway)] http-metrics/metrics")]
    [InlineData("mimir-distributed",
        $"PodMonitor ns/entkube-mimir-rel [{N}=mimir, {I}=rel, {C} not in (gateway, smoke-test)] http-metrics/metrics",
        $"PodMonitor ns/entkube-mimir-rollout-operator-rel [{N}=rollout-operator, {I}=rel] http-metrics/metrics")]
    [InlineData("tempo-distributed", $"PodMonitor ns/entkube-tempo-rel [{N}=tempo, {I}=rel, {C} not in (memcached, gateway)] http-metrics/metrics")]
    [InlineData("argo-cd", $"PodMonitor ns/entkube-argocd-rel [app.kubernetes.io/part-of=argocd, {I}=rel, {C} not in (dex-server, redis, redis-secret-init)] metrics/metrics")]
    [InlineData("snapshot-controller", $"PodMonitor ns/entkube-snapshot-controller-rel [{N}=snapshot-controller, {I}=rel] http/metrics")]
    [InlineData("trust-manager", $"ServiceMonitor ns/entkube-trust-manager-rel [app=trust-manager, {C}=metrics] metrics/metrics")]
    [InlineData("kyverno", $"ServiceMonitor ns/entkube-kyverno-rel [{I}=rel, app.kubernetes.io/part-of=kyverno] metrics-port/metrics")]
    [InlineData("tetragon",
        $"ServiceMonitor ns/entkube-tetragon-rel [{N}=tetragon, {I}=rel] metrics/metrics honor relabel",
        $"ServiceMonitor ns/entkube-tetragon-operator-rel [{N}=tetragon-operator, {I}=rel] metrics/metrics")]
    [InlineData("keda",
        "ServiceMonitor ns/entkube-keda-adapter-rel [app=keda-operator-metrics-apiserver] metrics/metrics",
        $"ServiceMonitor ns/entkube-keda-operator-rel [{N}=keda-operator, {I}=rel] metrics/metrics honor")]
    [InlineData("gpu-operator", "ServiceMonitor ns/entkube-dcgm-exporter-rel [app=nvidia-dcgm-exporter] gpu-metrics/metrics honor")]
    [InlineData("keycloakx", $"ServiceMonitor ns/entkube-keycloak-rel [{N}=keycloakx, {I}=rel, {C}=http] http-internal/auth/metrics")]
    [InlineData("harbor", "ServiceMonitor ns/entkube-harbor-rel [release=rel, app=harbor] http-metrics/metrics")]
    [InlineData("seaweedfs",
        $"ServiceMonitor ns/entkube-seaweedfs-master-rel [{N}=seaweedfs, {I}=rel, {C}=master] metrics/metrics",
        $"ServiceMonitor ns/entkube-seaweedfs-volume-rel [{N}=seaweedfs, {I}=rel, {C}=volume] metrics/metrics",
        $"ServiceMonitor ns/entkube-seaweedfs-filer-rel [{N}=seaweedfs, {I}=rel, {C}=filer, monitoring=true] metrics/metrics",
        $"ServiceMonitor ns/entkube-seaweedfs-s3-rel [{N}=seaweedfs, {I}=rel, {C}=s3] metrics/metrics")]
    public void A_chart_is_scraped_like_this(string chart, params string[] expected)
    {
        // The release is not the chart's name and not the component's, so a selector that used
        // either of those instead shows up here.
        world.Component(chart, "ns", "rel", name: "some-name");

        world.Specs().Select(Describe).Should().Equal(expected);
    }

    [Theory]
    [InlineData("rabbitmq-cluster-operator", $"PodMonitor rabbitmq-system/entkube-rabbitmq-operator-rel [{N}=rabbitmq-cluster-operator] metrics/metrics")]
    [InlineData("rabbitmq-messaging-topology-operator", $"ServiceMonitor rabbitmq-system/entkube-rabbitmq-topology-operator-rel [{N}=messaging-topology-operator, control-plane=controller-manager] https/metrics https token")]
    [InlineData("headscale", "PodMonitor ns/entkube-headscale-rel [app=headscale] metrics/metrics")]
    [InlineData("kubevirt", "ServiceMonitor kubevirt/entkube-kubevirt-rel [prometheus.kubevirt.io=true] metrics/metrics https honor")]
    public void A_component_installed_from_a_manifest_is_scraped_like_this(string key, string expected)
    {
        world.Component(key, "ns", "rel", chart: false);

        world.Specs().Select(Describe).Should().Equal(expected);
    }

    [Fact]
    public void Tetragons_events_say_which_node_they_are_from()
    {
        world.Component("tetragon", "ns", "rel");

        ScrapeRelabeling rule = world.Specs()[0].Monitor!.Endpoints.Single().Relabelings.Should().ContainSingle().Subject;
        rule.SourceLabels.Should().Equal("__meta_kubernetes_pod_node_name");
        rule.TargetLabel.Should().Be("node");
    }

    [Theory]
    [InlineData("traefik", true, "annotations")]
    [InlineData("cert-manager", true, "annotations")]
    [InlineData("velero", true, "annotations")]
    [InlineData("kube-prometheus-stack", true, "its own monitors")]
    [InlineData("opentelemetry-ebpf-instrumentation", true, "its own monitors")]
    [InlineData("submariner-operator", true, "its own monitors")]
    [InlineData("opentelemetry-collector", true, "does not expose it yet")]
    [InlineData("metrics-server", true, "token")]
    [InlineData("tailscale-operator", true, "no metrics port")]
    [InlineData("openldap-stack-ha", true, "no Prometheus endpoint")]
    [InlineData("kubevirt-cdi", false, "its own monitors")]
    [InlineData("stalwart", false, "does not expose it yet")]
    [InlineData("rspamd", false, "password")]
    public void A_component_with_nothing_to_create_says_why(string key, bool chart, string why)
    {
        world.Component(key, "ns", "rel", chart: chart);

        ScrapeTargetSpec row = world.Specs().Should().ContainSingle().Subject;
        row.Monitor.Should().BeNull();
        row.Info.Should().Contain(why);
    }

    /// <summary>
    /// The catalog knows the chart annotates its pods, not that anything takes them: what the planner
    /// has seen of the annotation job is said beside it, and the catalog's own sentence claims nothing.
    /// </summary>
    [Theory]
    [InlineData("traefik")]
    [InlineData("cert-manager")]
    [InlineData("velero")]
    public void A_component_left_to_the_annotation_job_is_marked_so_and_claims_no_scrape(string chart)
    {
        world.Component(chart, "ns", "rel");

        ScrapeTargetSpec row = world.Specs().Should().ContainSingle().Subject;
        row.LeftToAnnotationJob.Should().BeTrue();
        row.Info.Should().NotContain("already scrapes").And.NotContain("which the Prometheus stack");
    }

    [Fact]
    public void Only_a_component_its_chart_annotates_is_left_to_the_annotation_job()
    {
        world.Component("kube-prometheus-stack", "monitoring", "kps");
        world.Component("loki", "loki", "loki");

        world.Specs().Should().OnlyContain(s => !s.LeftToAnnotationJob);
    }

    [Theory]
    [InlineData("gateway", "istio")]
    [InlineData("gateway", "istio-internal")]
    [InlineData("base", "istio-base")]
    public void Istios_charts_have_names_so_plain_that_the_component_has_to_be_Istios(string chart, string name)
    {
        world.Component(chart, "istio-system", "rel", name: name);
        world.Component(chart, "apps", "theirs", name: "some-teams-chart");

        world.Specs().Should().ContainSingle().Which.Info.Should().Contain("annotations");
    }

    [Theory]
    [InlineData(false, false, "outside the cluster")]
    [InlineData(true, false, "password")]
    public void MongoDb_is_not_scraped_and_says_why(bool inCluster, bool exporter, string why)
    {
        world.DataService(ManagedServiceKind.MongoDb, "data", "x", inCluster: inCluster, exporter: exporter);

        world.Specs().Should().ContainSingle().Which.Info.Should().Contain(why);
    }

    [Theory]
    [InlineData(true, "x-es-exporter")]
    [InlineData(false, "Turn on")]
    public void Elasticsearch_is_scraped_through_its_own_exporter(bool exporter, string says)
    {
        world.DataService(ManagedServiceKind.Elasticsearch, "data", "x", exporter: exporter);

        ScrapeTargetSpec row = world.Specs().Should().ContainSingle().Subject;
        row.Monitor.Should().BeNull("its exporter's monitor is the Elasticsearch service's");
        row.Info.Should().Contain(says);
    }

    [Fact]
    public void A_broker_EntKube_only_adopted_is_not_guessed_at()
    {
        world.DataService(ManagedServiceKind.RabbitMq, "data", "x", operatorManaged: false);

        world.Specs().Should().ContainSingle().Which.Monitor.Should().BeNull();
    }

    [Fact]
    public void A_chart_nobody_here_knows_gets_no_row()
    {
        world.Component("a-teams-own-chart", "apps", "rel");
        world.Component("a-teams-own-manifest", "apps", "rel", chart: false);

        world.Specs().Should().BeEmpty();
    }

    /// <summary>A component of the cluster's own is another's release under a name EntKube recorded.</summary>
    [Fact]
    public void A_component_the_cluster_provides_is_not_matched_by_our_charts_labels()
    {
        world.Component("snapshot-controller", "kube-system", "rel", type: ClusterProvidedComponents.ComponentType);

        world.Specs().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ComponentStatus.Installed, true, true)]
    [InlineData(ComponentStatus.Failed, true, true)]
    [InlineData(ComponentStatus.Installing, true, true)]
    [InlineData(ComponentStatus.Installing, false, false)]
    [InlineData(ComponentStatus.Failed, false, false)]
    [InlineData(ComponentStatus.Uninstalling, true, false)]
    [InlineData(ComponentStatus.NotInstalled, true, false)]
    public void A_component_counts_while_a_release_of_it_is_running(ComponentStatus status, bool ranBefore, bool counts)
    {
        world.Components.Add(world.InstalledComponent("reloader", "reloader", "reloader", status: status) with
        {
            InstalledAt = ranBefore ? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        });

        world.Specs().Should().HaveCount(counts ? 1 : 0);
    }

    [Theory]
    [InlineData(null, "/auth/metrics")]
    [InlineData("http:\n  relativePath: /auth\n", "/auth/metrics")]
    [InlineData("http:\n  relativePath: /\n", "/metrics")]
    [InlineData("http:\n  relativePath: /id/\n", "/id/metrics")]
    [InlineData("http:\n  relativePath: /auth\n  managementRelativePath: /internal\n", "/internal/metrics")]
    [InlineData("http:\n  relativePath: /auth\n  managementRelativePath: \"\"\n", "/auth/metrics")]
    public void Keycloaks_metrics_are_under_its_own_path(string? values, string path)
    {
        world.Component("keycloakx", "keycloak", "keycloak", values);

        world.Specs().Single().Monitor!.Endpoints.Single().Path.Should().Be(path);
    }

    [Fact]
    public void A_component_with_no_release_name_goes_by_its_own()
    {
        world.Component("reloader", "ns", release: null, name: "my-reloader");

        Describe(world.Specs().Single()).Should().Contain("entkube-reloader-my-reloader").And.Contain($"{I}=my-reloader");
    }

    [Fact]
    public void A_component_with_no_namespace_is_not_looked_for_in_a_guessed_one()
    {
        world.Components.Add(world.InstalledComponent("reloader", ns: null, "rel"));

        ScrapeTargetSpec row = world.Specs().Should().ContainSingle().Subject;
        row.Monitor.Should().BeNull();
        row.Info.Should().Contain("no namespace recorded");
    }

    /// <summary>The same database registered twice is one database, whatever its rows' ids are.</summary>
    [Fact]
    public void One_database_is_one_target()
    {
        world.DataService(ManagedServiceKind.Postgres, "data", "x");
        world.DataService(ManagedServiceKind.Postgres, "data", "x");

        world.Specs().Should().ContainSingle().Which.Key.Should().Be("cnpg/data/x");
    }

    /// <summary>
    /// SeaweedFS's chart owns its NetworkPolicies and undoes an edit to them on its next upgrade. What
    /// lets Prometheus in is the rule EntKube adds when SeaweedFS is applied; whether a re-apply would add
    /// one that lets the running Prometheus in is the planner's to say (it knows what Catalog records and
    /// what is live), so the catalog names the component and says only who owns the policy.
    /// </summary>
    [Fact]
    public void Every_part_of_SeaweedFS_is_opened_by_applying_it_when_Prometheus_is_kept_out()
    {
        world.Component("seaweedfs", "seaweedfs", "seaweedfs");

        world.Specs().Should().HaveCount(4).And.OnlyContain(t => t.OpenedByApplyOf == "SeaweedFS"
            && t.WhenBlocked != null && t.WhenBlocked.Contains("owns that policy") && t.WhenBlocked.EndsWith('.')
            && !t.WhenBlocked.Contains("Re-apply"));
    }
}
