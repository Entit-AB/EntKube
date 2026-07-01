namespace EntKube.Web.Data;

/// <summary>
/// A Kafka topic managed on a <see cref="KafkaCluster"/> via a Strimzi
/// <c>KafkaTopic</c> CR. The Strimzi topic operator reconciles this into a real
/// topic on the broker. EntKube tracks the desired spec so the topic list
/// survives even if the operator is temporarily unreachable.
/// </summary>
public class KafkaTopic
{
    public Guid Id { get; set; }

    public Guid KafkaClusterId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The topic name (spec.topicName / metadata.name). DNS-safe.</summary>
    public required string Name { get; set; }

    /// <summary>Number of partitions.</summary>
    public int Partitions { get; set; } = 3;

    /// <summary>Replication factor. Must be &lt;= the cluster's broker count.</summary>
    public int Replicas { get; set; } = 3;

    /// <summary>Optional retention in milliseconds (config retention.ms). Null uses the broker default.</summary>
    public long? RetentionMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KafkaCluster KafkaCluster { get; set; } = null!;
}
