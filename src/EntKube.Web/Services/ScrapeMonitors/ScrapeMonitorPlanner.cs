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
/// <para><b>Who scrapes it is asked of every endpoint.</b> Ours scrapes all of a target's endpoints
/// or none, so it is made only where nothing else scrapes any of them, and given up only where
/// something else scrapes every one. In between, ours would double the endpoints that are covered:
/// it is neither made nor kept, and the row names the ones nothing scrapes.</para>
///
/// <para><b>What counts as somebody else scraping an endpoint.</b> Prometheus listing a target on the
/// pod's address and port, whatever the path, unless the scrape asks for something else
/// (<see cref="ScrapedEndpoints.ServesItsOwnMetrics"/>), or on Istio's agent where it merges the port
/// (<see cref="ScrapedEndpoints.MergedByIstio"/>). And, for the moments before Prometheus lists it: a
/// monitor it selects that was made less than <see cref="LoadGrace"/> ago, or a pod that has run less
/// than that which the annotation job, seen scraping something, takes by its own rules. After that,
/// only the target list counts.</para>
///
/// <para><b>What cannot be told yet changes nothing.</b> A selected monitor older than that with no
/// target on an endpoint scrapes nothing there, unless it was edited less than <see cref="LoadGrace"/>
/// ago or the pod started less than that ago: then Prometheus may simply not have come round to it,
/// and ours is neither made nor removed for it. Likewise a target scraped in part on evidence still in
/// its grace keeps ours as it is.</para>
///
/// <para>Pure. It is handed a complete <see cref="LiveScrapeState"/> and reads nothing else, so every
/// decision it makes can be tested with the labels a chart really renders.</para>
/// </summary>
public static class ScrapeMonitorPlanner
{
    /// <summary>
    /// How long Prometheus has to have run before its list of targets is the whole of what it scrapes.
    /// Ready comes when its storage and configuration are loaded; discovery fills the list over the
    /// first few scrape intervals after that.
    /// </summary>
    public static readonly TimeSpan PrometheusSettles = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long a monitor, or an annotated pod, may go without Prometheus listing a target on it and
    /// still be counted as about to be scraped. The operator and the config reloader take a minute or
    /// two over a new monitor, discovery seconds over a new pod; what is still not listed after this is
    /// not scraped by that job.
    /// </summary>
    public static readonly TimeSpan LoadGrace = TimeSpan.FromMinutes(10);

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

    /// <summary>What says one endpoint is scraped already.</summary>
    private enum Evidence
    {
        /// <summary>Nothing does.</summary>
        None,

        /// <summary>Prometheus lists a target of another job on it.</summary>
        Target,

        /// <summary>A monitor that is not ours, that Prometheus selects, made too recently to have a target yet.</summary>
        Monitor,

        /// <summary>The annotation job takes it, and the pod started too recently to be listed yet.</summary>
        Annotation,

        /// <summary>Whether something scrapes it cannot be told.</summary>
        Unclear,
    }

    /// <summary>One endpoint and what scrapes it, named for a sentence.</summary>
    private sealed record Coverage(ScrapedEndpoint Endpoint, Evidence By, string Who = "");

    /// <summary>A monitor that is not ours, whether Prometheus selects it, and the pod ports it ends at.</summary>
    private sealed record Foreign(
        LiveMonitor Monitor, bool? Selected, IReadOnlySet<string> Certain, IReadOnlySet<string> Possible);

    /// <summary>One run over one cluster's targets.</summary>
    private sealed class Pass
    {
        private readonly IReadOnlyList<ScrapeTargetSpec> targets;
        private readonly LiveScrapeState live;
        private readonly LivePrometheus? prometheus;
        private readonly PrometheusAdmission admission;
        private readonly Dictionary<MonitorIdentity, LiveMonitor> liveMonitors;
        private readonly HashSet<MonitorIdentity> ours;

        /// <summary>Why Prometheus's list of targets cannot be taken as the whole of it yet; null when it can.</summary>
        private readonly string? starting;

        /// <summary>Whether Prometheus lists any target of the annotation job, which is how it is known to be there.</summary>
        private readonly bool annotationJobRuns;

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
            starting = prometheus is null ? null : WhyNotSettled(prometheus);
            annotationJobRuns = live.Monitoring.Targets.Any(t => t.Pool == ScrapedEndpoints.AnnotationJob);
        }

