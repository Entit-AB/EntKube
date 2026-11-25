using System.Text.Json.Nodes;

namespace EntKube.Web.Services;

/// <summary>
/// Why a workload's metrics are missing, told from what is actually in the cluster: whether a
/// monitor selects the workload, and whether Prometheus reads that monitor.
/// </summary>
public class ScrapeDiagnosis
{
    /// <summary>A monitor selects the workload's metrics endpoint, whether or not Prometheus reads it.</summary>
    public bool MonitorExists { get; init; }

    /// <summary>Labels on that monitor — what Prometheus's selector is matched against.</summary>
    public Dictionary<string, string> MonitorLabels { get; init; } = [];

    /// <summary>matchLabels of the Prometheus resource's monitor selector.</summary>
    public Dictionary<string, string> PrometheusSelector { get; init; } = [];

    /// <summary>
    /// Prometheus reads the monitor: its labels <em>and</em> its namespace are accepted. False also
    /// when that could not be told, in which case <see cref="Findings"/> says so.
    /// </summary>
    public bool SelectorAcceptsMonitor { get; init; }

    /// <summary>
    /// Prometheus reads more than one monitor for the same endpoint, so every sum over the
    /// workload's series counts it once per monitor.
    /// </summary>
    public bool SelectedTwice { get; init; }

    /// <summary>Exactly one monitor selects the endpoint and Prometheus reads it.</summary>
    public bool WiringIsRight => SelectorAcceptsMonitor && !SelectedTwice;

    /// <summary>What was found, in plain words, worst first.</summary>
    public List<string> Findings { get; init; } = [];

    /// <summary>The concrete next step, when there is one.</summary>
    public string? Remedy { get; init; }
}

/// <summary>
/// The two monitor types of the Prometheus operator, with the field names that differ between
/// them. They have separate selectors on the Prometheus resource, so a cluster can read every
/// ServiceMonitor and no PodMonitor at all.
/// </summary>
public sealed record MonitorType(
    string Kind, string Plural, string Selector, string NamespaceSelector, string Endpoints)
{
    public static readonly MonitorType Pod = new(
        "PodMonitor", "podmonitors", "podMonitorSelector", "podMonitorNamespaceSelector", "podMetricsEndpoints");

    public static readonly MonitorType Service = new(
        "ServiceMonitor", "servicemonitors", "serviceMonitorSelector", "serviceMonitorNamespaceSelector", "endpoints");
}

/// <summary>
/// A workload whose scraping is being diagnosed: what a monitor has to select to scrape it, and
/// which monitor is EntKube's own.
///
/// <para>Monitors are recognised by <b>what they select</b>, not by what they are called. They used
/// to be recognised by a substring of the name, so the monitor of a broker called <c>my-app</c>
/// answered for the broker <c>app</c> — and a monitor with a name nobody expected was "not there".</para>
/// </summary>
public sealed record MonitoredWorkload
{
    /// <summary>How the workload is named in a finding, e.g. <c>RabbitMQ 'orders'</c>.</summary>
    public required string Title { get; init; }

    public required string Namespace { get; init; }

    public required MonitorType Type { get; init; }

    /// <summary>
    /// The monitor EntKube makes for this workload, or null where EntKube makes none (a broker it
    /// did not install).
    /// </summary>
    public string? EntKubeMonitor { get; init; }

    /// <summary>
    /// The labels of the pods (PodMonitor) or Services (ServiceMonitor) a monitor has to select.
    /// Empty when nobody but the workload's own chart knows them; then
    /// <see cref="StatefulSet"/> is how its Services are found.
    /// </summary>
    public Dictionary<string, string> Labels { get; init; } = [];

    /// <summary>The StatefulSet whose Services are the workload's, when <see cref="Labels"/> is unknown.</summary>
    public string? StatefulSet { get; init; }

    /// <summary>
    /// The metrics port under every name it goes by. Empty when it is not known, and then any
    /// port a monitor names counts.
    /// </summary>
    public List<(string Name, int Number)> Ports { get; init; } = [];

