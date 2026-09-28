namespace EntKube.Web.Data;

/// <summary>
/// Links an <see cref="ElasticsearchCluster"/> to an AppDeployment. On sync, the cluster's endpoint,
/// the selected <see cref="ElasticsearchUser"/>'s credentials and the cluster's CA certificate are
/// written as a Kubernetes Secret into the app deployment's namespace, so the application connects
/// without anybody copying a password between namespaces by hand.
///
/// <para>Keys written: <c>ELASTICSEARCH_URL</c>, <c>ELASTICSEARCH_USERNAME</c>,
/// <c>ELASTICSEARCH_PASSWORD</c> and <c>ELASTICSEARCH_CA_CRT</c>. The CA matters: the HTTP layer is
/// served with the operator's own certificate, so a client that does not trust it either fails or
/// is talked into skipping verification — and the second one is how a search cluster ends up
/// reachable by anything on the network.</para>
/// </summary>
public class ElasticsearchBinding
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    public Guid AppDeploymentId { get; set; }

    /// <summary>The user whose credentials are synced. Required — there is no anonymous access.</summary>
    public Guid ElasticsearchUserId { get; set; }

    /// <summary>Kubernetes Secret name created in the app deployment's namespace.</summary>
    public required string KubernetesSecretName { get; set; }

    public bool SyncEnabled { get; set; } = true;

    public DateTime? LastSyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;
    public ElasticsearchUser ElasticsearchUser { get; set; } = null!;
    public AppDeployment AppDeployment { get; set; } = null!;
}
