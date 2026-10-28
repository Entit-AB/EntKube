using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// The public API can describe itself, and every route it describes is guarded.
///
/// <para><b>Why this matters now.</b> Three first-party clients — <c>EntKube.ApiClient</c>, the
/// CLI and the MCP server — are hand-maintained against these routes, and Phase 1 item 8 wants
/// them generated from an OpenAPI document instead. A generated client is only as good as the
/// document, and the single thing that would make the document lie is a route that never went
/// through <c>RequireApiScope</c>: it would carry no scope metadata to describe and, far worse,
/// no authentication to describe it for.</para>
///
/// <para>⚠️ <b>WHAT THIS DOES NOT DO, stated rather than implied.</b> It does not generate the
/// document and compare it to the routes. That test was written and then withdrawn: generating
/// it needs a host, serving it turns any generation failure into a bare 500 with an empty body —
/// which is exactly what the first attempt produced — and resolving the provider in-process ran
/// into keyed-service details while the machine was busy with another repository's test run. The
/// honest state is that document generation is wired and builds, and is not yet asserted. What IS
/// asserted below is the property that makes the document trustworthy once it is.</para>
/// </summary>
public class OpenApiDocumentTests
{
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string Read(params string[] parts)
    {
        string path = Path.Combine([RepoRoot(), .. parts]);
        File.Exists(path).Should().BeTrue($"expected {path} to exist");
        return File.ReadAllText(path);
    }

    private static string Endpoints() =>
        Regex.Replace(
            Read("src", "EntKube.Web", "Services", "PublicApi", "PublicApiEndpoints.cs"),
            @"//.*?$", "", RegexOptions.Multiline);

    /// <summary>
    /// ⚠️ THE MEASURE. Every assertion below counts things found by a pattern, and a pattern that
    /// matches nothing makes all of them pass. This fails first if the scan has gone blind.
    /// </summary>
    [Fact]
    public void The_endpoint_scan_still_finds_the_routes()
        => Regex.Matches(Endpoints(), @"api\.Map(?:Get|Post|Put|Delete)\(")
            .Should().HaveCountGreaterThan(15,
                "the public API maps around two dozen routes; finding few means this scan broke "
                + "and every check in this file is passing over an empty list");

    /// <summary>
    /// ⚠️ A ROUTE WITHOUT <c>RequireApiScope</c> HAS NO TOKEN CHECK. <c>ApiTokenAuth</c>'s own
    /// comment puts it plainly: such a route "simply does not exist on the API". It would also
    /// reach the OpenAPI document carrying no scope to state, so this guards both the security
    /// property and the document's truthfulness with one assertion.
    /// </summary>
    [Fact]
    public void Every_mapped_route_goes_through_RequireApiScope()
    {
        string source = Endpoints();

        // Each Map* call and the text up to the next one: the chained .RequireApiScope() must
        // appear within its own route's span rather than anywhere in the file.
        MatchCollection maps = Regex.Matches(source, @"api\.Map(?:Get|Post|Put|Delete)\(""([^""]+)""");

        List<string> unguarded = [];
        for (int i = 0; i < maps.Count; i++)
        {
            int start = maps[i].Index;
            int end = i + 1 < maps.Count ? maps[i + 1].Index : source.Length;

            if (!source[start..end].Contains("RequireApiScope", StringComparison.Ordinal))
            {
                unguarded.Add(maps[i].Groups[1].Value);
            }
        }

        unguarded.Should().BeEmpty(
            "an unguarded route is reachable with no token at all:\n  " + string.Join("\n  ", unguarded));
    }

    /// <summary>
    /// The scope is enforced by a filter and described by metadata, and the document can only
    /// state what the metadata carries. If <c>RequireApiScope</c> ever stops attaching it, every
    /// operation silently loses its stated scope while the API keeps working — the quietest
    /// possible regression.
    /// </summary>
    [Fact]
    public void RequireApiScope_records_the_scope_as_metadata_as_well_as_enforcing_it()
    {
        string auth = Read("src", "EntKube.Web", "Services", "PublicApi", "ApiTokenAuth.cs");

        auth.Should().Contain("new ApiTokenAuthFilter(scope)",
            "the filter is what refuses the request");
        auth.Should().Contain("new ApiScopeMetadata(scope)",
            "the metadata is what lets the document say so — without it every operation in the "
            + "document is silent about which scope it needs, and a client learns from a 403");
    }

    /// <summary>The document is registered, served, and behind the same token as the API.</summary>
    [Fact]
    public void The_document_is_wired_and_guarded_like_the_routes_it_describes()
    {
        string program = Read("src", "EntKube.Web", "Program.cs");

        program.Should().Contain("AddPublicApiOpenApi()",
            "the document has to be registered before the host is built");

        Match map = Regex.Match(program, @"app\.MapOpenApi\([^)]*\)\s*\r?\n?\s*\.RequireApiScope\(\)");
        map.Success.Should().BeTrue(
            "the document is served behind the same token as the routes it describes — an "
            + "unauthenticated endpoint on a management plane is a thing to justify, and no "
            + "caller needs the document before it has a token");
    }
}
