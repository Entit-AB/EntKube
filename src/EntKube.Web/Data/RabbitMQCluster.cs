namespace EntKube.Web.Data;

public enum RabbitMQClusterStatus
{
    Creating,
    Running,
    Failed,
    Deleting
}

/// <summary>
/// A managed RabbitMQ cluster provisioned via the RabbitMQ Cluster Operator.
/// EntKube owns the full lifecycle: create, delete, backup (definitions export), restore.
/// Topology (vhosts, queues, exchanges) is managed declaratively by the RabbitMQ
/// Messaging Topology Operator and discovered live from Kubernetes.
/// </summary>
public class RabbitMQCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KubernetesClusterId { get; set; }

    /// <summary>
    /// The name of the RabbitmqCluster resource (metadata.name). Lowercase, DNS-safe.
    /// The operator derives all child resource names from this (secret, service, StatefulSet).
    /// </summary>
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    /// <summary>
    /// RabbitMQ version tag (e.g. "3.13", "4.0"). Maps to the rabbitmq:{version}-management image.
    /// </summary>
    public required string RabbitMQVersion { get; set; }

    /// <summary>
    /// Number of RabbitMQ nodes. 1 = standalone, 3 = quorum-capable HA cluster.
    /// </summary>
    public int Replicas { get; set; } = 3;

    /// <summary>PVC storage size per node (e.g. "10Gi").</summary>
    public required string StorageSize { get; set; }

    /// <summary>Optional StorageClass name. When null the cluster default is used.</summary>
    public string? StorageClass { get; set; }

    /// <summary>
    /// Optional link to an S3 bucket used for definitions.json backups.
    /// When set, the MessagingTab shows backup/restore controls.
    /// </summary>
    public Guid? StorageLinkId { get; set; }

    /// <summary>Cron schedule for automated backups. Null = on-demand only.</summary>
    public string? BackupSchedule { get; set; }

    /// <summary>Maximum number of completed backup records to retain in the DB.</summary>
    public int MaxBackups { get; set; } = 10;

    public RabbitMQClusterStatus Status { get; set; } = RabbitMQClusterStatus.Creating;

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public StorageLink? StorageLink { get; set; }
    public ICollection<RabbitMQBackup> Backups { get; set; } = [];
}
