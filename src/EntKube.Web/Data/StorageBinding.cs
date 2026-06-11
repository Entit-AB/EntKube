namespace EntKube.Web.Data;

/// <summary>
/// A storage binding connects a StorageLink to a workload — either an AppDeployment
/// or a ClusterComponent. When a binding exists, the platform knows to sync the
/// storage credentials (access key, secret key, endpoint, bucket, region) into a
/// Kubernetes Secret in the workload's namespace so the pods can consume them.
///
/// Think of it as "this deployment uses that storage bucket." The binding carries
/// the target K8s Secret name so the workload's env vars or volume mounts can
/// reference a well-known secret name regardless of which provider backs it.
/// </summary>
public class StorageBinding
{
    public Guid Id { get; set; }

    /// <summary>
    /// The storage link being bound to the workload. This is where the credentials
    /// and connection details (endpoint, bucket, region) live.
    /// </summary>
    public Guid StorageLinkId { get; set; }

    /// <summary>
    /// If set, this binding targets an app deployment. The secret will be synced
    /// to the deployment's namespace on its target cluster.
    /// </summary>
    public Guid? AppDeploymentId { get; set; }

    /// <summary>
    /// If set, this binding targets a cluster component. The secret will be synced
    /// to the component's namespace on its cluster.
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// The Kubernetes Secret name that will hold the storage credentials in the
    /// target namespace. Workloads reference this name in their env/volume config.
    /// Example: "media-s3-credentials", "backup-storage".
    /// </summary>
    public required string KubernetesSecretName { get; set; }

    /// <summary>
    /// When true, the platform will automatically create/update the K8s Secret
    /// whenever the underlying StorageLink credentials change. When false, the
    /// binding exists as metadata only (manual sync or not yet activated).
    /// </summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Last time the secret was successfully synced to the target cluster.
    /// Null if never synced.
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public StorageLink StorageLink { get; set; } = null!;
    public AppDeployment? AppDeployment { get; set; }
    public ClusterComponent? Component { get; set; }
}
