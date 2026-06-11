namespace EntKube.Web.Data;

/// <summary>
/// Connects a managed database (CNPG or MongoDB) to an app deployment so the
/// platform can sync credentials into the app's namespace automatically.
///
/// When a binding exists and SyncEnabled is true, any credential sync (including
/// after password rotation) will also push a Kubernetes Secret named
/// KubernetesSecretName into the AppDeployment's namespace on its cluster.
/// The app can then mount the secret as env vars without knowing which database
/// provider backs it.
/// </summary>
public class DatabaseBinding
{
    public Guid Id { get; set; }

    /// <summary>
    /// The CNPG database whose credentials should be synced to the app.
    /// Mutually exclusive with MongoDatabaseId.
    /// </summary>
    public Guid? CnpgDatabaseId { get; set; }

    /// <summary>
    /// The MongoDB database whose credentials should be synced to the app.
    /// Mutually exclusive with CnpgDatabaseId.
    /// </summary>
    public Guid? MongoDatabaseId { get; set; }

    /// <summary>
    /// The registered (non-CNPG) PostgreSQL database whose credentials should be synced.
    /// Mutually exclusive with CnpgDatabaseId and MongoDatabaseId.
    /// </summary>
    public Guid? RegisteredPostgresDatabaseId { get; set; }

    /// <summary>
    /// The deployment that consumes the database credentials.
    /// The secret is written into this deployment's namespace on its cluster.
    /// </summary>
    public Guid AppDeploymentId { get; set; }

    /// <summary>
    /// The Kubernetes Secret name to create in the app's namespace.
    /// Choose something meaningful to the app (e.g. "billing-db", "app-postgres").
    /// </summary>
    public required string KubernetesSecretName { get; set; }

    /// <summary>
    /// When true, credential syncs (including password rotations) automatically
    /// propagate to this app's namespace. Disable to pause propagation without
    /// removing the binding record.
    /// </summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Last time credentials were successfully written to the app's namespace.
    /// Null means the binding exists but has not been synced yet.
    /// </summary>
    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public CnpgDatabase? CnpgDatabase { get; set; }
    public MongoDatabase? MongoDatabase { get; set; }
    public RegisteredPostgresDatabase? RegisteredPostgresDatabase { get; set; }
    public AppDeployment AppDeployment { get; set; } = null!;
}
