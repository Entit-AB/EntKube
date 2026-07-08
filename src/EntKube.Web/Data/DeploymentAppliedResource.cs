namespace EntKube.Web.Data;

/// <summary>
/// One resource that EntKube applied to the cluster for a non-Helm deployment
/// (types Manual/Yaml/GitYaml), recorded after a successful <c>kubectl apply</c>.
/// This is the applied-manifest <em>inventory</em> — the set that lets the next
/// apply diff against what was applied last time and prune resources that were
/// removed from the manifest set (Helm does this via its release; plain
/// <c>kubectl apply</c> does not). Distinct from <see cref="DeploymentResource"/>,
/// which models the observed live resource tree.
/// </summary>
public class DeploymentAppliedResource
{
    public Guid Id { get; set; }

    public Guid DeploymentId { get; set; }

    /// <summary>Kubernetes API group (e.g. "apps", "" for core, "gateway.networking.k8s.io").</summary>
    public required string Group { get; set; }

    /// <summary>Kubernetes API version (e.g. "v1", "v1beta1"). Informational — identity ignores it.</summary>
    public required string Version { get; set; }

    /// <summary>The resource kind (e.g. "Deployment", "Service").</summary>
    public required string Kind { get; set; }

    /// <summary>The resource name from metadata.name.</summary>
    public required string Name { get; set; }

    /// <summary>Namespace the resource lives in. Null for cluster-scoped kinds.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// False when the manifest opted out of pruning at apply time via
    /// <c>helm.sh/resource-policy: keep</c> or <c>entkube.io/prune: disabled</c>.
    /// A non-prunable resource is never auto-deleted when removed from the set.
    /// </summary>
    public bool Prunable { get; set; } = true;

    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppDeployment Deployment { get; set; } = null!;
}
