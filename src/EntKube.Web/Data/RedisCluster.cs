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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
}
