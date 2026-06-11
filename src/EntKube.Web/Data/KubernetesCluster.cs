namespace EntKube.Web.Data;

/// <summary>
/// A Kubernetes cluster registered in EntKube. Belongs to a tenant and is
/// placed into an environment. One environment can host many clusters
/// (e.g. for regional distribution or capacity scaling).
/// </summary>
public class KubernetesCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// Human-friendly cluster name (e.g. "prod-eu-west-1").
    /// Must be unique within a tenant.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Kubernetes API server URL used to connect to this cluster.
    /// </summary>
    public required string ApiServerUrl { get; set; }

    /// <summary>
    /// The kubeconfig context name that was selected when registering this cluster.
    /// </summary>
    public string? ContextName { get; set; }

    /// <summary>
    /// The raw kubeconfig YAML content for connecting to this cluster.
    /// Stored so the platform can authenticate against the K8s API later.
    /// In production this should be encrypted via the tenant vault.
    /// </summary>
    public string? Kubeconfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ICollection<ClusterComponent> Components { get; set; } = [];
    public ICollection<ClusterServer> Servers { get; set; } = [];
}
