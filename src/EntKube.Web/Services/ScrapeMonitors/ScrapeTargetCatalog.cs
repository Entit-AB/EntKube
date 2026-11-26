using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Everything EntKube knows how to have scraped, and how: which pods or Services, which port, which
/// path. Nothing here looks at a cluster. What is true of one right now is
/// <see cref="ScrapeMonitorPlanner"/>'s business.
///
/// <para><b>Where the selectors come from.</b> Each was read off the rendered chart or manifest at
/// the version the catalog installs, or off the operator's source, and checked against what else in
/// the same release carries the same labels. Several are narrower than they look like they need to
/// be, and each of those is a workload that would otherwise be scraped twice or a target that would
/// be down for ever; the comment beside it says which.</para>
///
/// <para><b>Pods rather than Services wherever the container declares the port.</b> Charts routinely
/// put two Services in front of the same pods (Redis, Loki, Mimir, Tempo, SeaweedFS's filer), and a
/// ServiceMonitor matching both scrapes every pod twice. A ServiceMonitor is used where the port is
/// not declared on the container, or where the workload ships its own metrics Service.</para>
/// </summary>
public static class ScrapeTargetCatalog
{
    private const string Name = "app.kubernetes.io/name";
    private const string Instance = "app.kubernetes.io/instance";
    private const string Component = "app.kubernetes.io/component";
    private const string PartOf = "app.kubernetes.io/part-of";

    private const string ServiceAccountToken = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>
    /// The targets on one cluster, given everything installed on it and every data service the
    /// tenant has. Data services first, then components, each once.
    /// </summary>
    public static IReadOnlyList<ScrapeTargetSpec> For(
        IReadOnlyList<InstalledComponent> components, IReadOnlyList<ManagedServiceSummary> services)
        =>
        [
            // Existence is the row, whatever state it is in. Failed means the last thing EntKube did
            // to it failed, and Creating is before its first pod: neither is a reason to stop
            // scraping a database that answers. Only Deleting is on its way out.
            .. services
                .Where(s => s.Status != ManagedServiceStatus.Deleting)
                .SelectMany(DataService)
                .Concat(components.Where(IsOurs).SelectMany(InstalledPart))
                .DistinctBy(t => t.Key),
        ];

    /// <summary>
    /// The targets of one component, whatever its status: what its monitors are called does not depend
    /// on it. None for a component the cluster provides. For <see cref="ScrapeMonitorIdentity"/>.
    /// </summary>
    internal static IEnumerable<ScrapeTargetSpec> PartsOf(InstalledComponent component)
        => component.ComponentType == ClusterProvidedComponents.ComponentType ? [] : InstalledPart(component);

    /// <summary>The targets of one data service, whatever its status. For <see cref="ScrapeMonitorIdentity"/>.</summary>
    internal static IEnumerable<ScrapeTargetSpec> PartsOf(ManagedServiceSummary service) => DataService(service);

    /// <summary>
    /// Whether a component is one whose labels follow from EntKube's own install. A component the
    /// cluster provides (a managed Kubernetes' own metrics-server) is somebody else's release under
    /// a name EntKube recorded, so no chart convention holds for it.
    /// </summary>
    private static bool IsOurs(InstalledComponent component)
        => component.ComponentType != ClusterProvidedComponents.ComponentType
           && PrometheusPresence.Serving(component.Status, component.InstalledAt);

    // ── Data services ─────────────────────────────────────────────────────────

