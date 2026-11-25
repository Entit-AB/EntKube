using System.Text.Json.Nodes;
using k8s.Models;

namespace EntKube.Web.Services.ScrapeMonitors;

/// <summary>
/// Decides, from the targets and what is live, which monitors should exist.
///
/// <para>Two rules, and everything here is one of them. <b>A pod endpoint is scraped by exactly one
/// job</b>, because two jobs make every <c>sum()</c> twice what it is. <b>Unknown means do
/// nothing</b>: a question that cannot be answered neither creates a monitor nor removes one.</para>
///
/// <para>Pure. It is handed a complete <see cref="LiveScrapeState"/> and reads nothing else, so every
/// decision it makes can be tested with the labels a chart really renders.</para>
/// </summary>
public static class ScrapeMonitorPlanner
{
    private const string ScrapeAnnotation = "prometheus.io/scrape";
    private const string PortAnnotation = "prometheus.io/port";

    public static ScrapeMonitorPlan Plan(IReadOnlyList<ScrapeTargetSpec> targets, LiveScrapeState live)
        => new Pass(targets, live).Run();

    /// <summary>
    /// Which of the cluster's Prometheus objects is the one EntKube installed: the one its release
    /// carries, or the only one in its namespace, or the only one there is. An adopted release may be
    /// recorded under the catalog's name rather than its own, which is what the last two are for.
    /// Null when that leaves a choice, and the sentence says so.
    /// </summary>
    public static LivePrometheus? ChoosePrometheus(LiveScrapeState live)
    {
        IReadOnlyList<LivePrometheus> all = live.Monitoring.Prometheuses;
        List<LivePrometheus> there = [.. all.Where(p => p.Namespace == live.Release.Namespace)];
        List<LivePrometheus> ofRelease =
            [.. there.Where(p => p.Labels.GetValueOrDefault("release") == live.Release.Name)];

        return ofRelease is [LivePrometheus released] ? released
            : there is [LivePrometheus only] ? only
            : there.Count == 0 && all is [LivePrometheus single] ? single
            : null;
    }

    /// <summary>One run over one cluster's targets.</summary>
    private sealed class Pass
    {
        private readonly IReadOnlyList<ScrapeTargetSpec> targets;
        private readonly LiveScrapeState live;
        private readonly LivePrometheus? prometheus;
        private readonly PrometheusAdmission admission;
        private readonly Dictionary<MonitorIdentity, LiveMonitor> liveMonitors;
        private readonly HashSet<MonitorIdentity> ours;

        private readonly List<MonitorPlanRow> rows = [];
        private readonly List<PlannedMonitor> apply = [];
        private readonly List<MonitorIdentity> prune = [];
        private readonly HashSet<MonitorIdentity> claimed = [];

        public Pass(IReadOnlyList<ScrapeTargetSpec> targets, LiveScrapeState live)
        {
            this.targets = targets;
            this.live = live;

            prometheus = ChoosePrometheus(live);
            admission = AdmissionOf(prometheus, live.Workloads);
            liveMonitors = live.Monitoring.Monitors.ToDictionary(m => m.Identity);
            ours =
            [
                .. live.Monitoring.Monitors.Where(IsOurs).Select(m => m.Identity),
                .. targets.Select(t => t.Monitor).OfType<MonitorSpec>().Where(TakesOver).Select(m => m.Identity),
            ];
        }

        public ScrapeMonitorPlan Run()
        {
            foreach (ScrapeTargetSpec target in targets)
            {
                rows.Add(target.Monitor is null
                    ? Row(target, MonitorState.Info, MonitorChange.None, target.Info ?? "")
                    : Decide(target, target.Monitor));
            }

            // What is left of ours has no target any more: its database, broker or component is gone
            // from a list that was read in full.
            prune.AddRange(live.Monitoring.Monitors
                .Where(m => IsOurs(m) && !claimed.Contains(m.Identity))
                .Select(m => m.Identity));

            return new ScrapeMonitorPlan(rows, apply, prune);
        }

