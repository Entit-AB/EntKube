namespace EntKube.Web.Data;

/// <summary>
/// An Elasticsearch ingest pipeline: the processors every document passes through on its way into
/// an index.
///
/// <para><b>Why it belongs beside the tiers.</b> Node roles decide where a document lands and the
/// lifecycle policy decides how long it stays; the pipeline decides whether it is worth anything
/// when it gets there. A log line indexed as one opaque <c>message</c> field cannot be filtered by
/// level, grouped by service, or aged by its own timestamp rather than its arrival time.</para>
///
/// <para>The common processors are fields here. Anything beyond them goes in as raw JSON, the same
/// bargain the policy editor elsewhere in EntKube strikes: the routine case without writing JSON,
/// and the whole of Elasticsearch for everything else.</para>
/// </summary>
public class ElasticsearchIngestPipeline
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    /// <summary>The pipeline's id in Elasticsearch, and what an index template points at.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// A field holding the event's own timestamp, parsed into <c>@timestamp</c>. Without it every
    /// document is dated by when it arrived, which is the wrong axis the moment anything is delayed.
    /// </summary>
    public string? TimestampField { get; set; }

    /// <summary>Date formats to try, comma-separated. "ISO8601" covers most of what ships as JSON.</summary>
    public string TimestampFormats { get; set; } = "ISO8601";

    /// <summary>Field a grok pattern is applied to, usually "message".</summary>
    public string? GrokField { get; set; }

    /// <summary>The grok pattern itself, e.g. <c>%{LOGLEVEL:level} %{GREEDYDATA:msg}</c>.</summary>
    public string? GrokPattern { get; set; }

    /// <summary>Fields to rename, as "from:to" pairs separated by commas.</summary>
    public string? RenameFields { get; set; }

    /// <summary>Fields to drop, comma-separated — the ones that are noise, or should not be stored at all.</summary>
    public string? RemoveFields { get; set; }

    /// <summary>Constants to stamp on every document, as "key=value" pairs separated by commas.</summary>
    public string? SetFields { get; set; }

    /// <summary>Raw processor JSON, appended after the ones above. An array, or empty.</summary>
    public string? CustomProcessorsJson { get; set; }

    public DateTime? LastAppliedAt { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;
}
