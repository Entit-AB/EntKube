using EntKube.Web.Services;

namespace EntKube.Telemetry;

/// <summary>
/// Wire shapes for the in-cluster telemetry node's query API — the contract between the management plane
/// and a node, and between a querier and its indexer.
///
/// They live in the engine assembly rather than on either side so there is exactly one definition. Three
/// separate callers serialize these; a field added on one side and forgotten on another would not fail to
/// compile, it would silently deserialize to a default — a filter quietly ignored, a limit quietly wrong.
/// </summary>
public sealed record LogSearchBody(
    DateTime From,
    DateTime To,
    IReadOnlyCollection<string>? Namespaces = null,
    string? Pod = null,
    string? Container = null,
    string? Text = null,
    LogLevel MinLevel = LogLevel.None,
    string? AttrKey = null,
    string? AttrValue = null,
    int Limit = 200,
    int Buckets = 48)
{
    public LogQueryFilter ToFilter() => new()
    {
        Namespaces = Namespaces ?? [],
        Pod = Pod,
        Container = Container,
        Text = Text,
        MinLevel = MinLevel,
        AttrKey = AttrKey,
        AttrValue = AttrValue,
    };

    /// <summary>Builds a body from a filter. Not named <c>From</c> — that is already the time bound.</summary>
    public static LogSearchBody ForFilter(LogQueryFilter filter, DateTime from, DateTime to, int limit = 200, int buckets = 48)
        => new(from, to, [.. filter.Namespaces], filter.Pod, filter.Container, filter.Text, filter.MinLevel,
               filter.AttrKey, filter.AttrValue, limit, buckets);
}

/// <summary>
/// One body covering every trace query, rather than seven near-identical records. The node's trace routes
/// are an internal contract between components of the same build, so a single shape is simpler to keep in
/// step than seven.
/// </summary>
public sealed class TraceQueryBody
{
    public string? Service { get; init; }
    public string? TraceId { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public double MinDurationMs { get; init; }
    public bool ErrorsOnly { get; init; }
    public int Limit { get; init; } = 50;
    public int Buckets { get; init; } = 48;
    public int WindowMinutes { get; init; } = 60;
    public IReadOnlyList<string>? Namespaces { get; init; }
    public string? PodPattern { get; init; }
}
