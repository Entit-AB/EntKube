namespace EntKube.Web.Data;

/// <summary>
/// Links a RabbitMQ vhost/queue/exchange to an AppDeployment.
/// The platform syncs AMQP connection details (host, port, vhost, credentials,
/// queue/exchange name) into a Kubernetes Secret in the app's namespace,
/// mirroring the DatabaseBinding pattern.
/// </summary>
public class MessagingBinding
{
    public Guid Id { get; set; }

    public Guid RabbitMQClusterId { get; set; }

    public Guid AppDeploymentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The RabbitMQ virtual host the app connects to.</summary>
    public required string Vhost { get; set; }

    /// <summary>Queue name the app consumes from, if this is a queue binding.</summary>
    public string? QueueName { get; set; }

    /// <summary>Exchange name the app publishes to, if this is an exchange binding.</summary>
    public string? ExchangeName { get; set; }

    /// <summary>
    /// Target Kubernetes Secret name in the app deployment's namespace.
    /// The secret will contain RABBITMQ_HOST, RABBITMQ_PORT, RABBITMQ_VHOST,
    /// RABBITMQ_USERNAME, RABBITMQ_PASSWORD, RABBITMQ_URL, and RABBITMQ_QUEUE
    /// or RABBITMQ_EXCHANGE if applicable.
    /// </summary>
    public required string KubernetesSecretName { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public RabbitMQCluster Cluster { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
