namespace EntKube.Web.Data;

/// <summary>
/// One index lifecycle policy, and the index template that binds it to a pattern, for a managed
/// Elasticsearch cluster.
///
/// <para>Node tiers on their own only give data somewhere to move to — nothing moves it. The
/// lifecycle policy is what rolls an index over before it grows past a shard size worth searching,
/// walks it down hot → warm → cold as it ages, and finally deletes it. Without one, a hot/warm/cold
/// topology fills the hot tier and stops, which looks exactly like a disk sizing mistake.</para>
///
/// <para>ECK has a declarative route for this (StackConfigPolicy), but it requires an ECK
/// Enterprise licence. These are applied instead by a short-lived Job inside the cluster that PUTs
/// them to the Elasticsearch API as the <c>elastic</c> user — ILM itself is free — so the
/// credentials never leave the cluster and nothing here needs a licence.</para>
/// </summary>
public class ElasticsearchIlmPolicy
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    /// <summary>Policy and index-template name, e.g. "app-logs". Lowercase, no spaces.</summary>
    public required string Name { get; set; }

    /// <summary>Index pattern the template claims, e.g. "logs-app-*".</summary>
    public required string IndexPattern { get; set; }

    /// <summary>
    /// Create the template as a data stream template. Data streams are append-only and roll over
    /// without an alias to maintain, which is what makes rollover work unattended.
    /// </summary>
    public bool UseDataStream { get; set; } = true;

    public int Shards { get; set; } = 1;

    public int Replicas { get; set; } = 1;

    /// <summary>Roll over once a primary shard reaches this size, in GB.</summary>
    public int RolloverMaxPrimaryShardGb { get; set; } = 50;

    /// <summary>Roll over once the write index reaches this age, in days.</summary>
    public int RolloverMaxAgeDays { get; set; } = 7;

    /// <summary>Move to the warm tier after this many days. Null omits the warm phase.</summary>
    public int? WarmAfterDays { get; set; }

    /// <summary>Move to the cold tier after this many days. Null omits the cold phase.</summary>
    public int? ColdAfterDays { get; set; }

    /// <summary>Delete after this many days. Null keeps the data forever — say so out loud in the UI.</summary>
    public int? DeleteAfterDays { get; set; }

    /// <summary>
    /// An ingest pipeline every document written through this template passes through, by name.
    /// Null leaves the data exactly as the client sent it.
    /// </summary>
    public string? DefaultPipelineName { get; set; }

    /// <summary>Index template priority. Higher wins when several templates match a pattern.</summary>
    public int TemplatePriority { get; set; } = 200;

    /// <summary>When this policy was last pushed to Elasticsearch successfully.</summary>
    public DateTime? LastAppliedAt { get; set; }

    /// <summary>Why the last apply failed, if it did.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;
}
