namespace EntKube.Web.Data;

/// <summary>
/// A tracked Kubernetes resource in the live cluster, modeled after ArgoCD's
/// resource tree. Each resource has its own sync and health status, and resources
/// form a parent-child tree (e.g. Deployment → ReplicaSet → Pod).
///
/// The platform populates and refreshes these by watching the cluster. Users
/// see the full resource tree with per-resource health indicators, just like
/// the ArgoCD application detail view.
/// </summary>
public class DeploymentResource
{
    public Guid Id { get; set; }

    public Guid DeploymentId { get; set; }

    /// <summary>
    /// Kubernetes API group (e.g. "apps", "" for core, "networking.k8s.io").
    /// </summary>
    public required string Group { get; set; }

    /// <summary>
    /// Kubernetes API version (e.g. "v1", "v1beta1").
    /// </summary>
    public required string Version { get; set; }

    /// <summary>
    /// The resource kind (e.g. "Deployment", "ReplicaSet", "Pod", "Service").
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>
    /// The resource name from metadata.name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The namespace the resource lives in. Null for cluster-scoped resources.
    /// </summary>
    public string? Namespace { get; set; }

    public SyncStatus SyncStatus { get; set; } = SyncStatus.Unknown;
    public HealthStatus HealthStatus { get; set; } = HealthStatus.Unknown;
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Parent resource for tree rendering. A Pod's parent is a ReplicaSet,
    /// a ReplicaSet's parent is a Deployment, etc. Null for root resources.
    /// </summary>
    public Guid? ParentResourceId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppDeployment Deployment { get; set; } = null!;
    public DeploymentResource? ParentResource { get; set; }
    public ICollection<DeploymentResource> ChildResources { get; set; } = [];
}
