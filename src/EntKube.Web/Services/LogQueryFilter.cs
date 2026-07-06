namespace EntKube.Web.Services;

/// <summary>
/// The filter criteria for a log query, bundled into one value so the query methods don't carry a
/// long positional parameter list (namespaces/pod/container/text/level/attr…) threaded through the
/// service, the facade, and the SQL builder. <paramref name="Namespaces"/> is required; everything
/// else is optional. Time range, limit, and bucket count stay as explicit method parameters since
/// they're query-shape, not filter, concerns.
/// </summary>
public sealed record LogQueryFilter
{
    public required IReadOnlyCollection<string> Namespaces { get; init; }
    public string? Pod { get; init; }
    public string? Container { get; init; }
    public string? Text { get; init; }
    public LogLevel MinLevel { get; init; } = LogLevel.None;
    public string? AttrKey { get; init; }
    public string? AttrValue { get; init; }

    /// <summary>Convenience for the common single-namespace case.</summary>
    public static LogQueryFilter ForNamespace(string ns) => new() { Namespaces = [ns] };
}
