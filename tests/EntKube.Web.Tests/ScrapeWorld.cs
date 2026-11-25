using System.Text.Json.Nodes;
using EntKube.Contracts.Catalog;
using EntKube.Contracts.DataServices;
using EntKube.Web.Services.ScrapeMonitors;
using k8s.Models;

namespace EntKube.Web.Tests;

/// <summary>
/// A cluster for the monitor tests, described the way the charts and operators render it: pods and
/// Services with their literal labels and ports, monitors, policies, and what Prometheus says it
/// scrapes.
///
/// <para>Starts as kube-prometheus-stack 93.9.0 makes it: one Prometheus in <c>monitoring</c>, its
/// selectors empty, which selects everything.</para>
/// </summary>
internal sealed class ScrapeWorld
{
    public const string Release = "kube-prometheus-stack";

    public readonly Guid ClusterId = Guid.Parse("7a1e5f0c-94c9-4f0e-8f5c-0a8f9b5a6d11");
    public readonly Guid TenantId = Guid.Parse("5b1e5f0c-94c9-4f0e-8f5c-0a8f9b5a6d22");

    public readonly List<LivePrometheus> Prometheuses = [];
    public readonly List<LiveMonitor> Monitors = [];
    public readonly List<ActiveTarget> Targets = [];
    public readonly List<InstalledComponent> Components = [];
    public readonly List<ManagedServiceSummary> Services = [];

    private readonly Dictionary<string, Dictionary<string, string>> namespaceLabels = new(StringComparer.Ordinal);
    private readonly List<V1Pod> pods = [];
    private readonly List<V1Service> services = [];
    private readonly List<V1NetworkPolicy> policies = [];
    private int lastAddress = 10;

    public ScrapeWorld()
    {
        Namespace("monitoring");
        Prometheus("""{"serviceMonitorSelector":{},"podMonitorSelector":{},"serviceMonitorNamespaceSelector":{},"podMonitorNamespaceSelector":{}}""");
        Pod("monitoring", "prometheus-kps-0", Labels(
            ("app.kubernetes.io/name", "prometheus"), ("operator.prometheus.io/name", $"{Release}-prometheus")),
            Container("prometheus", ("http-web", 9090)));
    }

    // ── What is live ──────────────────────────────────────────────────────────

    /// <summary>A namespace, with the name label Kubernetes puts on every one.</summary>
    public ScrapeWorld Namespace(string name, params (string Key, string Value)[] labels)
    {
        namespaceLabels[name] = Labels([("kubernetes.io/metadata.name", name), .. labels]);
        return this;
    }

    /// <summary>Replaces the Prometheus with one whose spec is this.</summary>
    public ScrapeWorld Prometheus(
        string spec, string ns = "monitoring", string name = $"{Release}-prometheus", string release = Release,
        bool another = false)
    {
        if (!another) Prometheuses.Clear();
        Prometheuses.Add(new LivePrometheus(ns, name, Labels(("release", release)), Json(spec)));
        return this;
    }

    /// <summary>A running pod, with an address of its own.</summary>
    public V1Pod Pod(string ns, string name, Dictionary<string, string> labels, params V1Container[] containers)
    {
        if (!namespaceLabels.ContainsKey(ns)) Namespace(ns);

        V1Pod pod = new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1PodSpec { Containers = [.. containers] },
            Status = new V1PodStatus { Phase = "Running", PodIP = $"10.0.0.{++lastAddress}" },
        };

