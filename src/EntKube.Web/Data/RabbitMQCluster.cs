using System.ComponentModel.DataAnnotations.Schema;

namespace EntKube.Web.Data;

public enum RabbitMQClusterStatus
{
    Creating,
    Running,
    Failed,
    Deleting
}

/// <summary>
/// A RabbitMQ cluster known to EntKube.
///
/// Two flavours, distinguished by <see cref="IsOperatorManaged"/>:
///
/// <para><b>Operator-managed</b> (the default) — provisioned by EntKube via the RabbitMQ
/// Cluster Operator (RabbitmqCluster CRD). EntKube owns the full lifecycle: create, update,
/// delete, backup, restore. Topology (vhosts, queues, exchanges) is managed declaratively by
/// the RabbitMQ Messaging Topology Operator and discovered live from Kubernetes.</para>
///
/// <para><b>External</b> — a broker installed outside EntKube (typically a Helm chart such as
/// bitnami/rabbitmq) that runs as a plain StatefulSet with no CRs at all. EntKube adopts these
/// read-only: monitoring, topology inspection, vhost/user/permission management and definitions
/// backup all work (driven through <c>rabbitmqctl</c> on the broker pod), but infrastructure
/// lifecycle — scaling, version changes, deletion — stays with whoever owns the Helm release.
/// Because nothing about the child resource names is derivable from the cluster name, discovery
/// records them explicitly in the <c>StatefulSetName</c>/<c>ServiceName</c>/<c>Credentials*</c>
/// fields below.</para>
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

    // ── External (non-operator) brokers ───────────────────────────────────────

    /// <summary>
    /// True when this cluster is backed by a RabbitmqCluster CR that EntKube may create,
    /// patch and delete. False for brokers adopted from a plain StatefulSet (Helm charts),
    /// where EntKube must not touch the workload. Defaults to true so pre-existing rows —
    /// all of which came from the operator — keep their behaviour.
    /// </summary>
    public bool IsOperatorManaged { get; set; } = true;

    /// <summary>
    /// External only: the StatefulSet running the broker. Pod names derive from it, so an
    /// arbitrary chart's naming (e.g. "mq-rabbitmq-0") is honoured rather than guessed.
    /// </summary>
    public string? StatefulSetName { get; set; }

    /// <summary>
    /// External only: the Service that exposes AMQP, used when building app connection
    /// strings. The operator's convention is "{name}-svc"; charts vary.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>External only: the Secret holding the admin credentials.</summary>
    public string? CredentialsSecretName { get; set; }

    /// <summary>
    /// External only: the key inside <see cref="CredentialsSecretName"/> holding the admin
    /// username. Null when the chart passes the username as a literal instead of a secret
    /// reference — see <see cref="AdminUsername"/>.
    /// </summary>
    public string? CredentialsUsernameKey { get; set; }

    /// <summary>External only: the key inside <see cref="CredentialsSecretName"/> holding the password.</summary>
    public string? CredentialsPasswordKey { get; set; }

    /// <summary>
    /// External only: literal admin username, used when the chart sets it as a plain env
    /// value rather than a secret key (bitnami/rabbitmq does exactly this).
    /// </summary>
    public string? AdminUsername { get; set; }

    /// <summary>
    /// The pod to exec <c>rabbitmqctl</c> against. The cluster operator names broker pods
    /// "{cluster}-server-N"; a StatefulSet names them "{sts}-N".
    /// </summary>
    [NotMapped]
    public string PrimaryPodName =>
        IsOperatorManaged ? $"{Name}-server-0" : $"{StatefulSetName ?? Name}-0";

    /// <summary>The in-cluster Service name that serves AMQP on 5672.</summary>
    [NotMapped]
    public string AmqpServiceName =>
        IsOperatorManaged ? $"{Name}-svc" : (ServiceName ?? Name);

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public StorageLink? StorageLink { get; set; }
    public ICollection<RabbitMQBackup> Backups { get; set; } = [];
}
