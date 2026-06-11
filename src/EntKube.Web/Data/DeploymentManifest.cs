namespace EntKube.Web.Data;

/// <summary>
/// An individual Kubernetes manifest within a deployment. For Manual deployments,
/// these are generated from the structured form (containers, services, PVCs).
/// For Yaml deployments, these are the raw YAML documents the user pasted/uploaded.
///
/// Each manifest represents a single K8s resource (Deployment, Service, PVC,
/// ConfigMap, Secret, etc.). They are applied in SortOrder sequence.
/// </summary>
public class DeploymentManifest
{
    public Guid Id { get; set; }

    public Guid DeploymentId { get; set; }

    /// <summary>
    /// The Kubernetes resource kind (e.g. "Deployment", "Service", "PersistentVolumeClaim").
    /// </summary>
    public required string Kind { get; set; }

    /// <summary>
    /// The resource name as it appears in metadata.name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Controls the order manifests are applied. Lower numbers go first.
    /// Namespaces and PVCs before Deployments, Deployments before Services, etc.
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// The full YAML content of this manifest. This is what gets applied to the cluster.
    /// </summary>
    public required string YamlContent { get; set; }

    /// <summary>
    /// For Git-sourced manifests: the relative file path within the repository that
    /// produced this manifest. Null for manually created or pasted manifests.
    /// </summary>
    public string? SourceFile { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public AppDeployment Deployment { get; set; } = null!;
}
