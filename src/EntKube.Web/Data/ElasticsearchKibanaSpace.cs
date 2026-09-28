namespace EntKube.Web.Data;

/// <summary>
/// A Kibana space on a managed cluster: a partition of the UI with its own dashboards, data views
/// and saved searches.
///
/// <para><b>Why EntKube tracks them.</b> A space is what lets several teams share one Elasticsearch
/// cluster without seeing each other's work — and, paired with a user whose Kibana privileges name
/// that space, without being able to. Left to Kibana, spaces are created by whoever gets there
/// first and are invisible from here, so a user could be scoped to a space that no longer exists
/// and simply see nothing on sign-in.</para>
/// </summary>
public class ElasticsearchKibanaSpace
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    /// <summary>
    /// Kibana's own identifier for the space, used in its URLs and in the privilege that grants
    /// access to it. Lowercase, DNS-ish, and fixed once created — Kibana will not rename one.
    /// </summary>
    public required string SpaceId { get; set; }

    /// <summary>What it is called in the space menu.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTime? LastAppliedAt { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;
}
