using EntKube.Web.Services;

namespace EntKube.Telemetry;

/// <summary>
/// Encodes a query body into a URL parameter, so a request that logically has a body can still be sent as
/// a GET.
///
/// This exists for the Kubernetes API server's proxy. It maps HTTP methods onto RBAC verbs on the
/// <c>services/proxy</c> subresource — GET needs <c>get</c>, POST needs <c>create</c> — so a kubeconfig
/// perfectly able to read through the proxy can be refused the moment a query is sent as a POST. Loki and
/// Prometheus never hit this because their APIs are GET-only; ours has bodies, so the body travels in the
/// URL instead.
/// </summary>
public static class NodeQuery
{
    /// <summary>Query-string parameter carrying the encoded body.</summary>
    public const string Parameter = "q";

    public static string Encode<T>(T body) => Base64Url(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body));

    public static T? Decode<T>(string? encoded) =>
        string.IsNullOrEmpty(encoded) ? default
        : System.Text.Json.JsonSerializer.Deserialize<T>(FromBase64Url(encoded));

    // Base64url: '+' and '/' are not safe unescaped in a URL, and '=' padding is noise here.
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}

/// <summary>Transport-level constants shared by the management plane, the node, and a querier.</summary>
public static class NodeApi
{
    /// <summary>
    /// Header carrying the node's own credential.
    ///
    /// Deliberately NOT <c>Authorization</c> when the caller arrives through the Kubernetes API server's
    /// proxy — there, <c>Authorization</c> belongs to the API server, and a caller that overwrites it is
    /// rejected by the API server rather than reaching the node at all. That failure presents as a 401 and
    /// looks exactly like the node refusing the token, which it never saw. Unknown headers are forwarded
    /// untouched, so this one arrives intact.
    /// </summary>
    public const string TokenHeader = "X-EntKube-Ingest-Key";
}

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
