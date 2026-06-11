namespace EntKube.Web.Data;

/// <summary>
/// The lifecycle status of a managed CNPG cluster. Tracks where the cluster
/// is in the provisioning/operational/teardown cycle.
/// </summary>
public enum CnpgClusterStatus
{
    Creating,
    Running,
    Upgrading,
    Restoring,
    Failed,
    Deleting
}

/// <summary>
/// A managed CloudNativePG cluster that EntKube provisions and controls.
/// Each cluster lives on a Kubernetes cluster where the CNPG operator is installed.
/// Optionally backed by a StorageLink (S3 bucket) for Barman backups and PITR.
///
/// The cluster is represented as a CNPG Cluster CRD in Kubernetes.
/// EntKube owns the full lifecycle: create, upgrade, backup, restore, delete.
/// </summary>
public class CnpgCluster
{
    public Guid Id { get; set; }

    /// <summary>
    /// The tenant that owns this cluster.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// The Kubernetes cluster where this CNPG cluster runs.
    /// Must have the cloudnative-pg operator installed.
    /// </summary>
    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// The name of the CNPG Cluster resource in Kubernetes (metadata.name).
    /// Lowercase, DNS-safe, max 63 chars.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The Kubernetes namespace where the cluster lives.
    /// </summary>
    public required string Namespace { get; set; }

    /// <summary>
    /// The major PostgreSQL version (e.g. "18", "17", "16").
    /// Maps to the CNPG container image tag.
    /// </summary>
    public required string PostgresVersion { get; set; }

    /// <summary>
    /// Number of PostgreSQL instances (1 = standalone, 3 = HA with streaming replication).
    /// </summary>
    public int Instances { get; set; } = 3;

    /// <summary>
    /// PVC storage size for each instance (e.g. "10Gi", "50Gi").
    /// </summary>
    public required string StorageSize { get; set; }

    /// <summary>
    /// Optional link to an S3 bucket for Barman backup/restore.
    /// When set, the cluster is configured with continuous WAL archiving
    /// and on-demand/scheduled base backups.
    /// </summary>
    public Guid? StorageLinkId { get; set; }

    /// <summary>
    /// Cron schedule for automated backups (e.g. "0 0 2 * * *" for daily at 2 AM).
    /// Null means no scheduled backups — only on-demand.
    /// </summary>
    public string? BackupSchedule { get; set; }

    /// <summary>
    /// Number of days to retain backups. The Barman Cloud Plugin uses this as the
    /// recovery window (e.g. "30d"). Expired backups are cleaned up automatically.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Maximum number of completed Backup CRs to keep in Kubernetes. When exceeded
    /// during a cleanup, the oldest completed backups are deleted from K8s and the DB.
    /// Does not affect WAL archiving or Barman's own retention — use RetentionDays for that.
    /// </summary>
    public int MaxBackups { get; set; } = 20;

    /// <summary>
    /// True when the cluster was registered from an existing CNPG deployment (not provisioned by EntKube).
    /// Delete should offer "Unregister" (remove from EntKube only) in addition to full deletion.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// Current lifecycle status of the cluster.
    /// </summary>
    public CnpgClusterStatus Status { get; set; } = CnpgClusterStatus.Creating;

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
    public ICollection<CnpgDatabase> Databases { get; set; } = [];
    public ICollection<CnpgBackup> Backups { get; set; } = [];
}
