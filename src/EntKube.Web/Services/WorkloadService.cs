using System.Text.Json;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>The workload kinds the browser can list.</summary>
public enum WorkloadKind
{
    Pod,
    Deployment,
    ReplicaSet,
    StatefulSet,
    DaemonSet
}

/// <summary>One container inside a pod (or one container template of a controller).</summary>
public record WorkloadContainerView(
    string Name,
    string Image,
    bool Ready,
    int Restarts,
    string State,
    string? Reason,
    string? Message);

/// <summary>
/// A single row in the workload browser: one Pod / Deployment / ReplicaSet /
/// StatefulSet / DaemonSet with the state we can show without a second API call.
/// </summary>
public record WorkloadView
{
    public required WorkloadKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Namespace { get; init; }

    /// <summary>Rolled-up health used for the row badge and the "problems only" filter.</summary>
    public HealthStatus Health { get; init; } = HealthStatus.Unknown;

    /// <summary>kubectl-style status word — "Running", "CrashLoopBackOff", "Completed", "Terminating".</summary>
    public string StatusText { get; init; } = "Unknown";

    /// <summary>Ready replicas (controllers) or ready containers (pods).</summary>
    public int Ready { get; init; }

    /// <summary>Desired replicas (controllers) or total containers (pods).</summary>
    public int Desired { get; init; }

    /// <summary>Replicas updated to the current spec — controllers only, 0 for pods.</summary>
    public int Updated { get; init; }

    public int Restarts { get; init; }
    public DateTime? CreatedAt { get; init; }

    /// <summary>Node the pod is scheduled on; null for controllers.</summary>
    public string? Node { get; init; }

    public string? PodIP { get; init; }
    public string? OwnerKind { get; init; }
    public string? OwnerName { get; init; }

    /// <summary>Container images, deduplicated, in spec order.</summary>
    public IReadOnlyList<string> Images { get; init; } = [];

    public IReadOnlyList<WorkloadContainerView> Containers { get; init; } = [];

    /// <summary>Human-readable detail for the row — the waiting reason, a scale-down note, etc.</summary>
    public string? Message { get; init; }

    /// <summary>A ReplicaSet kept around by a Deployment's revision history but running nothing.</summary>
    public bool IsInactive => Kind == WorkloadKind.ReplicaSet && Desired == 0 && Ready == 0;

    /// <summary>Sort/search key: "namespace/name".</summary>
    public string Key => $"{Namespace}/{Name}";
}

/// <summary>
/// One read of a cluster's workloads. Each kind is fetched independently, so a
/// partial failure (RBAC, an unreachable API server mid-read) degrades to a
/// warning rather than an empty page.
/// </summary>
public record WorkloadSnapshot
{
    public IReadOnlyList<WorkloadView> Workloads { get; init; } = [];

    /// <summary>Every namespace in the cluster — the filter's option list, not just the ones with workloads.</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];

    /// <summary>Non-fatal problems ("could not list daemonsets: …") to show as a banner.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Set when nothing could be read at all (no cluster, no kubeconfig).</summary>
    public string? Error { get; init; }

    public DateTime FetchedAt { get; init; } = DateTime.UtcNow;

    public bool IsSuccess => Error is null;

    public static WorkloadSnapshot Failure(string error) => new() { Error = error };
}

