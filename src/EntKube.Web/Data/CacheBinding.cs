namespace EntKube.Web.Data;

/// <summary>
/// Links a Redis cluster to an AppDeployment. Many bindings can point to the
/// same cluster — a shared Redis instance can serve multiple apps simultaneously.
///
/// On sync, REDIS_HOST, REDIS_PORT, REDIS_PASSWORD, and REDIS_URL are written
/// as a Kubernetes Secret into the app deployment's namespace so the app can
/// connect without knowing which Redis cluster backs it.
/// </summary>
public class CacheBinding
{
    public Guid Id { get; set; }

    public Guid RedisClusterId { get; set; }

    public Guid AppDeploymentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Kubernetes Secret name created in the app deployment's namespace.</summary>
    public required string KubernetesSecretName { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public RedisCluster RedisCluster { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
