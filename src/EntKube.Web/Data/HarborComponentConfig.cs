namespace EntKube.Web.Data;

/// <summary>
/// Harbor-specific configuration for a managed Harbor instance.
/// Tracks the backing CNPG database, S3 storage bucket, admin credentials, and registry URL.
///
/// Admin password is stored as a component vault secret (key: HARBOR_ADMIN_PASSWORD).
/// S3 credentials are stored as component vault secrets (keys: harbor-s3-access-key,
/// harbor-s3-secret-key) and injected into Helm values at install time.
/// CNPG database password is stored as a component vault secret (key: harbor-db-password).
/// A shared Redis password is stored as a component vault secret (key: harbor-redis-password).
/// </summary>
public class HarborComponentConfig
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The installed Harbor ClusterComponent this config belongs to.</summary>
    public Guid ClusterComponentId { get; set; }

    /// <summary>Managed CNPG database backing this Harbor instance. Null uses Harbor's built-in Postgres.</summary>
    public Guid? CnpgDatabaseId { get; set; }

    /// <summary>S3-compatible storage link for Harbor artifact storage. Null uses local filesystem PVC.</summary>
    public Guid? StorageLinkId { get; set; }

    /// <summary>
    /// A Redis already running on the cluster that this Harbor shares, as <c>host:port</c>. Null runs the
    /// chart's own Redis.
    ///
    /// <para>Stored as the address rather than a foreign key on purpose: the endpoint is the whole of the
    /// configuration, and it is equally valid for a Redis EntKube never created — a Helm chart's, a bare
    /// StatefulSet's. Whether it happens to be one EntKube manages is re-derived from the address at
    /// install time, which is also how its vaulted password is found, so a rotated password is picked up
    /// without anything here changing.</para>
    /// </summary>
    public string? RedisEndpoint { get; set; }

    /// <summary>Harbor admin username (default "admin").</summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>Public URL of the Harbor registry (e.g. "https://registry.example.com").</summary>
    public string? RegistryUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public ClusterComponent ClusterComponent { get; set; } = null!;
    public CnpgDatabase? CnpgDatabase { get; set; }
    public StorageLink? StorageLink { get; set; }
}
