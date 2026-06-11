namespace EntKube.Web.Data;

public enum RabbitMQBackupStatus
{
    Creating,
    Ready,
    Failed
}

/// <summary>
/// A RabbitMQ definitions backup — the full broker topology (vhosts, exchanges, queues,
/// bindings, policies, users, permissions) exported as definitions.json via rabbitmqctl
/// and stored in an S3 bucket. Used for disaster recovery and cross-environment migration.
/// </summary>
public class RabbitMQBackup
{
    public Guid Id { get; set; }

    public Guid RabbitMQClusterId { get; set; }

    public Guid TenantId { get; set; }

    public Guid? StorageLinkId { get; set; }

    /// <summary>S3 object key (e.g. "rabbitmq/my-cluster/20260601-120000.json").</summary>
    public required string ObjectKey { get; set; }

    /// <summary>Snapshot of the cluster name at backup time, for display after deletion.</summary>
    public required string ClusterName { get; set; }

    public long SizeBytes { get; set; }

    public RabbitMQBackupStatus Status { get; set; } = RabbitMQBackupStatus.Creating;

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    // Navigation
    public RabbitMQCluster Cluster { get; set; } = null!;
    public StorageLink? StorageLink { get; set; }
}
