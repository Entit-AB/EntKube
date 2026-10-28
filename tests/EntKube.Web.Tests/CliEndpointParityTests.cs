using System.Text.RegularExpressions;
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
/// </summary>
public class CliEndpointParityTests
{
    /// <summary>
    /// ⚠️ ONE NAMED EXEMPTION, not a silent gap. <c>PUT /cost-rates/{clusterId}</c> takes a
    /// <c>CostRateInput</c> body, and the shared API client deliberately has no <c>PutAsync</c>:
    /// every PUT on this surface carries a body, and a bodyless one would be read by the server
    /// as "no fields supplied", turning a partial update into a silent no-op. Reaching it needs
    /// request-body support in the CLI designed alongside it, which is a larger change than the
    /// command. Written here so the absence is a decision with a reason rather than an oversight
    /// that looks like one.
    /// </summary>
    private static readonly HashSet<string> NoCommandYet = new(StringComparer.Ordinal)
    {
        "PUT /cost-rates/{}",
    };

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "charts")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    /// <summary>Every route the public API maps, as "VERB /path" with route parameters flattened.</summary>
    private static List<string> ApiRoutes()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "EntKube.Web", "Services", "PublicApi", "PublicApiEndpoints.cs");
        File.Exists(path).Should().BeTrue($"the public API lives at {path}");

        string source = Regex.Replace(File.ReadAllText(path), @"//.*?$", "", RegexOptions.Multiline);

        return [.. Regex.Matches(source, @"api\.Map(Get|Post|Put|Delete)\(""([^""]+)""")
            .Select(m => $"{m.Groups[1].Value.ToUpperInvariant()} {Flatten(m.Groups[2].Value)}")];
    }

    /// <summary>
    /// A route parameter's name and constraint are the route's business, not the CLI's — the two
    /// files would otherwise have to agree on <c>{clusterId:guid}</c> character for character.
    /// </summary>
    private static string Flatten(string route) => Regex.Replace(route, @"\{[^}]+\}", "{}");

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
                built = command.BuildPath(CliArgs.Parse(
                [
                    "--cluster", "{}", "--id", "{}", "--app", "{}",
                    "--workload", "{}", "--environment", "{}",
                ]));
            }
            catch (CliUsageException)
            {
                continue;   // a command whose stub arguments are not enough; not a parity gap
            }

            string verb = command.Verb switch
            {
                CliVerb.Post => "POST",
                CliVerb.Delete => "DELETE",
                _ => "GET",
            };

            // Query strings are the command's business; the route is what the API maps.
            string withoutQuery = built.Split('?')[0];
            routes.Add($"{verb} {withoutQuery.Replace("/api/v1", "")}");
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
        ApiRoutes().Should().HaveCountGreaterThan(15,
            "the public API maps around two dozen routes; finding few means the scan broke, "
            + "and an empty API list makes the parity check below pass over nothing");

        CliRoutes().Should().HaveCountGreaterThan(15,
            "the CLI has around twenty commands; finding few means BuildPath threw for most of "
            + "them, which would make every endpoint look unreachable rather than reachable");
    }

    [Fact]
    public void Every_public_api_route_can_be_reached_from_the_cli()
    {
        List<string> cli = CliRoutes();

        List<string> unreachable = [.. ApiRoutes()
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
        List<string> routes = ApiRoutes();

        foreach (string exempt in NoCommandYet)
        {
            routes.Should().Contain(exempt,
                $"'{exempt}' is exempted from needing a CLI command, but the API no longer maps "
                + "it — delete the exemption rather than leaving it to be trusted");
        }
    }
}
