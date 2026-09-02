using EntKube.Telemetry;
using EntKube.Web.Services;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// The encoding that lets a query with a body be sent as a GET.
///
/// It exists because the Kubernetes API server's proxy maps HTTP methods onto RBAC verbs on the
/// <c>services/proxy</c> subresource: a GET needs <c>get</c>, a POST needs <c>create</c>. A kubeconfig that
/// reads a cluster perfectly well can lack the second, and the refusal arrives as a bare 401/403 that looks
/// like the node rejecting a token. Both ends are .NET, so the round-trip is what has to hold.
/// </summary>
public class NodeQueryCodecTests
{
    [Fact]
    public void A_log_search_survives_the_round_trip()
    {
        LogSearchBody original = new(
            new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
            Namespaces: ["prod", "staging"],
            Pod: "api-7d9f",
            Container: "api",
            Text: "payment gateway",
            MinLevel: LogLevel.Warn,
            Limit: 42,
            Buckets: 24);

        LogSearchBody? decoded = NodeQuery.Decode<LogSearchBody>(NodeQuery.Encode(original));

        decoded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void A_trace_query_survives_the_round_trip()
    {
        TraceQueryBody original = new()
        {
            Service = "payments",
            From = new DateTime(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc),
            MinDurationMs = 250.5,
            ErrorsOnly = true,
            Limit = 25,
            Namespaces = ["prod"],
            PodPattern = "api-*",
        };

        TraceQueryBody? decoded = NodeQuery.Decode<TraceQueryBody>(NodeQuery.Encode(original));

        decoded.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void The_encoding_is_safe_to_put_in_a_url_unescaped()
    {
        // base64url, not base64: '+' and '/' change meaning in a query string, and '=' padding is noise.
        string encoded = NodeQuery.Encode(new LogSearchBody(
            DateTime.UtcNow, DateTime.UtcNow, Text: "a/b+c d?e&f=g", Namespaces: ["ns"]));

        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        Uri.EscapeDataString(encoded).Should().Be(encoded, "it must survive a URL untouched");
    }

    [Fact]
    public void Free_text_that_looks_like_a_query_string_round_trips_intact()
    {
        // The filter is user input and routinely contains characters that would otherwise terminate or
        // reinterpret the query string. Encoding it is what makes that a non-issue.
        const string nasty = "level=error&ns=../../etc passwd?x=1#frag";
        LogSearchBody original = new(DateTime.UtcNow, DateTime.UtcNow, Namespaces: ["ns"], Text: nasty);

        NodeQuery.Decode<LogSearchBody>(NodeQuery.Encode(original))!.Text.Should().Be(nasty);
    }

    [Fact]
    public void An_absent_parameter_decodes_to_nothing_rather_than_throwing()
    {
        NodeQuery.Decode<LogSearchBody>(null).Should().BeNull();
        NodeQuery.Decode<LogSearchBody>("").Should().BeNull();
    }
}
