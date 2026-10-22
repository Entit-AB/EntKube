using EntKube.Web.Data;
using EntKube.Web.Data.Modules;
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
    EntKube.Web.Services.Clusters.IClusterClientFactory clusterAccess,
    EntKube.Web.Services.ClusterChanges.IClusterChangeGate gate,
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
    /// <param name="tenantId">
    /// Who is remediating. This method loaded its cluster by id with <b>no tenant predicate</b>,
    /// so the id was the whole authorization — on a method whose job is deleting pods and
    /// restarting workloads. The alerts page has always had a tenant.
    /// </param>
    public async Task<KubernetesOperationResult> ExecuteAsync(
        Guid tenantId,
        Guid clusterId,
        AlertRemediation remediation,
        string? performedBy = null,
        CancellationToken ct = default)
    {
        // Both refusals in one: not this tenant's cluster, or no credential stored for it. This
        // service used to build its own Kubernetes client from the kubeconfig off the row; the
        // seam hands out a configured one and keeps the credential.
        EntKube.Web.Services.Clusters.IClusterClient? cluster =
            await clusterAccess.ForAsync(tenantId, clusterId, ct);

        if (cluster is null)
            return KubernetesOperationResult.Failure(
                "That cluster is not this tenant's, or has no kubeconfig configured.");

        try
        {
            // Remediation goes through the seam like every other cluster write, and says what it
            // is rather than saying nothing. The operator picked this action from the alerts page
            // and confirmed it in a panel that names the resource and describes the effect, so the
            // gate's own dialog would be a second prompt for one decision — the ceremony §5.4's
            // doc-comments warn about. DeclareConsequence suppresses the question and records each
            // change as AppliedAsConsequence.
            //
            // ⚠️ What this replaced was an exemption that worked by NOT CALLING the gate at all, so
            // there was no record of any outcome: invisible rather than ungated, which is the
            // distinction docs/decomposition.md §5.4.1 exists to kill. The AuditService row below
            // is a different trail — it says a human asked for a remediation, not that a cluster
            // was changed, and IClusterChangeRecorder had nothing either way.
            //
            // §5.4's fourth bullet proposed an Ungated("remediation") marker, and DeclareUnattended
            // is the primitive that grew into that slot. It would have been the wrong one here:
            // it only takes effect when there is no sink (ClusterChangeGate.AcknowledgeAsync tests
            // _sink first), and this flow's one caller is TenantAlertsTab — a Blazor circuit, which
            // always has a sink because ClusterChangeAckDialog is mounted in MainLayout. Declaring
            // unattended would have left the operator acknowledging every remediation twice.
            using IDisposable consequence = gate.DeclareConsequence(
                $"the {remediation.Action} remediation the operator confirmed on the alerts page");

            switch (remediation.Action)
            {
                // The seam's delete passes --ignore-not-found, so a pod that has already gone is a
                // success. The SDK call this replaced threw 404 and reported "Remediation failed"
                // for an alert that resolved itself between the page loading and the click.
                case RemediationActionKind.DeletePod:
                    await cluster.DeleteManifestAsync(
                        "pod", remediation.ResourceName, remediation.Namespace, ct);
                    break;

                case RemediationActionKind.RestartDeployment:
                    await cluster.PatchJsonAsync(
                        "deployment", remediation.ResourceName, remediation.Namespace,
                        RestartPatch(), ct);
                    break;

                case RemediationActionKind.RestartStatefulSet:
                    await cluster.PatchJsonAsync(
                        "statefulset", remediation.ResourceName, remediation.Namespace,
                        RestartPatch(), ct);
                    break;

                case RemediationActionKind.RestartDaemonSet:
                    await cluster.PatchJsonAsync(
                        "daemonset", remediation.ResourceName, remediation.Namespace,
                        RestartPatch(), ct);
                    break;

                // kubectl delete's default cascade is background, which is what the
                // PropagationPolicy = "Background" on the SDK call said explicitly.
                case RemediationActionKind.DeleteJob:
                    await cluster.DeleteManifestAsync(
                        "job", remediation.ResourceName, remediation.Namespace, ct);
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

    /// <summary>
    /// The rolling-restart annotation, as the patch JSON the seam takes.
    ///
    /// <para><c>PatchJsonAsync</c> is <c>kubectl patch --type=merge</c>, which is the same patch
    /// type the <c>V1Patch</c> this replaced declared — so the request the API server sees is
    /// unchanged. <c>PatchStrategicAsync</c> would also work for an annotation and is <em>not</em>
    /// used, because it sends a different request for no reason.</para>
    /// </summary>
    private static string RestartPatch() =>
        "{\"spec\":{\"template\":{\"metadata\":{\"annotations\":"
        + $"{{\"kubectl.kubernetes.io/restartedAt\":\"{DateTime.UtcNow:O}\"}}"
        + "}}}}";
}
