namespace EntKube.Web.Data;

/// <summary>
/// The lifecycle status of a managed Percona MongoDB cluster. Tracks where the
/// cluster is in the provisioning/operational/teardown cycle.
/// </summary>
public enum MongoClusterStatus
{
    Creating,
    Running,
    Upgrading,
    Restoring,
    Failed,
    Deleting
}

/// <summary>
/// A managed Percona Server for MongoDB cluster that EntKube provisions and controls.
/// Each cluster lives on a Kubernetes cluster where the Percona MongoDB operator is installed.
/// Optionally backed by a StorageLink (S3 bucket) for automated backups and point-in-time restore.
///
/// The cluster is represented as a PerconaServerMongoDB CRD (psmdb.percona.com/v1) in Kubernetes.
/// EntKube owns the full lifecycle: create, upgrade, backup, restore, delete.
/// </summary>
public class MongoCluster
{
    public Guid Id { get; set; }

    /// <summary>
    /// The tenant that owns this cluster.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The Kubernetes cluster where this MongoDB cluster runs.
    /// Must have the Percona MongoDB operator installed.
    /// </summary>
    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// The name of the PerconaServerMongoDB resource in Kubernetes (metadata.name).
    /// Lowercase, DNS-safe, max 63 chars.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Kubernetes namespace where the cluster lives.
    /// </summary>
    public required string Namespace { get; set; }

    /// <summary>
    /// The MongoDB version (e.g. "8.0", "7.0", "6.0").
    /// Maps to the Percona Server for MongoDB container image tag.
    /// </summary>
    public required string MongoVersion { get; set; }

    /// <summary>
    /// Number of replica set members (1 = standalone, 3 = HA with automatic failover).
    /// </summary>
    public int Members { get; set; } = 3;

    /// <summary>
    /// PVC storage size for each member (e.g. "10Gi", "50Gi").
    /// </summary>
    public required string StorageSize { get; set; }

    /// <summary>
    /// CPU request for each MongoDB pod (e.g. "500m", "2"). Null means no request is set.
    /// </summary>
    public string? CpuRequest { get; set; }

    /// <summary>
    /// CPU limit for each MongoDB pod (e.g. "2", "4"). Null means no limit is set.
    /// </summary>
    public string? CpuLimit { get; set; }

    /// <summary>
    /// Memory request for each MongoDB pod (e.g. "2Gi", "8Gi"). Null means no request is set.
    /// </summary>
    public string? MemoryRequest { get; set; }

    /// <summary>
    /// Memory limit for each MongoDB pod (e.g. "8Gi", "16Gi"). Null means no limit is set.
    /// </summary>
    public string? MemoryLimit { get; set; }

    /// <summary>
    /// Optional link to an S3 bucket for automated backups and point-in-time restore.
    /// When set, the cluster is configured with Percona Backup for MongoDB (PBM).
    /// </summary>
    public Guid? StorageLinkId { get; set; }

    /// <summary>
    /// Cron schedule for automated backups (e.g. "0 2 * * *" for daily at 2 AM).
    /// Null means no scheduled backups — only on-demand.
    /// </summary>
    public string? BackupSchedule { get; set; }

    /// <summary>
    /// Number of days to retain backups. Percona Backup for MongoDB uses this
    /// to auto-delete old backups. Default is 30 days.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Maximum number of completed backup Jobs to keep in Kubernetes. When exceeded
    /// during a cleanup, the oldest completed Jobs are deleted from K8s and the DB.
    /// Does not affect backup data in S3 — use S3 lifecycle rules or RetentionDays for that.
    /// </summary>
    public int MaxBackups { get; set; } = 20;

    /// <summary>
    /// True when the cluster was registered from an existing deployment (not provisioned by EntKube).
    /// Delete should offer "Unregister" (remove from EntKube only) in addition to full deletion.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// MongoDB connection URI for external clusters not managed by the Community Operator
    /// (e.g. mongodb://user:pass@host:27017/?authSource=admin). When set, enables S3 backup,
    /// database discovery, and migration to a managed cluster without pod exec.
    /// Treat as a sensitive credential — do not log or expose in responses.
    /// </summary>
    public string? ExternalUri { get; set; }

    /// <summary>
    /// Current lifecycle status of the cluster.
    /// </summary>
    public MongoClusterStatus Status { get; set; } = MongoClusterStatus.Creating;

    /// <summary>
    /// The last error encountered during a lifecycle operation.
    /// Cleared on successful operations.
    /// </summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public StorageLink? StorageLink { get; set; }
    public ICollection<MongoDatabase> Databases { get; set; } = [];
    public ICollection<MongoBackup> Backups { get; set; } = [];
}

/// <summary>
/// The status of a database within a managed MongoDB cluster.
/// </summary>
public enum MongoDatabaseStatus
{
    Creating,
    Ready,
    Failed
}

/// <summary>
/// A database (and its owner user) within a managed Percona MongoDB cluster.
/// MongoDB databases are created implicitly when data is first written, but
/// EntKube creates a dedicated user with readWrite permissions and stores
/// the connection string in the vault.
/// </summary>
public class MongoDatabase
{
    public Guid Id { get; set; }
    public Guid MongoClusterId { get; set; }

    /// <summary>
    /// The database name in MongoDB.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The username that owns this database (has readWrite role).
    /// </summary>
    public required string Owner { get; set; }

    public MongoDatabaseStatus Status { get; set; } = MongoDatabaseStatus.Creating;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public MongoCluster MongoCluster { get; set; } = null!;
    public ICollection<DatabaseBinding> DatabaseBindings { get; set; } = [];
}

/// <summary>
/// The type of a MongoDB backup — scheduled or triggered on-demand.
/// </summary>
public enum MongoBackupType
{
    Scheduled,
    OnDemand
}

/// <summary>
/// The status of a MongoDB backup operation.
/// </summary>
public enum MongoBackupStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>
/// A backup record for a managed Percona MongoDB cluster.
/// Corresponds to a PerconaServerMongoDBBackup CRD in Kubernetes.
/// </summary>
public class MongoBackup
{
    public Guid Id { get; set; }
    public Guid MongoClusterId { get; set; }

    /// <summary>
    /// The name of the backup CR in Kubernetes.
    /// </summary>
    public required string Name { get; set; }

    public MongoBackupType Type { get; set; } = MongoBackupType.OnDemand;
    public MongoBackupStatus Status { get; set; } = MongoBackupStatus.Running;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long? SizeBytes { get; set; }

    // Navigation
    public MongoCluster MongoCluster { get; set; } = null!;
}
