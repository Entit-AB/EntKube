namespace EntKube.Web.Data;

public enum RedisClusterStatus
{
    Creating,
    Running,
    Failed,
    Deleting
}

/// <summary>
/// A managed Redis cluster provisioned via the OT-Container-Kit Redis Operator.
/// Applies a RedisCluster CRD (redis.redis.opstreelabs.in/v1beta2) which the
/// operator reconciles into a sharded Redis cluster StatefulSet.
///
/// EntKube generates the auth password, stores it in the tenant vault, and
/// creates a Kubernetes Secret before applying the CRD so the operator can
/// read the credentials at startup.
/// </summary>
public class RedisCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// The RedisCluster resource name (metadata.name). Lowercase, DNS-safe.
    /// The operator derives all child resources from this name.
    /// </summary>
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    /// <summary>Number of leader shards. Each shard gets one follower. Min 3 for cluster mode.</summary>
    public int ClusterSize { get; set; } = 3;

    /// <summary>Redis version tag used on the operator image (e.g. "v7", "v7.0.15").</summary>
    public required string RedisVersion { get; set; }

    /// <summary>PVC storage size per node (e.g. "1Gi", "10Gi").</summary>
    public required string StorageSize { get; set; }

    /// <summary>Optional StorageClass override. Null uses the cluster default.</summary>
    public string? StorageClass { get; set; }

    /// <summary>When true, PVCs are created for each pod for data persistence.</summary>
    public bool PersistenceEnabled { get; set; } = true;

    public RedisClusterStatus Status { get; set; } = RedisClusterStatus.Creating;

    public string? LastError { get; set; }

    /// <summary>
    /// When every app bound to this Redis started getting its own ACL user instead of the cluster's
    /// <c>default</c> password; null while apps still share it.
    ///
    /// <para>⚠️ The <c>default</c> user is the admin: it can read and delete every app's keys,
    /// <c>FLUSHALL</c> the whole cache and <c>CONFIG SET</c> or <c>CLUSTER RESET</c> the cluster. It
    /// stays what the operator, the health probes, replication and the exporter authenticate as, and
    /// is no longer handed to apps once this is on.</para>
    ///
    /// <para>Null for clusters created before per-app users existed: switching one over adds the
    /// users file to the pod template, which restarts the Redis pods, so it is an explicit action
    /// rather than something a sync does on the side. Every cluster created since starts with it set.</para>
    ///
    /// <para>A timestamp rather than a flag because the switch is not instant for the apps: a binding
    /// last synced before this moment still holds the admin password in its Secret, and the Cache tab
    /// says so until it is synced again.</para>
    /// </summary>
    public DateTime? PerAppUsersSince { get; set; }

    /// <summary>True when apps get their own users; see <see cref="PerAppUsersSince"/>. Not stored.</summary>
    public bool PerAppUsers => PerAppUsersSince is not null;

    /// <summary>
    /// Until when one EntKube instance holds the right to change this Redis's per-app users. Claimed
    /// by a conditional update (only when null or past), so of several instances reconciling the same
    /// Redis exactly one proceeds; the others wait. It expires rather than being trusted to be
    /// released, so an instance that dies mid-reconcile holds nothing for long.
    ///
    /// <para>Without it, two instances could each read the bindings, and the one that read first
    /// would revoke a user the other had just created, and drop that binding's password.</para>
    /// </summary>
    public DateTime? UsersLeaseUntil { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
}
