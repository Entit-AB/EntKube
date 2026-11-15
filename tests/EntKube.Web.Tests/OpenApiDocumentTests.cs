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
/// <para><b>This file scans the source; it does not generate the document.</b> That is now
/// <see cref="GeneratedOpenApiDocumentTests"/>'s job, which boots the application and compares the
/// real document against the real route table.</para>
///
/// <para>⚠️ <b>The split is worth keeping, and worth the reason.</b> These checks are instant and
/// have no host, so they stay useful when the suite is run in a hurry. But they could not have
/// found either of the two defects generation found — the document threw on every request, and it
/// described sixteen routes belonging to other subsystems — and the earlier version of this
/// comment claimed generation was "wired and builds" on the strength of exactly these scans.
/// <b>A source scan can only ever prove the code was written; it cannot prove it runs.</b></para>
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
    /// operation silently loses its stated scope while the API keeps working.
    ///
    /// <para>⚠️ <b>This test caught the change that made it necessary to rewrite it.</b> Its
    /// earlier form looked for the two literals <c>new ApiTokenAuthFilter(scope)</c> and
    /// <c>new ApiScopeMetadata(scope)</c> — satisfied by the same argument being passed twice,
    /// which is precisely the arrangement that can drift. It now asserts the stronger structural
    /// property: the metadata is constructed from the FILTER, so there is no edit to that method
    /// which changes what is enforced and leaves what is described behind.</para>
    ///
    /// <para>The behavioural counterpart is
    /// <c>GeneratedOpenApiDocumentTests.The_scope_each_operation_states_is_the_scope_the_server_enforces</c>,
    /// which calls every documented route with a token holding exactly the stated scope and with
    /// one holding a different scope. That is the test that actually proves the two agree; this
    /// one states the shape that makes disagreement impossible to write.</para>
    /// </summary>
    [Fact]
    public void RequireApiScope_derives_the_described_scope_from_the_enforcing_filter()
    {
        string auth = Read("src", "EntKube.Web", "Services", "PublicApi", "ApiTokenAuth.cs");

        auth.Should().Contain("ApiTokenAuthFilter filter = new(scope);",
            "the filter is what refuses the request, and it has to be named to be read back from");
        auth.Should().Contain(".AddEndpointFilter(filter)",
            "the named filter is the one actually installed on the route — constructing a second "
            + "one for the metadata would reintroduce exactly the divergence this prevents");
        auth.Should().Contain("new ApiScopeMetadata(filter.RequiredScope)",
            "the metadata is what lets the document say which scope a route needs, and taking it "
            + "off the filter rather than from a second copy of the argument is what stops the "
            + "description from drifting away from the enforcement");
    }

    /// <summary>The document is registered, served, and behind the same token as the API.</summary>
    [Fact]
    public void The_document_is_wired_and_guarded_like_the_routes_it_describes()
    {
        string program = Read("src", "EntKube.Web", "Program.cs");

        program.Should().Contain("AddPublicApiOpenApi()",
            "the document has to be registered before the host is built");

        // ⚠️ The argument list is deliberately not pinned. RequireApiScope now also takes the
        // operation's name, so a regex demanding `RequireApiScope()` with nothing in it failed on
        // a change that did not touch what this test is about. What is asserted is that the
        // document route goes through the guard at all; the scope it passes — none, meaning "any
        // live token" — is read off the generated document by ApiScopeAdvertisementTests, where
        // it is a fact about the server rather than about this file's punctuation.
        Match map = Regex.Match(program, @"app\.MapOpenApi\([^)]*\)\s*\r?\n?\s*\.RequireApiScope\(");
        map.Success.Should().BeTrue(
            "the document is served behind the same token as the routes it describes — an "
            + "unauthenticated endpoint on a management plane is a thing to justify, and no "
            + "caller needs the document before it has a token");
    }
}