        private MonitorPlanRow Decide(ScrapeTargetSpec target, MonitorSpec monitor)
        {
            // Two targets can work out to the same monitor name (a Kafka called "entity-x" and the
            // entity operator of one called "x"). The first has it; writing both would be one
            // overwriting the other on every pass.
            if (!claimed.Add(monitor.Identity))
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None,
                    $"Another service on this cluster already has the {monitor.Identity}, so there is no name left "
                    + "for this one's monitor. Rename one of the two.");
            }

            LiveMonitor? existing = liveMonitors.GetValueOrDefault(monitor.Identity);
            bool existingIsOurs = existing is not null && ours.Contains(monitor.Identity);

            if (!live.Workloads.Namespaces.TryGetValue(target.Namespace, out LiveNamespace? ns))
            {
                return Row(target, MonitorState.NotRunning, MonitorChange.None,
                    $"Namespace {target.Namespace} does not exist on this cluster.");
            }

            ResolvedTargets resolved = ScrapedEndpoints.Of(monitor, ns);

            if (resolved.Pods.Count == 0)
            {
                return Row(target, MonitorState.NotRunning, MonitorChange.None,
                    $"Nothing with {monitor.Selector.Describe()} is running in {target.Namespace}.");
            }

            if (resolved.Endpoints.Count == 0)
            {
                return Row(target, MonitorState.NotExposed, MonitorChange.None,
                    $"It is running, but none of its {Count(resolved.Pods.Count, "pod")} exposes the metrics port "
                    + $"({string.Join(", ", monitor.Endpoints.Select(e => e.PortLabel))}). Turn its metrics on and "
                    + "EntKube picks it up by itself.");
            }

            // From here on it is a question of who scrapes these endpoints. What Prometheus itself
            // says comes first: an active target is evidence, whatever any selector reads like.
            if (OtherPoolOn(resolved) is { } pool)
            {
                return Yield(target, monitor, MonitorState.CoveredElsewhere, existingIsOurs, resolved,
                    $"Prometheus already scrapes it through {pool}. EntKube adds no monitor of its own, because two "
                    + "would count everything twice.");
            }

            (LiveMonitor? covering, LiveMonitor? unclear) = ForeignMonitorsOn(monitor, resolved, ns);

            if (covering is not null)
            {
                return Yield(target, monitor, MonitorState.CoveredElsewhere, existingIsOurs, resolved,
                    $"{covering.Identity}, which EntKube did not create, already selects it. EntKube adds no monitor "
                    + "of its own, because two would count everything twice.");
            }

            if (resolved.Endpoints.FirstOrDefault(IsAnnotated) is { } annotated)
            {
                return Yield(target, monitor, MonitorState.Annotated, existingIsOurs, resolved,
                    $"Pod {annotated.PodName} asks to be scraped with a {ScrapeAnnotation} annotation, which the "
                    + "Prometheus stack already does. A monitor beside it would count everything twice.");
            }

