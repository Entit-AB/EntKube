namespace EntKube.Web.Data;

/// <summary>
/// The cloud provider or platform for a storage link.
/// </summary>
public enum StorageProvider
{
    /// <summary>MinIO running on the cluster (auto-discovered).</summary>
    MinIO,

    /// <summary>Amazon Web Services S3.</summary>
    AwsS3,

    /// <summary>Microsoft Azure Blob Storage / Storage Account.</summary>
    AzureStorage,

    /// <summary>Cleura (City Cloud) S3-compatible object storage.</summary>
    CleuraS3,

    /// <summary>
    /// CubeFS object gateway running on the cluster (auto-discovered / component-linked).
    /// S3-compatible; reached the same way as MinIO — through the Kubernetes API server
    /// proxy when its endpoint is a cluster-internal service URL. Provides a single,
    /// cloud-portable S3 story regardless of what the underlying OpenStack exposes.
    /// </summary>
    CubeFS
}

/// <summary>
/// A storage link represents a connection to an object storage provider.
/// It can be an auto-discovered MinIO instance running on a cluster, or an
/// external cloud storage (AWS S3, Azure Storage, Cleura S3) registered
/// manually. External links store their credentials in the vault.
///
/// This entity is the source of truth for "which storage does this tenant use?"
/// — regardless of where it's hosted.
/// </summary>
public class StorageLink
{
    public Guid Id { get; set; }

    /// <summary>The tenant that owns this storage link.</summary>
    public Guid TenantId { get; set; }

    /// <summary>The environment this storage is associated with.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>Which provider hosts this storage.</summary>
    public StorageProvider Provider { get; set; }

    /// <summary>Human-friendly name for display (e.g. "Production Backups", "Media Assets").</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The endpoint URL for S3-compatible access.
    /// For AWS: "https://s3.eu-west-1.amazonaws.com"
    /// For MinIO: "http://minio.minio.svc.cluster.local:9000"
    /// For Cleura: "https://s3-<region>.cloudferro.com"
    /// For Azure: "https://<account>.blob.core.windows.net"
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The bucket or container name.
    /// For Azure this is the container name within the storage account.
    /// </summary>
    public string? BucketName { get; set; }

    /// <summary>
    /// Region for the storage (e.g. "eu-west-1", "swedencentral").
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// For MinIO links discovered from a cluster, reference the cluster component.
    /// Null for external providers.
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// For Cleura S3 links, reference the OpenStack connection used to manage
    /// buckets and credentials. Required when Provider is CleuraS3.
    /// </summary>
    public Guid? OpenStackConnectionId { get; set; }

    /// <summary>
    /// Notes or description for this storage link.
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
    public ClusterComponent? Component { get; set; }
    public OpenStackConnection? OpenStackConnection { get; set; }
    public ICollection<StorageBinding> StorageBindings { get; set; } = [];
}
