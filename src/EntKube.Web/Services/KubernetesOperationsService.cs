using System.Text.Json;
using System.Text.Json.Nodes;
using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace EntKube.Web.Services;

/// <summary>
/// A simple result type for Kubernetes operations. Operations can fail for
/// many reasons (no kubeconfig, cluster unreachable, pod not found), so we
/// use Result rather than exceptions for expected failures.
/// </summary>
public class KubernetesOperationResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }

    public static KubernetesOperationResult Success() => new() { IsSuccess = true };
    public static KubernetesOperationResult Failure(string error) => new() { IsSuccess = false, Error = error };
}

/// <summary>
/// Result type with a data payload for operations that return information
/// (e.g. pod lists, log content).
/// </summary>
public class KubernetesOperationResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? Error { get; init; }

    public static KubernetesOperationResult<T> Success(T data) => new() { IsSuccess = true, Data = data };
    public static KubernetesOperationResult<T> Failure(string error) => new() { IsSuccess = false, Error = error };
}

/// <summary>
/// A snapshot of a Kubernetes pod's state — name, status, container count,
/// restarts, age. Rendered in the portal's deployment detail view.
/// </summary>
public class PodInfo
{
    public required string Name { get; set; }
    public required string Namespace { get; set; }
    public required string Status { get; set; }
    public int ReadyContainers { get; set; }
    public int TotalContainers { get; set; }
    public int Restarts { get; set; }
    public DateTime? StartTime { get; set; }
    public List<ContainerInfo> Containers { get; set; } = [];
}

/// <summary>
/// Per-container state within a pod. Lets the user pick which container
/// to view logs for in multi-container pods.
/// </summary>
public class ContainerInfo
{
    public required string Name { get; set; }
    public required string Image { get; set; }
    public bool Ready { get; set; }
    public int RestartCount { get; set; }
    public string? State { get; set; }
}

/// A Kubernetes Service port entry (name optional, port number, protocol).
public record KubeServicePort(string? Name, int Port, string Protocol);

/// <summary>A Kubernetes Service with its exposed ports.</summary>
public record KubeServiceInfo(string Name, string Type, string? ClusterIP, List<KubeServicePort> Ports);

/// <summary>Ready and not-ready endpoint IP:port strings for a Service.</summary>
public record KubeEndpointSummary(List<string> Ready, List<string> NotReady)
{
    public int TotalReady => Ready.Count;
    public int TotalNotReady => NotReady.Count;
    public bool HasEndpoints => Ready.Count + NotReady.Count > 0;
}