    /// <summary>
    /// One CNPG database. <c>cnpg.io/podRole</c> is part of it because PgBouncer pooler pods carry
    /// <c>cnpg.io/cluster</c> too, and a port that is also called <c>metrics</c>.
    /// </summary>
    public static MonitoredWorkload Cnpg(string clusterName, string ns) => new()
    {
        Title = $"database '{clusterName}'",
        Namespace = ns,
        Type = MonitorType.Pod,
        EntKubeMonitor = $"entkube-cnpg-{clusterName}",
        Labels = new() { ["cnpg.io/cluster"] = clusterName, ["cnpg.io/podRole"] = "instance" },
        Ports = [("metrics", 9187)],
    };

    /// <summary>One broker run by the RabbitMQ cluster operator, which labels the broker's Services.</summary>
    public static MonitoredWorkload RabbitMQ(string brokerName, string ns) => new()
    {
        Title = $"RabbitMQ '{brokerName}'",
        Namespace = ns,
        Type = MonitorType.Service,
        EntKubeMonitor = $"{brokerName}-metrics",
        Labels = new()
        {
            ["app.kubernetes.io/name"] = brokerName,
            ["app.kubernetes.io/component"] = "rabbitmq",
        },
        Ports = [("prometheus", 15692), ("prometheus-tls", 15691)],
    };

    /// <summary>
    /// One broker EntKube adopted from a plain StatefulSet. Its Service labels and its metrics port
    /// come from whatever chart installed it, so its Services are found from its pods instead.
    /// </summary>
    public static MonitoredWorkload ExternalRabbitMQ(string statefulSet, string ns) => new()
    {
        Title = $"RabbitMQ '{statefulSet}'",
        Namespace = ns,
        Type = MonitorType.Service,
        StatefulSet = statefulSet,
    };

    /// <summary>One Keycloak release (the keycloakx chart), by the Service that has the management port.</summary>
    public static MonitoredWorkload Keycloak(string release, string ns) => new()
    {
        Title = $"Keycloak '{release}'",
        Namespace = ns,
        Type = MonitorType.Service,
        EntKubeMonitor = $"entkube-keycloak-{release}",
        Labels = new()
        {
            ["app.kubernetes.io/name"] = "keycloakx",
            ["app.kubernetes.io/instance"] = release,
            ["app.kubernetes.io/component"] = "http",
        },
    };
}

/// <summary>
/// Everything the diagnosis read from the cluster. Every part of it was actually read: a read that
/// failed throws instead of leaving a part empty, because an empty list here is the finding "none
/// exists".
/// </summary>
public sealed record ScrapeEvidence
{
    /// <summary>Every monitor of the workload's type, in all namespaces. Null when the cluster has no such type (the CRD is not installed).</summary>
    public required IReadOnlyList<JsonNode>? Monitors { get; init; }

    /// <summary>Every Prometheus resource. Null when the cluster has no such type.</summary>
    public required IReadOnlyList<JsonNode>? Prometheuses { get; init; }

    /// <summary>Labels of every namespace, by name. Read only where a Prometheus picks namespaces by their labels.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, string>> NamespaceLabels { get; init; } =
        new Dictionary<string, Dictionary<string, string>>();

    /// <summary>Labels of the workload's live pods or Services. Empty when none are there.</summary>
    public IReadOnlyList<Dictionary<string, string>> Live { get; init; } = [];

    /// <summary>The namespace of the Prometheus EntKube queries, to tell its resource from another one's.</summary>
    public required string PrometheusNamespace { get; init; }

    /// <summary>The service of the Prometheus EntKube queries.</summary>
    public required string PrometheusService { get; init; }
}

/// <summary>
/// A Kubernetes label selector as it appears on a Prometheus resource or a monitor.
///
/// <para>The answer has three values because the diagnosis must not guess: an operator this code
/// does not know makes it "cannot tell" (null), never yes and never no.</para>
/// </summary>
public static class MonitorSelector
{
    /// <summary>
    /// Whether <paramref name="selector"/> selects something carrying <paramref name="labels"/>.
    ///
    /// <para>The Prometheus CRD: "An empty label selector matches all objects. A null label
    /// selector matches no objects." An absent selector used to be read as "selects everything",
    /// which reported scraping as running on a Prometheus that reads no monitors at all.</para>
    /// </summary>
    public static bool? Selects(JsonNode? selector, IReadOnlyDictionary<string, string> labels)
    {
        if (selector is null) return false;
        if (selector is not JsonObject parts) return null;

        bool cannotTell = false;

        if (parts["matchLabels"] is JsonObject wanted)
        {
            foreach (KeyValuePair<string, JsonNode?> w in wanted)
            {
                if (!labels.TryGetValue(w.Key, out string? value) || value != Text(w.Value)) return false;
            }
        }
        else if (parts["matchLabels"] is not null)
        {
            cannotTell = true;
        }

        if (parts["matchExpressions"] is JsonArray expressions)
        {
            foreach (JsonNode? expression in expressions)
            {
                switch (Satisfies(expression, labels))
                {
                    case false: return false;
                    case null: cannotTell = true; break;
                }
            }
        }
        else if (parts["matchExpressions"] is not null)
        {
            cannotTell = true;
        }

        return cannotTell ? null : true;
    }