/// <summary>
/// Read-only cluster workload browser: lists Pods, Deployments, ReplicaSets,
/// StatefulSets and DaemonSets from a registered cluster, optionally scoped to a
/// single namespace, and normalizes each into a <see cref="WorkloadView"/> with a
/// kubectl-style status and a rolled-up <see cref="HealthStatus"/>.
///
/// Everything goes through <see cref="IKubernetesClientFactory"/> (kubectl -o json)
/// so the cluster is never mutated and the parsing is unit-testable without a live
/// API server. Each kind is fetched in its own guarded call: a kind that fails
/// (RBAC, transient error) becomes a warning and the rest of the page still renders.
/// </summary>
public class WorkloadService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IKubernetesClientFactory k8s,
    ILogger<WorkloadService> logger)
{
    /// <summary>Reasons that mean "still starting", not "broken" — a waiting pod with one of these is Progressing.</summary>
    private static readonly HashSet<string> BenignWaitingReasons =
        new(StringComparer.OrdinalIgnoreCase) { "ContainerCreating", "PodInitializing" };

    /// <summary>
    /// Reads the workloads of one cluster. When <paramref name="ns"/> is null or empty
    /// all namespaces are listed; otherwise only that namespace is queried.
    /// </summary>
    public async Task<WorkloadSnapshot> LoadAsync(Guid clusterId, string? ns = null, CancellationToken ct = default)
    {
        KubernetesCluster? cluster;
        await using (ApplicationDbContext db = await dbFactory.CreateDbContextAsync(ct))
        {
            cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        }

        if (cluster is null)
            return WorkloadSnapshot.Failure("Cluster not found.");

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return WorkloadSnapshot.Failure("Cluster has no kubeconfig configured.");

        string kubeconfig = cluster.Kubeconfig;
        string? scope = string.IsNullOrWhiteSpace(ns) ? null : ns.Trim();

        List<string> warnings = [];
        List<WorkloadView> workloads = [];

        // Namespace list for the filter — independent of the scope so switching namespaces
        // never depends on what the current scope happened to contain.
        List<string> namespaces = await GetNamespacesAsync(kubeconfig, warnings, ct);

        // Pods and the four controller kinds. Each is best-effort and independently guarded.
        workloads.AddRange(await FetchAsync("pods", kubeconfig, scope, warnings, ParsePods, ct));
        workloads.AddRange(await FetchAsync("deployments", kubeconfig, scope, warnings,
            items => ParseReplicaController(items, WorkloadKind.Deployment), ct));
        workloads.AddRange(await FetchAsync("statefulsets", kubeconfig, scope, warnings,
            items => ParseReplicaController(items, WorkloadKind.StatefulSet), ct));
        workloads.AddRange(await FetchAsync("replicasets", kubeconfig, scope, warnings,
            items => ParseReplicaController(items, WorkloadKind.ReplicaSet), ct));
        workloads.AddRange(await FetchAsync("daemonsets", kubeconfig, scope, warnings, ParseDaemonSets, ct));

        return new WorkloadSnapshot
        {
            Workloads = [.. workloads.OrderBy(w => w.Namespace, StringComparer.Ordinal)
                                     .ThenBy(w => w.Kind)
                                     .ThenBy(w => w.Name, StringComparer.Ordinal)],
            Namespaces = namespaces,
            Warnings = warnings,
        };
    }

    /// <summary>Lists every namespace in the cluster. A failure here is a warning, not a fatal error.</summary>
    private async Task<List<string>> GetNamespacesAsync(
        string kubeconfig, List<string> warnings, CancellationToken ct)
    {
        try
        {
            string json = await k8s.GetJsonAllNamespacesAsync("namespaces", kubeconfig, "", ct);
            return [.. EnumerateItems(json)
                .Select(item => GetString(item, "metadata", "name"))
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list namespaces");
            warnings.Add($"Could not list namespaces: {Summarize(ex)}");
            return [];
        }
    }

    /// <summary>
    /// Fetches one resource kind (namespace-scoped or cluster-wide) and parses it.
    /// Any failure is recorded as a warning and yields no rows.
    /// </summary>
    private async Task<List<WorkloadView>> FetchAsync(
        string resource, string kubeconfig, string? scope, List<string> warnings,
        Func<IEnumerable<JsonElement>, List<WorkloadView>> parse, CancellationToken ct)
    {
        try
        {
            string json = scope is null
                ? await k8s.GetJsonAllNamespacesAsync(resource, kubeconfig, "", ct)
                : await k8s.GetJsonAsync(resource, scope, kubeconfig, "", ct);

            return parse(EnumerateItems(json));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list {Resource}", resource);
            warnings.Add($"Could not list {resource}: {Summarize(ex)}");
            return [];
        }
    }

    // ──────── Parsers ────────

    /// <summary>
    /// Pods, with the status word kubectl would show: Terminating for a pod under
    /// deletion, the blocking container's waiting/terminated reason (CrashLoopBackOff,
    /// ImagePullBackOff, OOMKilled) when one is stuck, otherwise the phase.
    /// </summary>
    private static List<WorkloadView> ParsePods(IEnumerable<JsonElement> items)
    {
        List<WorkloadView> rows = [];

        foreach (JsonElement item in items)
        {
            string? name = GetString(item, "metadata", "name");
            if (name is null) continue;

            JsonElement status = GetElement(item, "status");
            JsonElement spec = GetElement(item, "spec");

            string phase = GetString(status, "phase") ?? "Unknown";
            bool terminating = GetString(item, "metadata", "deletionTimestamp") is not null;

            List<WorkloadContainerView> containers = ParseContainerStatuses(status, "containerStatuses");
            List<WorkloadContainerView> initContainers = ParseContainerStatuses(status, "initContainerStatuses");

            // Total containers comes from the spec: a pod whose containers never started
            // has no containerStatuses yet, and "0/0" would read as healthy.
            int total = TryGetArray(spec, "containers")?.Count ?? containers.Count;
            int ready = containers.Count(c => c.Ready);
            int restarts = containers.Sum(c => c.Restarts) + initContainers.Sum(c => c.Restarts);

            // Init containers block the pod, so their reason wins over the app containers'.
            WorkloadContainerView? blocking =
                initContainers.FirstOrDefault(c => IsBlockingReason(c.Reason))
                ?? containers.FirstOrDefault(c => IsBlockingReason(c.Reason));

            string statusText = terminating ? "Terminating"
                : blocking?.Reason is { Length: > 0 } reason ? reason
                : phase == "Succeeded" ? "Completed"    // what kubectl shows for a finished Job pod
                : GetString(status, "reason") ?? phase;

            HealthStatus health = terminating ? HealthStatus.Progressing
                : phase switch
                {
                    "Succeeded" => HealthStatus.Suspended,   // completed Job pod — not a fault
                    "Failed" => HealthStatus.Degraded,
                    "Pending" => blocking is not null ? HealthStatus.Degraded : HealthStatus.Progressing,
                    "Running" when blocking is not null => HealthStatus.Degraded,
                    "Running" when total > 0 && ready >= total => HealthStatus.Healthy,
                    "Running" => HealthStatus.Progressing,
                    _ => HealthStatus.Unknown,
                };

            (string? ownerKind, string? ownerName) = ParseOwner(item);

            rows.Add(new WorkloadView
            {
                Kind = WorkloadKind.Pod,
                Name = name,
                Namespace = GetString(item, "metadata", "namespace") ?? "default",
                Health = health,
                StatusText = statusText,
                Ready = ready,
                Desired = total,
                Restarts = restarts,
                CreatedAt = GetDate(item, "metadata", "creationTimestamp"),
                Node = GetString(spec, "nodeName"),
                PodIP = GetString(status, "podIP"),
                OwnerKind = ownerKind,
                OwnerName = ownerName,
                Images = [.. containers.Select(c => c.Image).Where(i => i.Length > 0).Distinct()],
                Containers = [.. initContainers, .. containers],
                Message = blocking?.Message ?? GetString(status, "message"),
            });
        }

        return rows;
    }

    /// <summary>
    /// Deployments, StatefulSets and ReplicaSets — all three carry spec.replicas
    /// plus status.readyReplicas/updatedReplicas, so one parser covers them.
    /// </summary>
    private static List<WorkloadView> ParseReplicaController(IEnumerable<JsonElement> items, WorkloadKind kind)
    {
        List<WorkloadView> rows = [];

        foreach (JsonElement item in items)
        {
            string? name = GetString(item, "metadata", "name");
            if (name is null) continue;

            JsonElement spec = GetElement(item, "spec");
            JsonElement status = GetElement(item, "status");

            // An omitted spec.replicas means 1 (the API default), not 0.
            int desired = GetInt(spec, "replicas") ?? 1;
            int ready = GetInt(status, "readyReplicas") ?? 0;
            int updated = GetInt(status, "updatedReplicas") ?? 0;

            (string? ownerKind, string? ownerName) = ParseOwner(item);

            string statusText = desired == 0 ? "Scaled to zero" : $"{ready}/{desired} ready";
            HealthStatus health = HealthFromReplicas(desired, ready);

            rows.Add(new WorkloadView
            {
                Kind = kind,
                Name = name,
                Namespace = GetString(item, "metadata", "namespace") ?? "default",
                Health = health,
                StatusText = statusText,
                Ready = ready,
                Desired = desired,
                Updated = updated,
                CreatedAt = GetDate(item, "metadata", "creationTimestamp"),
                OwnerKind = ownerKind,
                OwnerName = ownerName,
                Images = ParseTemplateImages(spec),
                Containers = ParseTemplateContainers(spec),
                Message = ConditionMessage(status),
            });
        }

        return rows;
    }

    /// <summary>DaemonSets count nodes, not replicas: desiredNumberScheduled vs numberReady.</summary>
    private static List<WorkloadView> ParseDaemonSets(IEnumerable<JsonElement> items)
    {
        List<WorkloadView> rows = [];

        foreach (JsonElement item in items)
        {
            string? name = GetString(item, "metadata", "name");
            if (name is null) continue;

            JsonElement spec = GetElement(item, "spec");
            JsonElement status = GetElement(item, "status");

            int desired = GetInt(status, "desiredNumberScheduled") ?? 0;
            int ready = GetInt(status, "numberReady") ?? 0;
            int updated = GetInt(status, "updatedNumberScheduled") ?? 0;
            int misscheduled = GetInt(status, "numberMisscheduled") ?? 0;

            string statusText = desired == 0 ? "No matching nodes" : $"{ready}/{desired} ready";

            rows.Add(new WorkloadView
            {
                Kind = WorkloadKind.DaemonSet,
                Name = name,
                Namespace = GetString(item, "metadata", "namespace") ?? "default",
                Health = HealthFromReplicas(desired, ready),
                StatusText = statusText,
                Ready = ready,
                Desired = desired,
                Updated = updated,
                CreatedAt = GetDate(item, "metadata", "creationTimestamp"),
                Images = ParseTemplateImages(spec),
                Containers = ParseTemplateContainers(spec),
                Message = misscheduled > 0 ? $"{misscheduled} pod(s) running on nodes that no longer match" : null,
            });
        }

        return rows;
    }

    // ──────── Shared mapping helpers ────────

    /// <summary>
    /// Replica-count health, shared by every controller kind: nothing desired is a
    /// deliberate scale-down (Suspended), full readiness is Healthy, partial is
    /// Degraded, and none-ready-but-some-wanted is still rolling out.
    /// </summary>
    private static HealthStatus HealthFromReplicas(int desired, int ready) =>
        desired == 0 ? HealthStatus.Suspended
        : ready >= desired ? HealthStatus.Healthy
        : ready > 0 ? HealthStatus.Degraded
        : HealthStatus.Progressing;

    /// <summary>A waiting/terminated reason that means the container is stuck, not merely starting.</summary>
    private static bool IsBlockingReason(string? reason) =>
        !string.IsNullOrEmpty(reason) && !BenignWaitingReasons.Contains(reason);

    private static List<WorkloadContainerView> ParseContainerStatuses(JsonElement status, string property)
    {
        List<JsonElement>? statuses = TryGetArray(status, property);
        if (statuses is null) return [];

        List<WorkloadContainerView> containers = [];

        foreach (JsonElement cs in statuses)
        {
            JsonElement state = GetElement(cs, "state");
            string phase = "unknown";
            string? reason = null;
            string? message = null;

            if (state.ValueKind == JsonValueKind.Object)
            {
                if (state.TryGetProperty("running", out _))
                {
                    phase = "running";
                }
                else if (state.TryGetProperty("waiting", out JsonElement waiting))
                {
                    phase = "waiting";
                    reason = GetString(waiting, "reason");
                    message = GetString(waiting, "message");
                }
                else if (state.TryGetProperty("terminated", out JsonElement terminated))
                {
                    phase = "terminated";
                    reason = GetString(terminated, "reason");
                    message = GetString(terminated, "message");
                    // A clean exit is how init containers and Job pods finish — not a fault.
                    if (GetInt(terminated, "exitCode") == 0) reason = null;
                }
            }

            containers.Add(new WorkloadContainerView(
                GetString(cs, "name") ?? "container",
                GetString(cs, "image") ?? "",
                cs.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True,
                GetInt(cs, "restartCount") ?? 0,
                phase,
                reason,
                message));
        }

        return containers;
    }

    /// <summary>Container templates of a controller — spec.template.spec.containers.</summary>
    private static List<JsonElement> TemplateContainers(JsonElement spec)
    {
        JsonElement podSpec = GetElement(GetElement(spec, "template"), "spec");
        return TryGetArray(podSpec, "containers") ?? [];
    }

    private static List<string> ParseTemplateImages(JsonElement spec) =>
        [.. TemplateContainers(spec)
            .Select(c => GetString(c, "image"))
            .OfType<string>()
            .Distinct()];

    private static List<WorkloadContainerView> ParseTemplateContainers(JsonElement spec) =>
        [.. TemplateContainers(spec)
            .Select(c => new WorkloadContainerView(
                GetString(c, "name") ?? "container",
                GetString(c, "image") ?? "",
                Ready: false, Restarts: 0, State: "template", Reason: null, Message: null))];

    /// <summary>The first not-yet-satisfied condition's message — why a rollout is stuck.</summary>
    private static string? ConditionMessage(JsonElement status)
    {
        List<JsonElement>? conditions = TryGetArray(status, "conditions");
        if (conditions is null) return null;

        foreach (JsonElement condition in conditions)
        {
            if (GetString(condition, "status") == "False" && GetString(condition, "message") is { Length: > 0 } msg)
                return msg;
        }

        return null;
    }

    /// <summary>The controlling owner (Deployment → ReplicaSet → Pod), used for the "owned by" column.</summary>
    private static (string? Kind, string? Name) ParseOwner(JsonElement item)
    {
        List<JsonElement>? owners = TryGetArray(GetElement(item, "metadata"), "ownerReferences");
        if (owners is null || owners.Count == 0) return (null, null);

        JsonElement owner = owners.FirstOrDefault(
            o => o.TryGetProperty("controller", out JsonElement c) && c.ValueKind == JsonValueKind.True);
        if (owner.ValueKind != JsonValueKind.Object) owner = owners[0];

        return (GetString(owner, "kind"), GetString(owner, "name"));
    }

    // ──────── JSON helpers ────────

    /// <summary>Items of a kubectl list response; an empty sequence for anything unparseable.</summary>
    private static IEnumerable<JsonElement> EnumerateItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
                return [];

            // Clone: the elements outlive the JsonDocument's using scope.
            return [.. items.EnumerateArray().Select(e => e.Clone())];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement GetElement(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out JsonElement child)
            ? child
            : default;

    private static string? GetString(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetString(JsonElement parent, string first, string second) =>
        GetString(GetElement(parent, first), second);

    private static int? GetInt(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int result)
            ? result
            : null;

    private static DateTime? GetDate(JsonElement parent, string first, string second) =>
        DateTime.TryParse(GetString(parent, first, second), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out DateTime parsed)
            ? parsed
            : null;

    private static List<JsonElement>? TryGetArray(JsonElement parent, string property) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()]
            : null;

    /// <summary>First line of an exception message — kubectl errors are multi-line and noisy in a banner.</summary>
    private static string Summarize(Exception ex)
    {
        string message = ex.Message.Trim();
        int newline = message.IndexOf('\n');
        return newline > 0 ? message[..newline].Trim() : message;
    }
}
