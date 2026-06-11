using EntKube.Web.Data;
using k8s;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

public enum RemediationActionKind
{
    DeletePod,
    RestartDeployment,
    RestartStatefulSet,
    RestartDaemonSet,
    DeleteJob
}

public record AlertRemediation(
    string Description,
    RemediationActionKind Action,
    string ResourceKind,
    string ResourceName,
    string Namespace
);

/// <summary>
/// Maps known Kubernetes alert names to concrete remediation actions and executes
/// them against a cluster. The goal is to let operators fix common alert patterns
/// (CrashLoopBackOff, replica mismatches, stuck rollouts) without navigating to
/// individual cluster pages.
///
/// Remediation is intentionally conservative — only actions that are safe to
/// retry (delete pod → immediate replacement, rolling restart) are included.
/// Node-level actions (drain, cordon) are excluded.
/// </summary>
public class RemediationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AuditService auditService,
    ILogger<RemediationService> logger)
{
    /// <summary>
    /// Returns a remediation for the given alert if one is known, otherwise null.
    /// The remediation describes what will happen and carries the target resource info
    /// extracted from the alert's labels.
    /// </summary>
    public AlertRemediation? TryGetRemediation(AlertInfo alert)
    {
        Dictionary<string, string> L = alert.Labels;
        string? ns  = L.GetValueOrDefault("namespace");
        string? pod = L.GetValueOrDefault("pod");
        string? dep = L.GetValueOrDefault("deployment");
        string? sts = L.GetValueOrDefault("statefulset");
        string? ds  = L.GetValueOrDefault("daemonset");
        string? job = L.GetValueOrDefault("job");

        return alert.Name switch
        {
            "KubePodCrashLooping" or "KubePodNotReady" or "KubeContainerWaiting"
                when !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(pod)
                => new AlertRemediation(
                    $"Delete pod '{pod}' — Kubernetes will immediately schedule a fresh replacement.",
                    RemediationActionKind.DeletePod, "Pod", pod!, ns!),

            "KubeDeploymentReplicasMismatch" or "KubeDeploymentRolloutStuck"
                when !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(dep)
                => new AlertRemediation(
                    $"Rolling restart of deployment '{dep}' — replaces all pods one by one without downtime.",
                    RemediationActionKind.RestartDeployment, "Deployment", dep!, ns!),

            "KubeStatefulSetReplicasMismatch" or "KubeStatefulSetUpdateNotRolledOut"
                when !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(sts)
                => new AlertRemediation(
                    $"Rolling restart of StatefulSet '{sts}'.",
                    RemediationActionKind.RestartStatefulSet, "StatefulSet", sts!, ns!),

            "KubeDaemonSetRolloutStuck" or "KubeDaemonSetMisScheduled"
                when !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(ds)
                => new AlertRemediation(
                    $"Rolling restart of DaemonSet '{ds}'.",
                    RemediationActionKind.RestartDaemonSet, "DaemonSet", ds!, ns!),

            "KubeJobFailed"
                when !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(job)
                => new AlertRemediation(
                    $"Delete failed job '{job}' to remove it from the cluster.",
                    RemediationActionKind.DeleteJob, "Job", job!, ns!),

            _ => null
        };
    }

    /// <summary>
    /// Executes the remediation against the specified cluster.
    /// The cluster kubeconfig is loaded from the database by clusterId.
    /// </summary>
    public async Task<KubernetesOperationResult> ExecuteAsync(
        Guid clusterId,
        AlertRemediation remediation,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        using ApplicationDbContext db = dbFactory.CreateDbContext();
        KubernetesCluster? cluster = await db.KubernetesClusters
            .FirstOrDefaultAsync(c => c.Id == clusterId, ct);

        if (cluster is null)
            return KubernetesOperationResult.Failure("Cluster not found.");
        if (string.IsNullOrWhiteSpace(cluster.Kubeconfig))
            return KubernetesOperationResult.Failure("Cluster has no kubeconfig configured.");

        try
        {
            using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(cluster.Kubeconfig));
            KubernetesClientConfiguration config = KubernetesClientConfiguration.BuildConfigFromConfigFile(stream);
            using Kubernetes client = new(config);

            switch (remediation.Action)
            {
                case RemediationActionKind.DeletePod:
                    await client.CoreV1.DeleteNamespacedPodAsync(
                        remediation.ResourceName, remediation.Namespace, cancellationToken: ct);
                    break;

                case RemediationActionKind.RestartDeployment:
                    await client.AppsV1.PatchNamespacedDeploymentAsync(
                        MakeRestartPatch(), remediation.ResourceName, remediation.Namespace, cancellationToken: ct);
                    break;

                case RemediationActionKind.RestartStatefulSet:
                    await client.AppsV1.PatchNamespacedStatefulSetAsync(
                        MakeRestartPatch(), remediation.ResourceName, remediation.Namespace, cancellationToken: ct);
                    break;

                case RemediationActionKind.RestartDaemonSet:
                    await client.AppsV1.PatchNamespacedDaemonSetAsync(
                        MakeRestartPatch(), remediation.ResourceName, remediation.Namespace, cancellationToken: ct);
                    break;

                case RemediationActionKind.DeleteJob:
                    await client.BatchV1.DeleteNamespacedJobAsync(
                        remediation.ResourceName, remediation.Namespace,
                        body: new V1DeleteOptions { PropagationPolicy = "Background" },
                        cancellationToken: ct);
                    break;
            }

            logger.LogInformation(
                "Remediation {Action} {Kind}/{Name} in {Namespace} on cluster {ClusterId} by {User}",
                remediation.Action, remediation.ResourceKind, remediation.ResourceName,
                remediation.Namespace, clusterId, performedBy ?? "system");

            await auditService.RecordAsync(
                deploymentId: null,
                action: $"Remediation:{remediation.Action}",
                resourceKind: remediation.ResourceKind,
                resourceName: $"{remediation.Namespace}/{remediation.ResourceName}",
                details: $"cluster={clusterId}",
                performedBy: performedBy,
                ct: ct);

            return KubernetesOperationResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Remediation failed: {Action} {Kind}/{Name} in {Namespace} on cluster {ClusterId}",
                remediation.Action, remediation.ResourceKind, remediation.ResourceName,
                remediation.Namespace, clusterId);
            return KubernetesOperationResult.Failure($"Remediation failed: {ex.Message}");
        }
    }

    private static V1Patch MakeRestartPatch() => new(
        "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":" +
        $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}" +
        "}}}}",
        V1Patch.PatchType.MergePatch);
}
