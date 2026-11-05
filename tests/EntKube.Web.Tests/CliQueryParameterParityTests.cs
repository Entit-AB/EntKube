using System.Text.RegularExpressions;
using EntKube.Cli;
using FluentAssertions;
using Microsoft.OpenApi;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The query string the CLI sends and the query parameters the API declares are the same set.
///
/// <para><b>Why this is its own ratchet.</b> <c>CliEndpointParityTests</c> compares which ROUTES a
/// client can reach and <c>CliColumnParityTests</c> compares what it reads out of the ANSWER —
/// and both of them throw the query string away (<c>built.Split('?')[0]</c>). So the part of a
/// request that decides WHICH rows come back was compared by nothing.</para>
///
/// <para>⚠️ <b>The interesting direction is the one that fails silently.</b> Minimal APIs bind
/// query parameters by name and simply ignore a name they do not have. So
/// <c>?appid=</c> for a handler expecting <c>appId</c>, or a parameter renamed on the server with
/// the CLI left behind, does not error: the request succeeds and returns the UNFILTERED list. The
/// operator sees a plausible table and believes it is scoped to the app they asked for. That is the
/// same shape as a dead table column — it looks like it works — and it is worse, because the rows
/// are real rows about other things rather than visible dashes.</para>
///
/// <para>The other direction is a guard rather than a fix: a REQUIRED parameter the CLI omits makes
/// every invocation of that command answer 400, which is loud. Only one operation on this API has a
/// required query parameter today and the CLI does send it, so that half is pinned against a
/// regression and <see cref="Both_directions_had_something_to_check"/> says so rather than letting
/// an empty set read as success.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class CliQueryParameterParityTests(GeneratedOpenApiDocumentFixture fixture)
{
    private sealed record Sent(string Name, string Route, string[] Parameters);

    /// <summary>
    /// ⚠️ The query string is taken by RUNNING <c>BuildPath</c>, as the sibling ratchets take the
    /// path — a regex over the source found three of sixteen paths, because C# interpolation holes
    /// contain quotes.
    /// </summary>
    private static List<Sent> CliQueries()
    {
        List<Sent> sent = [];

        foreach (CliCommand command in CliCommands.All)
        {
            string built;
            try
            {
                built = command.BuildPath(CliArgs.Parse(
                [
                    "--cluster", "{}", "--id", "{}", "--app", "{}",
                    "--workload", "{}", "--environment", "{}",
                ]));
            }
            catch (CliUsageException)
            {
                continue;
            }

            string[] halves = built.Split('?');
            if (halves.Length < 2 || halves[1].Length == 0) continue;

            string verb = command.Verb switch
            {
                CliVerb.Get => "GET",
                CliVerb.Post => "POST",
                CliVerb.Delete => "DELETE",
                _ => throw new InvalidOperationException(
                    $"CliVerb.{command.Verb} is not mapped here; add it rather than letting it "
                    + "read as a GET"),
            };

            sent.Add(new Sent(
                string.Join(' ', command.Path),
                $"{verb} {Regex.Replace(halves[0], @"\{[^}]+\}", "{}")}",
                [.. halves[1].Split('&').Select(p => p.Split('=')[0])]));
        }

        return sent;
    }

    /// <summary>Every query parameter the document declares for an operation, required or not.</summary>
    private Dictionary<string, HashSet<string>> DeclaredByRoute()
    {
        Dictionary<string, HashSet<string>> declared = [];

        foreach ((string path, IOpenApiPathItem item) in fixture.Document!.Paths ?? [])
        {
            string flat = Regex.Replace(path, @"\{[^}]+\}", "{}");

            foreach ((HttpMethod verb, OpenApiOperation operation) in item.Operations ?? [])
            {
                HashSet<string> names = new(StringComparer.Ordinal);

                // ⚠️ Path-item parameters as well as the operation's own. A parameter declared
                // once for every method on a path is a legal second location, and reading only
                // the operation's list would call a correctly-declared parameter undeclared.
                foreach (IOpenApiParameter parameter in
                         (operation.Parameters ?? []).Concat(item.Parameters ?? []))
                {
                    if (parameter.In == ParameterLocation.Query && parameter.Name is { Length: > 0 } name)
                    {
                        names.Add(name);
                    }
                }

                declared[$"{verb.Method.ToUpperInvariant()} {flat}"] = names;
            }
        }

        return declared;
    }

    /// <summary>
    /// ⚠️ The measure. Both assertions below walk sets that could be empty — no command building a
    /// query string, or no operation declaring a parameter — and each would then pass over nothing
    /// and report a healthy client.
    /// </summary>
    [Fact]
    public void Both_directions_had_something_to_check()
    {
        fixture.GenerationFailure.Should().BeNull();

        List<Sent> sent = CliQueries();

        sent.Should().HaveCountGreaterThan(
            2, "several commands filter or parameterise through the query string");

        sent.SelectMany(s => s.Parameters).Should().NotBeEmpty();

        fixture.Operations
            .Where(o => o.RequiredQueryParameters.Count > 0)
            .Should().NotBeEmpty(
                "otherwise the required-parameter assertion is vacuous — and today exactly one "
                + "operation has one, so this is the thin half of the ratchet and says so");

        DeclaredByRoute().Values.SelectMany(v => v).Should().NotBeEmpty(
            "a document whose operations declare no query parameters at all would make every "
            + "parameter the CLI sends look undeclared, or nothing look wrong, depending on the "
            + "direction — either way the comparison would be about a parsing failure");
    }

    /// <summary>
    /// ⚠️ The one that catches the silent failure: a parameter the server does not bind is ignored,
    /// and the command returns an UNFILTERED answer that looks filtered.
    /// </summary>
    [Fact]
    public void Every_query_parameter_the_cli_sends_is_one_the_api_declares()
    {
        Dictionary<string, HashSet<string>> declared = DeclaredByRoute();
        List<string> ignored = [];

        foreach (Sent sent in CliQueries())
        {
            if (!declared.TryGetValue(sent.Route, out HashSet<string>? names))
            {
                ignored.Add($"`{sent.Name}` sends a query to {sent.Route}, which the document does "
                            + "not describe at all");
                continue;
            }

            foreach (string parameter in sent.Parameters.Where(p => !names.Contains(p)))
            {
                ignored.Add($"`{sent.Name}` sends '{parameter}' to {sent.Route}, which binds "
                            + $"[{string.Join(", ", names.OrderBy(n => n, StringComparer.Ordinal))}]");
            }
        }

        ignored.Should().BeEmpty(
            "minimal APIs ignore a query parameter they do not have, so each of these is a filter "
            + "the operator believes is applied and the server never saw:\n  "
            + string.Join("\n  ", ignored));
    }

    /// <summary>
    /// The guard half: a required parameter the CLI omits answers 400 every time. Loud rather than
    /// silent, but worth pinning — the command would be entirely unusable and nothing else here
    /// would notice.
    /// </summary>
    [Fact]
    public void Every_required_query_parameter_is_one_the_cli_sends()
    {
        List<Sent> sent = CliQueries();
        List<string> missing = [];

        foreach (DocumentedOperation operation in
                 fixture.Operations.Where(o => o.RequiredQueryParameters.Count > 0))
        {
            string route = $"{operation.Method} {Regex.Replace(operation.Path, @"\{[^}]+\}", "{}")}";

            // A route with no command at all is CliEndpointParityTests' business, not this one.
            Sent? command = sent.FirstOrDefault(s => s.Route == route);
            if (command is null) continue;

            foreach (string required in
                     operation.RequiredQueryParameters.Where(r => !command.Parameters.Contains(r)))
            {
                missing.Add($"`{command.Name}` does not send '{required}', which {route} requires");
            }
        }

        missing.Should().BeEmpty(
            "every invocation of these would answer 400 before reaching the handler:\n  "
            + string.Join("\n  ", missing));
    }
}