    private static IEnumerable<ScrapeTargetSpec> DataService(ManagedServiceSummary service)
        => service.Kind switch
        {
            ManagedServiceKind.Postgres =>
            [
                // podRole is not decoration: PgBouncer pooler pods carry the same cnpg.io/cluster
                // and a port of the same name, so without it their metrics mix into the database's.
                // The job is the one the operator's own PodMonitor gives (namespace/cluster), so the
                // series stay the same ones when ours takes over from it.
                Data(service, "cnpg", "PostgreSQL", MonitorKind.PodMonitor, $"entkube-cnpg-{service.Name}",
                    LabelSelector.Of(("cnpg.io/cluster", service.Name), ("cnpg.io/podRole", "instance")),
                    new ScrapeEndpointSpec("metrics", HttpsWhenContainerArgument: "--metrics-port-tls", Relabelings:
                        [new ScrapeRelabeling(TargetLabel: "job", Replacement: $"{service.Namespace}/{service.Name}")])),
            ],

            ManagedServiceKind.Redis =>
            [
                // Pods, because <name>-leader and <name>-leader-metrics are two Services with the
                // same labels and the same port in front of the same pods.
                Data(service, "redis", "Redis", MonitorKind.PodMonitor, $"entkube-redis-{service.Name}",
                    LabelSelector.Of(("redis_setup_type", "cluster"))
                        .In("app", $"{service.Name}-leader", $"{service.Name}-follower"),
                    new ScrapeEndpointSpec("redis-exporter")),
            ],

            ManagedServiceKind.RabbitMq when service.IsOperatorManaged =>
            [
                // The name is the old "Enable metrics" button's, so its monitor is taken over rather
                // than doubled. A broker with TLS serves prometheus-tls instead of prometheus, never
                // both, so the two endpoints cannot scrape the same pod twice.
                DataMonitor(service, "rabbitmq", "RabbitMQ",
                    new MonitorSpec(
                        new MonitorIdentity(MonitorKind.ServiceMonitor, service.Namespace, $"{service.Name}-metrics"),
                        LabelSelector.Of((Name, service.Name), (Component, "rabbitmq")),
                        [new ScrapeEndpointSpec("prometheus"), new ScrapeEndpointSpec("prometheus-tls", Https: true)])
                    {
                        TakesOverUnlabelled = true,
                    }),
            ],

            ManagedServiceKind.RabbitMq =>
            [
                DataInfo(service, "rabbitmq", "RabbitMQ",
                    "This broker was installed by something other than EntKube, so its Services, labels and "
                    + "metrics port are whatever that chart made them. Turn on that chart's own metrics; if it "
                    + "annotates the pods, Prometheus scrapes them without a monitor."),
            ],

            ManagedServiceKind.Kafka =>
            [
                // One monitor for brokers, controllers and the kafka-exporter: they share the kind
                // label and the port name. Nothing declares the port until the Kafka resource has a
                // metricsConfig, which EntKube does not set yet.
                Data(service, "kafka", "Kafka", MonitorKind.PodMonitor, $"entkube-kafka-{service.Name}",
                    LabelSelector.Of(("strimzi.io/cluster", service.Name), ("strimzi.io/kind", "Kafka")),
                    new ScrapeEndpointSpec("tcp-prometheus", Relabelings:
                        [new ScrapeRelabeling(Regex: "__meta_kubernetes_pod_label_(strimzi_io_.+)", Action: "labelmap")])),

                // Two names since Strimzi 1.1.0; both containers called theirs "healthcheck" before.
                Data(service, "kafka-entity", "Kafka entity operator", MonitorKind.PodMonitor,
                    $"entkube-kafka-entity-{service.Name}",
                    LabelSelector.Of(("strimzi.io/cluster", service.Name), (Name, "entity-operator")),
                    new ScrapeEndpointSpec("healthcheck-to"),
                    new ScrapeEndpointSpec("healthcheck-uo"),
                    new ScrapeEndpointSpec("healthcheck")),
            ],

            ManagedServiceKind.MongoDb when !service.IsInCluster =>
            [
                DataInfo(service, "mongodb", "MongoDB",
                    "This MongoDB runs outside the cluster and is reached by a connection string, so there is "
                    + "nothing here for Prometheus to scrape."),
            ],

            ManagedServiceKind.MongoDb =>
            [
                DataInfo(service, "mongodb", "MongoDB",
                    "MongoDB's metrics endpoint needs a user name and password, and EntKube does not expose it yet."),
            ],

            ManagedServiceKind.Elasticsearch =>
            [
                DataInfo(service, "elasticsearch", "Elasticsearch", service.HasExporter
                    ? "Scraped through the exporter EntKube installed for it, which has its own ServiceMonitor, "
                      + $"{service.Name}-es-exporter."
                    : "Elasticsearch needs an exporter to have Prometheus metrics. Turn on \"Export to Prometheus\" "
                      + "on its own page; that creates the exporter and the ServiceMonitor for it."),
            ],

            _ => [],
        };

