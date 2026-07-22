using System.IO;
using System.Text.Json;
using EntKube.Web.Data;
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
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuditService auditService,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
    ILogger<NodeManagementService> logger)
{
    private Task RequireNodeAckAsync(
        EntKube.Web.Services.ClusterChanges.ChangeVerb verb, string kubeconfig,
        string clusterLabel, string summary, string? patch, CancellationToken ct)
        => gate.AcknowledgeAsync(new EntKube.Web.Services.ClusterChanges.PlannedClusterChange
        {
            Verb = verb,
            Kubeconfig = kubeconfig,
            ClusterLabel = string.IsNullOrWhiteSpace(clusterLabel) ? "cluster" : clusterLabel,
            Summary = summary,
            Patch = patch,
        }, ct);

    // ── Cluster lookup helper ─────────────────────────────────────────────────

    private async Task<(KubernetesCluster? Cluster, KubernetesOperationResult? Error)>
        LoadClusterAsync(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
            return (null, KubernetesOperationResult.Failure("Cluster not found."));

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (null, KubernetesOperationResult.Failure(
                "Cluster has no kubeconfig configured."));

        return (cluster, null);
    }

    private async Task<(KubernetesCluster? Cluster, KubernetesOperationResult<T>? Error)>
        LoadClusterAsync<T>(Guid clusterId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
            return (null, KubernetesOperationResult<T>.Failure("Cluster not found."));

        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return (null, KubernetesOperationResult<T>.Failure(
                "Cluster has no kubeconfig configured."));

        return (cluster, null);
    }

    // ── Node listing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all nodes in the cluster with full detail: conditions, labels,
    /// taints, capacity, allocatable resources, and addresses.
    /// </summary>
    public async Task<KubernetesOperationResult<List<NodeInfo>>> GetNodesAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult<List<NodeInfo>>? err) =
            await LoadClusterAsync<List<NodeInfo>>(clusterId, ct);
        if (err is not null) return err;

        try
        {
            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);
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
        Guid clusterId, string nodeName, CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult<List<PodInfo>>? err) =
            await LoadClusterAsync<List<PodInfo>>(clusterId, ct);
        if (err is not null) return err;

        try
        {
            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);

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
        Guid clusterId, string nodeName, CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult<List<NodeEventInfo>>? err) =
            await LoadClusterAsync<List<NodeEventInfo>>(clusterId, ct);
        if (err is not null) return err;

        try
        {
            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);

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
        Guid clusterId, string nodeName,
        string? performedBy = null, CancellationToken ct = default)
        => await SetNodeSchedulableAsync(clusterId, nodeName, schedulable: false, performedBy, ct);

    /// <summary>
    /// Re-enables scheduling on a cordoned node.
    /// Equivalent to "kubectl uncordon &lt;node&gt;".
    /// </summary>
    public async Task<KubernetesOperationResult> UncordonNodeAsync(
        Guid clusterId, string nodeName,
        string? performedBy = null, CancellationToken ct = default)
        => await SetNodeSchedulableAsync(clusterId, nodeName, schedulable: true, performedBy, ct);

    private async Task<KubernetesOperationResult> SetNodeSchedulableAsync(
        Guid clusterId, string nodeName, bool schedulable,
        string? performedBy, CancellationToken ct)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(clusterId, ct);
        if (err is not null) return err;

        try
        {
            // Patch spec.unschedulable: null means schedulable, true means cordoned.
            string value = schedulable ? "null" : "true";

            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                cluster!.Kubeconfig!, cluster.Name,
                $"{(schedulable ? "Uncordon" : "Cordon")} Node/{nodeName}",
                $"spec.unschedulable = {value}", ct);

            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);

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
        Guid clusterId, string nodeName,
        int gracePeriodSeconds = 30,
        bool ignoreDaemonSets = true,
        bool deleteEmptyDirData = false,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult<string>? err) =
            await LoadClusterAsync<string>(clusterId, ct);
        if (err is not null) return err;

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Delete,
                cluster!.Kubeconfig!, cluster.Name,
                $"Drain Node/{nodeName} (cordon + evict all evictable pods)", null, ct);

            await File.WriteAllTextAsync(tempKubeconfig, cluster!.Kubeconfig, ct);

            List<string> args = ["drain", nodeName,
                $"--grace-period={gracePeriodSeconds}",
                "--timeout=5m"];

            if (ignoreDaemonSets) args.Add("--ignore-daemonsets");
            if (deleteEmptyDirData) args.Add("--delete-emptydir-data");
            args.Add($"--kubeconfig={tempKubeconfig}");

            HelmExecutionResult result = await RunCliAsync(
                "kubectl", string.Join(" ", args), ct);

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
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    // ── Label management ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets a label on a node. If value is null, the label is removed.
    /// </summary>
    public async Task<KubernetesOperationResult> SetNodeLabelAsync(
        Guid clusterId, string nodeName, string key, string? value,
        string? performedBy = null, CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(clusterId, ct);
        if (err is not null) return err;

        try
        {
            // JSON Merge Patch: null removes the key, a string value sets it.
            string labelValue = value is null ? "null" : $"\"{JsonEscape(value)}\"";

            await RequireNodeAckAsync(EntKube.Web.Services.ClusterChanges.ChangeVerb.Patch,
                cluster!.Kubeconfig!, cluster.Name,
                value is null ? $"Remove label {key} from Node/{nodeName}" : $"Set label {key}={value} on Node/{nodeName}",
                $"metadata.labels.{key} = {(value is null ? "(removed)" : value)}", ct);

            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);

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
        Guid clusterId, string nodeName, List<NodeTaintInfo> taints,
        string? performedBy = null, CancellationToken ct = default)
    {
        (KubernetesCluster? cluster, KubernetesOperationResult? err) =
            await LoadClusterAsync(clusterId, ct);
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
                cluster!.Kubeconfig!, cluster.Name,
                $"Replace taints on Node/{nodeName} ({taints.Count} taint(s))",
                $"spec.taints = {taintsJson}", ct);

            using Kubernetes client = CreateClient(cluster!.Kubeconfig!);

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

    public async Task<List<ClusterServer>> GetServersAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        return await db.ClusterServers
            .Where(s => s.ClusterId == clusterId)
            .OrderBy(s => s.DisplayName)
            .ToListAsync(ct);
    }

    public async Task<ClusterServer> SaveServerAsync(
        ClusterServer server, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        server.UpdatedAt = DateTime.UtcNow;

        if (server.Id == Guid.Empty)
        {
            server.Id = Guid.NewGuid();
            server.CreatedAt = DateTime.UtcNow;
            db.ClusterServers.Add(server);
        }
        else
        {
            ClusterServer existing = await db.ClusterServers.FindAsync([server.Id], ct)
                ?? throw new InvalidOperationException("Server not found.");

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

    public async Task DeleteServerAsync(Guid serverId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        ClusterServer? server = await db.ClusterServers.FindAsync([serverId], ct);
        if (server is not null)
        {
            db.ClusterServers.Remove(server);
            await db.SaveChangesAsync(ct);
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static Kubernetes CreateClient(string kubeconfig)
    {
        using System.IO.MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config =
            KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<HelmExecutionResult> RunCliAsync(
        string program, string arguments, CancellationToken ct)
    {
        System.Diagnostics.ProcessStartInfo psi = new()
        {
            FileName = program,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using System.Diagnostics.Process process = new() { StartInfo = psi };
        try
        {
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string combined = (await stdout).Trim();
            string errStr = (await stderr).Trim();
            if (!string.IsNullOrEmpty(errStr))
                combined = string.IsNullOrEmpty(combined) ? errStr : combined + "\n" + errStr;

            return new HelmExecutionResult
            {
                Success = process.ExitCode == 0,
                ExitCode = process.ExitCode,
                Output = combined
            };
        }
        catch (Exception ex)
        {
            return new HelmExecutionResult
            {
                Success = false,
                Output = $"Failed to run {program}: {ex.Message}"
            };
        }
    }
}
