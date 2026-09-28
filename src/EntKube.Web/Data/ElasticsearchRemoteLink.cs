namespace EntKube.Web.Data;

/// <summary>
/// A one-way cross-cluster search link: the local cluster may search indices that live on the
/// remote one, under an alias, without any of that data being copied.
///
/// <para><b>What it is for.</b> Keeping each environment's (or each customer's) data in its own
/// cluster, and still answering a question that spans them from one Kibana — <c>prod:logs-*</c>
/// beside <c>staging:logs-*</c>. The alternative is one cluster holding everyone's data, which is
/// the arrangement every isolation decision in this feature exists to avoid.</para>
///
/// <para>Access is granted by a cross-cluster API key that ECK creates and rotates, scoped to the
/// index patterns named here — so the searching cluster reaches exactly those and nothing else.
/// Replication is deliberately not offered: cross-cluster replication is a Platinum feature of
/// Elasticsearch, while cross-cluster search is not.</para>
/// </summary>
public class ElasticsearchRemoteLink
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The cluster doing the searching — the one whose Kibana runs the query.</summary>
    public Guid LocalClusterId { get; set; }

    /// <summary>The cluster holding the data.</summary>
    public Guid RemoteClusterId { get; set; }

    /// <summary>
    /// What the remote is called in a query, as in <c>{alias}:logs-*</c>. Lowercase, and fixed once
    /// created: every saved search and dashboard that spans clusters has it written into them.
    /// </summary>
    public required string Alias { get; set; }

    /// <summary>
    /// Index patterns the local cluster may search, comma-separated. "*" is everything on the
    /// remote, which is rarely what is meant.
    /// </summary>
    public string SearchIndexPatterns { get; set; } = "*";

    public DateTime? LastAppliedAt { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster LocalCluster { get; set; } = null!;
    public ElasticsearchCluster RemoteCluster { get; set; } = null!;

    /// <summary>The patterns as a list, trimmed and without the empties.</summary>
    public IReadOnlyList<string> Patterns =>
        [.. (SearchIndexPatterns ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