    private static bool? Satisfies(JsonNode? expression, IReadOnlyDictionary<string, string> labels)
    {
        if (expression is not JsonObject e || Text(e["key"]) is not { Length: > 0 } key) return null;

        bool present = labels.TryGetValue(key, out string? value);
        List<string?>? values = (e["values"] as JsonArray)?.Select(Text).ToList();

        return Text(e["operator"]) switch
        {
            "Exists" => present,
            "DoesNotExist" => !present,
            "In" when values is not null => present && values.Contains(value),
            // Kubernetes: NotIn also selects what does not carry the key at all.
            "NotIn" when values is not null => !present || !values.Contains(value),
            _ => null,
        };
    }

    /// <summary>The selector in one line, as a reader would write it.</summary>
    public static string Describe(JsonNode? selector)
    {
        if (selector is not JsonObject parts) return "nothing";

        List<string> said = [];
        if (parts["matchLabels"] is JsonObject wanted)
            said.AddRange(wanted.Select(w => $"{w.Key}={Text(w.Value)}"));
        if (parts["matchExpressions"] is JsonArray expressions)
        {
            said.AddRange(expressions.Select(e =>
            {
                string values = e?["values"] is JsonArray v && v.Count > 0 ? $" ({string.Join(", ", v.Select(Text))})" : "";
                return $"{Text(e?["key"])} {Text(e?["operator"])}{values}";
            }));
        }

        return said.Count == 0 ? "everything" : string.Join(", ", said);
    }

    /// <summary>A scalar as text. Ports are a name in one monitor and a number in the next.</summary>
    internal static string? Text(JsonNode? node) =>
        node is JsonValue ? node.ToString() : null;
}

/// <summary>
/// Compares the two halves that have to line up — the monitor that selects a workload, and the
/// selector Prometheus decides by which monitors it reads — and says which one is missing.
/// Pure: it decides over a <see cref="ScrapeEvidence"/> that somebody else read.
/// </summary>
public static class ScrapeDiagnostician
{
    /// <summary>The one remedy for a monitor that is missing or that Prometheus does not select.</summary>
    public const string ApplyMonitors =
        "Press \"Apply monitors now\". EntKube creates its own monitor and labels it the way this Prometheus asks for.";

