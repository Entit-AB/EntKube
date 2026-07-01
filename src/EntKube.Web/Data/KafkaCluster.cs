namespace EntKube.Web.Data;

public enum KafkaClusterStatus
{
    Creating,
    Running,
    Updating,
    Failed,
    Deleting
}

/// <summary>
/// A managed Apache Kafka cluster provisioned via the Strimzi operator. EntKube
/// applies a <c>Kafka</c> CR plus a dual-role <c>KafkaNodePool</c> CR (KRaft mode,
/// no ZooKeeper). Strimzi reconciles these into broker/controller StatefulSets.
///
/// Clients connect through the bootstrap service. When <see cref="AuthEnabled"/>
/// is set, a TLS listener with SCRAM-SHA-512 authentication is exposed on 9093 and
/// per-app credentials are issued as <see cref="KafkaUser"/>s; otherwise a plaintext
/// internal listener on 9092 is used (relying on NetworkPolicy for isolation).
/// </summary>
public class KafkaCluster
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid KubernetesClusterId { get; set; }

    /// <summary>The Kafka resource name (metadata.name). Lowercase, DNS-safe.</summary>
    public required string Name { get; set; }

    public required string Namespace { get; set; }

    /// <summary>Apache Kafka version (must be supported by the installed Strimzi operator, e.g. "4.3.0").</summary>
    public required string KafkaVersion { get; set; }

    /// <summary>Number of dual-role (broker + controller) nodes. Min 3 for production HA.</summary>
    public int Replicas { get; set; } = 3;

    /// <summary>PVC storage size per broker (e.g. "20Gi").</summary>
    public required string StorageSize { get; set; }

    /// <summary>Optional StorageClass override. Null uses the cluster default.</summary>
    public string? StorageClass { get; set; }

    /// <summary>Optional CPU request per broker (e.g. "500m").</summary>
    public string? CpuRequest { get; set; }

    /// <summary>Optional memory request per broker (e.g. "1Gi").</summary>
    public string? MemoryRequest { get; set; }

    /// <summary>Optional memory limit per broker (e.g. "2Gi").</summary>
    public string? MemoryLimit { get; set; }

    /// <summary>
    /// When true, a TLS listener with SCRAM-SHA-512 authentication is exposed on 9093
    /// and clients must authenticate as a <see cref="KafkaUser"/>. When false, only a
    /// plaintext internal listener on 9092 is exposed.
    /// </summary>
    public bool AuthEnabled { get; set; } = true;

    public KafkaClusterStatus Status { get; set; } = KafkaClusterStatus.Creating;

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Tenant Tenant { get; set; } = null!;
    public KubernetesCluster KubernetesCluster { get; set; } = null!;
    public ICollection<KafkaTopic> Topics { get; set; } = [];
    public ICollection<KafkaUser> Users { get; set; } = [];

    /// <summary>The plaintext client port (internal listener).</summary>
    public const int PlaintextPort = 9092;

    /// <summary>The TLS + SCRAM client port.</summary>
    public const int TlsPort = 9093;

    /// <summary>DNS name of the bootstrap service Strimzi creates for this cluster.</summary>
    public string BootstrapHost => $"{Name}-kafka-bootstrap.{Namespace}.svc.cluster.local";

    /// <summary>host:port a client should use, honouring the auth/TLS listener choice.</summary>
    public string BootstrapAddress => $"{BootstrapHost}:{(AuthEnabled ? TlsPort : PlaintextPort)}";
}
