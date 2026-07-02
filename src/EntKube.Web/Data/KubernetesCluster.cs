using System.ComponentModel.DataAnnotations.Schema;

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
    /// The vault secret (<see cref="VaultSecretType.Kubeconfig"/>) that holds this
    /// cluster's encrypted kubeconfig, with expiry tracking and version history.
    /// Null only until the cluster's kubeconfig has been stored in the vault.
    /// </summary>
    public Guid? KubeconfigSecretId { get; set; }

    /// <summary>
    /// The raw kubeconfig YAML content for connecting to this cluster.
    /// NOT persisted — the kubeconfig lives encrypted in the tenant vault
    /// (see <see cref="KubeconfigSecretId"/>). This property is transparently
    /// populated from the vault when a cluster is materialized (via the
    /// kubeconfig materialization interceptor), so the many existing consumers
    /// that read <c>cluster.Kubeconfig</c> keep working unchanged. It is also set
    /// directly when registering a cluster, before the vault secret is written.
    /// </summary>
    [NotMapped]
    public string? Kubeconfig { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ICollection<ClusterComponent> Components { get; set; } = [];
    public ICollection<ClusterServer> Servers { get; set; } = [];
}