    // ── Components ────────────────────────────────────────────────────────────

    /// <summary>
    /// What one installed component contributes. Charts are recognised by chart name, and the
    /// components EntKube installs from a manifest (which have no chart) by their catalog key.
    /// A chart or manifest that is not listed contributes nothing, not even a row: its ports are
    /// unknown and a guess would be a target that is down for ever.
    /// </summary>
    private static IEnumerable<ScrapeTargetSpec> InstalledPart(InstalledComponent c)
        => string.IsNullOrWhiteSpace(c.HelmChartName) ? FromManifest(c) : FromChart(c);

    private static IEnumerable<ScrapeTargetSpec> FromChart(InstalledComponent c)
    {
        string release = Release(c);

        return c.HelmChartName switch
        {
            // ── Scraped by pod ────────────────────────────────────────────────

            "cloudnative-pg" => [Pods(c, "cloudnative-pg", "CloudNativePG operator",
                LabelSelector.Of((Name, "cloudnative-pg"), (Instance, release)), new("metrics"))],

            // The operator pod carries only this one label; no app.kubernetes.io/* at all.
            "redis-operator" => [Pods(c, "redis-operator", "Redis operator",
                LabelSelector.Of(("name", "redis-operator")), new("metrics"))],

            "strimzi-kafka-operator" => [Pods(c, "strimzi", "Strimzi cluster operator",
                LabelSelector.Of(("strimzi.io/kind", "cluster-operator")), new("http"))],

            "reloader" => [Pods(c, "reloader", "Reloader",
                LabelSelector.Of((Name, "reloader"), (Instance, release)), new("http"))],

            // The only port with no name, hence the number.
            "descheduler" => [Pods(c, "descheduler", "Descheduler",
                LabelSelector.Of((Name, "descheduler"), (Instance, release)),
                new(Port: null, ContainerPort: 10258, Https: true))],

            "eck-operator" => [Pods(c, "eck-operator", "ECK operator",
                LabelSelector.Of((Name, "elastic-operator"), (Instance, release)), new("metrics"))],

            "alloy" => [Pods(c, "alloy", "Grafana Alloy",
                LabelSelector.Of((Name, "alloy"), (Instance, release)), new("http-metrics"))],

            // The gateway is an nginx whose port is NAMED http-metrics and which serves no metrics:
            // selecting it is a target that is down for ever. The same in Mimir.
            "loki" => [Pods(c, "loki", "Loki",
                LabelSelector.Of((Name, "loki"), (Instance, release)).NotIn(Component, "gateway"),
                new("http-metrics"))],

            "mimir-distributed" =>
            [
                Pods(c, "mimir", "Mimir",
                    LabelSelector.Of((Name, "mimir"), (Instance, release)).NotIn(Component, "gateway", "smoke-test"),
                    new("http-metrics")),
                Pods(c, "mimir-rollout-operator", "Mimir rollout operator",
                    LabelSelector.Of((Name, "rollout-operator"), (Instance, release)), new("http-metrics")),
            ],

            "tempo-distributed" => [Pods(c, "tempo", "Tempo",
                LabelSelector.Of((Name, "tempo"), (Instance, release)).NotIn(Component, "memcached", "gateway"),
                new("http-metrics"))],

            // Dex declares its metrics port whether or not Dex is configured, and serves it only when
            // it is. Argo's Redis has no exporter.
            "argo-cd" => [Pods(c, "argocd", "Argo CD",
                LabelSelector.Of((PartOf, "argocd"), (Instance, release))
                    .NotIn(Component, "dex-server", "redis", "redis-secret-init"),
                new("metrics"))],

            "snapshot-controller" => [Pods(c, "snapshot-controller", "CSI snapshot controller",
                LabelSelector.Of((Name, "snapshot-controller"), (Instance, release)), new("http"))],

            // ── Scraped by Service ────────────────────────────────────────────

            // The component label is what separates this Service from the webhook's, which is also
            // app=trust-manager.
            "trust-manager" => [Services(c, "trust-manager", "trust-manager",
                LabelSelector.Of(("app", "trust-manager"), (Component, "metrics")), new("metrics"))],

            // Four controllers, one monitor. By Service because the container port is called
            // metrics-port on one of them and metrics on the other three.
            "kyverno" => [Services(c, "kyverno", "Kyverno",
                LabelSelector.Of((Instance, release), (PartOf, "kyverno")), new("metrics-port"))],

            "tetragon" =>
            [
                // The agent's containers declare no ports, so a PodMonitor would find nothing.
                Services(c, "tetragon", "Tetragon agent",
                    LabelSelector.Of((Name, "tetragon"), (Instance, release)),
                    new("metrics", HonorLabels: true, Relabelings:
                        [new ScrapeRelabeling(["__meta_kubernetes_pod_node_name"], "node")])),
                Services(c, "tetragon-operator", "Tetragon operator",
                    LabelSelector.Of((Name, "tetragon-operator"), (Instance, release)), new("metrics")),
            ],

            "keda" =>
            [
                Services(c, "keda-adapter", "KEDA metrics adapter",
                    LabelSelector.Of(("app", "keda-operator-metrics-apiserver")), new("metrics")),
                Services(c, "keda-operator", "KEDA operator",
                    LabelSelector.Of((Name, "keda-operator"), (Instance, release)),
                    new("metrics", HonorLabels: true)),
            ],

            // Not called nvidia-dcgm-exporter: the GPU operator deletes a ServiceMonitor of that name.
            "gpu-operator" => [Services(c, "dcgm-exporter", "NVIDIA DCGM exporter",
                LabelSelector.Of(("app", "nvidia-dcgm-exporter")), new("gpu-metrics", HonorLabels: true))],

            "keycloakx" => [Services(c, "keycloak", "Keycloak",
                LabelSelector.Of((Name, "keycloakx"), (Instance, release), (Component, "http")),
                new("http-internal", Path: KeycloakMetricsPath(c.HelmValues)))],

            "harbor" => [Services(c, "harbor", "Harbor",
                LabelSelector.Of(("release", release), ("app", "harbor")), new("http-metrics"))],

            "seaweedfs" =>
            [
                SeaweedFs(c, "master", "SeaweedFS master"),
                SeaweedFs(c, "volume", "SeaweedFS volume server"),
                // Two headless Services, seaweedfs-filer-client and seaweedfs-filer, select the same
                // pods with the same port. Only the second carries this label.
                SeaweedFs(c, "filer", "SeaweedFS filer", ("monitoring", "true")),
                SeaweedFs(c, "s3", "SeaweedFS S3 gateway"),
            ],

            // ── Nothing for EntKube to create ─────────────────────────────────

            "traefik" => [Annotated(c, "Traefik")],
            "gateway" when IsIstio(c) => [Annotated(c, "Istio gateway")],
            "base" when IsIstio(c) => [Annotated(c, "Istio control plane (istiod)")],
            "cert-manager" => [Annotated(c, "cert-manager")],
            "velero" => [Annotated(c, "Velero")],

            "kube-prometheus-stack" => [MakesItsOwn(c, "Prometheus stack")],
            "opentelemetry-ebpf-instrumentation" => [MakesItsOwn(c, "OBI eBPF agent")],
            "submariner-operator" => [MakesItsOwn(c, "Submariner")],

            "opentelemetry-collector" => [NotYet(c, "Telemetry collector",
                "The collector serves its own metrics on a port its pods do not declare, so no monitor can "
                + "select it. EntKube does not expose it yet.")],
            "metrics-server" => [NotYet(c, "Metrics server",
                "metrics-server asks for a token on its metrics endpoint, and EntKube does not scrape it yet. "
                + "The node and pod figures it serves come from the kubelet, which is already scraped.")],
            "tailscale-operator" => [NotYet(c, "Tailscale operator",
                "The Tailscale operator declares no metrics port, so no monitor can select it.")],
            "openldap-stack-ha" => [NotYet(c, "OpenLDAP",
                "OpenLDAP has no Prometheus endpoint of its own and EntKube installs no exporter for it.")],

            _ => [],
        };
    }

