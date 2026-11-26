using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>One port of one running pod, and the path on it.</summary>
public sealed record ScrapedEndpoint(V1Pod Pod, int Port, string Path)
{
    public string PodName => Pod.Metadata.Name;

    public string PodIp => Pod.Status.PodIP;
}

/// <summary>One endpoint a ServiceMonitor would reach through more than one Service, and those Services.</summary>
public sealed record TwiceScrapedEndpoint(ScrapedEndpoint Endpoint, IReadOnlyList<string> Services);

/// <summary>What a monitor would scrape in a namespace, as it runs right now.</summary>
/// <param name="Pods">The running pods it selects, whether or not they expose the port.</param>
/// <param name="Endpoints">The ports of theirs it would scrape, each once.</param>
/// <param name="Twice">
/// The ones it would scrape more than once. Prometheus makes a ServiceMonitor one target per Service
/// that leads to a pod, each with its own job label, so two Services in front of the same pods with
/// the same port double every sum over them.
/// </param>
public sealed record ResolvedTargets(
    IReadOnlyList<V1Pod> Pods, IReadOnlyList<ScrapedEndpoint> Endpoints, IReadOnlyList<TwiceScrapedEndpoint> Twice);

/// <summary>
/// Follows a monitor's selector to the pod ports it ends at, the way the Prometheus Operator's own
/// configuration does: a PodMonitor to the containers that declare the port, a ServiceMonitor
/// through a Service's port to the pods behind it.
///
/// <para>Both questions EntKube has come down to this. Where would our monitor scrape? And where
/// does somebody else's already? Answering them with the same rules is what makes "the same
/// endpoint" mean something.</para>
/// </summary>
public static class ScrapedEndpoints
{
    /// <summary>Where one of EntKube's own monitors would scrape.</summary>
    public static ResolvedTargets Of(MonitorSpec monitor, LiveNamespace ns)
    {
        if (monitor.Identity.Kind == MonitorKind.PodMonitor)
        {
            List<V1Pod> selected = [.. RunningPods(ns).Where(p => monitor.Selector.Matches(Labels(p)) is true)];

            return new ResolvedTargets(selected, Distinct(
                from endpoint in monitor.Endpoints
                from pod in selected
                from port in DeclaredPorts(pod)
                where endpoint.Port is not null ? port.Name == endpoint.Port : port.ContainerPort == endpoint.ContainerPort
                select new ScrapedEndpoint(pod, port.ContainerPort, endpoint.Path)), []);
        }

        List<V1Service> services = [.. ns.Services.Where(s => monitor.Selector.Matches(Labels(s)) is true)];

        List<(ScrapedEndpoint Endpoint, string Service)> reached =
        [
            .. from endpoint in monitor.Endpoints
               from service in services
               from port in service.Spec?.Ports ?? []
               where port.Name == endpoint.Port
               from target in Behind(service, port, ns)
               select (new ScrapedEndpoint(target.Pod, target.Port, endpoint.Path), service.Metadata.Name),
        ];

        return new ResolvedTargets(
            [.. services.SelectMany(s => PodsBehind(s, ns)).DistinctBy(p => p.Metadata.Name)],
            Distinct(reached.Select(r => r.Endpoint)),
            [
                .. reached
                    .GroupBy(r => (r.Endpoint.PodIp, r.Endpoint.Port, r.Endpoint.Path))
                    .Where(g => g.Count() > 1)
                    .Select(g => new TwiceScrapedEndpoint(g.First().Endpoint, [.. g.Select(r => r.Service).Distinct()])),
            ]);
    }

