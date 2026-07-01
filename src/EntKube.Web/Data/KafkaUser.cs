namespace EntKube.Web.Data;

/// <summary>
/// A SCRAM-SHA-512 Kafka user managed on a <see cref="KafkaCluster"/> via a
/// Strimzi <c>KafkaUser</c> CR. Strimzi generates the password and stores it in a
/// Kubernetes Secret named after the user; EntKube reads that secret when syncing
/// an app binding. Requires the owning cluster to have <c>AuthEnabled</c>.
///
/// Authorization is expressed as simple per-topic ACLs: the user may produce to
/// <see cref="ProducerTopics"/> and consume from <see cref="ConsumerTopics"/>
/// (comma-separated topic names, or "*" for all). Consumers also get Read/Describe
/// on <see cref="ConsumerGroup"/>. A <see cref="SuperUser"/> bypasses ACLs entirely.
/// </summary>
public class KafkaUser
{
    public Guid Id { get; set; }

    public Guid KafkaClusterId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The username (KafkaUser metadata.name). DNS-safe.</summary>
    public required string Username { get; set; }

    /// <summary>Comma-separated topics the user may produce to ("*" = all). Empty = none.</summary>
    public string? ProducerTopics { get; set; }

    /// <summary>Comma-separated topics the user may consume from ("*" = all). Empty = none.</summary>
    public string? ConsumerTopics { get; set; }

    /// <summary>Consumer group pattern the user may join (default "*").</summary>
    public string? ConsumerGroup { get; set; } = "*";

    /// <summary>When true, the user is granted full access (superuser) and ACLs are ignored.</summary>
    public bool SuperUser { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public KafkaCluster KafkaCluster { get; set; } = null!;

    /// <summary>Name of the Strimzi-generated Secret holding this user's SCRAM password.</summary>
    public string CredentialsSecretName => Username;
}