        pods.Add(pod);
        return pod;
    }

    public V1Service Service(
        string ns, string name, Dictionary<string, string> labels, Dictionary<string, string>? selector,
        params (string Name, int Port, string? Target)[] ports)
    {
        if (!namespaceLabels.ContainsKey(ns)) Namespace(ns);

        V1Service service = new()
        {
            Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1ServiceSpec
            {
                Selector = selector,
                Ports = [.. ports.Select(p => new V1ServicePort { Name = p.Name, Port = p.Port, TargetPort = p.Target })],
            },
        };

        services.Add(service);
        return service;
    }

    public ScrapeWorld Policy(string ns, string spec)
    {
        policies.Add(k8s.KubernetesJson.Deserialize<V1NetworkPolicy>(
            $$$"""{"metadata":{"name":"policy","namespace":"{{{ns}}}"},"spec":{{{spec}}}}"""));
        return this;
    }

    /// <summary>A monitor somebody made. Not EntKube's unless the labels say so.</summary>
    public LiveMonitor Monitor(
        MonitorKind kind, string ns, string name, string spec, params (string Key, string Value)[] labels)
    {
        LiveMonitor monitor = new(
            new MonitorIdentity(kind, ns, name), Labels(labels), new Dictionary<string, string>(), Json(spec));
        Monitors.Add(monitor);
        return monitor;
    }

    /// <summary>A monitor of EntKube's for this cluster registration, selecting nothing in particular.</summary>
    public LiveMonitor OurMonitor(MonitorKind kind, string ns, string name)
        => Monitor(kind, ns, name, "{}", (ScrapeMonitorManifest.OwnershipLabel, ClusterId.ToString()));

    /// <summary>Prometheus scrapes this pod's port through that pool.</summary>
    public ScrapeWorld Scraped(string pool, V1Pod pod, int port, string path = "/metrics", bool up = true)
    {
        Targets.Add(new ActiveTarget(pool, pod.Status.PodIP, port, path, up));
        return this;
    }

    // ── What EntKube knows ────────────────────────────────────────────────────

    public ScrapeWorld DataService(
        ManagedServiceKind kind, string ns, string name,
        ManagedServiceStatus status = ManagedServiceStatus.Running,
        bool operatorManaged = true, bool inCluster = true, bool exporter = false)
    {
        Services.Add(new ManagedServiceSummary(
            Guid.NewGuid(), TenantId, ClusterId, kind, name, ns, status, operatorManaged, inCluster, exporter));
        return this;
    }

    public ScrapeWorld Component(
        string chartOrKey, string ns, string? release = null, string? values = null, bool chart = true,
        ComponentStatus status = ComponentStatus.Installed, string type = "HelmChart", string? name = null)
    {
        Components.Add(InstalledComponent(chartOrKey, ns, release, values, chart, status, type, name));
        return this;
    }

    public InstalledComponent InstalledComponent(
        string chartOrKey, string? ns, string? release = null, string? values = null, bool chart = true,
        ComponentStatus status = ComponentStatus.Installed, string type = "HelmChart", string? name = null)
        => new(
            Guid.NewGuid(), TenantId, ClusterId, "prod", Guid.NewGuid(), name ?? chartOrKey,
            chart ? type : "Manifest", status, ns, release, chart ? chartOrKey : "", null, values, null, null,
            status == ComponentStatus.Installed ? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc) : null);

    // ── The plan ──────────────────────────────────────────────────────────────

    public IReadOnlyList<ScrapeTargetSpec> Specs() => ScrapeTargetCatalog.For(Components, Services);

    public LiveMonitoring Monitoring() => new([.. Prometheuses], [.. Monitors], [.. Targets]);

    public LiveWorkloads Workloads() => new(
        namespaceLabels.ToDictionary(n => n.Key, n => (IReadOnlyDictionary<string, string>)n.Value),
        namespaceLabels.Keys.ToDictionary(n => n, n => new LiveNamespace(
            n,
            [.. pods.Where(p => p.Metadata.NamespaceProperty == n)],
            [.. services.Where(s => s.Metadata.NamespaceProperty == n)],
            [.. policies.Where(p => p.Metadata.NamespaceProperty == n)])));

    public LiveScrapeState Live() => new(
        ClusterId, new PrometheusRelease("monitoring", Release), Monitoring(), Workloads());

    public ScrapeMonitorPlan Plan() => ScrapeMonitorPlanner.Plan(Specs(), Live());

    /// <summary>
    /// The plan carried out, the way the API server would leave it: the manifests as live objects,
    /// with the default it fills in, and what was pruned gone.
    /// </summary>
    public ScrapeWorld Apply(ScrapeMonitorPlan plan)
    {
        foreach (MonitorIdentity gone in plan.Delete) Monitors.RemoveAll(m => m.Identity == gone);

        foreach (PlannedMonitor planned in plan.Apply)
        {
            Monitors.RemoveAll(m => m.Identity == planned.Identity);
            JsonObject metadata = planned.Manifest["metadata"]!.AsObject();
            JsonObject spec = planned.Manifest["spec"]!.DeepClone().AsObject();

            foreach (JsonNode? rule in (spec["endpoints"] ?? spec["podMetricsEndpoints"])!.AsArray()
                         .SelectMany(e => e!["relabelings"]?.AsArray() ?? []))
            {
                if (rule!["action"] is null) rule["action"] = "replace";
            }

            Monitors.Add(new LiveMonitor(
                planned.Identity, Pairs(metadata["labels"]!), Pairs(metadata["annotations"]!), spec));
        }

        return this;
    }

    // ── Shorthand ─────────────────────────────────────────────────────────────

    public static Dictionary<string, string> Labels(params (string Key, string Value)[] labels)
        => labels.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);

    public static V1Container Container(string name, params (string? Name, int Number)[] ports) => new()
    {
        Name = name,
        Ports = [.. ports.Select(p => new V1ContainerPort { Name = p.Name, ContainerPort = p.Number, Protocol = "TCP" })],
    };

    public static JsonObject Json(string json) => JsonNode.Parse(json)!.AsObject();

    private static Dictionary<string, string> Pairs(JsonNode node)
        => node.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<string>(), StringComparer.Ordinal);

    // ── Services as their operators make them ─────────────────────────────────

    /// <summary>A CNPG instance pod (operator 1.30.2).</summary>
    public V1Pod CnpgPod(string cluster, string ns = "databases", int instance = 1, params string[] moreArguments)
    {
        V1Container postgres = Container("postgres", ("postgresql", 5432), ("metrics", 9187), ("status", 8000));
        postgres.Command = ["/controller/manager", "instance", "run", .. moreArguments];

        return Pod(ns, $"{cluster}-{instance}", Labels(
            ("cnpg.io/cluster", cluster), ("cnpg.io/podRole", "instance"), ("app.kubernetes.io/name", "postgresql")),
            postgres);
    }

    /// <summary>The PodMonitor the CNPG operator makes for <c>enablePodMonitor: true</c>. No release label.</summary>
    public LiveMonitor CnpgOperatorsPodMonitor(string cluster, string ns = "databases")
        => Monitor(MonitorKind.PodMonitor, ns, cluster,
            $$$"""{"selector":{"matchLabels":{"cnpg.io/cluster":"{{{cluster}}}","cnpg.io/podRole":"instance"}},"podMetricsEndpoints":[{"port":"metrics"}]}""",
            ("app.kubernetes.io/managed-by", "cloudnative-pg"), ("cnpg.io/cluster", cluster));

    /// <summary>A RabbitMQ broker pod and its two Services (cluster-operator 2.23.3).</summary>
    public V1Pod RabbitBroker(string name, string ns = "queues")
    {
        Dictionary<string, string> labels = Labels(
            ("app.kubernetes.io/name", name), ("app.kubernetes.io/component", "rabbitmq"));
        V1Pod pod = Pod(ns, $"{name}-server-0", labels,
            Container("rabbitmq", ("epmd", 4369), ("amqp", 5672), ("management", 15672), ("prometheus", 15692)));

        Service(ns, name, labels, Labels(("app.kubernetes.io/name", name)),
            ("amqp", 5672, null), ("prometheus", 15692, null));
        Service(ns, $"{name}-nodes", labels, Labels(("app.kubernetes.io/name", name)),
            ("epmd", 4369, null), ("cluster-rpc", 25672, null));

        return pod;
    }

    /// <summary>The ServiceMonitor the RabbitMQ panel's old button wrote: no ownership, no release.</summary>
    public LiveMonitor OldRabbitMonitor(string name, string ns = "queues")
        => Monitor(MonitorKind.ServiceMonitor, ns, $"{name}-metrics",
            $$$"""{"selector":{"matchLabels":{"app.kubernetes.io/name":"{{{name}}}","app.kubernetes.io/component":"rabbitmq"}},"endpoints":[{"port":"prometheus","scheme":"http","interval":"15s","scrapeTimeout":"14s"}]}""",
            ("app.kubernetes.io/name", name), ("app.kubernetes.io/component", "rabbitmq"));

    /// <summary>A Keycloak pod and its Services (keycloakx 7.4.0).</summary>
    public V1Pod KeycloakPod(string ns = "keycloak", string release = "keycloak")
    {
        Dictionary<string, string> selector = Labels(
            ("app.kubernetes.io/name", "keycloakx"), ("app.kubernetes.io/instance", release));
        V1Pod pod = Pod(ns, $"{release}-keycloakx-0", selector,
            Container("keycloak", ("http", 8080), ("http-internal", 9000)));

        Service(ns, $"{release}-keycloakx-http", Labels(("app.kubernetes.io/name", "keycloakx"),
            ("app.kubernetes.io/instance", release), ("app.kubernetes.io/component", "http")), selector,
            ("http", 80, "http"), ("http-internal", 9000, "http-internal"));
        Service(ns, $"{release}-keycloakx-headless", Labels(("app.kubernetes.io/name", "keycloakx"),
            ("app.kubernetes.io/instance", release), ("app.kubernetes.io/component", "headless")), selector,
            ("http", 80, "http"));

        return pod;
    }

    /// <summary>The ServiceMonitor the keycloakx chart renders for <c>serviceMonitor.enabled: true</c>. No release label.</summary>
    public LiveMonitor KeycloakChartsMonitor(string ns = "keycloak", string release = "keycloak")
        => Monitor(MonitorKind.ServiceMonitor, ns, $"{release}-keycloakx-keycloakx",
            $$$"""{"selector":{"matchLabels":{"app.kubernetes.io/name":"keycloakx","app.kubernetes.io/instance":"{{{release}}}","app.kubernetes.io/component":"http"}},"endpoints":[{"port":"http-internal","path":"/auth/metrics","interval":"10s"}]}""",
            ("app.kubernetes.io/name", "keycloakx"));

    /// <summary>A SeaweedFS filer pod and the two Services in front of it (chart 4.54.0).</summary>
    public V1Pod SeaweedFiler(string ns = "seaweedfs", string release = "seaweedfs")
    {
        (string, string)[] labels =
        [
            ("app.kubernetes.io/name", "seaweedfs"), ("app.kubernetes.io/instance", release),
            ("app.kubernetes.io/component", "filer"),
        ];
        V1Pod pod = Pod(ns, $"{release}-filer-0", Labels(labels),
            Container("seaweedfs", ("swfs-filer", 8888), ("metrics", 9327)));

        Service(ns, $"{release}-filer-client", Labels(labels), Labels(labels), ("metrics", 9327, "9327"));
        Service(ns, $"{release}-filer", Labels([.. labels, ("monitoring", "true")]), Labels(labels), ("metrics", 9327, "9327"));

        return pod;
    }
}
