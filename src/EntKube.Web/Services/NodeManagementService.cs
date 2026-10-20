using System.IO;
using System.Text.Json;
using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

// ── View types returned to the UI ────────────────────────────────────────────

public class NodeConditionInfo
{
    public required string Type { get; set; }
    public required string Status { get; set; }
    public string? Reason { get; set; }
    public string? Message { get; set; }
    public DateTime? LastTransitionTime { get; set; }
}

public class NodeTaintInfo
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public required string Effect { get; set; }
}

public class NodeInfo
{
    public required string Name { get; set; }
    public bool Ready { get; set; }
    public bool Schedulable { get; set; }
    public List<string> Roles { get; set; } = [];
    public string? KubeletVersion { get; set; }
    public string? OsImage { get; set; }
    public string? KernelVersion { get; set; }
    public string? ContainerRuntime { get; set; }
    public string? Architecture { get; set; }
    public string? CpuCapacity { get; set; }
    public string? MemoryCapacity { get; set; }
    public string? CpuAllocatable { get; set; }
    public string? MemoryAllocatable { get; set; }
    public string? EphemeralStorageAllocatable { get; set; }
    public int? MaxPodsAllocatable { get; set; }
    public List<NodeConditionInfo> Conditions { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
    public Dictionary<string, string> Annotations { get; set; } = [];
    public List<NodeTaintInfo> Taints { get; set; } = [];
    public List<string> Addresses { get; set; } = [];
    public DateTime? CreatedAt { get; set; }
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Performs live Kubernetes node operations — listing nodes with full detail,
/// cordon/uncordon/drain, and label/taint management.
/// Also provides CRUD for the ClusterServer inventory (physical servers behind nodes).
/// </summary>
public class NodeManagementService(
    IDbContextFactory<FleetDbContext> dbFactory,
    AuditService auditService,
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterClients,
    ILogger<NodeManagementService> logger)
{
    /// <summary>
    /// Raises the acknowledgment for a node change this service carries out through the SDK.
    ///
    /// <para>No credential: the seam fills it in, along with the cluster's label. That is the
    /// whole reason this service no longer loads a cluster row — the kubeconfig was read to
    /// satisfy <c>PlannedClusterChange</c>, not to talk to the cluster.</para>
    /// </summary>
    private static Task RequireNodeAckAsync(
        EntKube.Web.Services.ClusterChanges.ChangeVerb verb,
        EntKube.Web.Services.Clusters.IClusterClient cluster,
        string summary, string? patch, CancellationToken ct)
        => cluster.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedChange
        {
            Verb = verb,
            Summary = summary,
            Patch = patch,
        }, ct);

    // ── Cluster lookup helper ─────────────────────────────────────────────────

    /// <summary>
    /// The cluster as a seam client, or the failure to report. Replaces a pair of helpers that
    /// loaded the row and checked its kubeconfig column: "may this tenant hold that cluster's
    /// credential" and "does a credential exist" turn out to be the same question, and the seam
    /// answers both. No node operation here reads anything else off the row.
    /// </summary>
    private async Task<(EntKube.Web.Services.Clusters.IClusterClient? Cluster, KubernetesOperationResult? Error)>
        LoadClusterAsync(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterClients.ForAsync(tenantId, clusterId, ct);

        return cluster is null
            ? (null, KubernetesOperationResult.Failure(
                "Cluster not found for this tenant, or it has no kubeconfig configured."))
            : (cluster, null);
    }

    private async Task<(EntKube.Web.Services.Clusters.IClusterClient? Cluster, KubernetesOperationResult<T>? Error)>
        LoadClusterAsync<T>(Guid tenantId, Guid clusterId, CancellationToken ct)
    {
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterClients.ForAsync(tenantId, clusterId, ct);

        return cluster is null
            ? (null, KubernetesOperationResult<T>.Failure(
                "Cluster not found for this tenant, or it has no kubeconfig configured."))
            : (cluster, null);
    }

    // ── Node listing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all nodes in the cluster with full detail: conditions, labels,
    /// taints, capacity, allocatable resources, and addresses.
    /// </summary>
    public async Task<KubernetesOperationResult<List<NodeInfo>>> GetNodesAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult<List<NodeInfo>>? err) =
            await LoadClusterAsync<List<NodeInfo>>(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();
            V1NodeList list = await client.CoreV1.ListNodeAsync(cancellationToken: ct);

            List<NodeInfo> nodes = list.Items.Select(node =>
            {
                V1NodeCondition? readyCondition = node.Status?.Conditions?
                    .FirstOrDefault(c => c.Type == "Ready");

                bool ready = readyCondition?.Status == "True";
                bool schedulable = node.Spec?.Unschedulable != true;

                List<string> roles = node.Metadata?.Labels?
                    .Where(l => l.Key.StartsWith("node-role.kubernetes.io/", StringComparison.Ordinal))
                    .Select(l => l.Key["node-role.kubernetes.io/".Length..])
                    .ToList() ?? ["worker"];

                List<NodeConditionInfo> conditions = (node.Status?.Conditions ?? [])
                    .Select(c => new NodeConditionInfo
                    {
                        Type = c.Type,
                        Status = c.Status,
                        Reason = c.Reason,
                        Message = c.Message,
                        LastTransitionTime = c.LastTransitionTime
                    }).ToList();

                List<NodeTaintInfo> taints = (node.Spec?.Taints ?? [])
                    .Select(t => new NodeTaintInfo
                    {
                        Key = t.Key,
                        Value = t.Value,
                        Effect = t.Effect
                    }).ToList();

                // Addresses: prefer InternalIP first, then ExternalIP
                List<string> addresses = (node.Status?.Addresses ?? [])
                    .OrderBy(a => a.Type == "InternalIP" ? 0 : a.Type == "ExternalIP" ? 1 : 2)
                    .Select(a => $"{a.Type}: {a.Address}")
                    .ToList();

                // Strip noisy system labels from the display labels dict
                Dictionary<string, string> labels = node.Metadata?.Labels?
                    .Where(l => !l.Key.StartsWith("node-role.kubernetes.io/", StringComparison.Ordinal))
                    .ToDictionary(l => l.Key, l => l.Value)
                    ?? [];

                // Strip managed-fields noise from annotations
                Dictionary<string, string> annotations = node.Metadata?.Annotations?
                    .Where(a => a.Key != "kubectl.kubernetes.io/last-applied-configuration")
                    .ToDictionary(a => a.Key, a => a.Value)
                    ?? [];

                string? cpuCap = node.Status?.Capacity?.TryGetValue("cpu", out ResourceQuantity? cpuQ) == true
                    ? cpuQ?.ToString() : null;
                string? memCap = node.Status?.Capacity?.TryGetValue("memory", out ResourceQuantity? memQ) == true
                    ? memQ?.ToString() : null;
                string? cpuAlloc = node.Status?.Allocatable?.TryGetValue("cpu", out ResourceQuantity? cpuAQ) == true
                    ? cpuAQ?.ToString() : null;
                string? memAlloc = node.Status?.Allocatable?.TryGetValue("memory", out ResourceQuantity? memAQ) == true
                    ? memAQ?.ToString() : null;
                string? ephAlloc = node.Status?.Allocatable?.TryGetValue("ephemeral-storage", out ResourceQuantity? ephQ) == true
                    ? ephQ?.ToString() : null;
                int? maxPods = node.Status?.Allocatable?.TryGetValue("pods", out ResourceQuantity? podsQ) == true
                    ? int.TryParse(podsQ?.ToString(), out int p) ? p : null : null;

                return new NodeInfo
                {
                    Name = node.Metadata!.Name,
                    Ready = ready,
                    Schedulable = schedulable,
                    Roles = roles.Count > 0 ? roles : ["worker"],
                    KubeletVersion = node.Status?.NodeInfo?.KubeletVersion,
                    OsImage = node.Status?.NodeInfo?.OsImage,
                    KernelVersion = node.Status?.NodeInfo?.KernelVersion,
                    ContainerRuntime = node.Status?.NodeInfo?.ContainerRuntimeVersion,
                    Architecture = node.Status?.NodeInfo?.Architecture,
                    CpuCapacity = cpuCap,
                    MemoryCapacity = memCap,
                    CpuAllocatable = cpuAlloc,
                    MemoryAllocatable = memAlloc,
                    EphemeralStorageAllocatable = ephAlloc,
                    MaxPodsAllocatable = maxPods,
                    Conditions = conditions,
                    Labels = labels,
                    Annotations = annotations,
                    Taints = taints,
                    Addresses = addresses,
                    CreatedAt = node.Metadata?.CreationTimestamp
                };
            })
            .OrderBy(n => n.Name)
            .ToList();

            return KubernetesOperationResult<List<NodeInfo>>.Success(nodes);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<NodeInfo>>.Failure(
                $"Failed to list nodes: {ex.Message}");
        }
    }

    // ── Pods on a node ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all pods currently scheduled on a specific node (across all namespaces).
    /// </summary>
    public async Task<KubernetesOperationResult<List<PodInfo>>> GetPodsOnNodeAsync(
        Guid tenantId, Guid clusterId, string nodeName, CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult<List<PodInfo>>? err) =
            await LoadClusterAsync<List<PodInfo>>(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();

            V1PodList podList = await client.CoreV1.ListPodForAllNamespacesAsync(
                fieldSelector: $"spec.nodeName={nodeName}",
                cancellationToken: ct);

            List<PodInfo> pods = podList.Items.Select(pod => new PodInfo
            {
                Name = pod.Metadata.Name,
                Namespace = pod.Metadata.NamespaceProperty ?? "default",
                Status = pod.Status?.Phase ?? "Unknown",
                ReadyContainers = pod.Status?.ContainerStatuses?.Count(cs => cs.Ready) ?? 0,
                TotalContainers = pod.Spec?.Containers?.Count ?? 0,
                Restarts = pod.Status?.ContainerStatuses?.Sum(cs => cs.RestartCount) ?? 0,
                StartTime = pod.Status?.StartTime,
                Containers = []
            }).OrderBy(p => p.Namespace).ThenBy(p => p.Name).ToList();

            return KubernetesOperationResult<List<PodInfo>>.Success(pods);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<PodInfo>>.Failure(
                $"Failed to list pods on node: {ex.Message}");
        }
    }

    // ── Node events ───────────────────────────────────────────────────────────

    public class NodeEventInfo
    {
        public required string Type { get; set; }
        public required string Reason { get; set; }
        public required string Message { get; set; }
        public string? Component { get; set; }
        public int Count { get; set; }
        public DateTime? LastSeen { get; set; }
        public DateTime? FirstSeen { get; set; }
    }

    /// <summary>
    /// Returns Kubernetes events involving a specific node (equivalent to
    /// "kubectl describe node" events section).
    /// </summary>
    public async Task<KubernetesOperationResult<List<NodeEventInfo>>> GetNodeEventsAsync(
        Guid tenantId, Guid clusterId, string nodeName, CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult<List<NodeEventInfo>>? err) =
            await LoadClusterAsync<List<NodeEventInfo>>(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();

            var events = await client.CoreV1.ListEventForAllNamespacesAsync(
                fieldSelector: $"involvedObject.name={nodeName},involvedObject.kind=Node",
                cancellationToken: ct);

            List<NodeEventInfo> result = events.Items
                .OrderByDescending(e => e.LastTimestamp ?? e.EventTime)
                .Select(e => new NodeEventInfo
                {
                    Type = e.Type ?? "Normal",
                    Reason = e.Reason ?? "",
                    Message = e.Message ?? "",
                    Component = e.Source?.Component,
                    Count = e.Count ?? 1,
                    LastSeen = e.LastTimestamp ?? e.EventTime,
                    FirstSeen = e.FirstTimestamp ?? e.EventTime
                })
                .ToList();

            return KubernetesOperationResult<List<NodeEventInfo>>.Success(result);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<NodeEventInfo>>.Failure(
                $"Failed to list node events: {ex.Message}");
        }
    }

    // ── Cordon / Uncordon ─────────────────────────────────────────────────────

    /// <summary>
    /// Marks a node as unschedulable (cordon). Existing pods are not evicted.
    /// Equivalent to "kubectl cordon &lt;node&gt;".
    /// </summary>
    public async Task<KubernetesOperationResult> CordonNodeAsync(
        Guid tenantId, Guid clusterId, string nodeName,
        string? performedBy = null, CancellationToken ct = default)
        => await SetNodeSchedulableAsync(tenantId, clusterId, nodeName, schedulable: false, performedBy, ct);

    /// <summary>
    /// Re-enables scheduling on a cordoned node.
    /// Equivalent to "kubectl uncordon &lt;node&gt;".
    /// </summary>
    public async Task<KubernetesOperationResult> UncordonNodeAsync(
        Guid tenantId, Guid clusterId, string nodeName,
        string? performedBy = null, CancellationToken ct = default)
        => await SetNodeSchedulableAsync(tenantId, clusterId, nodeName, schedulable: true, performedBy, ct);

    private async Task<KubernetesOperationResult> SetNodeSchedulableAsync(
        Guid tenantId, Guid clusterId, string nodeName, bool schedulable,
        string? performedBy, CancellationToken ct)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            // Patch spec.unschedulable: null means schedulable, true means cordoned.
            string value = schedulable ? "null" : "true";

            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                cluster!,
                $"{(schedulable ? "Uncordon" : "Cordon")} Node/{nodeName}",
                $"spec.unschedulable = {value}", ct);

            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();

            V1Patch patch = new(
                $"{{\"spec\":{{\"unschedulable\":{value}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.CoreV1.PatchNodeAsync(patch, nodeName, cancellationToken: ct);

            string action = schedulable ? "UncordonNode" : "CordonNode";
            logger.LogInformation("Node {Node} {Action} by {User}", nodeName, action, performedBy ?? "system");

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            string action = schedulable ? "uncordon" : "cordon";
            return KubernetesOperationResult.Failure($"Failed to {action} node: {ex.Message}");
        }
    }

    // ── Drain ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drains a node by cordoning it and evicting all evictable pods.
    /// Uses "kubectl drain" for proper eviction handling (respects PodDisruptionBudgets,
    /// ignores DaemonSet pods, and deletes local storage pods if requested).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DrainNodeAsync(
        Guid tenantId, Guid clusterId, string nodeName,
        int gracePeriodSeconds = 30,
        bool ignoreDaemonSets = true,
        bool deleteEmptyDirData = false,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult<string>? err) =
            await LoadClusterAsync<string>(tenantId, clusterId, ct);
        if (err is not null) return err;

        // Through the seam, which raises the acknowledgment and supplies the credential. A drain
        // is its own verb there: it is neither a patch nor a delete of one object, and the
        // PodDisruptionBudget handling lives in kubectl rather than in the API.
        HelmExecutionResult result = await cluster!.DrainNodeAsync(
            nodeName, gracePeriodSeconds, ignoreDaemonSets, deleteEmptyDirData,
            $"Drain Node/{nodeName} (cordon + evict all evictable pods)", ct);

        if (result.Success)
        {
            logger.LogInformation("Node {Node} drained by {User}", nodeName, performedBy ?? "system");
        }
        else
        {
            logger.LogWarning("Drain failed for node {Node}: {Output}", nodeName, result.Output);
        }

        return result.Success
            ? KubernetesOperationResult<string>.Success(result.Output)
            : KubernetesOperationResult<string>.Failure(result.Output);
    }

    // ── Label management ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets a label on a node. If value is null, the label is removed.
    /// </summary>
    public async Task<KubernetesOperationResult> SetNodeLabelAsync(
        Guid tenantId, Guid clusterId, string nodeName, string key, string? value,
        string? performedBy = null, CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            // JSON Merge Patch: null removes the key, a string value sets it.
            string labelValue = value is null ? "null" : $"\"{JsonEscape(value)}\"";

            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                cluster!,
                value is null ? $"Remove label {key} from Node/{nodeName}" : $"Set label {key}={value} on Node/{nodeName}",
                $"metadata.labels.{key} = {(value is null ? "(removed)" : value)}", ct);

            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();

            V1Patch patch = new(
                $"{{\"metadata\":{{\"labels\":{{\"{JsonEscape(key)}\":{labelValue}}}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.CoreV1.PatchNodeAsync(patch, nodeName, cancellationToken: ct);

            string action = value is null ? "removed label" : "set label";
            logger.LogInformation("Node {Node}: {Action} {Key}={Value} by {User}",
                nodeName, action, key, value, performedBy ?? "system");

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult.Failure($"Failed to set label: {ex.Message}");
        }
    }

    // ── Taint management ──────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the full taint list on a node with the provided taints.
    /// </summary>
    public async Task<KubernetesOperationResult> SetNodeTaintsAsync(
        Guid tenantId, Guid clusterId, string nodeName, List<NodeTaintInfo> taints,
        string? performedBy = null, CancellationToken ct = default)
    {
        (EntKube.Web.Services.Clusters.IClusterClient? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(tenantId, clusterId, ct);
        if (err is not null) return err;

        try
        {
            string taintsJson = JsonSerializer.Serialize(taints.Select(t => new
            {
                key = t.Key,
                value = t.Value,
                effect = t.Effect
            }));

            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                cluster!,
                $"Replace taints on Node/{nodeName} ({taints.Count} taint(s))",
                $"spec.taints = {taintsJson}", ct);

            // Not disposed: pooled per credential and shared across callers.
            Kubernetes client = cluster!.GetPooledSdkClient();

            V1Patch patch = new(
                $"{{\"spec\":{{\"taints\":{taintsJson}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.CoreV1.PatchNodeAsync(patch, nodeName, cancellationToken: ct);

            logger.LogInformation("Node {Node}: taints updated by {User}", nodeName, performedBy ?? "system");
            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult.Failure($"Failed to update taints: {ex.Message}");
        }
    }

    // ── Server inventory CRUD ─────────────────────────────────────────────────

    /// <param name="tenantId">
    /// Whose cluster. A server record carries the node's address, its <b>management
    /// address</b>, the SSH user and port and the jump host used to reach it — so listing
    /// them by cluster id alone described another tenant's hardware and how it is reached.
    /// ClusterServer has no TenantId of its own; the cluster it belongs to does.
    /// </param>
    public async Task<List<ClusterServer>> GetServersAsync(
        Guid tenantId, Guid clusterId, CancellationToken ct = default)
    {
        using FleetDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterServers
            .Where(s => s.ClusterId == clusterId && s.Cluster.TenantId == tenantId)
            .OrderBy(s => s.DisplayName)
            .ToListAsync(ct);
    }

    /// <param name="tenantId">
    /// Whose cluster. These are inventory records rather than live connection settings — the
    /// screen says as much when it offers to remove one — so what this protects is the record
    /// being correct, not a connection being redirected. The ClusterId rides on the entity, so
    /// a create needs the cluster checked; an edit needs the row.
    /// </param>
    public async Task<ClusterServer> SaveServerAsync(
        Guid tenantId, ClusterServer server, CancellationToken ct = default)
    {
        using FleetDbContext db = dbFactory.CreateDbContext();
        server.UpdatedAt = DateTime.UtcNow;

        if (!await db.KubernetesClusters.AnyAsync(
                c => c.Id == server.ClusterId && c.TenantId == tenantId, ct))
        {
            throw new InvalidOperationException(
                "Cluster not found, or it is not this tenant's.");
        }

        if (server.Id == Guid.Empty)
        {
            server.Id = Guid.NewGuid();
            server.CreatedAt = DateTime.UtcNow;
            db.ClusterServers.Add(server);
        }
        else
        {
            // FindAsync takes a key and cannot carry a predicate.
            ClusterServer existing = await db.ClusterServers
                .FirstOrDefaultAsync(
                    s => s.Id == server.Id && s.Cluster.TenantId == tenantId, ct)
                ?? throw new InvalidOperationException(
                    "Server not found, or it is not this tenant's.");

            existing.DisplayName = server.DisplayName;
            existing.NodeName = server.NodeName;
            existing.IpAddress = server.IpAddress;
            existing.ManagementIpAddress = server.ManagementIpAddress;
            existing.Provider = server.Provider;
            existing.OsDistribution = server.OsDistribution;
            existing.CpuCores = server.CpuCores;
            existing.RamGb = server.RamGb;
            existing.DiskGb = server.DiskGb;
            existing.Location = server.Location;
            existing.SshUser = server.SshUser;
            existing.SshPort = server.SshPort;
            existing.JumpHost = server.JumpHost;
            existing.Notes = server.Notes;
            existing.UpdatedAt = server.UpdatedAt;
            server = existing;
        }

        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task DeleteServerAsync(
        Guid tenantId, Guid serverId, CancellationToken ct = default)
    {
        using FleetDbContext db = dbFactory.CreateDbContext();
        ClusterServer? server = await db.ClusterServers
            .FirstOrDefaultAsync(s => s.Id == serverId && s.Cluster.TenantId == tenantId, ct);
        if (server is not null)
        {
            db.ClusterServers.Remove(server);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