    /// <summary>
    /// The pod ports somebody else's monitor scrapes in this namespace, as "ip:port". In the first
    /// set the ones it certainly scrapes; in the second, those its selector could not be read well
    /// enough to rule out.
    /// </summary>
    /// <param name="monitor">The monitor.</param>
    /// <param name="ns">The namespace.</param>
    /// <param name="namespaceSelectorIgnored">
    /// The Prometheus reading it ignores monitors' namespace selectors, so it looks in its own only.
    /// </param>
    public static (IReadOnlySet<string> Certain, IReadOnlySet<string> Possible) OfForeign(
        LiveMonitor monitor, LiveNamespace ns, bool namespaceSelectorIgnored = false)
    {
        HashSet<string> certain = new(StringComparer.Ordinal);
        HashSet<string> possible = new(StringComparer.Ordinal);

        if (!monitor.LooksIn(ns.Name, namespaceSelectorIgnored)) return (certain, possible);

        LabelSelector? selector = LabelSelector.From(monitor.Spec["selector"]);

        if (monitor.Identity.Kind == MonitorKind.PodMonitor)
        {
            foreach (V1Pod pod in RunningPods(ns))
            {
                bool? selected = LabelSelector.Selects(selector, Labels(pod));
                if (selected is false) continue;

                foreach (JsonObject endpoint in EndpointsIn(monitor, "podMetricsEndpoints"))
                {
                    foreach (V1ContainerPort port in DeclaredPorts(pod).Where(p => Takes(endpoint, p)))
                    {
                        (selected is true ? certain : possible).Add(Address(pod, port.ContainerPort));
                    }
                }
            }

            return (certain, possible);
        }

        foreach (V1Service service in ns.Services)
        {
            bool? selected = LabelSelector.Selects(selector, Labels(service));
            if (selected is false) continue;

            foreach (JsonObject endpoint in EndpointsIn(monitor, "endpoints"))
            {
                foreach (V1ServicePort port in service.Spec?.Ports ?? [])
                {
                    if (Text(endpoint["port"]) is { } name && port.Name != name) continue;

                    foreach ((V1Pod pod, int number) in Behind(service, port, ns))
                    {
                        if (!TakesTargetPort(endpoint, pod, number)) continue;

                        (selected is true ? certain : possible).Add(Address(pod, number));
                    }
                }
            }
        }

        return (certain, possible);
    }

    /// <summary>A pod port as the target list spells it.</summary>
    public static string Address(V1Pod pod, int port) => $"{pod.Status.PodIP}:{port}";

    /// <summary>
    /// Every port the pod's long-running containers declare. An init container that restarts is one
    /// of them: it is how Kubernetes spells a sidecar, and it runs for as long as the pod does.
    /// </summary>
    public static IEnumerable<V1ContainerPort> DeclaredPorts(V1Pod pod)
        => RunningContainers(pod)
            .SelectMany(c => c.Ports ?? [])
            .Where(p => p.Protocol is null or "TCP");

    /// <summary>The containers that run for as long as the pod does.</summary>
    public static IEnumerable<V1Container> RunningContainers(V1Pod pod)
        => (pod.Spec?.Containers ?? []).Concat((pod.Spec?.InitContainers ?? []).Where(c => c.RestartPolicy == "Always"));

    public static IReadOnlyDictionary<string, string> Labels(V1Pod pod) => AsReadOnly(pod.Metadata?.Labels);

    public static IReadOnlyDictionary<string, string> AsReadOnly(IDictionary<string, string>? pairs)
        => pairs is null
            ? new Dictionary<string, string>()
            : pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Labels(V1Service service) => AsReadOnly(service.Metadata?.Labels);

    private static IEnumerable<V1Pod> RunningPods(LiveNamespace ns)
        => ns.Pods.Where(p => p.Status?.Phase == "Running" && !string.IsNullOrEmpty(p.Status.PodIP));

    /// <summary>
    /// The running pods a Service sends to. One with no selector sends to addresses somebody wrote
    /// by hand, which this does not look up.
    /// </summary>
    private static IEnumerable<V1Pod> PodsBehind(V1Service service, LiveNamespace ns)
    {
        IDictionary<string, string>? selector = service.Spec?.Selector;

        return selector is null || selector.Count == 0
            ? []
            : RunningPods(ns).Where(p => selector.All(s =>
                Labels(p).TryGetValue(s.Key, out string? have) && have == s.Value));
    }

    /// <summary>
    /// The pod ports behind one port of a Service. A named target is looked up on each pod, and a
    /// pod that does not declare it has none; a number needs no declaration, which is how the
    /// Services of Tetragon and the SeaweedFS master work.
    /// </summary>
    private static IEnumerable<(V1Pod Pod, int Port)> Behind(V1Service service, V1ServicePort port, LiveNamespace ns)
    {
        string? target = port.TargetPort?.Value;

        foreach (V1Pod pod in PodsBehind(service, ns))
        {
            if (string.IsNullOrEmpty(target))
            {
                yield return (pod, port.Port);
            }
            else if (int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                yield return (pod, number);
            }
            else if (DeclaredPorts(pod).FirstOrDefault(p => p.Name == target) is { } declared)
            {
                yield return (pod, declared.ContainerPort);
            }
        }
    }