    private static IEnumerable<ScrapeTargetSpec> FromManifest(InstalledComponent c) => c.Name switch
    {
        // The topology operator's pod carries component=rabbitmq-operator as well, so this selects by
        // name. The manifest fixes its own namespace.
        "rabbitmq-cluster-operator" => [Pods(c, "rabbitmq-operator", "RabbitMQ cluster operator",
            LabelSelector.Of((Name, "rabbitmq-cluster-operator")), new("metrics"), ns: "rabbitmq-system")],

        // Its metrics port is not declared on the container, and the webhook's Service carries the
        // same name label without control-plane.
        "rabbitmq-messaging-topology-operator" => [Services(c, "rabbitmq-topology-operator", "RabbitMQ topology operator",
            LabelSelector.Of((Name, "messaging-topology-operator"), ("control-plane", "controller-manager")),
            new("https", Https: true, BearerTokenFile: ServiceAccountToken), ns: "rabbitmq-system")],

        "headscale" => [Pods(c, "headscale", "Headscale", LabelSelector.Of(("app", "headscale")), new("metrics"))],

        "kubevirt" => [Services(c, "kubevirt", "KubeVirt",
            LabelSelector.Of(("prometheus.kubevirt.io", "true")),
            new("metrics", Https: true, HonorLabels: true), ns: "kubevirt")],

        "kubevirt-cdi" => [MakesItsOwn(c, "KubeVirt CDI")],

        "stalwart" => [NotYet(c, "Stalwart mail server",
            "Stalwart's metrics endpoint is off, and without a password it would be published through the "
            + "mail server's own address. EntKube does not expose it yet.")],
        "rspamd" => [NotYet(c, "Rspamd",
            "Rspamd asks for its controller password in a header Prometheus cannot send, so it is not scraped.")],

        _ => [],
    };