/// <summary>
/// Performs live Kubernetes cluster operations for the customer portal —
/// listing pods, viewing logs, restarting deployments, deleting pods, and
/// scaling. Uses the stored kubeconfig from KubernetesCluster to authenticate.
///
/// Each operation:
/// 1. Loads the deployment to find which cluster it targets
/// 2. Reads the cluster's kubeconfig
/// 3. Creates a K8s client and performs the operation
/// 4. Returns a Result so the UI can show success/failure gracefully
/// </summary>
public class KubernetesOperationsService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuditService auditService,
    KyvernoPolicyService kyvernoPolicyService,
    ILogger<KubernetesOperationsService> logger)
{
    /// <summary>
    /// Applies the tenant+environment's enabled Kyverno policies to a deployment's namespace.
    /// Runs after a successful deploy so a newly-created customer app namespace inherits the same
    /// admission policies as the rest of the environment. Best-effort: failures are logged but never
    /// fail the deployment itself, and a cluster without Kyverno simply has the manifests rejected.
    /// </summary>
    private async Task ApplyKyvernoPoliciesAsync(AppDeployment deployment, CancellationToken ct)
    {
        try
        {
            if (deployment.Cluster is null || string.IsNullOrWhiteSpace(deployment.Cluster.Kubeconfig))
                return;

            Guid tenantId;
            using (ApplicationDbContext db = dbFactory.CreateDbContext())
            {
                tenantId = await db.Apps
                    .Where(a => a.Id == deployment.AppId)
                    .Select(a => a.Customer.TenantId)
                    .FirstOrDefaultAsync(ct);
            }

            if (tenantId == Guid.Empty) return;

            List<KyvernoPolicy> policies =
                await kyvernoPolicyService.GetPoliciesAsync(tenantId, deployment.EnvironmentId, ct);
            if (policies.Count == 0) return;

            (bool ok, string output) = await kyvernoPolicyService.ApplyToNamespaceAsync(
                policies, deployment.Cluster, deployment.Namespace, ct);

            if (ok)
                logger.LogInformation(
                    "Applied {Count} Kyverno policies to namespace {Namespace} for deployment {DeploymentId}",
                    policies.Count, deployment.Namespace, deployment.Id);
            else
                logger.LogWarning(
                    "Kyverno policy apply to namespace {Namespace} for deployment {DeploymentId} reported errors: {Output}",
                    deployment.Namespace, deployment.Id, output);
        }
        catch (Exception ex)
        {
            // Never let policy application break a deployment.
            logger.LogWarning(ex,
                "Failed to apply Kyverno policies to namespace {Namespace} for deployment {DeploymentId}",
                deployment.Namespace, deployment.Id);
        }
    }

    /// <summary>
    /// Loads a deployment with its cluster attached, so we know where
    /// to connect for Kubernetes API calls.
    /// </summary>
    public async Task<AppDeployment?> GetDeploymentWithClusterAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        return await db.AppDeployments
            .Include(d => d.Cluster)
            .Include(d => d.Environment)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
    }

    /// <summary>
    /// Lists the namespace names on a cluster directly from its Kubernetes API. Used to
    /// populate the log browser's namespace picker so it works even when no logs have yet
    /// been discovered in the telemetry / Loki backend for the cluster.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> ListNamespacesAsync(
        Guid clusterId, CancellationToken ct = default)
    {
        KubernetesCluster? cluster;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        }

        if (cluster is null)
        {
            return KubernetesOperationResult<List<string>>.Failure("Cluster not found.");
        }
        if (string.IsNullOrEmpty(cluster.Kubeconfig))
        {
            return KubernetesOperationResult<List<string>>.Failure(
                "Cluster has no kubeconfig configured.");
        }

        try
        {
            using Kubernetes client = CreateClient(cluster.Kubeconfig);
            V1NamespaceList list = await client.CoreV1.ListNamespaceAsync(cancellationToken: ct);
            List<string> names = list.Items
                .Select(n => n.Metadata?.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return KubernetesOperationResult<List<string>>.Success(names);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure($"Could not list namespaces: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the governance-locked namespace for a deployment's app+environment,
    /// or null if no namespace lock is configured.
    /// </summary>
    private async Task<string?> GetGovernanceNamespaceAsync(
        AppDeployment deployment, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppEnvironment? ae = await db.AppEnvironments
            .FirstOrDefaultAsync(e =>
                e.AppId == deployment.AppId &&
                e.EnvironmentId == deployment.EnvironmentId, ct);
        return ae?.Namespace;
    }

    /// <summary>
    /// Checks that a deployment's namespace matches the governance lock (if any),
    /// and that no manifest document contains an explicit namespace pointing elsewhere.
    /// Returns a non-null error string if the check fails.
    /// </summary>
    private static string? CheckNamespaceGovernance(
        string deploymentNamespace, string? lockedNamespace, IEnumerable<string>? manifestContents = null)
    {
        if (string.IsNullOrEmpty(lockedNamespace)) return null;

        if (!string.Equals(deploymentNamespace.Trim(), lockedNamespace, StringComparison.OrdinalIgnoreCase))
            return $"Namespace governance violation: deployment targets namespace '{deploymentNamespace}' " +
                   $"but governance policy locks this environment to '{lockedNamespace}'.";

        if (manifestContents is null) return null;

        // Scan each manifest document for explicit metadata.namespace fields that
        // differ from the locked namespace. We look for lines indented at exactly
        // 2 spaces (the standard YAML indentation for top-level metadata fields).
        foreach (string content in manifestContents)
        {
            foreach (string line in content.Split('\n'))
            {
                // Match "  namespace: <value>" — metadata-level namespace field.
                // Skip kind: Namespace documents (the name field, not a target namespace).
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("namespace:", StringComparison.Ordinal)
                    && line.Length > trimmed.Length  // has leading whitespace
                    && (line.Length - trimmed.Length) == 2) // exactly 2 spaces indent
                {
                    string value = trimmed["namespace:".Length..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrEmpty(value)
                        && !string.Equals(value, lockedNamespace, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"Namespace governance violation: manifest contains 'namespace: {value}' " +
                               $"but governance policy locks this environment to '{lockedNamespace}'.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lists all pods in the deployment's namespace, filtered by the deployment
    /// name. Gives the portal a real-time view of what's actually running.
    /// </summary>
    public async Task<KubernetesOperationResult<List<PodInfo>>> GetPodsAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        // First, load the deployment and its cluster so we know where to connect.
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
        {
            return KubernetesOperationResult<List<PodInfo>>.Failure("Deployment not found.");
        }

        // The cluster needs a kubeconfig to connect.
        if (string.IsNullOrEmpty(deployment.Cluster.Kubeconfig))
        {
            return KubernetesOperationResult<List<PodInfo>>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");
        }

        try
        {
            // Build a Kubernetes client from the stored kubeconfig.
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            // List pods in the deployment's namespace.
            V1PodList podList = await client.CoreV1.ListNamespacedPodAsync(
                deployment.Namespace, cancellationToken: ct);

            List<PodInfo> pods = podList.Items.Select(pod => new PodInfo
            {
                Name = pod.Metadata.Name,
                Namespace = pod.Metadata.NamespaceProperty ?? deployment.Namespace,
                Status = pod.Status?.Phase ?? "Unknown",
                ReadyContainers = pod.Status?.ContainerStatuses?.Count(cs => cs.Ready) ?? 0,
                TotalContainers = pod.Spec?.Containers?.Count ?? 0,
                Restarts = pod.Status?.ContainerStatuses?.Sum(cs => cs.RestartCount) ?? 0,
                StartTime = pod.Status?.StartTime,
                Containers = (pod.Status?.ContainerStatuses ?? []).Select(cs => new ContainerInfo
                {
                    Name = cs.Name,
                    Image = cs.Image,
                    Ready = cs.Ready,
                    RestartCount = cs.RestartCount,
                    State = cs.State?.Running is not null ? "Running"
                        : cs.State?.Waiting is not null ? $"Waiting: {cs.State.Waiting.Reason}"
                        : cs.State?.Terminated is not null ? $"Terminated: {cs.State.Terminated.Reason}"
                        : "Unknown"
                }).ToList()
            }).ToList();

            return KubernetesOperationResult<List<PodInfo>>.Success(pods);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<PodInfo>>.Failure($"Failed to list pods: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves log output from a specific pod. Optionally targets a
    /// specific container in multi-container pods. Returns the last
    /// tailLines lines to keep output manageable.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> GetPodLogsAsync(
        Guid deploymentId,
        string podName,
        string? containerName = null,
        int tailLines = 500,
        CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
        {
            return KubernetesOperationResult<string>.Failure("Deployment not found.");
        }

        if (string.IsNullOrEmpty(deployment.Cluster.Kubeconfig))
        {
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");
        }

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            // Fetch the last N lines of logs from the pod.
            using Stream logStream = await client.CoreV1.ReadNamespacedPodLogAsync(
                podName,
                deployment.Namespace,
                container: containerName,
                tailLines: tailLines,
                cancellationToken: ct);

            using StreamReader reader = new(logStream);
            string logs = await reader.ReadToEndAsync(ct);

            return KubernetesOperationResult<string>.Success(logs);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<string>.Failure($"Failed to fetch logs: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts a Kubernetes Deployment by patching its pod template annotation
    /// with the current timestamp. This triggers a rolling restart — Kubernetes
    /// creates new pods and terminates old ones gradually.
    /// </summary>
    public async Task<KubernetesOperationResult> RestartDeploymentAsync(
        Guid deploymentId,
        string k8sDeploymentName,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
        {
            return KubernetesOperationResult.Failure("Deployment not found.");
        }

        if (string.IsNullOrEmpty(deployment.Cluster.Kubeconfig))
        {
            return KubernetesOperationResult.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");
        }

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            // Patching the pod template annotation forces a rollout restart,
            // identical to "kubectl rollout restart deployment/<name>".
            V1Patch patch = new(
                "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":" +
                $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}" +
                "}}}}",
                V1Patch.PatchType.MergePatch);

            await client.AppsV1.PatchNamespacedDeploymentAsync(
                patch, k8sDeploymentName, deployment.Namespace, cancellationToken: ct);

            logger.LogInformation(
                "Deployment {K8sName} in {Namespace} restarted by {User}",
                k8sDeploymentName, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "RestartDeployment", "Deployment",
                k8sDeploymentName, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to restart deployment {K8sName} in {Namespace}",
                k8sDeploymentName, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Failed to restart deployment: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a specific pod, which triggers Kubernetes to create a replacement
    /// (assuming the pod is managed by a Deployment/ReplicaSet). This is the
    /// equivalent of "kubectl delete pod <name>".
    /// </summary>
    public async Task<KubernetesOperationResult> DeletePodAsync(
        Guid deploymentId,
        string podName,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
        {
            return KubernetesOperationResult.Failure("Deployment not found.");
        }

        if (string.IsNullOrEmpty(deployment.Cluster.Kubeconfig))
        {
            return KubernetesOperationResult.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");
        }

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            await client.CoreV1.DeleteNamespacedPodAsync(
                podName, deployment.Namespace, cancellationToken: ct);

            logger.LogInformation(
                "Pod {PodName} in {Namespace} deleted by {User}",
                podName, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "DeletePod", "Pod",
                podName, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete pod {PodName} in {Namespace}",
                podName, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Failed to delete pod: {ex.Message}");
        }
    }

    /// <summary>
    /// Scales a Kubernetes Deployment to the specified number of replicas.
    /// Useful for scaling up during traffic spikes or scaling down to save resources.
    /// </summary>
    public async Task<KubernetesOperationResult> ScaleDeploymentAsync(
        Guid deploymentId,
        string k8sDeploymentName,
        int replicas,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
        {
            return KubernetesOperationResult.Failure("Deployment not found.");
        }

        if (string.IsNullOrEmpty(deployment.Cluster.Kubeconfig))
        {
            return KubernetesOperationResult.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");
        }

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            V1Patch patch = new(
                $"{{\"spec\":{{\"replicas\":{replicas}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.AppsV1.PatchNamespacedDeploymentAsync(
                patch, k8sDeploymentName, deployment.Namespace, cancellationToken: ct);

            logger.LogInformation(
                "Deployment {K8sName} in {Namespace} scaled to {Replicas} by {User}",
                k8sDeploymentName, deployment.Namespace, replicas, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "ScaleDeployment", "Deployment",
                k8sDeploymentName, $"replicas={replicas}", performedBy, ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to scale deployment {K8sName} in {Namespace}",
                k8sDeploymentName, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Failed to scale deployment: {ex.Message}");
        }
    }

    // ──────── Resource tree actions ────────

    /// <summary>
    /// Returns the container names for a specific pod — used to populate the
    /// container picker in the log viewer for multi-container pods.
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetPodContainersAsync(
        Guid deploymentId, string podName, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult<List<string>>.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<List<string>>.Failure(
                "Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);
            V1Pod pod = await client.CoreV1.ReadNamespacedPodAsync(
                podName, deployment.Namespace, cancellationToken: ct);
            List<string> containers = pod.Spec?.Containers?
                .Select(c => c.Name).ToList() ?? [];
            return KubernetesOperationResult<List<string>>.Success(containers);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure(
                $"Failed to read pod: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves recent Kubernetes Events for a named resource. Events surface
    /// scheduling failures, image pull errors, and readiness probe results —
    /// the same information shown by "kubectl describe".
    /// </summary>
    public async Task<KubernetesOperationResult<List<string>>> GetResourceEventsAsync(
        Guid deploymentId, string kind, string name, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult<List<string>>.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<List<string>>.Failure(
                "Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);
            var events = await client.CoreV1.ListNamespacedEventAsync(
                deployment.Namespace, cancellationToken: ct);

            List<string> lines = events.Items
                .Where(e => e.InvolvedObject?.Name == name
                         && e.InvolvedObject?.Kind == kind)
                .OrderBy(e => e.LastTimestamp ?? e.EventTime)
                .Select(e =>
                {
                    string ts = (e.LastTimestamp ?? e.EventTime)?.ToString("HH:mm:ss") ?? "?";
                    string type = e.Type ?? "Normal";
                    string reason = e.Reason ?? "";
                    string msg = e.Message ?? "";
                    return $"{ts}  {type,-8}  {reason,-24}  {msg}";
                })
                .ToList();

            return KubernetesOperationResult<List<string>>.Success(lines);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<string>>.Failure(
                $"Failed to list events: {ex.Message}");
        }
    }

    /// <summary>
    /// Scales a workload (Deployment, StatefulSet, or ReplicaSet) to the specified
    /// replica count. Unified entry point so callers don't branch on kind.
    /// </summary>
    public async Task<KubernetesOperationResult> ScaleWorkloadAsync(
        Guid deploymentId, string kind, string name, int replicas,
        string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult.Failure("Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);
            V1Patch patch = new(
                $"{{\"spec\":{{\"replicas\":{replicas}}}}}",
                V1Patch.PatchType.MergePatch);

            switch (kind)
            {
                case "Deployment":
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "ReplicaSet":
                    await client.AppsV1.PatchNamespacedReplicaSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Cannot scale {kind}.");
            }

            logger.LogInformation("{Kind} {Name} scaled to {Replicas} in {Namespace} by {User}",
                kind, name, replicas, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "ScaleWorkload", kind,
                name, $"replicas={replicas}", performedBy, ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scale failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Scale failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Restarts a workload (Deployment, StatefulSet, or DaemonSet) by patching
    /// the pod-template annotation — identical to "kubectl rollout restart".
    /// </summary>
    public async Task<KubernetesOperationResult> RestartWorkloadAsync(
        Guid deploymentId, string kind, string name,
        string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult.Failure("Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);
            V1Patch patch = new(
                "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":" +
                $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}" +
                "}}}}",
                V1Patch.PatchType.MergePatch);

            switch (kind)
            {
                case "Deployment":
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "StatefulSet":
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "DaemonSet":
                    await client.AppsV1.PatchNamespacedDaemonSetAsync(
                        patch, name, deployment.Namespace, cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Cannot restart {kind}.");
            }

            logger.LogInformation("{Kind} {Name} restarted in {Namespace} by {User}",
                kind, name, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, "RestartWorkload", kind,
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restart failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Restart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a resource by kind — Pod (triggers replacement) or Job.
    /// </summary>
    public async Task<KubernetesOperationResult> DeleteResourceAsync(
        Guid deploymentId, string kind, string name,
        string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult.Failure("Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            switch (kind)
            {
                case "Pod":
                    await client.CoreV1.DeleteNamespacedPodAsync(
                        name, deployment.Namespace, cancellationToken: ct);
                    break;
                case "Job":
                    await client.BatchV1.DeleteNamespacedJobAsync(
                        name, deployment.Namespace,
                        body: new V1DeleteOptions { PropagationPolicy = "Background" },
                        cancellationToken: ct);
                    break;
                case "ReplicaSet":
                    await client.AppsV1.DeleteNamespacedReplicaSetAsync(
                        name, deployment.Namespace,
                        body: new V1DeleteOptions { PropagationPolicy = "Background" },
                        cancellationToken: ct);
                    break;
                default:
                    return KubernetesOperationResult.Failure($"Delete not supported for {kind}.");
            }

            logger.LogInformation("{Kind} {Name} deleted in {Namespace} by {User}",
                kind, name, deployment.Namespace, performedBy ?? "system");

            await auditService.RecordAsync(deploymentId, $"Delete{kind}", kind,
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Delete failed for {Kind} {Name} in {Namespace}", kind, name, deployment.Namespace);
            return KubernetesOperationResult.Failure($"Delete failed: {ex.Message}");
        }
    }

    // ──────── Deployment-level cluster deletion ────────

    /// <summary>
    /// Removes a deployment's resources from the cluster without touching the database.
    /// For HelmChart/GitHelm deployments this runs "helm uninstall"; for Yaml/Manual/GitSync
    /// deployments it runs "kubectl delete -f" against the stored manifests. Callers should
    /// delete the DB record separately once this succeeds (or ignore the cluster result for
    /// an unregister-only flow).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteDeploymentFromClusterAsync(
        Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");

        if (string.IsNullOrWhiteSpace(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster.Kubeconfig, ct);

            HelmExecutionResult result;

            if (deployment.Type is DeploymentType.HelmChart or DeploymentType.GitHelm)
            {
                string releaseName = ToHelmReleaseName(deployment.Name);
                result = await RunCliAsync("helm",
                    $"uninstall {releaseName} --namespace {deployment.Namespace} --kubeconfig {tempKubeconfig}",
                    ct);
            }
            else
            {
                // Load stored manifests ordered by sort order.
                using ApplicationDbContext db = dbFactory.CreateDbContext();
                List<DeploymentManifest> manifests = await db.DeploymentManifests
                    .Where(m => m.DeploymentId == deploymentId)
                    .OrderBy(m => m.SortOrder)
                    .ToListAsync(ct);

                if (manifests.Count == 0)
                    return KubernetesOperationResult<string>.Failure(
                        "No manifests found. Resources may have been applied outside of EntKube and must be removed manually.");

                string combined = string.Join("\n---\n", manifests.Select(m => m.YamlContent));
                string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-manifest-{Guid.NewGuid()}.yaml");

                try
                {
                    await File.WriteAllTextAsync(tempManifest, combined, ct);
                    result = await RunCliAsync("kubectl",
                        $"delete -f {tempManifest} --kubeconfig {tempKubeconfig} --ignore-not-found",
                        ct);
                }
                finally
                {
                    if (File.Exists(tempManifest)) File.Delete(tempManifest);
                }
            }

            if (result.Success)
            {
                logger.LogInformation(
                    "Deployment {Name} ({DeploymentId}) cluster resources deleted by {User}",
                    deployment.Name, deploymentId, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "DeleteFromCluster", "Deployment",
                    deployment.Name, performedBy: performedBy, ct: ct);

                // Clear the applied-resource inventory so a later re-apply starts clean.
                using ApplicationDbContext cleanupDb = dbFactory.CreateDbContext();
                await cleanupDb.DeploymentAppliedResources
                    .Where(r => r.DeploymentId == deploymentId)
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                logger.LogWarning(
                    "Cluster deletion failed for deployment {DeploymentId}: {Output}",
                    deploymentId, result.Output);
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

    // ──────── App-level cluster deletion ────────

    /// <summary>
    /// Removes all cluster resources for every deployment belonging to an app.
    /// Best-effort: failures are collected and returned but do not stop subsequent
    /// deployments from being cleaned up. Callers should delete the DB record
    /// separately after this completes.
    /// </summary>
    public async Task<List<string>> DeleteAppFromClusterAsync(
        Guid appId, string? performedBy = null, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<Guid> deploymentIds = await db.AppDeployments
            .Where(d => d.AppId == appId)
            .Select(d => d.Id)
            .ToListAsync(ct);

        List<string> errors = [];

        foreach (Guid deploymentId in deploymentIds)
        {
            KubernetesOperationResult<string> result =
                await DeleteDeploymentFromClusterAsync(deploymentId, performedBy, ct);

            if (!result.IsSuccess && result.Error is not null)
                errors.Add(result.Error);
        }

        return errors;
    }

    // ──────── YAML / Manual apply ────────

    /// <summary>
    /// Applies all manifests for a YAML or Manual AppDeployment to the cluster
    /// using "kubectl apply". Idempotent: creates resources on first run, patches
    /// them on subsequent runs (standard server-side apply semantics).
    ///
    /// A bare Namespace manifest is prepended automatically so the target namespace
    /// is created if it doesn't exist — equivalent to Helm's --create-namespace.
    ///
    /// Manifests are applied in SortOrder sequence so foundational resources
    /// (PVCs, ConfigMaps) land before workloads (Deployments, Services).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyYamlDeploymentAsync(
        Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");

        if (!deployment.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This deployment is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        if (deployment.Cluster is null || string.IsNullOrWhiteSpace(deployment.Cluster.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");

        // Check namespace governance before touching the cluster.
        string? lockedNs = await GetGovernanceNamespaceAsync(deployment, ct);
        string? nsViolation = CheckNamespaceGovernance(deployment.Namespace, lockedNs);
        if (nsViolation is not null)
            return KubernetesOperationResult<string>.Failure(nsViolation);

        // Load manifests in apply order.
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId)
            .OrderBy(m => m.SortOrder)
            .ToListAsync(ct);

        if (manifests.Count == 0)
            return KubernetesOperationResult<string>.Failure(
                "No manifests defined for this deployment. Add YAML manifests before applying.");

        // Also check manifest content for embedded namespace violations.
        string? contentViolation = CheckNamespaceGovernance(
            deployment.Namespace, lockedNs, manifests.Select(m => m.YamlContent));
        if (contentViolation is not null)
            return KubernetesOperationResult<string>.Failure(contentViolation);

        // Prepend a Namespace manifest so the namespace is created automatically
        // if it doesn't exist yet — mirrors Helm's --create-namespace behaviour.
        // Strip any leading "---" document marker from individual manifests — some
        // Git-managed files start with "---" and the split preserves it, which would
        // produce a double "---\n---" separator in the combined output.
        string nsManifest = $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {deployment.Namespace}";
        string combined = nsManifest + "\n---\n" + string.Join("\n---\n", manifests.Select(m =>
        {
            string content = m.YamlContent.TrimStart();
            return content.StartsWith("---", StringComparison.Ordinal)
                ? content["---".Length..].TrimStart('\n', '\r')
                : content;
        }));

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-manifest-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster.Kubeconfig, ct);
            await File.WriteAllTextAsync(tempManifest, combined, ct);

            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"apply -f {tempManifest} --kubeconfig {tempKubeconfig} --namespace {deployment.Namespace}",
                ct);

            string output = result.Output;

            if (result.Success)
            {
                logger.LogInformation("YAML deployment {DeploymentId} applied to {Namespace} by {User}",
                    deploymentId, deployment.Namespace, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "ApplyYaml", "Deployment",
                    deployment.Name, performedBy: performedBy, ct: ct);

                // Ensure the (possibly brand-new) namespace inherits the environment's Kyverno policies.
                await ApplyKyvernoPoliciesAsync(deployment, ct);

                // Prune resources that were applied before but are no longer in the manifest
                // set — plain `kubectl apply` doesn't, so removed manifests orphan their
                // resources. Failure here never fails the (already successful) apply.
                try
                {
                    output += await PruneRemovedResourcesAsync(db, deployment, manifests, performedBy, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Prune after apply failed for deployment {DeploymentId}", deploymentId);
                    output += $"\n\nWarning: pruning of removed resources failed: {ex.Message}";
                }
            }
            else
            {
                logger.LogWarning("YAML apply failed for deployment {DeploymentId}: {Output}",
                    deploymentId, result.Output);
            }

            return result.Success
                ? KubernetesOperationResult<string>.Success(output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    // ──────── App route cluster sync ────────

    /// <summary>
    /// Applies the HTTPRoute (and Certificate if ClusterIssuer) for an AppDeploymentRoute
    /// to its cluster using "kubectl apply". Idempotent.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyDeploymentRouteAsync(
        Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute? dr = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.DeploymentRoutes)
                    .ThenInclude(sibDr => sibDr.AppDeployment)
                        .ThenInclude(d => d.Cluster)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct);

        if (dr is null)
            return KubernetesOperationResult<string>.Failure("Deployment route not found.");

        if (!dr.AppRoute.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This route is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        if (string.IsNullOrWhiteSpace(dr.AppDeployment?.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");

        Guid clusterId = dr.AppDeployment.ClusterId;
        string kubeconfig = dr.AppDeployment.Cluster.Kubeconfig;

        // Apply the Gateway first so the HTTPS listener + cert exist before the HTTPRoute attaches.
        KubernetesOperationResult<string> gatewayResult = await ApplyGatewayForClusterAsync(clusterId, kubeconfig, db, ct);
        if (!gatewayResult.IsSuccess)
            return gatewayResult;

        // Query ALL enabled routes for this hostname on this cluster, across every AppRoute that
        // shares the hostname. Navigation-property siblings only covers routes in the same AppRoute
        // record; routes from a different app linking the same hostname would be missed and
        // overwritten. The HTTPRoute name is hostname-derived so all must live in one resource.
        string hostname = dr.AppRoute.Hostname;
        List<AppDeploymentRoute> enabledRoutes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(r => r.AppRoute.Hostname == hostname
                     && r.AppDeployment.ClusterId == clusterId
                     && r.IsEnabled
                     && r.AppRoute.IsManaged)
            .OrderByDescending(r => r.PathPrefix.Length)
            .ThenBy(r => r.PathPrefix)
            .ToListAsync(ct);

        // For Istio clusters: apply a PERMISSIVE PeerAuthentication in every backend namespace.
        // Without it, Istio's ingress gateway uses mTLS when connecting to backend pods. If those
        // pods have no Istio sidecar injected, the TLS handshake fails → "remote connection failure".
        // PERMISSIVE allows both mTLS (sidecar present) and plaintext (no sidecar) so both work.
        // Apply to ALL namespaces involved — not just the primary — so cross-namespace backends work too.
        List<ClusterComponent> clusterComponents = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId)
            .ToListAsync(ct);
        string gatewayClass = ExternalRouteService.ResolveGatewayClass(clusterComponents);
        (_, string gwNamespace) = ExternalRouteService.ResolveGateway(clusterComponents);
        if (gatewayClass == "istio")
        {
            IEnumerable<string> backendNamespaces = enabledRoutes
                .Select(r => r.AppDeployment?.Namespace)
                .Where(n => n != null)
                .Distinct()!;
            foreach (string backendNs in backendNamespaces)
            {
                string peerAuthYaml =
                    $"apiVersion: security.istio.io/v1beta1\n" +
                    $"kind: PeerAuthentication\n" +
                    $"metadata:\n" +
                    $"  name: entkube-permissive\n" +
                    $"  namespace: {backendNs}\n" +
                    $"spec:\n" +
                    $"  mtls:\n" +
                    $"    mode: PERMISSIVE\n";
                // Ignore errors — cluster may already have PERMISSIVE policy or not need one.
                await ApplyRawYamlAsync(kubeconfig, peerAuthYaml, ct);
            }

            // DestinationRule is placed in the gateway's namespace (istio-system / root config
            // namespace) so the ingress gateway pod picks it up. A DestinationRule in a
            // non-root namespace is only guaranteed to affect traffic originating from that
            // namespace, not from gateway pods in istio-system. The FQDN host uniquely
            // identifies the target service regardless of where the rule is stored.
            foreach (AppDeploymentRoute enabledRoute in enabledRoutes)
            {
                string svc = enabledRoute.ServiceName;
                string svcNs = enabledRoute.AppDeployment?.Namespace ?? dr.AppDeployment.Namespace;
                string destinationRuleYaml =
                    $"apiVersion: networking.istio.io/v1beta1\n" +
                    $"kind: DestinationRule\n" +
                    $"metadata:\n" +
                    $"  name: entkube-disable-mtls-{svc}\n" +
                    $"  namespace: {gwNamespace}\n" +
                    $"spec:\n" +
                    $"  host: {svc}.{svcNs}.svc.cluster.local\n" +
                    $"  trafficPolicy:\n" +
                    $"    tls:\n" +
                    $"      mode: DISABLE\n";
                await ApplyRawYamlAsync(kubeconfig, destinationRuleYaml, ct);
            }
        }

        // GenerateManifestYaml includes the HTTPRoute + Certificate + ReferenceGrants for any
        // cross-namespace backendRefs (required by Gateway API when services are in other namespaces).
        string yaml = AppRouteService.GenerateManifestYaml(dr.AppRoute, enabledRoutes);
        KubernetesOperationResult<string> result = await ApplyRawYamlAsync(kubeconfig, yaml, ct);

        if (result.IsSuccess)
        {
            // Stamp all sibling routes as applied — they're all part of the same HTTPRoute resource.
            using ApplicationDbContext db2 = dbFactory.CreateDbContext();
            List<Guid> siblingIds = enabledRoutes.Select(r => r.Id).ToList();
            List<AppDeploymentRoute> toStamp = await db2.AppDeploymentRoutes
                .Where(r => siblingIds.Contains(r.Id))
                .ToListAsync(ct);
            DateTime now = DateTime.UtcNow;
            foreach (AppDeploymentRoute s in toStamp)
                s.ClusterAppliedAt = now;
            await db2.SaveChangesAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Turns EntKube management of an AppRoute on or off. Enabling reconciles the
    /// HTTPRoute immediately (no manual apply step); disabling relinquishes ownership
    /// and removes any HTTPRoute EntKube previously applied for this hostname. Used by
    /// the External Access panel's management switch (imported routes start unmanaged).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SetRouteManagementAsync(
        Guid appRouteId, bool managed, CancellationToken ct = default)
    {
        Guid? applyRouteId;
        bool wasApplied;
        using (ApplicationDbContext db = dbFactory.CreateDbContext())
        {
            AppRoute? route = await db.AppRoutes
                .Include(r => r.DeploymentRoutes)
                .FirstOrDefaultAsync(r => r.Id == appRouteId, ct);

            if (route is null)
                return KubernetesOperationResult<string>.Failure("Route not found.");

            if (route.IsManaged == managed)
                return KubernetesOperationResult<string>.Success("No change.");

            route.IsManaged = managed;

            // Any one enabled deployment route drives the combined HTTPRoute for the hostname.
            applyRouteId = route.DeploymentRoutes
                .Where(dr => dr.IsEnabled)
                .Select(dr => (Guid?)dr.Id)
                .FirstOrDefault();
            wasApplied = route.DeploymentRoutes.Any(dr => dr.ClusterAppliedAt != null);

            await db.SaveChangesAsync(ct);
        }

        if (applyRouteId is null)
            return KubernetesOperationResult<string>.Success(
                managed ? "Management enabled. Add a deployment route to publish it." : "Management disabled.");

        // Enabling → reconcile now (IsManaged is already true so the apply queries include it).
        if (managed)
            return await ApplyDeploymentRouteAsync(applyRouteId.Value, ct);

        // Disabling → only remove the HTTPRoute if EntKube had actually applied one.
        if (wasApplied)
            return await DeleteDeploymentRouteFromClusterAsync(applyRouteId.Value, ct);

        return KubernetesOperationResult<string>.Success("Management disabled.");
    }

    /// <summary>
    /// Deletes the HTTPRoute (and Certificate if ClusterIssuer) for an AppDeploymentRoute
    /// from its cluster. Should be called before deleting the route from the database.
    /// Uses --ignore-not-found so it is safe to call even if the resource was never applied.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteDeploymentRouteFromClusterAsync(
        Guid deploymentRouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppDeploymentRoute? dr = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
                .ThenInclude(ar => ar.DeploymentRoutes)
                    .ThenInclude(sibDr => sibDr.AppDeployment)
                        .ThenInclude(d => d.Cluster)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(r => r.Id == deploymentRouteId, ct);

        if (dr is null)
            return KubernetesOperationResult<string>.Failure("Deployment route not found.");

        if (string.IsNullOrWhiteSpace(dr.AppDeployment?.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured.");

        string ns = dr.AppDeployment.Namespace;
        string kubeconfig = dr.AppDeployment.Cluster.Kubeconfig;
        Guid clusterId = dr.AppDeployment.ClusterId;

        // Query remaining routes for this hostname across all AppRoutes on this cluster.
        string hostname = dr.AppRoute.Hostname;
        List<AppDeploymentRoute> remainingRoutes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .Where(r => r.AppRoute.Hostname == hostname
                     && r.AppDeployment.ClusterId == clusterId
                     && r.Id != deploymentRouteId
                     && r.IsEnabled
                     && r.AppRoute.IsManaged)
            .OrderByDescending(r => r.PathPrefix.Length)
            .ThenBy(r => r.PathPrefix)
            .ToListAsync(ct);

        if (remainingRoutes.Count > 0)
        {
            // Re-apply HTTPRoute + ReferenceGrants with only the remaining rules so other deployments stay live.
            // Gateway stays unchanged — the hostname listener remains for the remaining routes.
            string yaml = AppRouteService.GenerateManifestYaml(dr.AppRoute, remainingRoutes);
            return await ApplyRawYamlAsync(kubeconfig, yaml, ct);
        }

        // Last deployment route for this hostname — delete the HTTPRoute and update the Gateway
        // to remove the HTTPS listener for this hostname (and its Certificate in cert-manager).
        string routeName = ExternalRouteService.ToListenerName(dr.AppRoute.Hostname) + "-route";
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);

            HelmExecutionResult deleteResult = await RunCliAsync(
                "kubectl",
                $"delete httproute {routeName} --namespace {ns} --kubeconfig {tempKubeconfig} --ignore-not-found",
                ct);

            // Regenerate the Gateway without this hostname so the HTTPS listener is removed.
            // The Certificate in cert-manager namespace is left in place (cert-manager cleans it up
            // if needed, and deleting it here would break any in-flight ACME challenges).
            await ApplyGatewayForClusterAsync(clusterId, kubeconfig, db, ct);

            return deleteResult.Success
                ? KubernetesOperationResult<string>.Success(deleteResult.Output)
                : KubernetesOperationResult<string>.Failure(deleteResult.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    /// <summary>
    /// Regenerates and applies the full Gateway manifest for a cluster, combining
    /// all ExternalRoutes and enabled AppRoutes so every hostname has an HTTPS listener
    /// with a certificateRefs entry. This must be called whenever an AppRoute hostname
    /// is added or removed so the Gateway tracks the current set of exposed hostnames.
    /// </summary>
    private async Task<KubernetesOperationResult<string>> ApplyGatewayForClusterAsync(
        Guid clusterId, string kubeconfig, ApplicationDbContext db, CancellationToken ct)
    {
        KubernetesCluster? cluster = await db.KubernetesClusters
            .Include(c => c.Components)
                .ThenInclude(comp => comp.ExternalRoutes)
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
            return KubernetesOperationResult<string>.Failure("Cluster not found.");

        (string gatewayName, string gatewayNamespace) = ExternalRouteService.ResolveGateway(cluster.Components);

        List<ExternalRoute> externalRoutes = cluster.Components
            .SelectMany(c => c.ExternalRoutes.Select(r => { r.Component = c; return r; }))
            .ToList();

        List<AppRoute> appRoutes = await db.AppRoutes
            .Where(r => r.IsEnabled && r.IsManaged && r.DeploymentRoutes.Any(dr =>
                dr.IsEnabled && dr.AppDeployment.ClusterId == clusterId))
            .ToListAsync(ct);

        if (externalRoutes.Count == 0 && appRoutes.Count == 0)
            return KubernetesOperationResult<string>.Success("No routes to apply.");

        string gatewayClass = ExternalRouteService.ResolveGatewayClass(cluster.Components);
        string yaml = ExternalRouteService.GenerateGatewayYaml(gatewayName, gatewayNamespace, externalRoutes, appRoutes, gatewayClass: gatewayClass);
        return await ApplyRawYamlAsync(kubeconfig, yaml, ct);
    }

    // ──────── Raw L4 (TCP/UDP) routes (dedicated Istio L4 gateway) ────────

    /// <summary>
    /// Applies a raw L4 route (TCP or UDP): (re)generates the dedicated L4 Gateway (auto-provisions
    /// its own LoadBalancer + external IP, one listener per port), disables forced mTLS to the backend
    /// for TCP, then applies the TCPRoute/UDPRoute. On success returns the resolved external endpoint
    /// (LoadBalancer address:port).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> ApplyL4RouteAsync(
        Guid l4RouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppL4Route? route = await db.AppL4Routes
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(r => r.Id == l4RouteId, ct);

        if (route is null)
            return KubernetesOperationResult<string>.Failure("L4 route not found.");

        if (!route.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This route is observed only. Enable management to let EntKube apply it.");

        if (string.IsNullOrWhiteSpace(route.AppDeployment?.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");

        Guid clusterId = route.AppDeployment.ClusterId;
        string kubeconfig = route.AppDeployment.Cluster.Kubeconfig;
        string backendNs = route.AppDeployment.Namespace;
        string proto = route.Protocol.ToString().ToUpperInvariant();

        List<ClusterComponent> components = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId)
            .ToListAsync(ct);

        if (ExternalRouteService.ResolveGatewayClass(components) != "istio")
            return KubernetesOperationResult<string>.Failure(
                "L4 (TCP/UDP) routes require an Istio ingress gateway on this cluster.");

        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);

        // Preflight: the TCPRoute/UDPRoute CRD ships only in the experimental channel of Gateway API.
        // Without it kubectl apply fails obscurely — surface a clear, actionable error instead.
        string crdName = AppL4RouteService.RouteCrdName(route.Protocol);
        if (!await CrdInstalledAsync(kubeconfig, crdName, ct))
            return KubernetesOperationResult<string>.Failure(
                $"The {AppL4RouteService.RouteKind(route.Protocol)} CRD ({crdName}) is not installed on this cluster. " +
                "Install the experimental channel of the Gateway API CRDs to enable L4 routing.");

        // Apply the dedicated L4 Gateway first so the listener exists before the route attaches.
        KubernetesOperationResult<string> gatewayResult = await ApplyL4GatewayForClusterAsync(clusterId, kubeconfig, db, null, ct);
        if (!gatewayResult.IsSuccess)
            return gatewayResult;

        // TCP only: the ingress gateway would otherwise force mTLS to the backend, breaking
        // sidecar-less pods. PERMISSIVE in the backend ns + a DestinationRule (tls DISABLE) in the
        // gateway's root config namespace let the gateway reach the pod plainly. Istio mTLS does not
        // apply to UDP, so this step is skipped for UDP routes.
        if (route.Protocol == L4Protocol.Tcp)
        {
            string peerAuthYaml =
                $"apiVersion: security.istio.io/v1beta1\n" +
                $"kind: PeerAuthentication\n" +
                $"metadata:\n" +
                $"  name: entkube-permissive\n" +
                $"  namespace: {backendNs}\n" +
                $"spec:\n" +
                $"  mtls:\n" +
                $"    mode: PERMISSIVE\n";
            await ApplyRawYamlAsync(kubeconfig, peerAuthYaml, ct);

            string destinationRuleYaml =
                $"apiVersion: networking.istio.io/v1beta1\n" +
                $"kind: DestinationRule\n" +
                $"metadata:\n" +
                $"  name: entkube-disable-mtls-{route.ServiceName}\n" +
                $"  namespace: {gwNamespace}\n" +
                $"spec:\n" +
                $"  host: {route.ServiceName}.{backendNs}.svc.cluster.local\n" +
                $"  trafficPolicy:\n" +
                $"    tls:\n" +
                $"      mode: DISABLE\n";
            await ApplyRawYamlAsync(kubeconfig, destinationRuleYaml, ct);
        }

        string yaml = AppL4RouteService.GenerateRouteYaml(route, backendNs);
        KubernetesOperationResult<string> result = await ApplyRawYamlAsync(kubeconfig, yaml, ct);
        if (!result.IsSuccess)
            return result;

        route.ClusterAppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        string? address = await GetL4GatewayAddressAsync(kubeconfig, gwNamespace, ct);
        string endpoint = address is not null
            ? $"{address}:{route.ExternalPort}/{proto}"
            : $"(LoadBalancer address pending):{route.ExternalPort}/{proto}";
        return KubernetesOperationResult<string>.Success($"{proto} route applied. External endpoint: {endpoint}");
    }

    /// <summary>
    /// Deletes the TCPRoute/UDPRoute for a route from its cluster and regenerates the dedicated L4
    /// Gateway without this route's listener (removing the Gateway entirely — and releasing its
    /// LoadBalancer — when no L4 routes remain). Call before removing the route from the database.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> DeleteL4RouteFromClusterAsync(
        Guid l4RouteId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        AppL4Route? route = await db.AppL4Routes
            .Include(r => r.AppDeployment)
                .ThenInclude(d => d.Cluster)
            .FirstOrDefaultAsync(r => r.Id == l4RouteId, ct);

        if (route is null)
            return KubernetesOperationResult<string>.Failure("L4 route not found.");

        if (string.IsNullOrWhiteSpace(route.AppDeployment?.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure("Cluster has no kubeconfig configured.");

        string kubeconfig = route.AppDeployment.Cluster.Kubeconfig;
        string ns = route.AppDeployment.Namespace;
        Guid clusterId = route.AppDeployment.ClusterId;

        await DeleteResourceAsync(
            kubeconfig, AppL4RouteService.RouteResourceType(route.Protocol),
            AppL4RouteService.RouteResourceName(route), ns, ct);

        // Regenerate the gateway from the remaining routes (this one excluded — it is still in the DB).
        return await ApplyL4GatewayForClusterAsync(clusterId, kubeconfig, db, l4RouteId, ct);
    }

    /// <summary>
    /// Regenerates and applies the dedicated L4 Gateway for a cluster from all enabled, managed L4
    /// routes (optionally excluding one being deleted). When no ports remain the Gateway is deleted so
    /// its auto-provisioned LoadBalancer is released.
    /// </summary>
    private async Task<KubernetesOperationResult<string>> ApplyL4GatewayForClusterAsync(
        Guid clusterId, string kubeconfig, ApplicationDbContext db, Guid? excludeRouteId, CancellationToken ct)
    {
        List<ClusterComponent> components = await db.ClusterComponents
            .Where(c => c.ClusterId == clusterId)
            .ToListAsync(ct);

        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(components);
        string gatewayClass = ExternalRouteService.ResolveGatewayClass(components);

        List<AppL4Route> routes = await db.AppL4Routes
            .Where(r => r.IsEnabled && r.IsManaged
                     && r.AppDeployment.ClusterId == clusterId
                     && (excludeRouteId == null || r.Id != excludeRouteId))
            .ToListAsync(ct);

        string yaml = ExternalRouteService.GenerateL4GatewayYaml(gwNamespace, routes, gatewayClass);
        if (string.IsNullOrEmpty(yaml))
        {
            // No L4 ports left — remove the dedicated gateway so its LoadBalancer is freed.
            await DeleteResourceAsync(kubeconfig, "gateway.gateway.networking.k8s.io",
                ExternalRouteService.L4GatewayName, gwNamespace, ct);
            return KubernetesOperationResult<string>.Success("No L4 routes remain — dedicated L4 gateway removed.");
        }

        return await ApplyRawYamlAsync(kubeconfig, yaml, ct);
    }

    /// <summary>Returns the dedicated L4 gateway's external address (LoadBalancer IP/hostname), or null if not yet assigned.</summary>
    public async Task<string?> GetL4EndpointAddressAsync(Guid clusterId, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        KubernetesCluster? cluster = await db.KubernetesClusters
            .Include(c => c.Components)
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (string.IsNullOrWhiteSpace(cluster?.Kubeconfig)) return null;

        (_, string gwNamespace) = ExternalRouteService.ResolveL4Gateway(cluster.Components);
        return await GetL4GatewayAddressAsync(cluster.Kubeconfig, gwNamespace, ct);
    }

    private async Task<string?> GetL4GatewayAddressAsync(string kubeconfig, string gwNamespace, CancellationToken ct)
    {
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);
            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"get gateway {ExternalRouteService.L4GatewayName} --namespace {gwNamespace} " +
                $"--kubeconfig {tempKubeconfig} -o jsonpath={{.status.addresses[0].value}}",
                ct);
            string addr = result.Output.Trim();
            return result.Success && !string.IsNullOrEmpty(addr) ? addr : null;
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    private async Task<bool> CrdInstalledAsync(string kubeconfig, string crdName, CancellationToken ct)
    {
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);
            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"get crd {crdName} --kubeconfig {tempKubeconfig} --ignore-not-found -o name",
                ct);
            return result.Success && result.Output.Contains(crdName);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    private async Task DeleteResourceAsync(string kubeconfig, string kind, string name, string ns, CancellationToken ct)
    {
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);
            await RunCliAsync(
                "kubectl",
                $"delete {kind} {name} --namespace {ns} --kubeconfig {tempKubeconfig} --ignore-not-found",
                ct);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    /// <summary>
    /// Diffs the just-applied manifest set against the deployment's applied-resource
    /// inventory, deletes the resources that were removed (honoring keep-annotations
    /// and never touching Namespaces), then rewrites the inventory to the new set.
    /// Returns a human-readable summary to append to the apply output ("" when nothing pruned).
    /// </summary>
    private async Task<string> PruneRemovedResourcesAsync(
        ApplicationDbContext db, AppDeployment deployment, List<DeploymentManifest> manifests,
        string? performedBy, CancellationToken ct)
    {
        // Desired set from the current manifests (version-agnostic identity).
        Dictionary<(string, string, string?, string), ManifestResourceRef> newSet = new();
        foreach (DeploymentManifest m in manifests)
        {
            foreach (ManifestResourceRef r in ManifestResourceParser.Parse(m.YamlContent, deployment.Namespace))
            {
                newSet[r.Key] = r;
            }
        }

        List<DeploymentAppliedResource> previous = await db.DeploymentAppliedResources
            .Where(r => r.DeploymentId == deployment.Id)
            .ToListAsync(ct);

        // Prune what we recorded applying that is prunable, not a Namespace, and gone from the new set.
        List<DeploymentAppliedResource> toPrune = previous
            .Where(p => p.Prunable
                && !string.Equals(p.Kind, "Namespace", StringComparison.Ordinal)
                && !newSet.ContainsKey((p.Group, p.Kind, p.Namespace, p.Name)))
            .ToList();

        List<string> pruned = [];
        List<string> failed = [];
        string kubeconfig = deployment.Cluster!.Kubeconfig!;
        foreach (DeploymentAppliedResource r in toPrune)
        {
            string label = $"{r.Kind}/{r.Name}" + (r.Namespace is null ? "" : $" (ns: {r.Namespace})");
            if (await DeletePrunedResourceAsync(kubeconfig, r, ct))
            {
                pruned.Add(label);
                await auditService.RecordAsync(deployment.Id, "PruneResource", r.Kind, r.Name,
                    performedBy: performedBy, ct: ct);
            }
            else
            {
                failed.Add(label);
            }
        }

        // Rewrite the inventory to reflect exactly what is now applied.
        db.DeploymentAppliedResources.RemoveRange(previous);
        foreach (ManifestResourceRef r in newSet.Values)
        {
            db.DeploymentAppliedResources.Add(new DeploymentAppliedResource
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
                Group = r.Group,
                Version = r.Version,
                Kind = r.Kind,
                Name = r.Name,
                Namespace = r.Namespace,
                Prunable = r.Prunable
            });
        }
        await db.SaveChangesAsync(ct);

        string summary = "";
        if (pruned.Count > 0)
        {
            summary += $"\n\nPruned {pruned.Count} orphaned resource(s): {string.Join(", ", pruned)}";
        }
        if (failed.Count > 0)
        {
            summary += $"\n\nFailed to prune {failed.Count} resource(s): {string.Join(", ", failed)}";
        }
        return summary;
    }

    /// <summary>Deletes a single pruned resource by kind(.group)/name, scoping to its namespace only when it has one.</summary>
    private async Task<bool> DeletePrunedResourceAsync(string kubeconfig, DeploymentAppliedResource r, CancellationToken ct)
    {
        string target = string.IsNullOrEmpty(r.Group) ? r.Kind : $"{r.Kind}.{r.Group}";
        string nsArg = string.IsNullOrEmpty(r.Namespace) ? "" : $" --namespace {r.Namespace}";
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);
            HelmExecutionResult res = await RunCliAsync(
                "kubectl",
                $"delete {target} {r.Name}{nsArg} --kubeconfig {tempKubeconfig} --ignore-not-found",
                ct);
            return res.Success;
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    private async Task<KubernetesOperationResult<string>> ApplyRawYamlAsync(
        string kubeconfig, string yaml, CancellationToken ct)
    {
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string tempManifest = Path.Combine(Path.GetTempPath(), $"entkube-manifest-{Guid.NewGuid()}.yaml");

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, kubeconfig, ct);
            await File.WriteAllTextAsync(tempManifest, yaml, ct);

            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"apply -f {tempManifest} --kubeconfig {tempKubeconfig}",
                ct);

            return result.Success
                ? KubernetesOperationResult<string>.Success(result.Output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (File.Exists(tempManifest)) File.Delete(tempManifest);
        }
    }

    // ──────── Helm install / upgrade ────────

    /// <summary>
    /// Runs "helm upgrade --install" for a HelmChart AppDeployment.
    /// Idempotent: installs on first run, upgrades on subsequent ones.
    /// Always passes --create-namespace so the target namespace is created
    /// automatically if it doesn't exist yet.
    ///
    /// If a HelmRepoUrl is configured the repo is registered (helm repo add)
    /// and updated before the install. The release name is derived from the
    /// deployment name, sanitised to Helm's DNS-compatible format.
    ///
    /// Returns the combined stdout + stderr from the helm process so the UI
    /// can show exactly what Helm reported.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> HelmInstallOrUpgradeAsync(
        Guid deploymentId, string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");

        if (!deployment.IsManaged)
            return KubernetesOperationResult<string>.Failure(
                "This deployment is observed only (imported / managed by ArgoCD or Flux). Enable management to let EntKube apply it.");

        if (deployment.Cluster is null || string.IsNullOrWhiteSpace(deployment.Cluster.Kubeconfig))
            return KubernetesOperationResult<string>.Failure(
                "Cluster has no kubeconfig configured. Upload a kubeconfig to enable cluster operations.");

        // Check namespace governance before touching the cluster.
        string? lockedNsHelm = await GetGovernanceNamespaceAsync(deployment, ct);
        string? nsViolationHelm = CheckNamespaceGovernance(deployment.Namespace, lockedNsHelm);
        if (nsViolationHelm is not null)
            return KubernetesOperationResult<string>.Failure(nsViolationHelm);

        if (string.IsNullOrWhiteSpace(deployment.HelmChartName))
            return KubernetesOperationResult<string>.Failure(
                "No Helm chart name configured. Set the chart name in the deployment settings before installing.");

        string releaseName = ToHelmReleaseName(deployment.Name);
        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        string? tempValues = null;

        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster.Kubeconfig, ct);

            // Register and update the Helm repo if a URL is given.
            string chartRef = deployment.HelmChartName;

            if (!string.IsNullOrWhiteSpace(deployment.HelmRepoUrl))
            {
                if (deployment.HelmRepoUrl.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
                {
                    // OCI registries cannot be added with "helm repo add" — use the full URI directly.
                    chartRef = $"{deployment.HelmRepoUrl.TrimEnd('/')}/{deployment.HelmChartName}";
                }
                else
                {
                    string repoAlias = $"entkube-{releaseName}";

                    await RunCliAsync("helm",
                        $"repo add {repoAlias} {deployment.HelmRepoUrl} --force-update --kubeconfig {tempKubeconfig}", ct);
                    await RunCliAsync("helm",
                        $"repo update {repoAlias} --kubeconfig {tempKubeconfig}", ct);

                    chartRef = $"{repoAlias}/{deployment.HelmChartName}";
                }
            }

            // Build the main helm upgrade --install command.
            List<string> args =
            [
                "upgrade", "--install",
                releaseName,
                chartRef,
                "--namespace", deployment.Namespace,
                "--create-namespace",
            ];

            if (!string.IsNullOrWhiteSpace(deployment.HelmChartVersion))
            {
                args.Add("--version");
                args.Add(deployment.HelmChartVersion);
            }

            if (!string.IsNullOrWhiteSpace(deployment.HelmValues))
            {
                tempValues = Path.Combine(Path.GetTempPath(), $"entkube-values-{Guid.NewGuid()}.yaml");
                await File.WriteAllTextAsync(tempValues, deployment.HelmValues, ct);
                args.Add("--values");
                args.Add(tempValues);
            }

            args.Add("--kubeconfig");
            args.Add(tempKubeconfig);
            args.Add("--wait");
            args.Add("--timeout");
            args.Add("10m0s");

            HelmExecutionResult result = await RunCliAsync("helm", string.Join(" ", args), ct);

            if (result.Success)
            {
                logger.LogInformation(
                    "Helm install/upgrade {Chart} {Version} to {Namespace} succeeded for deployment {DeploymentId} by {User}",
                    deployment.HelmChartName, deployment.HelmChartVersion,
                    deployment.Namespace, deploymentId, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "HelmInstallOrUpgrade", "HelmRelease",
                    deployment.Name, $"{deployment.HelmChartName}@{deployment.HelmChartVersion}", performedBy, ct);

                // Ensure the (possibly brand-new) namespace inherits the environment's Kyverno policies.
                await ApplyKyvernoPoliciesAsync(deployment, ct);
            }
            else
            {
                logger.LogWarning(
                    "Helm install/upgrade failed for deployment {DeploymentId}: {Output}",
                    deploymentId, result.Output);
            }

            return result.Success
                ? KubernetesOperationResult<string>.Success(result.Output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
            if (tempValues is not null && File.Exists(tempValues)) File.Delete(tempValues);
        }
    }

    /// <summary>
    /// Queries the cluster live and returns a proper ownership tree of resources
    /// found in the deployment's namespace. OwnerReferences are used to wire up
    /// parent-child relationships (e.g. Deployment → ReplicaSet → Pod).
    /// Each resource type is fetched independently so a missing API group
    /// (e.g. no Ingress controller) doesn't block the others.
    /// </summary>
    public async Task<KubernetesOperationResult<List<DeploymentResource>>> GetLiveResourcesAsync(
        Guid deploymentId, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);

        if (deployment is null)
            return KubernetesOperationResult<List<DeploymentResource>>.Failure("Deployment not found.");

        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<List<DeploymentResource>>.Failure(
                "Cluster has no kubeconfig configured.");

        string ns = deployment.Namespace;

        // k8s UID → our resource node (for O(1) parent lookup during tree wiring)
        Dictionary<string, DeploymentResource> byUid = new();
        // Resources whose OwnerReferences need to be resolved after all types are fetched.
        List<(DeploymentResource Node, string[] OwnerUids)> pendingWire = [];
        // HTTPRoute → Service links parsed from spec.rules[].backendRefs, wired after all nodes exist.
        List<(DeploymentResource HttpRoute, string ServiceName)> httpRouteBackends = [];

        // Create a resource node, record its UID, and register its owner refs for later wiring.
        DeploymentResource MakeNode(string? uid, IList<V1OwnerReference>? owners,
            string group, string version, string kind, string name,
            HealthStatus health, string? message, DateTime? createdAt = null)
        {
            DeploymentResource r = new()
            {
                Id = !string.IsNullOrEmpty(uid) && Guid.TryParse(uid, out Guid k8sGuid)
                    ? k8sGuid : Guid.NewGuid(),
                DeploymentId = deploymentId,
                Group = group, Version = version, Kind = kind,
                Name = name, Namespace = ns,
                SyncStatus = SyncStatus.Synced,
                HealthStatus = health,
                StatusMessage = message,
                KubernetesCreatedAt = createdAt,
                ChildResources = []
            };

            if (!string.IsNullOrEmpty(uid))
                byUid[uid] = r;

            string[] ownerUids = owners?
                .Select(o => o.Uid).Where(u => !string.IsNullOrEmpty(u)).ToArray() ?? [];

            if (ownerUids.Length > 0)
                pendingWire.Add((r, ownerUids));

            return r;
        }

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);

            // ── Deployments ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1DeploymentList list = await client.AppsV1.ListNamespacedDeploymentAsync(ns, cancellationToken: ct);
                foreach (V1Deployment d in list.Items)
                {
                    int desired = d.Spec?.Replicas ?? 0;
                    int ready   = d.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(d.Metadata.Uid, d.Metadata.OwnerReferences,
                        "apps", "v1", "Deployment", d.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── StatefulSets ─────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1StatefulSetList list = await client.AppsV1.ListNamespacedStatefulSetAsync(ns, cancellationToken: ct);
                foreach (V1StatefulSet s in list.Items)
                {
                    int desired = s.Spec?.Replicas ?? 0;
                    int ready   = s.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(s.Metadata.Uid, s.Metadata.OwnerReferences,
                        "apps", "v1", "StatefulSet", s.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── DaemonSets ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1DaemonSetList list = await client.AppsV1.ListNamespacedDaemonSetAsync(ns, cancellationToken: ct);
                foreach (V1DaemonSet ds in list.Items)
                {
                    int desired = ds.Status?.DesiredNumberScheduled ?? 0;
                    int ready   = ds.Status?.NumberReady ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(ds.Metadata.Uid, ds.Metadata.OwnerReferences,
                        "apps", "v1", "DaemonSet", ds.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── ReplicaSets (ALL — including 0-replica old ones for tree completeness) ─
            await TryFetch(async () =>
            {
                V1ReplicaSetList list = await client.AppsV1.ListNamespacedReplicaSetAsync(ns, cancellationToken: ct);
                foreach (V1ReplicaSet rs in list.Items)
                {
                    int desired = rs.Spec?.Replicas ?? 0;
                    int ready   = rs.Status?.ReadyReplicas ?? 0;
                    HealthStatus h = desired == 0 ? HealthStatus.Suspended
                        : ready >= desired ? HealthStatus.Healthy
                        : ready > 0        ? HealthStatus.Degraded
                                           : HealthStatus.Progressing;
                    MakeNode(rs.Metadata.Uid, rs.Metadata.OwnerReferences,
                        "apps", "v1", "ReplicaSet", rs.Metadata.Name, h, $"{ready}/{desired} ready");
                }
            });

            // ── Services ────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1ServiceList list = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct);
                foreach (V1Service svc in list.Items)
                {
                    MakeNode(svc.Metadata.Uid, svc.Metadata.OwnerReferences,
                        "", "v1", "Service", svc.Metadata.Name, HealthStatus.Healthy,
                        $"{svc.Spec?.Type ?? "ClusterIP"}  {svc.Spec?.ClusterIP}");
                }
            });

            // ── Ingresses ───────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1IngressList list = await client.NetworkingV1.ListNamespacedIngressAsync(ns, cancellationToken: ct);
                foreach (V1Ingress ing in list.Items)
                {
                    string? lb = ing.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Ip
                              ?? ing.Status?.LoadBalancer?.Ingress?.FirstOrDefault()?.Hostname;
                    string hosts = string.Join(", ", (ing.Spec?.Rules?.Select(r => r.Host) ?? [])
                        .Where(h => !string.IsNullOrEmpty(h))!);
                    MakeNode(ing.Metadata.Uid, ing.Metadata.OwnerReferences,
                        "networking.k8s.io", "v1", "Ingress", ing.Metadata.Name, HealthStatus.Healthy,
                        lb ?? hosts);
                }
            });

            // ── HTTPRoutes (Gateway API) ─────────────────────────────────
            await TryFetch(async () =>
            {
                object raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                    "gateway.networking.k8s.io", "v1", ns, "httproutes", cancellationToken: ct);

                string json = JsonSerializer.Serialize(raw);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out JsonElement items)) return;

                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("metadata", out JsonElement meta)) continue;
                    string? name = meta.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? uid  = meta.TryGetProperty("uid",  out JsonElement u) ? u.GetString() : null;
                    if (name is null) continue;

                    string? hostnames = null;
                    List<string> backendSvcNames = [];

                    if (item.TryGetProperty("spec", out JsonElement itemSpec))
                    {
                        if (itemSpec.TryGetProperty("hostnames", out JsonElement hh))
                            hostnames = string.Join(", ", hh.EnumerateArray()
                                .Select(x => x.GetString()).OfType<string>());

                        if (itemSpec.TryGetProperty("rules", out JsonElement rules))
                        {
                            foreach (JsonElement rule in rules.EnumerateArray())
                            {
                                if (!rule.TryGetProperty("backendRefs", out JsonElement backendRefs)) continue;
                                foreach (JsonElement backendRef in backendRefs.EnumerateArray())
                                {
                                    string? svcName = backendRef.TryGetProperty("name", out JsonElement nm)
                                        ? nm.GetString() : null;
                                    if (!string.IsNullOrEmpty(svcName))
                                        backendSvcNames.Add(svcName);
                                }
                            }
                        }
                    }

                    // Collect owner UIDs if present
                    List<V1OwnerReference>? ownerRefs = null;
                    if (meta.TryGetProperty("ownerReferences", out JsonElement ownersEl))
                    {
                        ownerRefs = ownersEl.EnumerateArray()
                            .Select(o => new V1OwnerReference
                            {
                                Uid = o.TryGetProperty("uid", out JsonElement oUid) ? oUid.GetString() : null
                            })
                            .ToList();
                    }

                    DeploymentResource routeNode = MakeNode(uid, ownerRefs,
                        "gateway.networking.k8s.io", "v1", "HTTPRoute", name,
                        HealthStatus.Healthy, hostnames);

                    foreach (string svcName in backendSvcNames)
                        httpRouteBackends.Add((routeNode, svcName));
                }
            }, "HTTPRoute");

            // ── PersistentVolumeClaims ───────────────────────────────────
            await TryFetch(async () =>
            {
                V1PersistentVolumeClaimList list = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(ns, cancellationToken: ct);
                foreach (V1PersistentVolumeClaim pvc in list.Items)
                {
                    HealthStatus h = pvc.Status?.Phase == "Bound"   ? HealthStatus.Healthy
                                   : pvc.Status?.Phase == "Pending" ? HealthStatus.Progressing
                                                                     : HealthStatus.Degraded;
                    string? size = pvc.Spec?.Resources?.Requests is { } req &&
                                   req.TryGetValue("storage", out ResourceQuantity? qty)
                                   ? qty?.ToString() : null;
                    MakeNode(pvc.Metadata.Uid, pvc.Metadata.OwnerReferences,
                        "", "v1", "PersistentVolumeClaim", pvc.Metadata.Name, h,
                        string.Join("  ", new[] { pvc.Status?.Phase, size }.Where(s => s != null)));
                }
            });

            // ── ConfigMaps (exclude system-generated and owned ones) ─────
            await TryFetch(async () =>
            {
                V1ConfigMapList list = await client.CoreV1.ListNamespacedConfigMapAsync(ns, cancellationToken: ct);
                foreach (V1ConfigMap cm in list.Items.Where(c =>
                    c.Metadata.Name != "kube-root-ca.crt" &&
                    c.Metadata.OwnerReferences?.Any() != true))
                {
                    MakeNode(cm.Metadata.Uid, null,
                        "", "v1", "ConfigMap", cm.Metadata.Name, HealthStatus.Healthy, null);
                }
            });

            // ── Secrets (exclude service-account tokens and system-owned) ────────
            await TryFetch(async () =>
            {
                V1SecretList list = await client.CoreV1.ListNamespacedSecretAsync(ns, cancellationToken: ct);
                foreach (V1Secret secret in list.Items.Where(s =>
                    s.Metadata.OwnerReferences?.Any() != true &&
                    s.Type != "kubernetes.io/service-account-token" &&
                    s.Type != "bootstrap.kubernetes.io/token"))
                {
                    string? msg = string.IsNullOrEmpty(secret.Type) || secret.Type == "Opaque"
                        ? null : secret.Type;
                    MakeNode(secret.Metadata.Uid, null,
                        "", "v1", "Secret", secret.Metadata.Name, HealthStatus.Healthy, msg);
                }
            });

            // ── Jobs ─────────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1JobList list = await client.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: ct);
                foreach (V1Job job in list.Items)
                {
                    bool active    = (job.Status?.Active    ?? 0) > 0;
                    bool succeeded = (job.Status?.Succeeded ?? 0) > 0;
                    bool failed    = (job.Status?.Failed    ?? 0) > 0;
                    HealthStatus h = failed    ? HealthStatus.Degraded
                                   : active    ? HealthStatus.Progressing
                                   : succeeded ? HealthStatus.Healthy
                                               : HealthStatus.Suspended;
                    MakeNode(job.Metadata.Uid, job.Metadata.OwnerReferences,
                        "batch", "v1", "Job", job.Metadata.Name, h,
                        $"✓{job.Status?.Succeeded ?? 0}  ✗{job.Status?.Failed ?? 0}");
                }
            });

            // ── CronJobs ──────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1CronJobList list = await client.BatchV1.ListNamespacedCronJobAsync(ns, cancellationToken: ct);
                foreach (V1CronJob cj in list.Items)
                {
                    MakeNode(cj.Metadata.Uid, null,
                        "batch", "v1", "CronJob", cj.Metadata.Name,
                        cj.Spec?.Suspend == true ? HealthStatus.Suspended : HealthStatus.Healthy,
                        cj.Spec?.Schedule);
                }
            });

            // ── Pods ──────────────────────────────────────────────────────
            await TryFetch(async () =>
            {
                V1PodList list = await client.CoreV1.ListNamespacedPodAsync(ns, cancellationToken: ct);
                foreach (V1Pod pod in list.Items)
                {
                    int ready = pod.Status?.ContainerStatuses?.Count(cs => cs.Ready) ?? 0;
                    int total = pod.Spec?.Containers?.Count ?? 0;
                    HealthStatus h = pod.Status?.Phase switch
                    {
                        "Running"   => pod.Status.ContainerStatuses?.All(cs => cs.Ready) == true
                                        ? HealthStatus.Healthy : HealthStatus.Degraded,
                        "Pending"   => HealthStatus.Progressing,
                        "Succeeded" => HealthStatus.Healthy,
                        "Failed"    => HealthStatus.Degraded,
                        _           => HealthStatus.Unknown
                    };
                    MakeNode(pod.Metadata.Uid, pod.Metadata.OwnerReferences,
                        "", "v1", "Pod", pod.Metadata.Name, h,
                        $"{pod.Status?.Phase}  {ready}/{total}",
                        pod.Metadata?.CreationTimestamp);
                }
            });

            // ── Wire parent-child using OwnerReferences ───────────────────
            foreach ((DeploymentResource node, string[] ownerUids) in pendingWire)
            {
                foreach (string ownerUid in ownerUids)
                {
                    if (byUid.TryGetValue(ownerUid, out DeploymentResource? parent))
                    {
                        node.ParentResourceId = parent.Id;
                        parent.ChildResources.Add(node);
                        break;
                    }
                }
            }

            // ── Wire HTTPRoute → Service links via backendRefs ────────────
            // Services have no OwnerReferences to HTTPRoutes; we derive the link
            // from the HTTPRoute's spec.rules[].backendRefs parsed during fetch.
            Dictionary<string, DeploymentResource> servicesByName = byUid.Values
                .Where(r => r.Kind == "Service")
                .ToDictionary(r => r.Name, r => r);

            foreach ((DeploymentResource httpRoute, string svcName) in httpRouteBackends)
            {
                if (servicesByName.TryGetValue(svcName, out DeploymentResource? svc)
                    && svc.ParentResourceId == null)
                {
                    svc.ParentResourceId = httpRoute.Id;
                    httpRoute.ChildResources.Add(svc);
                }
            }

            // ── Build full root list ──────────────────────────────────────
            List<DeploymentResource> roots = byUid.Values
                .Where(r => r.ParentResourceId == null)
                .OrderBy(r => KindDisplayOrder(r.Kind))
                .ThenBy(r => r.Name)
                .ToList();

            // ── Filter roots to this specific deployment ──────────────────
            // If the deployment has manifests (Yaml/Manual/Git types), use
            // (Kind, Name) pairs from those manifests as an allow-list so that
            // resources from other workloads sharing the same namespace are excluded.
            // For HelmChart/GitHelm deployments (no manifest rows), fall back to
            // release-name prefix matching since Helm names resources after the release.
            HashSet<(string Kind, string Name)>? manifestFilter =
                await LoadManifestFilterAsync(deploymentId, ct);

            // Route-associated Services and HTTPRoutes are not in the manifest store but
            // should always appear in the tree alongside the workload they expose.
            HashSet<(string Kind, string Name)> routeFilter =
                await LoadRouteFilterAsync(deploymentId, ct);

            bool RouteMatch(DeploymentResource r) =>
                routeFilter.Count > 0 && routeFilter.Contains((r.Kind, r.Name));

            if (manifestFilter is { Count: > 0 })
            {
                roots = roots.Where(r => manifestFilter.Contains((r.Kind, r.Name)) || RouteMatch(r)).ToList();
            }
            else if (deployment.Type is DeploymentType.HelmChart or DeploymentType.GitHelm)
            {
                string release = deployment.Name;
                roots = roots
                    .Where(r => r.Name == release
                             || r.Name.StartsWith(release + "-", StringComparison.Ordinal)
                             || RouteMatch(r))
                    .ToList();
            }
            else if (deployment.Type != DeploymentType.GitAppOfApps)
            {
                string namePrefix = deployment.Name;
                roots = roots
                    .Where(r => r.Name == namePrefix
                             || r.Name.StartsWith(namePrefix + "-", StringComparison.Ordinal)
                             || RouteMatch(r))
                    .ToList();
            }
            // else (GitAppOfApps): manages the entire namespace — show everything.

            SortChildResources(roots);

            return KubernetesOperationResult<List<DeploymentResource>>.Success(roots);
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult<List<DeploymentResource>>.Failure(
                $"Failed to query cluster: {ex.Message}");
        }
    }

    /// <summary>
    /// Derives the overall sync + health status from a live resource tree.
    /// Called after GetLiveResourcesAsync so the UI can persist and display
    /// up-to-date status badges without a second cluster round-trip.
    ///
    /// Rules:
    /// - Empty tree → Unknown / Unknown
    /// - Any resources present → Synced
    /// - Health = worst-case across all root workload nodes
    ///   (Degraded > Progressing > Suspended > Healthy > Unknown)
    /// </summary>
    public static (SyncStatus Sync, HealthStatus Health) ComputeStatusFromResources(
        List<DeploymentResource> roots)
    {
        if (roots.Count == 0)
            return (SyncStatus.Unknown, HealthStatus.Unknown);

        // Prefer workload kinds for the aggregate; fall back to all roots.
        List<DeploymentResource> workloads = roots
            .Where(r => r.Kind is "Deployment" or "StatefulSet" or "DaemonSet")
            .ToList();

        if (workloads.Count == 0)
            workloads = roots;

        static int HealthSeverity(HealthStatus h) => h switch
        {
            HealthStatus.Degraded    => 4,
            HealthStatus.Missing     => 4,
            HealthStatus.Progressing => 3,
            HealthStatus.Suspended   => 2,
            HealthStatus.Healthy     => 1,
            _                        => 0,
        };

        HealthStatus worst = workloads
            .OrderByDescending(w => HealthSeverity(w.HealthStatus))
            .First()
            .HealthStatus;

        // Map Missing → Degraded for the top-level badge (Missing is a resource-level concept).
        if (worst == HealthStatus.Missing)
            worst = HealthStatus.Degraded;

        return (SyncStatus.Synced, worst);
    }

    /// <summary>
    /// Derives sync + health status from a list of live pods.
    /// Used in portal views that already have the pod list available.
    /// </summary>
    public static (SyncStatus Sync, HealthStatus Health) ComputeStatusFromPods(
        List<PodInfo> pods)
    {
        if (pods.Count == 0)
            return (SyncStatus.Unknown, HealthStatus.Unknown);

        bool allReady  = pods.All(p =>
            p.Status == "Running" && p.ReadyContainers == p.TotalContainers);
        bool anyFailed  = pods.Any(p => p.Status == "Failed");
        bool anyPending = pods.Any(p => p.Status == "Pending");
        bool anyRunning = pods.Any(p => p.Status == "Running");

        HealthStatus health =
            anyFailed            ? HealthStatus.Degraded
            : allReady           ? HealthStatus.Healthy
            : anyPending         ? HealthStatus.Progressing
            : anyRunning         ? HealthStatus.Degraded   // running but not all ready
                                 : HealthStatus.Unknown;

        return (SyncStatus.Synced, health);
    }

    /// <summary>
    /// Returns a set of (Kind, Name) pairs from the deployment's stored manifests,
    /// used to filter live namespace resources to just those belonging to this deployment.
    /// Returns null if the deployment has no manifests (Helm or new Manual deployment).
    ///
    /// A single manifest row may contain multiple YAML documents (separated by "---"),
    /// so we parse the YamlContent of each row to extract ALL resources, not just the
    /// stored Kind/Name columns which only capture the first resource in the row.
    /// </summary>
    private async Task<HashSet<(string Kind, string Name)>?> LoadManifestFilterAsync(
        Guid deploymentId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId)
            .ToListAsync(ct);

        if (manifests.Count == 0) return null;

        HashSet<(string Kind, string Name)> filter = [];

        foreach (DeploymentManifest manifest in manifests)
        {
            // Always include the stored Kind/Name (fast path, no parsing needed).
            filter.Add((manifest.Kind, manifest.Name));

            // Also parse the full YAML content — a single manifest row can contain
            // multiple documents separated by "---" (e.g. when pasted manually).
            foreach (string doc in manifest.YamlContent.Split("\n---", StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = doc.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                (string kind, string name) = ExtractKindAndName(trimmed);
                if (kind != "Unknown" && name != "unnamed")
                    filter.Add((kind, name));
            }
        }

        return filter;
    }

    private static (string Kind, string Name) ExtractKindAndName(string yaml)
    {
        string kind = "Unknown";
        string name = "unnamed";

        foreach (string line in yaml.Split('\n'))
        {
            if (line.StartsWith("kind:", StringComparison.Ordinal))
                kind = line["kind:".Length..].Trim();
            else if (line.TrimStart().StartsWith("name:", StringComparison.Ordinal) && name == "unnamed")
                name = line.Split(':')[1].Trim().Trim('"');
        }

        return (kind, name);
    }

    /// <summary>
    /// Returns (Kind, Name) pairs for Services and HTTPRoutes created by the AppRoute
    /// system for this deployment. These are never in the manifest store but should
    /// still appear in the live resource tree next to the workload they expose.
    /// </summary>
    private async Task<HashSet<(string Kind, string Name)>> LoadRouteFilterAsync(
        Guid deploymentId, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();

        List<AppDeploymentRoute> routes = await db.AppDeploymentRoutes
            .Include(r => r.AppRoute)
            .Where(r => r.AppDeploymentId == deploymentId && r.IsEnabled)
            .ToListAsync(ct);

        HashSet<(string, string)> filter = [];

        foreach (AppDeploymentRoute route in routes)
        {
            if (!string.IsNullOrEmpty(route.ServiceName))
                filter.Add(("Service", route.ServiceName));

            if (route.AppRoute?.Hostname is { Length: > 0 } hostname)
            {
                string routeName = ExternalRouteService.ToListenerName(hostname) + "-route";
                filter.Add(("HTTPRoute", routeName));
            }
        }

        return filter;
    }

    private static void SortChildResources(IEnumerable<DeploymentResource> nodes)
    {
        foreach (DeploymentResource node in nodes)
        {
            if (node.ChildResources is List<DeploymentResource> kids && kids.Count > 1)
            {
                kids.Sort((a, b) =>
                {
                    int ko = KindDisplayOrder(a.Kind).CompareTo(KindDisplayOrder(b.Kind));
                    return ko != 0 ? ko : string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });
                SortChildResources(kids);
            }
        }
    }

    private static int KindDisplayOrder(string kind) => kind switch
    {
        "Deployment"            => 0,
        "StatefulSet"           => 1,
        "DaemonSet"             => 2,
        "CronJob"               => 3,
        "Job"                   => 4,
        "Service"               => 5,
        "Ingress"               => 6,
        "HTTPRoute"             => 7,
        "PersistentVolumeClaim" => 8,
        "ConfigMap"             => 9,
        "Secret"                => 10,
        "ReplicaSet"            => 11,
        "Pod"                   => 12,
        _                      => 99
    };

    /// Runs a fetch action and swallows errors — a missing API group
    /// (e.g. no Ingress controller) should not abort the entire resource list.
    private async Task TryFetch(Func<Task> fetch, string resourceType = "")
    {
        try { await fetch(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Non-fatal: could not fetch {ResourceType} from cluster", resourceType);
        }
    }

    // ──────── CronJob operations ────────

    /// <summary>
    /// Creates a Job from a CronJob immediately — equivalent to
    /// "kubectl create job --from=cronjob/&lt;name&gt;". The job name is derived
    /// from the CronJob name and the current Unix timestamp to stay unique.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> TriggerCronJobAsync(
        Guid deploymentId, string cronJobName,
        string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure("Cluster has no kubeconfig configured.");

        // Build a unique, K8s-safe job name (max 63 chars).
        string raw = $"{cronJobName}-manual-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string jobName = System.Text.RegularExpressions.Regex.Replace(
            raw.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (jobName.Length > 63) jobName = jobName[..63].TrimEnd('-');

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster.Kubeconfig, ct);
            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"create job {jobName} --from=cronjob/{cronJobName}" +
                $" -n {deployment.Namespace} --kubeconfig {tempKubeconfig}",
                ct);

            if (result.Success)
            {
                logger.LogInformation("CronJob {CronJob} triggered as Job {Job} in {Namespace} by {User}",
                    cronJobName, jobName, deployment.Namespace, performedBy ?? "system");
                await auditService.RecordAsync(deploymentId, "TriggerCronJob", "CronJob",
                    cronJobName, $"job={jobName}", performedBy, ct);
            }

            return result.Success
                ? KubernetesOperationResult<string>.Success($"Job '{jobName}' created.")
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    /// <summary>
    /// Patches spec.suspend on a CronJob to pause or resume its schedule.
    /// </summary>
    public async Task<KubernetesOperationResult> SetCronJobSuspendedAsync(
        Guid deploymentId, string name, bool suspend,
        string? performedBy = null, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult.Failure("Cluster has no kubeconfig configured.");

        try
        {
            using Kubernetes client = CreateClient(deployment.Cluster.Kubeconfig);
            string suspendValue = suspend ? "true" : "false";
            V1Patch patch = new(
                $"{{\"spec\":{{\"suspend\":{suspendValue}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.BatchV1.PatchNamespacedCronJobAsync(
                patch, name, deployment.Namespace, cancellationToken: ct);

            string action = suspend ? "SuspendCronJob" : "ResumeCronJob";
            logger.LogInformation("CronJob {Name} {Action} in {Namespace} by {User}",
                name, action, deployment.Namespace, performedBy ?? "system");
            await auditService.RecordAsync(deploymentId, action, "CronJob",
                name, performedBy: performedBy, ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            return KubernetesOperationResult.Failure(
                $"Failed to {(suspend ? "suspend" : "resume")} CronJob: {ex.Message}");
        }
    }

    // ──────── Resource YAML ────────

    /// <summary>
    /// Turns EntKube management of a deployment on or off. Enabling first refreshes the
    /// stored manifests from the live cluster — so EntKube adopts whatever ArgoCD/Flux
    /// last applied rather than the (possibly stale) import-time snapshot — then marks
    /// the deployment managed so it can be applied. Disabling only flips the flag and
    /// never touches the cluster (the workload keeps running under its current owner).
    /// </summary>
    public async Task<KubernetesOperationResult<string>> SetDeploymentManagementAsync(
        Guid deploymentId, bool managed, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");

        if (deployment.IsManaged == managed)
            return KubernetesOperationResult<string>.Success("No change.");

        if (!managed)
        {
            await UpdateDeploymentIsManagedAsync(deploymentId, false, ct);
            return KubernetesOperationResult<string>.Success(
                "Management disabled — EntKube will no longer apply this deployment.");
        }

        // Enabling → adopt the current live spec before allowing apply.
        (int refreshed, int missing) = (0, 0);
        if (!string.IsNullOrWhiteSpace(deployment.Cluster?.Kubeconfig))
            (refreshed, missing) = await RefreshManifestsFromLiveAsync(deployment, ct);

        await UpdateDeploymentIsManagedAsync(deploymentId, true, ct);

        string note = $"Management enabled. Refreshed {refreshed} manifest(s) from the live cluster";
        note += missing > 0 ? $"; {missing} not found live (kept as imported)." : ".";
        return KubernetesOperationResult<string>.Success(note);
    }

    private async Task UpdateDeploymentIsManagedAsync(Guid deploymentId, bool managed, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        AppDeployment? d = await db.AppDeployments.FirstOrDefaultAsync(x => x.Id == deploymentId, ct);
        if (d is null) return;
        d.IsManaged = managed;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-reads each stored manifest from the cluster (kubectl get -o json), sanitizes
    /// it the same way the importer does, and overwrites the stored YAML — so the
    /// deployment reflects the current live spec. Best-effort: a resource that no longer
    /// exists live is left as-is and counted as missing.
    /// </summary>
    private async Task<(int refreshed, int missing)> RefreshManifestsFromLiveAsync(
        AppDeployment deployment, CancellationToken ct)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        List<DeploymentManifest> manifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deployment.Id)
            .ToListAsync(ct);

        if (manifests.Count == 0)
            return (0, 0);

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        int refreshed = 0, missing = 0;
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster!.Kubeconfig!, ct);

            foreach (DeploymentManifest m in manifests)
            {
                HelmExecutionResult res = await RunCliAsync(
                    "kubectl",
                    $"get {m.Kind.ToLowerInvariant()} {m.Name} -n {deployment.Namespace}" +
                    $" --kubeconfig {tempKubeconfig} -o json",
                    ct);

                if (!res.Success)
                {
                    missing++;
                    continue;
                }

                try
                {
                    JsonNode? node = JsonNode.Parse(res.Output);
                    if (node is null) { missing++; continue; }
                    m.YamlContent = ImportManifestSanitizer.ToYaml(node);
                    m.UpdatedAt = DateTime.UtcNow;
                    refreshed++;
                }
                catch
                {
                    missing++;
                }
            }

            await db.SaveChangesAsync(ct);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }

        return (refreshed, missing);
    }

    /// <summary>
    /// Returns the raw YAML for a single namespaced resource via "kubectl get -o yaml".
    /// Useful for inspecting live resource state directly from the UI.
    /// </summary>
    public async Task<KubernetesOperationResult<string>> GetResourceYamlAsync(
        Guid deploymentId, string kind, string name, CancellationToken ct = default)
    {
        AppDeployment? deployment = await GetDeploymentWithClusterAsync(deploymentId, ct);
        if (deployment is null)
            return KubernetesOperationResult<string>.Failure("Deployment not found.");
        if (string.IsNullOrEmpty(deployment.Cluster?.Kubeconfig))
            return KubernetesOperationResult<string>.Failure("Cluster has no kubeconfig configured.");

        string tempKubeconfig = Path.Combine(Path.GetTempPath(), $"entkube-{Guid.NewGuid()}.kubeconfig");
        try
        {
            await File.WriteAllTextAsync(tempKubeconfig, deployment.Cluster.Kubeconfig, ct);
            HelmExecutionResult result = await RunCliAsync(
                "kubectl",
                $"get {kind.ToLowerInvariant()} {name} -n {deployment.Namespace}" +
                $" --kubeconfig {tempKubeconfig} -o yaml",
                ct);
            return result.Success
                ? KubernetesOperationResult<string>.Success(result.Output)
                : KubernetesOperationResult<string>.Failure(result.Output);
        }
        finally
        {
            if (File.Exists(tempKubeconfig)) File.Delete(tempKubeconfig);
        }
    }

    // ──────── Route discovery helpers ────────

    /// <summary>
    /// Lists all Services in a namespace with their ports.
    /// Used to populate the service/port selector when configuring app routes.
    /// Returns an empty list rather than throwing if the cluster is unreachable.
    /// </summary>
    public async Task<List<KubeServiceInfo>> GetServicesInNamespaceAsync(
        Guid clusterId, string ns, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return [];

        try
        {
            using Kubernetes client = CreateClient(cluster.Kubeconfig);
            V1ServiceList list = await client.CoreV1.ListNamespacedServiceAsync(ns, cancellationToken: ct);

            return list.Items
                .Where(svc => svc.Spec?.Type != "ExternalName")
                .Select(svc => new KubeServiceInfo(
                    svc.Metadata.Name,
                    svc.Spec?.Type ?? "ClusterIP",
                    svc.Spec?.ClusterIP,
                    (svc.Spec?.Ports ?? [])
                        .Select(p => new KubeServicePort(p.Name, p.Port, p.Protocol ?? "TCP"))
                        .ToList()))
                .OrderBy(s => s.Name)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list services in {Namespace} on cluster {ClusterId}", ns, clusterId);
            return [];
        }
    }

    /// <summary>
    /// Returns the ready and not-ready endpoint addresses for a specific Service.
    /// Useful for showing operators whether the service has live pods behind it.
    /// </summary>
    public async Task<KubeEndpointSummary> GetEndpointsForServiceAsync(
        Guid clusterId, string ns, string serviceName, CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters.FirstOrDefaultAsync(c => c.Id == clusterId, ct);
        if (cluster is null || string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return new KubeEndpointSummary([], []);

        try
        {
            using Kubernetes client = CreateClient(cluster.Kubeconfig);
            V1Endpoints ep = await client.CoreV1.ReadNamespacedEndpointsAsync(serviceName, ns, cancellationToken: ct);

            List<string> ready = [];
            List<string> notReady = [];

            foreach (V1EndpointSubset subset in ep.Subsets ?? [])
            {
                List<int> ports = (subset.Ports ?? []).Select(p => p.Port).ToList();
                string portSuffix = ports.Count > 0 ? $":{string.Join(",", ports)}" : "";

                foreach (V1EndpointAddress addr in subset.Addresses ?? [])
                    ready.Add(addr.Ip + portSuffix);

                foreach (V1EndpointAddress addr in subset.NotReadyAddresses ?? [])
                    notReady.Add(addr.Ip + portSuffix);
            }

            return new KubeEndpointSummary(ready, notReady);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not get endpoints for {Service} in {Namespace}", serviceName, ns);
            return new KubeEndpointSummary([], []);
        }
    }

    // ──────── Internal ────────

    /// <summary>
    /// Creates a Kubernetes client from a raw kubeconfig YAML string.
    /// The kubeconfig is parsed in-memory — never written to disk.
    /// </summary>
    private static Kubernetes CreateClient(string kubeconfig)
    {
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(kubeconfig));
        KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
        return new Kubernetes(config);
    }

    /// <summary>
    /// Converts an arbitrary deployment name into a Helm-compatible release name:
    /// lowercase, alphanumeric + hyphens only, max 53 characters.
    /// </summary>
    private static string ToHelmReleaseName(string name)
    {
        string s = name.ToLowerInvariant();
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9]+", "-");
        s = s.Trim('-');
        return s.Length > 53 ? s[..53].TrimEnd('-') : s;
    }

    /// <summary>
    /// Runs an external CLI process (helm, kubectl) and returns the combined
    /// stdout + stderr. Exit code 0 → Success; non-zero → Failure.
    /// </summary>
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
        psi.EnvironmentVariables["HOME"] = "/tmp";

        using System.Diagnostics.Process process = new() { StartInfo = psi };

        try
        {
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            string combined = (await stdout).Trim();
            string err = (await stderr).Trim();
            if (!string.IsNullOrEmpty(err)) combined = string.IsNullOrEmpty(combined) ? err : combined + "\n" + err;

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
