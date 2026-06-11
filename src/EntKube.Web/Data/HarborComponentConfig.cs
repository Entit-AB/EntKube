namespace EntKube.Web.Data;

/// <summary>
/// Harbor-specific configuration for a managed Harbor instance.
/// Tracks the backing CNPG database, S3 storage bucket, admin credentials, and registry URL.
///
/// Admin password is stored as a component vault secret (key: HARBOR_ADMIN_PASSWORD).
/// S3 credentials are stored as component vault secrets (keys: harbor-s3-access-key,
/// harbor-s3-secret-key) and injected into Helm values at install time.
/// CNPG database password is stored as a component vault secret (key: harbor-db-password).
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