    private static bool IsIstio(InstalledComponent c) => c.Name.StartsWith("istio", StringComparison.Ordinal);

    /// <summary>
    /// Where this Keycloak serves its metrics: the management interface inherits the public path
    /// unless it was given its own, and the chart trims the slashes around either.
    /// </summary>
    private static string KeycloakMetricsPath(string? helmValues)
    {
        string values = helmValues ?? "";
        string? management = YamlFormMerger.ExtractValue(values, "http.managementRelativePath");
        string? relative = string.IsNullOrWhiteSpace(management)
            ? YamlFormMerger.ExtractValue(values, "http.relativePath")
            : management;

        string prefix = (string.IsNullOrWhiteSpace(relative) ? "/auth" : relative).Trim().Trim('/');
        return prefix.Length == 0 ? "/metrics" : $"/{prefix}/metrics";
    }

    // ── Shapes ────────────────────────────────────────────────────────────────

    private static ScrapeTargetSpec Data(
        ManagedServiceSummary service, string slug, string what, MonitorKind kind, string monitorName,
        LabelSelector selector, params ScrapeEndpointSpec[] endpoints)
        => DataMonitor(service, slug, what, new MonitorSpec(
            new MonitorIdentity(kind, service.Namespace, monitorName), selector, endpoints));

    private static ScrapeTargetSpec DataMonitor(
        ManagedServiceSummary service, string slug, string what, MonitorSpec monitor)
        => new($"{slug}/{service.Namespace}/{service.Name}", service.Name, what, service.Namespace, monitor);

