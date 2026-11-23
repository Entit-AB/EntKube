namespace EntKube.Web.Data;

/// <summary>
/// Links a Redis cluster to an AppDeployment. Many bindings can point to the
/// same cluster — a shared Redis instance can serve multiple apps simultaneously.
///
/// On sync, REDIS_HOST, REDIS_PORT, REDIS_PASSWORD, and REDIS_URL are written
/// as a Kubernetes Secret into the app deployment's namespace so the app can
/// connect without knowing which Redis cluster backs it.
///
/// <para>On a cluster with <see cref="RedisCluster.PerAppUsers"/>, each binding is its own Redis
/// user (<c>RedisAcl.UsernameFor</c>) with its own password, and REDIS_USERNAME is written too.
/// Without it, every binding still receives the cluster's admin password.</para>
/// </summary>
public class CacheBinding
{
    /// <summary>Longest <see cref="KeyPrefix"/> accepted; a prefix is a namespace, not a sentence.</summary>
    public const int MaxKeyPrefixLength = 48;

    public Guid Id { get; set; }

    public Guid RedisClusterId { get; set; }

    public Guid AppDeploymentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Kubernetes Secret name created in the app deployment's namespace.</summary>
    public required string KubernetesSecretName { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    /// <summary>
    /// The key prefix this app's Redis user is confined to, without the trailing colon: the user may
    /// touch only keys and channels under <c>{KeyPrefix}:</c>, and the app receives it as
    /// REDIS_KEY_PREFIX. Null means all keys — still its own user, still no admin commands, but able
    /// to read and overwrite other apps' keys. Existing bindings were moved over as null so no app
    /// broke; new ones default to a prefix.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// True while what the app's Secret holds may differ from what the binding says — set when the
    /// key prefix changes, cleared by the next successful sync. The background reconcile syncs every
    /// binding with this set, so a change whose sync failed still reaches the app.
    /// </summary>
    public bool SyncPending { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public RedisCluster RedisCluster { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
