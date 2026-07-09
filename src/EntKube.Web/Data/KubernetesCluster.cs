using System.ComponentModel.DataAnnotations.Schema;

namespace EntKube.Web.Data;

/// <summary>
/// Lifecycle of a cluster that EntKube provisions itself (Cluster API + CAPO).
/// A registered (externally-created) cluster stays at <see cref="None"/>.
/// </summary>
public enum ClusterProvisioningStatus
{
    /// <summary>Not provisioned by EntKube — registered from an existing kubeconfig.</summary>
    None = 0,
    /// <summary>Placeholder created; infrastructure is being stood up on the cloud.</summary>
    Provisioning = 1,
    /// <summary>Infrastructure provisioned; kubeconfig registered.</summary>
    Provisioned = 2,
    /// <summary>Provisioning failed; may be resumed or torn down.</summary>
    Failed = 3
}

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

    /// <summary>
    /// Whether EntKube provisioned this cluster's infrastructure and, if so, where it is
    /// in that lifecycle. <see cref="ClusterProvisioningStatus.None"/> for registered clusters.
    /// </summary>
    public ClusterProvisioningStatus ProvisioningStatus { get; set; } = ClusterProvisioningStatus.None;

    /// <summary>
    /// Opaque runner state for an in-flight provisioning (bootstrap VM ids, phase), as JSON.
    /// Lets a resumed run re-attach to the ephemeral bootstrap VM instead of re-creating it.
    /// Null once provisioning completes or for registered clusters.
    /// </summary>
    public string? ProvisioningStateJson { get; set; }

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ICollection<ClusterComponent> Components { get; set; } = [];
    public ICollection<ClusterServer> Servers { get; set; } = [];
}
