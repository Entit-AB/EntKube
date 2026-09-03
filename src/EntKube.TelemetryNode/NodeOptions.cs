namespace EntKube.TelemetryNode;

/// <summary>Which half of the telemetry plane this pod is. One image, two deployments.</summary>
public enum NodeRole
{
    /// <summary>Receives OTLP, owns the hot active index and the warm sealed segments, seals to object storage.</summary>
    Indexer,

    /// <summary>Answers queries. Reads cold segments from object storage into a bounded local cache.</summary>
    Querier,
}

/// <summary>
/// Everything the node needs that cannot be inferred: which role to run, which cluster and tenant it
/// serves, and the tokens callers must present. Bound from the <c>Node</c> configuration section, which in
/// a cluster arrives as <c>Node__*</c> environment variables from a ConfigMap and a Secret.
///
/// The identity fields matter more than they look. Segments are partitioned by tenant and the management
/// plane addresses queries by cluster, so a node that does not know who it is would either refuse every
/// query or, worse, file another tenant's data under its own. They are required, and startup fails loudly
/// rather than defaulting them.
/// </summary>
public sealed class NodeOptions
{
    public NodeRole Role { get; init; } = NodeRole.Indexer;

    /// <summary>The tenant whose telemetry this node stores. Every segment it seals belongs to this tenant.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The cluster this node serves — the id the management plane uses to address it.</summary>
    public Guid ClusterId { get; init; }

    /// <summary>
    /// Bearer token the in-cluster collector must present to write. In-cluster this is a far weaker
    /// requirement than it was over the internet — the listener is a ClusterIP Service reachable only from
    /// inside the cluster — but an unauthenticated ingest endpoint is still an unauthenticated ingest
    /// endpoint, and any pod can reach a ClusterIP.
    /// </summary>
    public string IngestToken { get; init; } = "";

    /// <summary>Bearer token the management plane presents when querying. Empty disables the query API.</summary>
    public string QueryToken { get; init; } = "";

    /// <summary>Where the SQLite segment catalog lives. Defaults under the data volume, beside the indexes.
    /// Indexer only — a querier borrows the catalog from the indexer over HTTP.</summary>
    public string? CatalogPath { get; init; }

    /// <summary>
    /// In-cluster URL of the indexer's Service, e.g. <c>http://entkube-telemetry-indexer:8080</c>. Required
    /// for the querier role: it is where the segment list comes from, and where hot-tier queries are sent
    /// for the unsealed events no other pod can see. Ignored by the indexer.
    /// </summary>
    public string IndexerUrl { get; init; } = "";

    /// <summary>Throws with an actionable message when the node has not been told who it is.</summary>
    public void Validate()
    {
        List<string> missing = [];
        if (TenantId == Guid.Empty) missing.Add("Node__TenantId");
        if (ClusterId == Guid.Empty) missing.Add("Node__ClusterId");
        if (Role == NodeRole.Indexer && string.IsNullOrWhiteSpace(IngestToken)) missing.Add("Node__IngestToken");
        if (Role == NodeRole.Querier && string.IsNullOrWhiteSpace(IndexerUrl)) missing.Add("Node__IndexerUrl");
        // The querier authenticates to the indexer with the same token the management plane presents to
        // it, so an empty one leaves it unable to read either the segment list or the hot tier.
        if (Role == NodeRole.Querier && string.IsNullOrWhiteSpace(QueryToken)) missing.Add("Node__QueryToken");

        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"The telemetry node is missing required configuration: {string.Join(", ", missing)}. " +
                "These identify which tenant's and cluster's telemetry this pod owns; without them it " +
                "cannot safely store or serve anything.");
    }
}