    public static ScrapeDiagnosis Diagnose(MonitoredWorkload workload, ScrapeEvidence seen)
    {
        string kind = workload.Type.Kind;

        if (seen.Monitors is null || seen.Prometheuses is null)
        {
            return Nothing(
                $"This cluster has no {(seen.Monitors is null ? kind : "Prometheus")} resource type: the Prometheus operator's "
                + "custom resource definitions are not installed, so no monitor can exist.",
                "Install or repair the Prometheus component.");
        }

        (JsonNode? prometheus, string? whyNoPrometheus) = ThePrometheus(seen);
        if (prometheus is null) return Nothing(whyNoPrometheus!, null);

        JsonNode? selector = prometheus["spec"]?[workload.Type.Selector];
        (List<JsonNode> selecting, List<string> doubts) = MonitorsOf(workload, seen);

        List<JsonNode> read = [];
        List<string> unread = [];
        foreach (JsonNode monitor in selecting)
        {
            bool? byLabels = MonitorSelector.Selects(selector, LabelsOf(monitor["metadata"]?["labels"]));
            bool? byNamespace = ReadsNamespace(prometheus, workload.Type, NamespaceOf(monitor), seen);

            if (byLabels == true && byNamespace == true) read.Add(monitor);
            else unread.Add(WhyNotRead(monitor, kind, selector, byLabels, byNamespace));
        }

        if (read.Count == 0)
        {
            List<string> blind = WhyPrometheusCannotLookHere(prometheus, workload, seen);
            List<string> findings = [.. blind, .. unread];
            if (selecting.Count == 0)
            {
                findings.Add(workload.Labels.Count == 0 && seen.Live.Count == 0
                    ? $"Which {kind} is its own cannot be told without its Services."
                    : $"No {kind} on this cluster selects the metrics port of {workload.Title}, so Prometheus was never told to scrape it.");
            }
            findings.AddRange(doubts);

            return new ScrapeDiagnosis
            {
                MonitorExists = selecting.Count > 0,
                MonitorLabels = LabelsOf(OursFirst(selecting, workload)?["metadata"]?["labels"]),
                PrometheusSelector = LabelsOf(selector?["matchLabels"]),
                Findings = findings,
                Remedy = Remedy(workload, prometheus, applyCanHelp: blind.Count == 0),
            };
        }

        List<JsonNode> doubling = [.. read.Where(m => read.Any(other =>
            other != m && Endpoints(m, workload).Overlaps(Endpoints(other, workload))))];

        // A doubled workload is worse than a missing one: its numbers are shown, and wrong.
        string headline = doubling.Count > 0
            ? $"{doubling.Count} {kind}s scrape the same endpoint of {workload.Title}: {string.Join(", ", doubling.Select(NameOf))}. "
              + "Every sum over its series counts it once for each of them."
            : $"{kind} {NameOf(OursFirst(read, workload)!)} selects {workload.Title} and Prometheus reads it.";

        return new ScrapeDiagnosis
        {
            MonitorExists = true,
            MonitorLabels = LabelsOf(OursFirst(read, workload)?["metadata"]?["labels"]),
            PrometheusSelector = LabelsOf(selector?["matchLabels"]),
            SelectorAcceptsMonitor = true,
            SelectedTwice = doubling.Count > 0,
            Findings = [headline, .. unread, .. doubts],
            Remedy = doubling.Count == 0
                ? "Scraping should be running. If metrics are still missing, look at Prometheus → Targets: "
                  + "a target listed as down points at the workload's metrics port, not at the wiring."
                : doubling.Any(m => IsOurs(m, workload))
                    ? "Press \"Apply monitors now\". EntKube removes its own monitor where another one already scrapes the endpoint."
                    : "Delete all but one of them.",
        };

        static ScrapeDiagnosis Nothing(string finding, string? remedy) =>
            new() { Findings = [finding], Remedy = remedy };
    }

    /// <summary>
    /// The monitors that select the workload's metrics port, and everything about the others worth
    /// saying: EntKube's own monitor pointing elsewhere, and selectors that could not be evaluated.
    /// </summary>
    private static (List<JsonNode> Selecting, List<string> Doubts) MonitorsOf(MonitoredWorkload workload, ScrapeEvidence seen)
    {
        string kind = workload.Type.Kind;
        List<JsonNode> selecting = [];
        List<string> doubts = [];

        if (seen.Live.Count == 0)
        {
            doubts.Add(workload.Type == MonitorType.Pod
                ? $"No pods of {workload.Title} were found in namespace {workload.Namespace}; this is judged by the labels its pods get."
                : $"No Service of {workload.Title} was found in namespace {workload.Namespace}"
                  + (workload.Labels.Count > 0 ? "; this is judged by the labels its Service is expected to have." : "."));
        }

        foreach (JsonNode monitor in seen.Monitors!)
        {
            bool? selects = SelectsTheWorkload(monitor, workload, seen);
            string asksFor = MonitorSelector.Describe(monitor["spec"]?["selector"]);

            if (selects is null)
                doubts.Add($"Cannot tell whether {kind} {NameOf(monitor)} selects {workload.Title}: its selector ({asksFor}) was not understood.");
            else if (selects == true && Endpoints(monitor, workload).Count > 0)
                selecting.Add(monitor);
            else if (IsOurs(monitor, workload))
                doubts.Add(selects == true
                    ? $"{kind} {NameOf(monitor)} selects {workload.Title} but names none of its metrics ports."
                    : $"{kind} {NameOf(monitor)} exists but does not select {workload.Title}: it asks for {asksFor}.");
        }

        return (selecting, doubts);
    }

