using System.ComponentModel.DataAnnotations.Schema;

namespace EntKube.Web.Data;

/// <summary>
/// Type of CNPG backup — on-demand (user-triggered) or scheduled (cron).
/// </summary>
public enum CnpgBackupType
{
    OnDemand,
    Scheduled
}

/// <summary>
/// Status of a CNPG backup operation.
/// </summary>
public enum CnpgBackupStatus
{
    Running,
    Completed,
    Failed
}

/// <summary>
/// A backup record for a managed CNPG cluster. Represents a Backup CR
/// in Kubernetes that triggers Barman to take a base backup to the
/// configured S3 bucket. Combined with continuous WAL archiving, this
/// enables point-in-time recovery (PITR) to any moment between backups.
/// </summary>
public class CnpgBackup
{
    public Guid Id { get; set; }

    /// <summary>
    /// The CNPG cluster this backup belongs to.
    /// </summary>
    public Guid CnpgClusterId { get; set; }

    /// <summary>
    /// The Kubernetes Backup resource name (e.g. "my-cluster-20260517-120000").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Whether this was triggered manually or by a schedule.
    /// </summary>
    public CnpgBackupType Type { get; set; } = CnpgBackupType.OnDemand;

    /// <summary>
    /// Current status of the backup.
    /// </summary>
    public CnpgBackupStatus Status { get; set; } = CnpgBackupStatus.Running;

    /// <summary>
    /// When the backup started.
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the backup completed (null if still running or failed before completion).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Backup size in bytes (reported by Barman after completion).
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Error message if the backup failed.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The barman-cloud backup identifier (e.g. "20260519T130201").
    /// Populated from the Backup CR status.backupId at display time.
    /// Not persisted — only available when the backup list comes from live K8s.
    /// </summary>
    [NotMapped]
    public string? BarmanId { get; set; }

    // Navigation
    public CnpgCluster CnpgCluster { get; set; } = null!;
}