    /// <summary>
    /// Whether a PodMonitor endpoint takes this container port: by name, by number, or, when it
    /// names neither, every port there is.
    /// </summary>
    private static bool Takes(JsonObject endpoint, V1ContainerPort port)
    {
        if (Text(endpoint["port"]) is { } name) return port.Name == name;
        if (Number(endpoint["portNumber"]) is { } portNumber) return port.ContainerPort == portNumber;
        if (Number(endpoint["targetPort"]) is { } number) return port.ContainerPort == number;
        if (Text(endpoint["targetPort"]) is { } target) return port.Name == target;
        return true;
    }

    /// <summary>The same for a ServiceMonitor endpoint's <c>targetPort</c>, which is about the pod.</summary>
    private static bool TakesTargetPort(JsonObject endpoint, V1Pod pod, int port)
    {
        if (Text(endpoint["port"]) is not null) return true;
        if (Number(endpoint["targetPort"]) is { } number) return port == number;
        if (Text(endpoint["targetPort"]) is { } name)
        {
            return DeclaredPorts(pod).Any(p => p.Name == name && p.ContainerPort == port);
        }

        return true;
    }

    /// <summary>
    /// The endpoints of a monitor that scrape what the port serves of its own. One that probes
    /// another address or asks with parameters gets other series, and is left out by the same rule
    /// as a target is (<see cref="ServesItsOwnMetrics"/>).
    /// </summary>
    private static IEnumerable<JsonObject> EndpointsIn(LiveMonitor monitor, string field)
        => (monitor.Spec[field] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => ServesItsOwnMetrics(Text(e["path"]) ?? "/metrics", e["params"] is JsonObject { Count: > 0 }));

    // ── What scrapes the same series ──────────────────────────────────────────

    /// <summary>
    /// The one rule for whether a scrape of a port gets the series the port serves of its own, for a
    /// target in Prometheus's list, a monitor's endpoint and the Scrape targets panel's duplicates
    /// alike: the same port is the same names whatever the path (RabbitMQ's per-object path serves them
    /// again), except a scrape that asks for something else. That is an exporter's probe, which reports
    /// on the address it is told to probe; a query (<c>?target=</c>, RabbitMQ's <c>?family=</c>), which
    /// picks other families; and the separate sets a Kubernetes component serves beside its
    /// <c>/metrics</c> (the kubelet's cAdvisor, probes and resource sets, and the SLI one of each).
    /// </summary>
    public static bool ServesItsOwnMetrics(string path, bool asksWithParameters)
        => !asksWithParameters
           && path.TrimEnd('/').Split('/')[^1] != "probe"
           && !SeparateSets.Contains(path.TrimEnd('/'));

    /// <summary>The paths beside <c>/metrics</c> on which a Kubernetes component serves other series.</summary>
    private static readonly HashSet<string> SeparateSets = new(StringComparer.Ordinal)
    {
        "/metrics/cadvisor", "/metrics/probes", "/metrics/resource", "/metrics/slis",
    };

    /// <summary>The annotation scrape job the catalog installs with the Prometheus stack.</summary>
    public const string AnnotationJob = "kubernetes-pods-annotated";

    private const string ScrapeAnnotation = "prometheus.io/scrape";
    private const string PortAnnotation = "prometheus.io/port";