        public ScrapeMonitorPlan Run()
        {
            foreach (ScrapeTargetSpec target in targets)
            {
                rows.Add(target.Monitor is null
                    ? Row(target, MonitorState.Info, MonitorChange.None, InfoOf(target))
                    : Decide(target, target.Monitor));
            }

            // What is left of ours has no target any more: its database, broker or component is gone
            // from a list that was read in full. Not while Prometheus is starting: that pass writes nothing.
            if (starting is null)
            {
                prune.AddRange(live.Monitoring.Monitors
                    .Where(m => IsOurs(m) && !claimed.Contains(m.Identity))
                    .Select(m => m.Identity));
            }

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

            // Everything from here on is a question of who scrapes these endpoints, which a Prometheus
            // that has only just started cannot answer yet.
            if (starting is not null)
            {
                return Row(target, MonitorState.PrometheusStarting, MonitorChange.None, starting, resolved);
            }

            List<string> notes = [];
            List<Coverage> coverage = [.. CoverageOf(monitor, resolved, ns, notes)];
            List<Coverage> covered = [.. coverage.Where(c => c.By is Evidence.Target or Evidence.Monitor or Evidence.Annotation)];
            List<Coverage> unclear = [.. coverage.Where(c => c.By == Evidence.Unclear)];

            if (covered.Count == coverage.Count)
            {
                return covered.All(c => c.By == Evidence.Annotation)
                    ? Yield(target, monitor, MonitorState.Annotated, existingIsOurs, resolved, AnnotatedSentence(covered))
                    : Yield(target, monitor, MonitorState.CoveredElsewhere, existingIsOurs, resolved,
                        CoveredSentence(covered) + " EntKube adds no monitor of its own, because two would count "
                        + "everything twice.");
            }

            // Some are scraped and some are not. Ours would scrape all of them, so the covered ones twice:
            // known, whatever is unknown about the rest, where Prometheus lists that other job's target.
            // Where it does not yet (a monitor still loading, a pod the annotation job is about to
            // take), giving ours up would leave every part unscraped on a hope, and making ours would
            // be the second job in a moment: ours stays as it is, or stays unmade, until it is listed.
            if (covered.Count > 0)
            {
                bool listed = covered.Any(c => c.By == Evidence.Target);

                return listed
                    ? Yield(target, monitor, MonitorState.PartlyCovered, existingIsOurs, resolved,
                        PartlySentence(coverage, covered, existingIsOurs, listed))
                    : Row(target, MonitorState.PartlyCovered, MonitorChange.None,
                        PartlySentence(coverage, covered, existingIsOurs, listed), resolved);
            }

            if (existing is not null && !existingIsOurs)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None,
                    $"A {monitor.Identity} exists and is not EntKube's for this cluster, so it is left alone, and it "
                    + "does not scrape this. Rename or delete it and EntKube creates its own.", resolved);
            }

            if (unclear.Count > 0)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None,
                    $"Whether {unclear[0].Who} already scrapes it cannot be told. Nothing is changed while that is so.",
                    resolved);
            }

            // Our own monitor would be the second job. Ours goes if there is one: a gap the row
            // explains is better than every sum over those pods silently twice what it is.
            if (resolved.Twice is [TwiceScrapedEndpoint twice, ..])
            {
                if (existingIsOurs) prune.Add(monitor.Identity);

                return Row(target, MonitorState.NotSelectable,
                    existingIsOurs ? MonitorChange.Remove : MonitorChange.None,
                    $"Pod {twice.Endpoint.PodName} is behind {string.Join(" and ", twice.Services)}, both with port "
                    + $"{twice.Endpoint.Port}, so a ServiceMonitor would scrape it twice and double every sum. EntKube "
                    + (existingIsOurs ? "removes its monitor" : "makes no monitor")
                    + " while that is so; remove the extra Service, or label it apart, and EntKube picks it up by itself.",
                    resolved);
            }

            if (WhyPrometheusWouldNotSelect(monitor.Identity, out IReadOnlyDictionary<string, string> labels) is { } why)
            {
                return Row(target, MonitorState.NotSelectable, MonitorChange.None, why, resolved);
            }

            List<ScrapedEndpoint> shut = [.. resolved.Endpoints.Where(e => !admission.Admits(e, ns))];

            if (shut.Count == resolved.Endpoints.Count)
            {
                return Row(target, MonitorState.BlockedByNetworkPolicy, MonitorChange.None,
                    $"A NetworkPolicy in {target.Namespace} keeps Prometheus out of port {shut[0].Port}. "
                    + WhatOpensIt(target), resolved);
            }

            return Manage(target, monitor, existing, labels, resolved, shut.Count, notes);
        }

        /// <summary>
        /// What lets Prometheus in, for a target a NetworkPolicy keeps it out of. Where a component's apply
        /// adds the rule (SeaweedFS), the rule names the namespace Catalog records for Prometheus, so the
        /// re-apply is the advice only where that is recorded and is where the live Prometheus runs. With
        /// nothing recorded the apply opens nothing, on purpose: a guessed namespace would let somebody
        /// else's pods in. With another recorded, it opens the door to a namespace nothing scrapes from.
        /// </summary>
        private string WhatOpensIt(ScrapeTargetSpec target)
        {
            string at = prometheus!.Namespace;
            if (target.OpenedByApplyOf is not { } component)
            {
                return target.WhenBlocked ?? $"Allow it in from namespace {at} and EntKube picks it up by itself.";
            }

            string owner = target.WhenBlocked is { } owns ? owns + " " : "";
            const string record = "kube-prometheus-stack component";

            if (!live.Release.NamespaceRecorded)
            {
                return owner + $"EntKube does not know which namespace Prometheus runs in, because none is recorded on "
                    + $"the {record}, so applying {component} opens nothing for it: a guessed namespace would let "
                    + $"somebody else's pods in. Record the namespace on the {record} ({at}, where it runs), then "
                    + $"re-apply {component}, and EntKube picks it up by itself.";
            }

            if (live.Release.Namespace != at)
            {
                return owner + $"The {record} is recorded in namespace {live.Release.Namespace}, but Prometheus "
                    + $"{prometheus.Name} runs in {at}, so the rule applying {component} adds lets in a namespace "
                    + $"nothing scrapes from. Correct the namespace recorded on the {record}, then re-apply "
                    + $"{component}, and EntKube picks it up by itself.";
            }

            return owner + $"Re-apply {component} and EntKube adds the rule that lets Prometheus in, then picks it "
                + "up by itself.";
        }

        /// <summary>
        /// Why there is no monitor to make, and, for a chart that leaves its pods to an annotation job,
        /// what Prometheus has shown of such a job: that is all EntKube knows of whether anything takes
        /// them. It does not know which pods are the chart's, so it says no more than that.
        /// </summary>
        private string InfoOf(ScrapeTargetSpec target)
        {
            string info = target.Info ?? "";
            if (!target.LeftToAnnotationJob) return info;

            string job = ScrapedEndpoints.AnnotationJob;

            if (starting is not null)
            {
                return info + $" Whether this Prometheus has a {job} job cannot be told yet: it started too recently "
                    + "for its list of targets to be the whole of it.";
            }

            return info + (annotationJobRuns
                ? $" This Prometheus has a {job} job, which takes such pods by its own rules; the Scrape targets "
                  + "panel shows whether it lists these."
                : $" This Prometheus lists no target of a {job} job, so nothing may scrape it. EntKube's own Prometheus "
                  + "stack has that job; one installed with other scrape configs may not.");
        }

        private MonitorPlanRow Manage(
            ScrapeTargetSpec target, MonitorSpec monitor, LiveMonitor? existing,
            IReadOnlyDictionary<string, string> labels, ResolvedTargets resolved, int shut, IReadOnlyList<string> notes)
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

            foreach (string note in notes) said += " " + note;

            return Row(target, MonitorState.Managed, change, said, resolved) with
            {
                TargetsUp = scraped.Count(t => t.Up),
                TargetsTotal = scraped.Count,
            };
        }

        /// <summary>A row for something another job scrapes, all of it or some: ours goes, if there is one.</summary>
        private MonitorPlanRow Yield(
            ScrapeTargetSpec target, MonitorSpec monitor, MonitorState state, bool existingIsOurs,
            ResolvedTargets resolved, string detail)
        {
            if (existingIsOurs) prune.Add(monitor.Identity);

            return Row(target, state, existingIsOurs ? MonitorChange.Remove : MonitorChange.None, detail, resolved);
        }

        // ── Whose ─────────────────────────────────────────────────────────────

        /// <summary>Written by EntKube for this very cluster registration: <see cref="ScrapeMonitorIdentity.IsOurs"/>.</summary>
        private bool IsOurs(LiveMonitor monitor) => ScrapeMonitorIdentity.IsOurs(monitor.Labels, live.ClusterId);

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

        // ── Whether Prometheus's list is the whole of it ──────────────────────

        /// <summary>
        /// Why the target list cannot be taken as everything this Prometheus scrapes, or null when it
        /// can. Every ready pod of it has to have run <see cref="PrometheusSettles"/>: it is one of them
        /// that answered, and which one cannot be told.
        /// </summary>
        private string? WhyNotSettled(LivePrometheus of)
        {
            string name = $"Prometheus {of.Namespace}/{of.Name}";
            const string nothing = "EntKube creates, changes and removes no monitor until it can.";

            List<V1ContainerStatus> ready =
            [
                .. (live.Workloads.Namespaces.GetValueOrDefault(of.Namespace)?.Pods ?? [])
                    .Where(p => BelongsTo(of, ScrapedEndpoints.Labels(p)))
                    .SelectMany(p => p.Status?.ContainerStatuses ?? [])
                    .Where(c => c.Name == "prometheus" && c.Ready),
            ];

            if (ready.Count == 0)
            {
                return $"None of the pods of {name} has a ready Prometheus container, so how long the one that "
                    + $"answered has run cannot be told, nor whether its list of targets is the whole of it. {nothing}";
            }

            if (ready.Any(c => c.State?.Running?.StartedAt is null))
            {
                return $"The kubelet does not say when {name} started, so whether its list of targets is the whole "
                    + $"of it cannot be told. {nothing}";
            }

            TimeSpan ran = live.Monitoring.ReadAt - ready.Max(c => Utc(c.State.Running.StartedAt!.Value));

            return ran < PrometheusSettles
                ? $"{name} started {Since(ran)} ago, and until it has run {PrometheusSettles.TotalMinutes:0} minutes its "
                  + $"list of targets may not be the whole of what it scrapes. {nothing}"
                : null;
        }

        // ── Who scrapes it already ────────────────────────────────────────────

        /// <summary>Each endpoint, and what scrapes it already, with what was seen but not counted in <paramref name="notes"/>.</summary>
        private IEnumerable<Coverage> CoverageOf(
            MonitorSpec wanted, ResolvedTargets resolved, LiveNamespace ns, List<string> notes)
        {
            bool ignored = prometheus?.IgnoresNamespaceSelectors is true;

            // Every monitor that is not ours and that this Prometheus may select, of either kind: both
            // end at pod ports.
            List<Foreign> foreign = [];
            foreach (LiveMonitor monitor in live.Monitoring.Monitors.Where(m => !ours.Contains(m.Identity)))
            {
                bool? selected = PrometheusSelects(monitor.Identity, monitor.Labels);
                if (selected is false) continue;

                (IReadOnlySet<string> certain, IReadOnlySet<string> possible) = ScrapedEndpoints.OfForeign(monitor, ns, ignored);
                foreign.Add(new Foreign(monitor, selected, certain, possible));
            }

            HashSet<MonitorIdentity> stale = [];
            HashSet<string> annotatedNotTaken = new(StringComparer.Ordinal);

            foreach (ScrapedEndpoint endpoint in resolved.Endpoints)
            {
                ScrapedEndpoints.IstioMerge merge = ScrapedEndpoints.MergedByIstio(endpoint.Pod, endpoint.Port, out int agent);
                int? merged = merge == ScrapedEndpoints.IstioMerge.Yes ? agent : null;
                List<int> ports = merged is { } at ? [endpoint.Port, at] : [endpoint.Port];
                HashSet<string> addresses = [.. ports.Select(p => ScrapedEndpoints.Address(endpoint.Pod, p))];

                // What Prometheus itself says comes first: an active target is evidence, whatever any
                // selector reads like.
                if (OtherTargetOn(wanted, endpoint, merged) is { } other)
                {
                    yield return new Coverage(endpoint, Evidence.Target, other.Port == endpoint.Port
                        ? other.Pool
                        : $"{other.Pool} on port {other.Port}, where Istio's agent serves port {endpoint.Port}'s "
                          + "metrics again with its own");
                    continue;
                }

                LiveMonitor? loading = null;
                string? cannotTell = null;

                foreach ((LiveMonitor monitor, bool? selected, IReadOnlySet<string> certain, IReadOnlySet<string> possible) in foreign)
                {
                    if (selected is true && addresses.Overlaps(certain))
                    {
                        // Prometheus lists no target of it here (that would have been the first check).
                        // A monitor made a moment ago may not have one yet, and counts: it is how a chart
                        // turning its own on takes over from ours. One edited a moment ago, or one that
                        // reaches a pod started a moment ago, may have one by the next pass, or may never:
                        // which cannot be told, so neither ours nor its absence is acted on.
                        if (monitor.Created is not { } created)
                        {
                            cannotTell ??= $"{monitor.Identity}, whose age the API server does not give,";
                        }
                        else if (live.Monitoring.ReadAt - created < LoadGrace)
                        {
                            loading ??= monitor;
                        }
                        else if (WhyNotYetListed(monitor, endpoint) is { } notYet)
                        {
                            cannotTell ??= notYet;
                        }
                        else
                        {
                            stale.Add(monitor.Identity);
                        }
                    }
                    else if (addresses.Overlaps(certain) || addresses.Overlaps(possible))
                    {
                        cannotTell ??= selected is null
                            ? $"{monitor.Identity}, which this Prometheus may or may not select,"
                            : $"{monitor.Identity}, whose selector uses something EntKube does not understand,";
                    }
                }

                if (loading is not null)
                {
                    yield return new Coverage(endpoint, Evidence.Monitor, loading.Identity.ToString());
                    continue;
                }

                if (ports.Any(p => ScrapedEndpoints.AnnotationJobTakes(endpoint.Pod, p)))
                {
                    if (annotationJobRuns && RanFor(endpoint.Pod) is { } ran && ran < LoadGrace)
                    {
                        yield return new Coverage(endpoint, Evidence.Annotation, ScrapedEndpoints.AnnotationJob);
                        continue;
                    }

                    annotatedNotTaken.Add(endpoint.PodName);
                }

                if (cannotTell is not null)
                {
                    yield return new Coverage(endpoint, Evidence.Unclear, cannotTell);
                    continue;
                }

                if (merge == ScrapedEndpoints.IstioMerge.CannotTell)
                {
                    yield return new Coverage(endpoint, Evidence.Unclear,
                        $"Istio's agent in pod {endpoint.PodName}, whose record of the pod's annotations cannot be read,");
                    continue;
                }

                yield return new Coverage(endpoint, Evidence.None);
            }

            foreach (MonitorIdentity monitor in stale.OrderBy(m => m.ToString(), StringComparer.Ordinal))
            {
                notes.Add($"{monitor}, which EntKube did not create, selects it too, but Prometheus has scraped "
                    + $"nothing through it on these pods in the {Since(live.Monitoring.ReadAt - liveMonitors[monitor].Created!.Value)} "
                    + "since it was made, so it is not counted.");
            }

            if (annotatedNotTaken.Count > 0)
            {
                notes.Add(annotationJobRuns
                    ? $"{Pods(annotatedNotTaken)} {Ask(annotatedNotTaken)} to be scraped by annotation, but the "
                      + $"{ScrapedEndpoints.AnnotationJob} job has not taken {Them(annotatedNotTaken)}, so that is not counted."
                    : $"{Pods(annotatedNotTaken)} {Ask(annotatedNotTaken)} to be scraped by annotation, but Prometheus "
                      + $"scrapes nothing through a {ScrapedEndpoints.AnnotationJob} job, so that is not counted.");
            }
        }

        /// <summary>
        /// Why a selected monitor that was not made a moment ago, and that Prometheus lists no target of
        /// on this endpoint, may still be about to scrape it; null where nothing says so, and it is
        /// stale. An edit a moment ago, or one at a time the API server keeps no record of, or a pod
        /// started a moment ago (or at a time the kubelet does not give): discovery and the config
        /// reloader may not have come round to it.
        /// </summary>
        private string? WhyNotYetListed(LiveMonitor monitor, ScrapedEndpoint endpoint)
        {
            if (monitor.Changed is { } changed && live.Monitoring.ReadAt - changed < LoadGrace)
            {
                return $"{monitor.Identity}, which was edited {Since(live.Monitoring.ReadAt - changed)} ago and which "
                    + "Prometheus may not have taken in yet,";
            }

            if (monitor.Changed is null && monitor.Generation > 1)
            {
                return $"{monitor.Identity}, which has been edited at a time the API server does not give,";
            }

            return RanFor(endpoint.Pod) switch
            {
                null => $"{monitor.Identity}, which selects pod {endpoint.PodName} that the kubelet gives no start for,",
                { } ran when ran < LoadGrace =>
                    $"{monitor.Identity}, which selects pod {endpoint.PodName} that started {Since(ran)} ago, too "
                    + "recently for Prometheus to list it yet,",
                _ => null,
            };
        }

        /// <summary>
        /// A target of a scrape job, not one of ours, on the endpoint's address and port that gets what
        /// the port serves of its own (<see cref="ScrapedEndpoints.ServesItsOwnMetrics"/>); or, where
        /// Istio's agent merges the port, on the agent's port at the one path it serves them again on.
        ///
        /// <para>The job of the monitor being decided is ours when nothing of that name is live: it
        /// is what Prometheus keeps of one of ours that was just removed, until its next reload. The
        /// same name is the same job, so writing the monitor again scrapes nothing twice.</para>
        /// </summary>
        private ActiveTarget? OtherTargetOn(MonitorSpec wanted, ScrapedEndpoint endpoint, int? agent)
            => live.Monitoring.Targets.FirstOrDefault(t =>
                t.Host == endpoint.PodIp
                && ScrapedEndpoints.ServesItsOwnMetrics(t.Path, t.Query.Length > 0)
                && (t.Port == endpoint.Port
                    || (t.Port == agent && t.Path.TrimEnd('/') == ScrapedEndpoints.IstioMergedPath))
                && (t.Monitor is not { } monitor
                    || !(ours.Contains(monitor) || (monitor == wanted.Identity && !liveMonitors.ContainsKey(monitor)))));

        /// <summary>How long the pod has run, or null where the kubelet does not say.</summary>
        private TimeSpan? RanFor(V1Pod pod)
            => pod.Status?.StartTime is { } started ? live.Monitoring.ReadAt - Utc(started) : null;

        // ── What the rows say about it ────────────────────────────────────────

        private static string CoveredSentence(IReadOnlyList<Coverage> covered)
        {
            List<string> through = [.. covered.Where(c => c.By == Evidence.Target).Select(c => c.Who).Distinct()];
            List<string> monitors = [.. covered.Where(c => c.By == Evidence.Monitor).Select(c => c.Who).Distinct()];
            List<string> annotated = [.. covered.Where(c => c.By == Evidence.Annotation).Select(c => c.Endpoint.PodName).Distinct()];

            List<string> parts = [];
            if (through.Count > 0) parts.Add($"Prometheus already scrapes it through {Join(through)}");
            if (monitors.Count > 0)
            {
                parts.Add($"{Join(monitors)}, which EntKube did not create, already {(monitors.Count == 1 ? "selects" : "select")} "
                    + (through.Count > 0 ? "the rest" : "it"));
            }

            if (annotated.Count > 0)
            {
                parts.Add($"the {ScrapedEndpoints.AnnotationJob} job is about to take {Pods(annotated)}");
            }

            string sentence = Join(parts) + ".";
            return char.ToUpperInvariant(sentence[0]) + sentence[1..];
        }

        private static string AnnotatedSentence(IReadOnlyList<Coverage> covered)
        {
            List<string> pods = [.. covered.Select(c => c.Endpoint.PodName).Distinct()];

            return $"{Pods(pods)} {Ask(pods)} to be scraped with a prometheus.io/scrape annotation, and the stack's "
                + $"{ScrapedEndpoints.AnnotationJob} job takes {Them(pods)}; {(pods.Count == 1 ? "it" : "they")} started too "
                + $"recently for Prometheus to list {Them(pods)} yet. A monitor beside it would count everything twice.";
        }

        private static string PartlySentence(
            IReadOnlyList<Coverage> all, IReadOnlyList<Coverage> covered, bool existingIsOurs, bool listed)
        {
            List<Coverage> rest = [.. all.Where(c => c.By is Evidence.None or Evidence.Unclear)];
            List<string> nothing = [.. rest.Where(c => c.By == Evidence.None).Select(Place)];
            List<Coverage> unclear = [.. rest.Where(c => c.By == Evidence.Unclear)];

            string sentence = $"{covered.Count} of its {Count(all.Count, "endpoint")} {(covered.Count == 1 ? "is" : "are")} "
                + $"scraped already, through {Join([.. covered.Select(c => c.Who).Distinct()])}";

            if (nothing.Count > 0)
            {
                sentence += $", and nothing scrapes {Join(nothing)}";
            }

            sentence += ".";

            if (unclear.Count > 0)
            {
                sentence += $" Whether {unclear[0].Who} scrapes {Join([.. unclear.Select(Place)])} cannot be told.";
            }

            if (!listed)
            {
                return sentence
                    + $" Prometheus lists no target of that yet, and may within {LoadGrace.TotalMinutes:0} minutes: until "
                    + "it does, EntKube "
                    + (existingIsOurs ? "keeps its monitor as it is" : "makes none")
                    + ". If it does, a monitor of EntKube's would scrape the covered ones twice, and EntKube "
                    + (existingIsOurs ? "removes its own." : "makes none.");
            }

            return sentence
                + " A monitor of EntKube's would scrape every one of them, and so the covered ones twice: EntKube "
                + (existingIsOurs ? "removes its monitor" : "makes none")
                + " while that is so. Have that job scrape all of them, or none, and EntKube picks it up by itself.";
        }

        private static string Place(Coverage c) => $"{c.Endpoint.PodName} ({ScrapedEndpoints.Address(c.Endpoint.Pod, c.Endpoint.Port)})";

        private static string Pods(IReadOnlyCollection<string> pods)
            => pods.Count == 1 ? $"Pod {pods.First()}" : $"Pods {Join([.. pods])}";

        private static string Ask(IReadOnlyCollection<string> pods) => pods.Count == 1 ? "asks" : "ask";

        private static string Them(IReadOnlyCollection<string> pods) => pods.Count == 1 ? "it" : "them";

        private static string Join(IReadOnlyList<string> parts) => parts.Count switch
        {
            0 => "",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };

        /// <summary>A length of time as a sentence says it.</summary>
        private static string Since(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;

            return span.TotalMinutes < 2 ? Count((int)span.TotalSeconds, "second")
                : span.TotalHours < 2 ? Count((int)span.TotalMinutes, "minute")
                : span.TotalDays < 2 ? Count((int)span.TotalHours, "hour")
                : Count((int)span.TotalDays, "day");
        }

        /// <summary>A time the Kubernetes client read, as the UTC it was sent in.</summary>
        private static DateTimeOffset Utc(DateTime at)
            => new(at.Kind == DateTimeKind.Local ? at.ToUniversalTime() : DateTime.SpecifyKind(at, DateTimeKind.Utc));

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

            // The labels that make a monitor ours are not the selector's to set: a monitor that gave them up
            // would be one EntKube no longer knows for its own, or one that calls itself somebody else's.
            if (ScrapeMonitorIdentity.ConflictWith(prometheus.MonitorSelector(monitor.Kind), live.ClusterId) is { } owned)
            {
                return $"{name} selects only {monitor.Kind}s whose {owned} is not what EntKube writes there "
                    + $"({owned}={ScrapeMonitorIdentity.Labels(live.ClusterId)[owned]}). That label is how EntKube tells "
                    + "its own monitors from everybody else's, so it gives it up for no selector, and makes no monitor "
                    + $"while that is so. Take {owned} out of the {monitor.Kind} selector in the Prometheus stack's values "
                    + "and EntKube picks it up by itself.";
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
        /// except for the ones that make it EntKube's (<see cref="ScrapeMonitorIdentity.Labels"/>), which
        /// nothing may take over; a selector that demands otherwise has been refused before this.
        /// </summary>
        private IReadOnlyDictionary<string, string> LabelsFor(MonitorKind kind)
        {
            Dictionary<string, string> labels = new(StringComparer.Ordinal) { ["release"] = live.Release.Name };

            foreach ((string key, string value) in prometheus?.MonitorSelector(kind)?.MatchLabels
                     ?? new Dictionary<string, string>())
            {
                labels[key] = value;
            }

            foreach ((string key, string value) in ScrapeMonitorIdentity.Labels(live.ClusterId)) labels[key] = value;

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