            if (existing is not null && !existingIsOurs)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None,
                    $"A {monitor.Identity} exists and is not EntKube's for this cluster, so it is left alone, and it "
                    + "does not scrape this. Rename or delete it and EntKube creates its own.", resolved);
            }

            if (unclear is not null)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None,
                    $"Whether {unclear.Identity} already scrapes it cannot be told, because its selector uses "
                    + "something EntKube does not understand. Nothing is changed while that is so.", resolved);
            }

            if (WhyPrometheusWouldNotSelect(monitor.Identity, out IReadOnlyDictionary<string, string> labels) is { } why)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None, why, resolved);
            }

            List<ScrapedEndpoint> shut = [.. resolved.Endpoints.Where(e => !admission.Admits(e, ns))];

            if (shut.Count == resolved.Endpoints.Count)
            {
                return Row(target, MonitorState.BlockedByNetworkPolicy, MonitorChange.None,
                    $"A NetworkPolicy in {target.Namespace} keeps Prometheus out of port {shut[0].Port}. Allow it in "
                    + $"from namespace {prometheus!.Namespace} and EntKube picks it up by itself.", resolved);
            }

            return Manage(target, monitor, existing, labels, resolved, shut.Count);
        }

        private MonitorPlanRow Manage(
            ScrapeTargetSpec target, MonitorSpec monitor, LiveMonitor? existing,
            IReadOnlyDictionary<string, string> labels, ResolvedTargets resolved, int shut)
        {
            JsonObject manifest = ScrapeMonitorManifest.Build(
                monitor, target.Key, labels, endpoint => OverTls(endpoint, resolved.Pods));

            MonitorChange change = existing is null
                ? MonitorChange.Create
                : ScrapeMonitorManifest.Compare(manifest, existing) switch
                {
                    MonitorDifference.None => MonitorChange.None,
                    MonitorDifference.Replace => MonitorChange.Replace,
                    _ => MonitorChange.Update,
                };

            if (change != MonitorChange.None) apply.Add(new PlannedMonitor(monitor.Identity, manifest, change));

            List<ActiveTarget> scraped =
                [.. live.Monitoring.Targets.Where(t => monitor.Identity == t.Monitor)];

            string endpoints = Count(resolved.Endpoints.Count, "endpoint");
            string said = change switch
            {
                MonitorChange.Create => $"EntKube will create {monitor.Identity.Kind} {monitor.Identity.Name} for its {endpoints}.",
                MonitorChange.None => $"EntKube's {monitor.Identity.Kind} {monitor.Identity.Name} scrapes its {endpoints}.",
                _ => $"EntKube's {monitor.Identity.Kind} {monitor.Identity.Name} is not what it should be and will be "
                     + $"written again, for its {endpoints}.",
            };

            if (shut > 0)
            {
                said += $" A NetworkPolicy keeps Prometheus out of {shut} of them, which will show as down.";
            }

            return Row(target, MonitorState.Managed, change, said, resolved) with
            {
                TargetsUp = scraped.Count(t => t.Up),
                TargetsTotal = scraped.Count,
            };
        }

        /// <summary>A row for something another job scrapes: ours goes, if there is one.</summary>
        private MonitorPlanRow Yield(
            ScrapeTargetSpec target, MonitorSpec monitor, MonitorState state, bool existingIsOurs,
            ResolvedTargets resolved, string detail)
        {
            if (existingIsOurs) prune.Add(monitor.Identity);

            return Row(target, state, existingIsOurs ? MonitorChange.Remove : MonitorChange.None, detail, resolved);
        }

        // ── Whose ─────────────────────────────────────────────────────────────

        /// <summary>Written by EntKube for this very cluster registration.</summary>
        private bool IsOurs(LiveMonitor monitor)
            => monitor.Labels.GetValueOrDefault(ScrapeMonitorManifest.OwnershipLabel) == live.ClusterId.ToString();

        /// <summary>
        /// Whether the live monitor of this name is the one EntKube wrote before it labelled them:
        /// unlabelled, and selecting exactly what it did then.
        /// </summary>
        private bool TakesOver(MonitorSpec monitor)
        {
            if (!monitor.TakesOverUnlabelled || liveMonitors.GetValueOrDefault(monitor.Identity) is not { } existing)
            {
                return false;
            }

            return !existing.Labels.ContainsKey(ScrapeMonitorManifest.OwnershipLabel)
                && JsonNode.DeepEquals(existing.Spec["selector"], monitor.Selector.ToJson());
        }

        // ── Who scrapes it already ────────────────────────────────────────────

        /// <summary>A scrape job, not one of ours, with an active target on one of these endpoints.</summary>
        private string? OtherPoolOn(ResolvedTargets resolved)
        {
            HashSet<(string, int, string)> endpoints = [.. resolved.Endpoints.Select(e => (e.PodIp, e.Port, e.Path))];

            return live.Monitoring.Targets
                .FirstOrDefault(t => endpoints.Contains((t.Host, t.Port, t.Path))
                    && (t.Monitor is not { } monitor || !ours.Contains(monitor)))
                ?.Pool;
        }

        /// <summary>
        /// A monitor of the same kind that is not ours, that this Prometheus selects, and that ends
        /// up on one of these ports. By port whatever the path: a second job on another path of the
        /// same port (RabbitMQ's per-object metrics) serves the same names again.
        /// </summary>
        private (LiveMonitor? Covering, LiveMonitor? Unclear) ForeignMonitorsOn(
            MonitorSpec wanted, ResolvedTargets resolved, LiveNamespace ns)
        {
            HashSet<string> ports = [.. resolved.Endpoints.Select(e => ScrapedEndpoints.Address(e.Pod, e.Port))];
            LiveMonitor? unclear = null;

            foreach (LiveMonitor monitor in live.Monitoring.Monitors)
            {
                if (monitor.Identity.Kind != wanted.Identity.Kind || ours.Contains(monitor.Identity)) continue;

                bool? selected = PrometheusSelects(monitor.Identity, monitor.Labels);
                if (selected is false) continue;

                (IReadOnlySet<string> certain, IReadOnlySet<string> possible) = ScrapedEndpoints.OfForeign(monitor, ns);

                if (selected is true && ports.Overlaps(certain)) return (monitor, null);
                if (ports.Overlaps(certain) || ports.Overlaps(possible)) unclear ??= monitor;
            }

            return (null, unclear);
        }

        /// <summary>
        /// Whether the annotation job takes this port: the pod asks for it by number, or asks to be
        /// scraped without saying where, in which case every declared port is taken.
        /// </summary>
        private static bool IsAnnotated(ScrapedEndpoint endpoint)
        {
            IDictionary<string, string>? annotations = endpoint.Pod.Metadata?.Annotations;

            if (annotations is null
                || !annotations.TryGetValue(ScrapeAnnotation, out string? scrape)
                || !string.Equals(scrape, "true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !annotations.TryGetValue(PortAnnotation, out string? port)
                || string.IsNullOrWhiteSpace(port)
                || port.Trim() == endpoint.Port.ToString();
        }

        // ── Whether Prometheus would pick ours up ─────────────────────────────

        /// <summary>
        /// Why Prometheus would not select the monitor, or null when it would. On the way, the labels
        /// the monitor has to carry for that.
        /// </summary>
        private string? WhyPrometheusWouldNotSelect(
            MonitorIdentity monitor, out IReadOnlyDictionary<string, string> labels)
        {
            labels = new Dictionary<string, string>();

            if (prometheus is null)
            {
                return live.Monitoring.Prometheuses.Count == 0
                    ? "The Prometheus Operator has no Prometheus on this cluster, so nothing would scrape the monitor."
                    : $"There are {live.Monitoring.Prometheuses.Count} Prometheus objects on this cluster and none of "
                      + $"them is clearly the {live.Release.Name} release in {live.Release.Namespace}, so EntKube cannot "
                      + "tell which one would scrape the monitor.";
            }

            string name = $"Prometheus {prometheus.Namespace}/{prometheus.Name}";

            if (prometheus.IsSharded)
            {
                return $"{name} is split into shards, and EntKube can ask only one of them what it scrapes. Nothing is "
                    + "changed, because what the others scrape cannot be told.";
            }

            labels = LabelsFor(monitor.Kind);

            if (PrometheusSelects(monitor, labels) is not { } selects)
            {
                return $"Whether {name} would select the monitor cannot be told, because its "
                    + $"{monitor.Kind} selector uses something EntKube does not understand.";
            }

            return selects
                ? null
                : $"{name} would not select a {monitor.Kind} in namespace {monitor.Namespace}. Set "
                  + (monitor.Kind == MonitorKind.PodMonitor
                      ? "podMonitorSelectorNilUsesHelmValues"
                      : "serviceMonitorSelectorNilUsesHelmValues")
                  + " to false in the Prometheus stack's values and check its namespace selector.";
        }

        /// <summary>
        /// Every label a monitor carries: whose it is, the release it was made for, and what this
        /// Prometheus selects that kind of monitor by. The selector's own labels win over EntKube's
        /// except for ownership, which nothing may take over.
        /// </summary>
        private IReadOnlyDictionary<string, string> LabelsFor(MonitorKind kind)
        {
            Dictionary<string, string> labels = new(StringComparer.Ordinal)
            {
                ["app.kubernetes.io/managed-by"] = "entkube",
                ["release"] = live.Release.Name,
            };

            foreach ((string key, string value) in prometheus?.MonitorSelector(kind)?.MatchLabels
                     ?? new Dictionary<string, string>())
            {
                labels[key] = value;
            }

            labels[ScrapeMonitorManifest.OwnershipLabel] = live.ClusterId.ToString();

            return labels;
        }

        /// <summary>Whether this Prometheus scrapes a monitor with these labels in that namespace.</summary>
        private bool? PrometheusSelects(MonitorIdentity monitor, IReadOnlyDictionary<string, string> labels)
        {
            if (prometheus is null) return null;

            bool? inNamespace = prometheus.NamespaceSelector(monitor.Kind) is { } selector
                ? live.Workloads.NamespaceLabels.TryGetValue(monitor.Namespace, out IReadOnlyDictionary<string, string>? of)
                    ? selector.Matches(of)
                    : null
                : monitor.Namespace == prometheus.Namespace;

            bool? byLabels = LabelSelector.Selects(prometheus.MonitorSelector(monitor.Kind), labels);

            if (inNamespace is false || byLabels is false) return false;

            return inNamespace is true && byLabels is true ? true : null;
        }

        private static PrometheusAdmission AdmissionOf(LivePrometheus? prometheus, LiveWorkloads workloads)
        {
            if (prometheus is null) return new PrometheusAdmission("", null, []);

            return new PrometheusAdmission(
                prometheus.Namespace,
                workloads.NamespaceLabels.GetValueOrDefault(prometheus.Namespace),
                workloads.Namespaces.TryGetValue(prometheus.Namespace, out LiveNamespace? ns)
                    ? [.. ns.Pods.Select(ScrapedEndpoints.Labels).Where(l => BelongsTo(prometheus, l))]
                    : []);
        }

        /// <summary>The operator labels the pods it makes with the Prometheus they are for.</summary>
        private static bool BelongsTo(LivePrometheus prometheus, IReadOnlyDictionary<string, string> podLabels)
            => podLabels.GetValueOrDefault("app.kubernetes.io/name") == "prometheus"
               && (podLabels.GetValueOrDefault("operator.prometheus.io/name")
                   ?? podLabels.GetValueOrDefault("app.kubernetes.io/instance")) == prometheus.Name;

        private static bool OverTls(ScrapeEndpointSpec endpoint, IReadOnlyList<V1Pod> pods)
            => endpoint.Https
               || (endpoint.HttpsWhenContainerArgument is { } argument
                   && pods.SelectMany(ScrapedEndpoints.RunningContainers)
                       .SelectMany(c => (c.Command ?? []).Concat(c.Args ?? []))
                       .Contains(argument, StringComparer.Ordinal));

        // ── Rows ──────────────────────────────────────────────────────────────

        private static MonitorPlanRow Row(
            ScrapeTargetSpec target, MonitorState state, MonitorChange change, string detail,
            ResolvedTargets? resolved = null)
            => new(target.Key, target.Title, target.ServiceKind, target.Namespace, target.Monitor?.Identity, state,
                change, detail,
                [.. (resolved?.Endpoints ?? []).Select(e =>
                    new PlannedEndpoint(e.PodName, ScrapedEndpoints.Address(e.Pod, e.Port), e.Path))]);

        private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";
    }
}