    /// <summary>
    /// The two things on the Prometheus resource that no monitor can make up for: it reads no
    /// monitors at all, or it does not look in the workload's namespace.
    /// </summary>
    private static List<string> WhyPrometheusCannotLookHere(JsonNode prometheus, MonitoredWorkload workload, ScrapeEvidence seen)
    {
        MonitorType type = workload.Type;
        List<string> blind = [];

        if (prometheus["spec"]?[type.Selector] is null)
        {
            blind.Add(
                $"Prometheus {NameOf(prometheus)} has no {type.Selector}, so it reads no {type.Kind} at all: "
                + "an absent selector selects nothing, only an empty one selects everything.");
        }

        if (ReadsNamespace(prometheus, type, workload.Namespace, seen) == false)
        {
            JsonNode? byNamespace = prometheus["spec"]?[type.NamespaceSelector];
            blind.Add(byNamespace is null
                ? $"Prometheus {NameOf(prometheus)} has no {type.NamespaceSelector}, so it only looks in its own "
                  + $"namespace and does not see a {type.Kind} in {workload.Namespace}."
                : $"Prometheus {NameOf(prometheus)} does not look in namespace {workload.Namespace}: its "
                  + $"{type.NamespaceSelector} asks for {MonitorSelector.Describe(byNamespace)}.");
        }

        return blind;
    }

    /// <summary>
    /// Pressing the button is the remedy whenever it can be. Where it cannot — Prometheus itself
    /// looks past every monitor, or the workload is not EntKube's to monitor — that is said,
    /// rather than sending the operator to press a button that changes nothing.
    /// </summary>
    private static string Remedy(MonitoredWorkload workload, JsonNode prometheus, bool applyCanHelp)
    {
        if (workload.EntKubeMonitor is null)
        {
            return "EntKube did not install this workload and creates no monitor for it: its metrics port and Service "
                + $"labels come from the chart that did. Create a {workload.Type.Kind} for it by hand, or enable metrics in that chart.";
        }

        return applyCanHelp
            ? ApplyMonitors
            : $"\"Apply monitors now\" cannot help with this: the Prometheus resource {NameOf(prometheus)} has to "
              + "look here before any monitor counts.";
    }

    private static string WhyNotRead(JsonNode monitor, string kind, JsonNode? selector, bool? byLabels, bool? byNamespace)
    {
        if (byNamespace == false)
            return $"{kind} {NameOf(monitor)} selects it, but Prometheus does not look in that monitor's namespace.";

        if (byLabels == false)
        {
            Dictionary<string, string> labels = LabelsOf(monitor["metadata"]?["labels"]);
            string have = labels.Count == 0 ? "no labels" : string.Join(", ", labels.Select(l => $"{l.Key}={l.Value}"));
            return $"{kind} {NameOf(monitor)} exists but Prometheus does not select it: it reads "
                + $"{kind}s by {MonitorSelector.Describe(selector)}, and this one has {have}.";
        }

        return $"Cannot tell whether Prometheus reads {kind} {NameOf(monitor)}: "
            + (byLabels is null
                ? $"its selector ({MonitorSelector.Describe(selector)}) was not understood."
                : "the selector it picks namespaces by was not understood.");
    }

    /// <summary>
    /// The Prometheus resource behind the service EntKube queries. kube-prometheus-stack gives the
    /// two the same name; failing that, the only one in the component's namespace, or the only one
    /// there is. Taking whichever came first answered for somebody else's Prometheus.
    /// </summary>
    private static (JsonNode? Prometheus, string? Why) ThePrometheus(ScrapeEvidence seen)
    {
        List<JsonNode> all = [.. seen.Prometheuses!];
        List<JsonNode> near = [.. all.Where(p => NamespaceOf(p) == seen.PrometheusNamespace)];

        JsonNode? chosen = near.FirstOrDefault(p => PlainName(p) == seen.PrometheusService)
            ?? (near.Count == 1 ? near[0] : null)
            ?? (all.Count == 1 ? all[0] : null);
        if (chosen is not null) return (chosen, null);

        return (null, all.Count == 0
            ? "There is no Prometheus resource on this cluster, so nothing reads monitors."
            : $"Cannot tell which Prometheus this is about: {string.Join(", ", all.Select(NameOf))} "
              + $"are on this cluster and none is called {seen.PrometheusNamespace}/{seen.PrometheusService}.");
    }

