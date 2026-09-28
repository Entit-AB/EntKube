namespace EntKube.Web.Data;

/// <summary>
/// A Kibana data view: the saved object that tells Kibana an index pattern exists, which field
/// carries its time, and therefore what Discover and every dashboard can be built on.
///
/// <para><b>Why EntKube creates them.</b> A person given a Kibana login and an index pattern still
/// signs in to an empty screen, because Kibana does not know the pattern is there until somebody
/// says so — and saying so is the one step that requires knowing which field is the timestamp.
/// Creating the data view alongside the account turns "you have access" into "there is something
/// to look at".</para>
///
/// <para>The id is EntKube's, not Kibana's generated one, so a re-apply updates the same object and
/// a delete knows what to remove without first searching for it by title.</para>
/// </summary>
public class ElasticsearchDataView
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ElasticsearchClusterId { get; set; }

    /// <summary>
    /// The Kibana space it belongs to, by <see cref="ElasticsearchKibanaSpace.SpaceId"/>. Null puts
    /// it in the default space — a data view lives in one space and is invisible from the others.
    /// </summary>
    public string? SpaceId { get; set; }

    /// <summary>The index pattern, e.g. "logs-orders-*". Kibana calls this the title.</summary>
    public required string Title { get; set; }

    /// <summary>What it is called in Kibana's lists. Defaults to the pattern itself.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// The field Kibana filters and sorts by time on. Empty means the data view has no time field,
    /// which is right for reference data and wrong for anything that arrives continuously.
    /// </summary>
    public string TimeFieldName { get; set; } = "@timestamp";

    public DateTime? LastAppliedAt { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ElasticsearchCluster ElasticsearchCluster { get; set; } = null!;

    /// <summary>
    /// The id Kibana stores it under. Derived from the row's own id so it is stable across
    /// re-applies and unique without asking Kibana what it chose.
    /// </summary>
    public string KibanaObjectId => $"entkube-{Id}";
}
