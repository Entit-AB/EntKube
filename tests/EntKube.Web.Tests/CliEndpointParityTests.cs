using EntKube.Cli;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Every route on the public API can be reached from the CLI.
///
/// <para><b>Why this exists.</b> Phase 1's exit criterion is that <c>EntKube.Cli</c> can do
/// everything the Tenants UI can (docs/decomposition.md §6). Nothing was checking the weaker
/// property underneath it — that the CLI can reach the API at all — and seven of twenty-three
/// endpoints had no command. ⚠️ One of them was <c>POST /apps/{appId}/secrets/sync</c>, added in
/// the same week by the author who then did not add its command. The gap was found by comparing
/// the two surfaces, not by anyone noticing.</para>
///
/// <para>⚠️ A ROUTE IS THE PRODUCT'S SURFACE; A COMMAND IS WHETHER ANYONE CAN USE IT. An endpoint
/// with no client is indistinguishable, from an operator's seat, from an endpoint that does not
/// exist — and it is worse than one that does not exist, because it looks like progress.</para>
///
/// <para>⚠️ <b>The API side used to be a regex over one source file, and it was blind in five ways
/// at once.</b> A pattern matching <c>api.MapGet("...")</c> cannot see a route behind a sub-group,
/// a route registered through a receiver not named <c>api</c>, a template held in a const, a
/// <c>MapPatch</c>, or a <c>MapMethods</c> — and because it built a path to
/// <c>PublicApiEndpoints.cs</c> alone, it could not see <c>/scim/v2</c> at all. Seven operations
/// sat outside this ratchet entirely while it reported itself healthy. It now reads
/// <see cref="GeneratedOpenApiDocumentFixture.ApiSurface"/>, which comes out of the router: the
/// thing that actually serves requests cannot be wrong about what it serves.</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class CliEndpointParityTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Named exemptions, not silent gaps.
    ///
    /// <para><c>PUT /api/v1/cost-rates/{clusterId}</c> takes a <c>CostRateInput</c> body and the
    /// shared API client deliberately has no body-carrying verb, so reaching it needs request-body
    /// support in the CLI designed alongside the command — a larger change than the command.</para>
    ///
    /// <para>⚠️ <b>The reason first written here was wrong, in the reassuring direction.</b> It
    /// said a bodyless PUT would be read as "no fields supplied", turning a partial update into a
    /// silent no-op. It would not: <c>CostRateInput</c> is a non-nullable body parameter whose
    /// first four members are non-nullable <c>decimal</c>s, so binding refuses the request before
    /// the handler runs — a 400, which is loud and safe. The dangerous shape is next door: a body
    /// of <c>{}</c> binds, and every price takes <c>default(decimal)</c>. An exemption's stated
    /// REASON is the one thing a ratchet never checks, so
    /// <c>McpEndpointParityTests.A_bodyless_put_is_refused_rather_than_silently_accepted</c> now
    /// asserts it.</para>
    ///
    /// <para>The seven <c>/scim/v2</c> operations came into view when this ratchet moved off its
    /// regex and onto the router. They are exempt for a reason that is not "not yet":
    /// <c>scim:provision</c> is its own scope because the token holding it is handed to a directory
    /// connector, and a general-purpose operator CLI is not that connector. Three of them need a
    /// request body besides.</para>
    /// </summary>
    private static readonly HashSet<string> NoCommandYet = new(StringComparer.Ordinal)
    {
        "PUT /api/v1/cost-rates/{}",

        "GET /scim/v2/ServiceProviderConfig",
        "GET /scim/v2/Users",
        "POST /scim/v2/Users",
        "GET /scim/v2/Users/{}",
        "PUT /scim/v2/Users/{}",
        "PATCH /scim/v2/Users/{}",
        "DELETE /scim/v2/Users/{}",
    };

    /// <summary>
    /// Every path the CLI can build. ⚠️ The paths are taken by RUNNING <c>BuildPath</c> with
    /// stub arguments rather than by reading the source: an earlier attempt to regex them out
    /// of the file silently found three of sixteen, because C# interpolation holes contain
    /// quotes. Executing the delegate cannot be wrong about what it produces.
    /// </summary>
    private static List<string> CliRoutes()
    {
        List<string> routes = [];

        foreach (CliCommand command in CliCommands.All)
        {
            string built;
            try
            {
                // Unescaped because a command escapes a path segment it was handed (a VM's namespace
                // and name), and "{}" escaped is "%7B%7D", which no route template spells.
                built = Uri.UnescapeDataString(command.BuildPath(CliArgs.Parse(
                [
                    "--cluster", "{}", "--id", "{}", "--app", "{}", "--query", "{}",
                    "--workload", "{}", "--environment", "{}", "--namespace", "{}", "--name", "{}",
                ])));
            }
            catch (CliUsageException)
            {
                continue;   // a command whose stub arguments are not enough; not a parity gap
            }

            // ⚠️ Exhaustive, with no `_ =>` arm. A default of "GET" meant a newly added write verb
            // would be reported as a GET, failing the comparison against the route it really
            // matches and sending the next reader after a parity gap that does not exist.
            string verb = command.Verb switch
            {
                CliVerb.Get => "GET",
                CliVerb.Post => "POST",
                CliVerb.Delete => "DELETE",
                _ => throw new InvalidOperationException(
                    $"CliVerb.{command.Verb} is not mapped here; add it rather than letting it "
                    + "read as a GET"),
            };

            // Query strings are the command's business; the route is what the API serves.
            routes.Add($"{verb} {built.Split('?')[0]}");
        }

        return routes;
    }

    /// <summary>
    /// ⚠️ THE MEASURE, and it is here because the comparison below cannot be trusted without it.
    /// Both sides are extracted by pattern; if either extraction breaks, both lists go empty and
    /// every route trivially "has a command". The counts are checked against the files first.
    /// </summary>
    [Fact]
    public void Both_surfaces_were_actually_read()
    {
        fixture.ApiSurface.Should().HaveCountGreaterThan(15,
            "the API serves around thirty operations; an empty route list makes the parity check "
            + "below pass over nothing");

        // ⚠️ Pinned exactly, not loosely. "More than fifteen" stays satisfied while a whole verb
        // silently stops being produced — which is how the regex version of this ratchet managed
        // to be blind to PATCH while reporting itself healthy.
        CliRoutes().Should().HaveCount(CliCommands.All.Count,
            "every command must contribute exactly one path, or the comparison below runs over a "
            + "partial surface because BuildPath threw for the rest");
    }

    [Fact]
    public void Every_public_api_route_can_be_reached_from_the_cli()
    {
        List<string> cli = CliRoutes();

        List<string> unreachable = [.. fixture.ApiSurface
            .Where(r => !cli.Contains(r) && !NoCommandYet.Contains(r))];

        unreachable.Should().BeEmpty(
            "an endpoint with no client is indistinguishable from one that does not exist, and "
            + "worse, because it looks like progress — add a command to CliCommands.All, or name "
            + "it in NoCommandYet with the reason:\n  " + string.Join("\n  ", unreachable));
    }

    /// <summary>
    /// The exemption list must not outlive its exemptions — the failure mode of every allow-list
    /// in this repo. An entry naming a route the API no longer maps is a line nobody will delete
    /// unless something fails.
    /// </summary>
    [Fact]
    public void The_exemption_list_names_only_routes_that_still_exist()
    {
        foreach (string exempt in NoCommandYet)
        {
            fixture.ApiSurface.Should().Contain(exempt,
                $"'{exempt}' is exempted from needing a CLI command, but the API no longer serves "
                + "it — delete the exemption rather than leaving it to be trusted");
        }
    }
}