    private static ScrapeTargetSpec DataInfo(ManagedServiceSummary service, string slug, string what, string info)
        => new($"{slug}/{service.Namespace}/{service.Name}", service.Name, what, service.Namespace, Info: info);

    private static ScrapeTargetSpec Pods(
        InstalledComponent c, string slug, string what, LabelSelector selector, ScrapeEndpointSpec endpoint,
        string? ns = null)
        => Part(c, slug, what, MonitorKind.PodMonitor, selector, endpoint, ns);

    private static ScrapeTargetSpec Services(
        InstalledComponent c, string slug, string what, LabelSelector selector, ScrapeEndpointSpec endpoint,
        string? ns = null)
        => Part(c, slug, what, MonitorKind.ServiceMonitor, selector, endpoint, ns);

    private static ScrapeTargetSpec SeaweedFs(
        InstalledComponent c, string component, string what, params (string Key, string Value)[] more)
        => Services(c, $"seaweedfs-{component}", what,
            LabelSelector.Of([(Name, "seaweedfs"), (Instance, Release(c)), (Component, component), .. more]),
            new ScrapeEndpointSpec("metrics")) with
        {
            // The chart owns its NetworkPolicies. What opens them to Prometheus is the rule EntKube adds
            // to the release's values when SeaweedFS is applied on a cluster with Prometheus, which a
            // SeaweedFS installed before Prometheus never got. Whether that apply would open it to the
            // Prometheus that runs is the planner's to say: it knows what Catalog records and what is live.
            WhenBlocked = "The SeaweedFS chart owns that policy, and an edit to it by hand is undone by its next "
                + "upgrade.",
            OpenedByApplyOf = "SeaweedFS",
        };

    private static ScrapeTargetSpec Part(
        InstalledComponent c, string slug, string what, MonitorKind kind, LabelSelector selector,
        ScrapeEndpointSpec endpoint, string? fixedNamespace)
    {
        string ns = fixedNamespace ?? c.Namespace ?? "";
        string release = Release(c);

        // Nowhere to look and nowhere to put it. Said, rather than guessed at with a default.
        if (string.IsNullOrWhiteSpace(ns))
        {
            return NotYet(c, what,
                "EntKube has no namespace recorded for this component, so it cannot tell where it runs.", slug);
        }

        return new ScrapeTargetSpec($"{slug}/{ns}/{release}", release, what, ns, new MonitorSpec(
            new MonitorIdentity(kind, ns, $"entkube-{slug}-{release}"), selector, [endpoint]));
    }

    /// <summary>
    /// A chart that annotates its pods for an annotation scrape job. The sentence says what the chart
    /// does and nothing of whether a job takes them, which only Prometheus's list can tell: the planner
    /// adds that (<see cref="ScrapeTargetSpec.LeftToAnnotationJob"/>).
    /// </summary>
    private static ScrapeTargetSpec Annotated(InstalledComponent c, string what) => NotYet(c, what,
        "Its chart puts prometheus.io/scrape annotations on its pods for an annotation scrape job to take, so "
        + "EntKube adds no monitor: beside such a job, one would count everything twice.") with
    {
        LeftToAnnotationJob = true,
    };

    private static ScrapeTargetSpec MakesItsOwn(InstalledComponent c, string what) => NotYet(c, what,
        "It creates its own monitors, so EntKube adds none.");

    private static ScrapeTargetSpec NotYet(InstalledComponent c, string what, string info, string? slug = null)
    {
        string ns = c.Namespace ?? "";
        string release = Release(c);
        string kind = slug ?? (string.IsNullOrWhiteSpace(c.HelmChartName) ? c.Name : c.HelmChartName);
        return new ScrapeTargetSpec($"{kind}/{ns}/{release}", release, what, ns, Info: info);
    }

    /// <summary>The helm release, which is what the charts' instance label carries.</summary>
    private static string Release(InstalledComponent c)
        => string.IsNullOrWhiteSpace(c.ReleaseName) ? c.Name : c.ReleaseName;
}
