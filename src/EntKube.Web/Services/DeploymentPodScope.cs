using System.Text.RegularExpressions;
using EntKube.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace EntKube.Web.Services;

/// <summary>
/// Resolves a deployment to a POSIX pod-name regex used to scope telemetry (spans/metrics/logs) to just
/// that deployment's pods — the finer grain below an app's namespace, since one app often spans several
/// deployments in the same namespace. Pods are named <c>&lt;workload&gt;-&lt;hash&gt;</c>, so a workload
/// name anchors its pods. Best-effort: if two of an app's deployments have prefix-related workload names
/// (e.g. "api" and "api-worker") the regex can over-match — the same limitation the log viewer has.
/// </summary>
public static class DeploymentPodScope
{
    // Kubernetes kinds whose metadata.name is the prefix of their pods' names.
    private static readonly string[] WorkloadKinds =
        ["Deployment", "StatefulSet", "DaemonSet", "Job", "CronJob", "ReplicaSet", "Rollout", "Pod"];

    /// <summary>
    /// Workload names for a deployment (pod-name prefixes). Reads the deployment's desired-state manifests
    /// first (available without a live sync), unioning live-synced resources as a fallback for deployment
    /// types that don't materialize manifests (e.g. Helm charts).
    /// </summary>
    public static async Task<List<string>> WorkloadNamesAsync(
        ApplicationDbContext db, Guid deploymentId, CancellationToken ct = default)
    {
        List<string> fromManifests = await db.DeploymentManifests
            .Where(m => m.DeploymentId == deploymentId && WorkloadKinds.Contains(m.Kind))
            .Select(m => m.Name).ToListAsync(ct);

        List<string> fromResources = await db.DeploymentResources
            .Where(r => r.DeploymentId == deploymentId && WorkloadKinds.Contains(r.Kind))
            .Select(r => r.Name).ToListAsync(ct);

        return fromManifests.Concat(fromResources)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// A POSIX regex (<c>^(workload1|workload2)-</c>) matching the deployment's pods, or null when no
    /// workload names are on record — in which case the caller should fall back to namespace-only scope.
    /// </summary>
    public static async Task<string?> PodRegexAsync(
        ApplicationDbContext db, Guid deploymentId, CancellationToken ct = default)
    {
        List<string> names = await WorkloadNamesAsync(db, deploymentId, ct);
        if (names.Count == 0) return null;
        return "^(" + string.Join("|", names.Select(Regex.Escape)) + ")-";
    }
}