    private static bool? ReadsNamespace(JsonNode prometheus, MonitorType type, string ns, ScrapeEvidence seen)
    {
        JsonNode? selector = prometheus["spec"]?[type.NamespaceSelector];

        // Unlike the monitor selector, an absent one here is not "none": the CRD confines
        // Prometheus to its own namespace.
        if (selector is null) return ns == NamespaceOf(prometheus);
        if (seen.NamespaceLabels.TryGetValue(ns, out Dictionary<string, string>? labels))
            return MonitorSelector.Selects(selector, labels);

        return selector is JsonObject { Count: 0 } ? true : null;
    }

    /// <summary>
    /// Whether the monitor selects the workload: its namespaceSelector reaches the namespace, and
    /// its selector matches any of the live pods or Services — or, when none are there, the labels
    /// they would have.
    /// </summary>
    private static bool? SelectsTheWorkload(JsonNode monitor, MonitoredWorkload workload, ScrapeEvidence seen)
    {
        if (!Reaches(monitor, workload.Namespace)) return false;

        IReadOnlyList<Dictionary<string, string>> judgedBy =
            seen.Live.Count > 0 ? seen.Live : workload.Labels.Count > 0 ? [workload.Labels] : [];

        bool cannotTell = false;
        foreach (Dictionary<string, string> labels in judgedBy)
        {
            switch (MonitorSelector.Selects(monitor["spec"]?["selector"], labels))
            {
                case true: return true;
                case null: cannotTell = true; break;
            }
        }
        return cannotTell ? null : false;
    }

    private static bool Reaches(JsonNode monitor, string ns)
    {
        JsonNode? selector = monitor["spec"]?["namespaceSelector"];
        if (MonitorSelector.Text(selector?["any"]) == "true") return true;

        return selector?["matchNames"] is JsonArray { Count: > 0 } names
            ? names.Any(n => MonitorSelector.Text(n) == ns)
            : NamespaceOf(monitor) == ns;
    }

    /// <summary>
    /// The endpoints a monitor scrapes on the workload's metrics port, as port and path. The path
    /// counts: RabbitMQ's published monitor reads <c>/metrics/detailed</c> beside
    /// <c>/metrics</c>, and a second monitor on the other path doubles nothing.
    /// </summary>
    private static HashSet<string> Endpoints(JsonNode monitor, MonitoredWorkload workload)
    {
        HashSet<string> endpoints = new(StringComparer.Ordinal);
        if (monitor["spec"]?[workload.Type.Endpoints] is not JsonArray listed) return endpoints;

        foreach (JsonNode? endpoint in listed)
        {
            string? port = MonitorSelector.Text(endpoint?["port"])
                ?? MonitorSelector.Text(endpoint?["portNumber"])
                ?? MonitorSelector.Text(endpoint?["targetPort"]);
            if (port is null) continue;

            if (workload.Ports.Count > 0)
            {
                List<int> known = [.. workload.Ports
                    .Where(p => p.Name == port || p.Number.ToString() == port)
                    .Select(p => p.Number)];
                if (known.Count == 0) continue;
                port = known[0].ToString();
            }

            endpoints.Add($"{port}{MonitorSelector.Text(endpoint?["path"]) ?? "/metrics"}");
        }
        return endpoints;
    }

    private static bool IsOurs(JsonNode monitor, MonitoredWorkload workload) =>
        NamespaceOf(monitor) == workload.Namespace && PlainName(monitor) == workload.EntKubeMonitor;

    private static JsonNode? OursFirst(List<JsonNode> monitors, MonitoredWorkload workload) =>
        monitors.FirstOrDefault(m => IsOurs(m, workload)) ?? monitors.FirstOrDefault();

    private static string PlainName(JsonNode resource) => MonitorSelector.Text(resource["metadata"]?["name"]) ?? "";

    private static string NamespaceOf(JsonNode resource) => MonitorSelector.Text(resource["metadata"]?["namespace"]) ?? "";

    private static string NameOf(JsonNode resource) => $"{NamespaceOf(resource)}/{PlainName(resource)}";

    private static Dictionary<string, string> LabelsOf(JsonNode? node) =>
        node is JsonObject labels
            ? labels.ToDictionary(l => l.Key, l => MonitorSelector.Text(l.Value) ?? "", StringComparer.Ordinal)
            : [];
}
