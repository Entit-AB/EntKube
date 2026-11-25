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

/// <summary>What a monitor would scrape in a namespace, as it runs right now.</summary>
/// <param name="Pods">The running pods it selects, whether or not they expose the port.</param>
/// <param name="Endpoints">The ports of theirs it would scrape.</param>
public sealed record ResolvedTargets(IReadOnlyList<V1Pod> Pods, IReadOnlyList<ScrapedEndpoint> Endpoints);

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
                select new ScrapedEndpoint(pod, port.ContainerPort, endpoint.Path)));
        }

        List<V1Service> services = [.. ns.Services.Where(s => monitor.Selector.Matches(Labels(s)) is true)];

        return new ResolvedTargets(
            [.. services.SelectMany(s => PodsBehind(s, ns)).DistinctBy(p => p.Metadata.Name)],
            Distinct(
                from endpoint in monitor.Endpoints
                from service in services
                from port in service.Spec?.Ports ?? []
                where port.Name == endpoint.Port
                from target in Behind(service, port, ns)
                select new ScrapedEndpoint(target.Pod, target.Port, endpoint.Path)));
    }

    /// <summary>
    /// The pod ports somebody else's monitor scrapes in this namespace, as "ip:port". In the first
    /// set the ones it certainly scrapes; in the second, those its selector could not be read well
    /// enough to rule out.
    /// </summary>
    public static (IReadOnlySet<string> Certain, IReadOnlySet<string> Possible) OfForeign(
        LiveMonitor monitor, LiveNamespace ns)
    {
        HashSet<string> certain = new(StringComparer.Ordinal);
        HashSet<string> possible = new(StringComparer.Ordinal);

        if (!monitor.LooksIn(ns.Name)) return (certain, possible);

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

    private static IEnumerable<JsonObject> EndpointsIn(LiveMonitor monitor, string field)
        => (monitor.Spec[field] as JsonArray ?? []).OfType<JsonObject>();

    private static IReadOnlyList<ScrapedEndpoint> Distinct(IEnumerable<ScrapedEndpoint> endpoints)
        => [.. endpoints.DistinctBy(e => (e.PodIp, e.Port, e.Path))];

    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static int? Number(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? (int)value.GetValue<double>() : null;
}