    /// <summary>
    /// Whether the catalog's annotation job takes this port of the pod, by its own rules rather than by
    /// what the annotations read like: it keeps only the literal <c>true</c> (an anchored regex that
    /// minds case); a port annotation of digits is the one port it scrapes; without one it scrapes the
    /// ports the containers declare, init containers included, and drops a container that declares none.
    /// Running pods only, which every endpoint already is.
    /// </summary>
    public static bool AnnotationJobTakes(V1Pod pod, int port)
    {
        IDictionary<string, string>? annotations = pod.Metadata?.Annotations;

        if (annotations is null
            || !annotations.TryGetValue(ScrapeAnnotation, out string? scrape)
            || !string.Equals(scrape, "true", StringComparison.Ordinal))
        {
            return false;
        }

        if (annotations.TryGetValue(PortAnnotation, out string? named) && AllDigits(named))
        {
            return int.TryParse(named, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number == port;
        }

        return (pod.Spec?.Containers ?? []).Concat(pod.Spec?.InitContainers ?? [])
            .SelectMany(c => c.Ports ?? [])
            .Any(p => p.ContainerPort == port);
    }

    /// <summary>Whether a pod asks to be scraped by annotation at all, whatever any job makes of it.</summary>
    public static bool AsksForAnnotationScrape(V1Pod pod)
        => pod.Metadata?.Annotations?.ContainsKey(ScrapeAnnotation) is true;

    /// <summary>What Istio's agent does with one port of a pod.</summary>
    public enum IstioMerge
    {
        /// <summary>Nothing: no merging, or merging of another port.</summary>
        No,

        /// <summary>It scrapes the port and serves its series again on the agent's port.</summary>
        Yes,

        /// <summary>The proxy's record of the pod's own annotations cannot be read.</summary>
        CannotTell,
    }

    private const string IstioAnnotations = "ISTIO_PROMETHEUS_ANNOTATIONS";

    /// <summary>The agent's status port, where it serves what it merges unless the mesh moves it.</summary>
    public const int IstioAgentPort = 15020;

    /// <summary>The one path on the agent's port that serves the merged series (pilot-agent's handleStats).</summary>
    public const string IstioMergedPath = "/stats/prometheus";

    /// <summary>
    /// The ports pilot-agent refuses to scrape: its own and Envoy's (pilot-agent/status/server.go,
    /// istioReservedPorts). An agent handed one does not start, and so merges nothing.
    /// </summary>
    private static readonly HashSet<int> IstioReservedPorts = [15000, 15001, 15004, 15006, 15008, 15020, 15021, 15053, 15090];

    /// <summary>
    /// Whether Istio's agent scrapes this port of the pod and serves its series again with its own, on
    /// <paramref name="agentPort"/> at <see cref="IstioMergedPath"/>.
    ///
    /// <para>As upstream does it (istio/istio master 9e3753c4, and release-1.29 for everything but
    /// scrape targets). The injector (pkg/kube/inject/webhook.go, <c>applyPrometheusMerge</c>) merges a
    /// pod whose <c>prometheus.io/scrape</c> is absent or anything <c>strconv.ParseBool</c> does not
    /// read as false; it writes the pod's prometheus.io annotations, as they were, into the proxy's
    /// <c>ISTIO_PROMETHEUS_ANNOTATIONS</c> (where there was one to write), and points the pod's at the
    /// agent: the mesh's status port, <c>/stats/prometheus</c>. The agent
    /// (pilot/cmd/pilot-agent/status/server.go, <c>NewServer</c>) reads that record on a sidecar only,
    /// not on a gateway's router, and merges unless its scrape is <c>"false"</c>, in so many words. With
    /// no scrape targets it scrapes one port, 80 where none was given, at the path given or
    /// <c>/metrics</c>; with one target, the record's port or else the target's; with more, every target
    /// (<c>handleStats</c>, <c>scrapeMultipleApps</c>). A port that is not a number from 1 to 65535, or
    /// is one of Istio's own, makes the agent refuse to start (master) or its scrape fail (1.29): either
    /// way nothing is merged.</para>
    ///
    /// <para>Merged is not doubled by itself: only a job that also scrapes the agent's port gets the
    /// series twice, which is for the caller to look for.</para>
    /// </summary>
    public static IstioMerge MergedByIstio(V1Pod pod, int port, out int agentPort)
    {
        agentPort = 0;

        V1Container? proxy = (pod.Spec?.Containers ?? []).Concat(pod.Spec?.InitContainers ?? [])
            .FirstOrDefault(c => c.Env?.Any(e => e.Name == IstioAnnotations) is true);
        if (proxy is null || RunsAsRouter(proxy)) return IstioMerge.No;

        string? original = proxy.Env.First(e => e.Name == IstioAnnotations).Value;

        // The injector writes the mesh's status port here; it is where the annotation job goes.
        agentPort = pod.Metadata?.Annotations?.TryGetValue(PortAnnotation, out string? rewritten) is true
                    && Port(rewritten) is { } written
            ? written
            : IstioAgentPort;

        if (agentPort == port) return IstioMerge.No;

        if (IstioRecord.Read(original) is not { } asked) return IstioMerge.CannotTell;
        if (asked.Scrape == "false") return IstioMerge.No;

        List<(string Port, string Path)> scraped = asked.Targets.Count switch
        {
            0 => [(Or(asked.Port, "80"), Or(asked.Path, "/metrics"))],
            1 => [(Or(asked.Port, asked.Targets[0].Port), Or(asked.Path, asked.Targets[0].Path))],
            _ => [.. asked.Targets.Select(t => (t.Port, Or(t.Path, "/metrics")))],
        };

        int agent = agentPort;
        List<int?> ports = [.. scraped.Select(s => Port(s.Port))];
        if (ports.Any(p => p is null || IstioReservedPorts.Contains(p.Value) || p == agent)) return IstioMerge.No;

        return scraped.Zip(ports).Any(s => s.Second == port && ServesItsOwnMetrics(
                s.First.Path.Split('?')[0], s.First.Path.Contains('?', StringComparison.Ordinal)))
            ? IstioMerge.Yes
            : IstioMerge.No;

        static string Or(string given, string otherwise) => given.Length > 0 ? given : otherwise;
    }

    /// <summary>
    /// A proxy started as <c>pilot-agent proxy router</c>: a gateway's. The agent reads the scrape
    /// record on a sidecar only (<c>config.NodeType == model.SidecarProxy</c>), and the node type is the
    /// argument after <c>proxy</c> (pilot/cmd/pilot-agent/app/cmd.go, <c>initProxy</c>), sidecar when none.
    /// </summary>
    private static bool RunsAsRouter(V1Container proxy)
        => (proxy.Args ?? []).SkipWhile(a => a != "proxy").Skip(1).FirstOrDefault() == "router";

    /// <summary>A port number as pilot-agent reads one, digits from 1 to 65535; null where it is not one.</summary>
    private static int? Port(string? text)
        => AllDigits(text)
           && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
           && port is >= 1 and <= 65535
            ? port
            : null;

    /// <summary>
    /// <c>ISTIO_PROMETHEUS_ANNOTATIONS</c> as pilot-agent unmarshals it into its
    /// <c>PrometheusScrapeConfiguration</c>: strings, or absent (null is absent to Go), and a list of
    /// targets. Anything else is a record the agent cannot read and refuses to start over; which of that
    /// and a record EntKube misreads it is cannot be told, so it is null.
    /// </summary>
    private sealed record IstioRecord(string Scrape, string Path, string Port, IReadOnlyList<(string Port, string Path)> Targets)
    {
        public static IstioRecord? Read(string? json)
        {
            JsonObject? record;
            try
            {
                record = JsonNode.Parse(json ?? "") as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }

            if (record is null
                || !Field(record, "scrape", out string scrape)
                || !Field(record, "path", out string path)
                || !Field(record, "port", out string port))
            {
                return null;
            }

            List<(string, string)> targets = [];
            switch (record["targets"])
            {
                case null:
                    break;
                case JsonArray list:
                    foreach (JsonNode? item in list)
                    {
                        if (item is not JsonObject target
                            || !Field(target, "port", out string targetPort)
                            || !Field(target, "path", out string targetPath))
                        {
                            return null;
                        }

                        targets.Add((targetPort, targetPath));
                    }

                    break;
                default:
                    return null;
            }

            return new IstioRecord(scrape, path, port, targets);
        }

        private static bool Field(JsonObject record, string name, out string value)
        {
            value = "";
            JsonNode? node = record[name];
            if (node is null) return true;
            if (Text(node) is not { } text) return false;
            value = text;
            return true;
        }
    }

    private static bool AllDigits(string? text) => text is { Length: > 0 } && text.All(char.IsAsciiDigit);

    private static IReadOnlyList<ScrapedEndpoint> Distinct(IEnumerable<ScrapedEndpoint> endpoints)
        => [.. endpoints.DistinctBy(e => (e.PodIp, e.Port, e.Path))];

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int? Number(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? (int)value.GetValue<double>() : null;
}
