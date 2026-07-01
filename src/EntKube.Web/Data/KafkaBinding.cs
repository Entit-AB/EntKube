namespace EntKube.Web.Data;

/// <summary>
/// Links a <see cref="KafkaCluster"/> to an AppDeployment. On sync, the bootstrap
/// address and (for SCRAM clusters) the selected <see cref="KafkaUser"/>'s SASL
/// credentials plus the cluster CA certificate are written as a Kubernetes Secret
/// into the app deployment's namespace, so the app can connect without knowing
/// which Kafka cluster backs it.
///
/// Keys written: KAFKA_BOOTSTRAP_SERVERS, KAFKA_SECURITY_PROTOCOL, and — when the
/// cluster requires auth — KAFKA_SASL_MECHANISM, KAFKA_SASL_USERNAME,
/// KAFKA_SASL_PASSWORD, and KAFKA_CA_CRT.
/// </summary>
public class KafkaBinding
{
    public Guid Id { get; set; }

    public Guid KafkaClusterId { get; set; }

    public Guid AppDeploymentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>
    /// The Kafka user whose SASL credentials are synced. Required for SCRAM
    /// (AuthEnabled) clusters; null for plaintext clusters (bootstrap only).
    /// </summary>
    public Guid? KafkaUserId { get; set; }

    /// <summary>Kubernetes Secret name created in the app deployment's namespace.</summary>
    public required string KubernetesSecretName { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KafkaCluster KafkaCluster { get; set; } = null!;
    public KafkaUser? KafkaUser { get; set; }
    public AppDeployment AppDeployment { get; set; } = null!;
}
