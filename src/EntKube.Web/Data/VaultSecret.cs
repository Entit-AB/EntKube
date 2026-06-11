namespace EntKube.Web.Data;

/// <summary>
/// An individual secret stored in a tenant's vault. The value is encrypted
/// with the tenant's DEK (AES-256-GCM). A secret is scoped to either an App
/// or a ClusterComponent — never both, never neither.
///
/// When SyncToKubernetes is enabled, the platform will create/update a Kubernetes
/// Secret resource matching KubernetesSecretName in the specified namespace.
/// </summary>
public class VaultSecret
{
    public Guid Id { get; set; }

    public Guid VaultId { get; set; }

    /// <summary>
    /// The secret key name (e.g. "DATABASE_PASSWORD", "API_KEY").
    /// Must be unique within its scope (app or component).
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The secret value, encrypted with the tenant's DEK.
    /// Combined format: ciphertext + 16-byte GCM authentication tag.
    /// </summary>
    public required byte[] EncryptedValue { get; set; }

    /// <summary>
    /// The nonce used when encrypting the value. Unique per encryption operation.
    /// </summary>
    public required byte[] Nonce { get; set; }

    /// <summary>
    /// If set, this secret belongs to a customer app.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a cluster component.
    /// </summary>
    public Guid? ComponentId { get; set; }

    /// <summary>
    /// If set, this secret belongs to an external storage link.
    /// </summary>
    public Guid? StorageLinkId { get; set; }

    /// <summary>
    /// If set, this secret belongs to an OpenStack connection.
    /// </summary>
    public Guid? OpenStackConnectionId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a CNPG cluster (e.g. superuser credentials).
    /// </summary>
    public Guid? CnpgClusterId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a CNPG database (connection credentials).
    /// </summary>
    public Guid? CnpgDatabaseId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a MongoDB database (connection credentials).
    /// </summary>
    public Guid? MongoDatabaseId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a managed MongoDB cluster (e.g. the admin password).
    /// </summary>
    public Guid? MongoClusterId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a registered (non-CNPG) PostgreSQL database.
    /// </summary>
    public Guid? RegisteredPostgresDatabaseId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a managed RabbitMQ cluster (e.g. admin credentials).
    /// </summary>
    public Guid? RabbitMQClusterId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a managed Redis cluster (connection credentials and auth password).
    /// </summary>
    public Guid? RedisClusterId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a VPN remote endpoint (e.g. PSK or certificate).
    /// </summary>
    public Guid? VpnRemoteEndpointId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a Git repository credential (PAT, SSH key, or password).
    /// Secret names follow the convention: "PAT", "SSH_PRIVATE_KEY", "PASSWORD".
    /// </summary>
    public Guid? GitRepositoryId { get; set; }

    /// <summary>
    /// If set, this secret belongs to a customer-level git credential.
    /// Secret names follow the same convention as GitRepositoryId: "PAT", "SSH_PRIVATE_KEY", "PASSWORD".
    /// </summary>
    public Guid? CustomerGitCredentialId { get; set; }

    /// <summary>
    /// When true, this secret will be synced to Kubernetes as a Secret resource.
    /// </summary>
    public bool SyncToKubernetes { get; set; }

    /// <summary>
    /// The target Kubernetes cluster for the synced secret.
    /// Required for app-scoped secrets where the cluster cannot be inferred
    /// from a component or storage link relationship.
    /// </summary>
    public Guid? KubernetesClusterId { get; set; }

    /// <summary>
    /// The target Kubernetes Secret name (e.g. "my-app-secrets").
    /// Multiple vault secrets can target the same K8s Secret as different data keys.
    /// </summary>
    public string? KubernetesSecretName { get; set; }

    /// <summary>
    /// The target Kubernetes namespace for the synced secret.
    /// </summary>
    public string? KubernetesNamespace { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The identity (email) of the user who last updated this secret's value.
    /// Null for secrets created before audit tracking was added.
    /// </summary>
    public string? UpdatedBy { get; set; }

    // Navigation
    public SecretVault Vault { get; set; } = null!;
    public App? App { get; set; }
    public ClusterComponent? Component { get; set; }
    public StorageLink? StorageLink { get; set; }
    public CnpgCluster? CnpgCluster { get; set; }
    public CnpgDatabase? CnpgDatabase { get; set; }
    public MongoDatabase? MongoDatabase { get; set; }
    public MongoCluster? MongoCluster { get; set; }
    public RegisteredPostgresDatabase? RegisteredPostgresDatabase { get; set; }
    public KubernetesCluster? KubernetesCluster { get; set; }
    public RabbitMQCluster? RabbitMQCluster { get; set; }
    public RedisCluster? RedisCluster { get; set; }
    public VpnRemoteEndpoint? VpnRemoteEndpoint { get; set; }
    public GitRepository? GitRepository { get; set; }
    public CustomerGitCredential? CustomerGitCredential { get; set; }

    public ICollection<VaultSecretVersion> Versions { get; set; } = [];
}
