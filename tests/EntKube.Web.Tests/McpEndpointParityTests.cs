using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EntKube.ApiClient;
using EntKube.Mcp;
using FluentAssertions;
using Xunit;

namespace EntKube.Web.Tests;

/// <summary>
/// Every route on the public API can be reached from the MCP server, or is named here with a reason.
///
/// <para><b>Why this exists.</b> <c>CliEndpointParityTests</c> was written after seven of
/// twenty-three endpoints turned out to have no CLI command — found by comparing the two surfaces,
/// not by anyone noticing. The MCP server was in exactly that state and nothing was measuring it:
/// it reached <b>17 of the API's 23</b> routes, missing <c>GET /cost/months</c>,
/// <c>GET /cost-rates</c>, <c>GET /cost-rates/{clusterId}</c>, <c>PUT</c> and <c>DELETE</c> on that
/// path, and <c>POST /apps/{appId}/secrets/sync</c>. Three were closed by adding read tools; the
/// three writes are exempted below, each with its own reason.</para>
///
/// <para>⚠️ <b>The route a tool requests is discovered by RUNNING the tool.</b> An
/// <see cref="McpTool"/>'s path is interpolated inside its handler delegate, so there is nothing to
/// read — and the CLI ratchet already learned this lesson the expensive way: regexing paths out of
/// C# source found three of sixteen, because interpolation holes contain quotes. Each handler is
/// invoked against a recording transport and asked what it actually requested. The alternative — a
/// declarative path template stored on <c>McpTool</c> beside the handler — would be the same fact
/// written twice, and this codebase has already had to make one such pair derived rather than
/// duplicated (the scope a route requires).</para>
/// </summary>
[Collection(ApiSurfaceCollection.Name)]
public class McpEndpointParityTests(GeneratedOpenApiDocumentFixture fixture)
{
    /// <summary>
    /// Routes the MCP server deliberately does not expose. Each entry is a decision with a reason,
    /// not a gap that happens to be known about.
    ///
    /// <para>⚠️ Three of these are the writes on the cost surface and the secret surface, and they
    /// are absent on purpose rather than pending. The caller here is a language model, not a script
    /// whose behaviour someone reviewed; the existing write tools are gated behind
    /// <c>--allow-write</c> AND the token's scopes, and widening that surface is a thing to be
    /// asked for, not inferred from a parity count. <b>Symmetry is not a reason to hand a model a
    /// capability.</b></para>
    /// </summary>
    private const string TicketsAreNotForAModel =
        "tickets carry customers' correspondence, which here includes patient data; passing it to "
        + "a language model is processing that needs a DPA (§17) and the customer's approval (§18)";

    private static readonly Dictionary<string, string> NoToolByDesign = new(StringComparer.Ordinal)
    {
        // ⚠️ This reason USED to be "the shared client has no body-carrying verb at all, so
        // this is unreachable by construction rather than by choice". That stopped being true
        // when EntKubeApiClient gained SendJsonAsync for the generated client, and nothing went
        // red: a ratchet asserts the NUMBER of exemptions, never the explanation, so a reason can
        // rot indefinitely while the count stays right. An exemption justified by "impossible" is
        // also the one most easily widened later — once it IS possible, the stated objection has
        // already been conceded.
        //
        // So it is written as the decision it actually is, which is the same decision as the
        // DELETE below.
        ["PUT /api/v1/cost-rates/{}"] =
            "rewriting a cluster's price sheet changes what a customer is invoiced. A model that "
            + "did this while investigating a cost question would cause a billing incident with no "
            + "obvious cause — the same reason as the DELETE below, and it was always the real one",

        ["DELETE /api/v1/cost-rates/{}"] =
            "deleting a cluster's price sheet silently re-prices it against the installation-wide "
            + "defaults, which changes what a customer is invoiced. A model that did this while "
            + "investigating a cost question would cause a billing incident with no obvious cause",

        ["POST /api/v1/apps/{}/secrets/sync"] =
            "this writes Secrets into live clusters. The read tools exist so a model can explain "
            + "and propose; applying credentials is the operator's move",

        // ── Tickets: one reason, ten operations ──
        //
        // A ticket is customer correspondence, and for the agreement the support module was built
        // for that includes patient data. Handing it to a language model is exactly the processing
        // §17 (DPA) and §18 (processor approval) govern, and the reason the support mail analyst
        // has no model behind it (docs/application-management.md). The read routes are the
        // dangerous half — a model reading the queue IS the transfer — so they are exempt with the
        // writes, not instead of them. Lifting this is a decision for whoever signs the agreement.
        ["GET /api/v1/tickets"] = TicketsAreNotForAModel,
        ["GET /api/v1/tickets/{}"] = TicketsAreNotForAModel,
        ["POST /api/v1/tickets/{}/tags"] = TicketsAreNotForAModel,
        ["DELETE /api/v1/tickets/{}/tags/{}"] = TicketsAreNotForAModel,
        ["POST /api/v1/tickets/{}/links"] = TicketsAreNotForAModel,
        ["DELETE /api/v1/tickets/{}/links/{}"] = TicketsAreNotForAModel,
        ["POST /api/v1/tickets/{}/merge"] = TicketsAreNotForAModel,
        ["PUT /api/v1/tickets/{}/reminder"] = TicketsAreNotForAModel,
        ["DELETE /api/v1/tickets/{}/reminder"] = TicketsAreNotForAModel,
        ["POST /api/v1/tickets/{}/notes"] = TicketsAreNotForAModel,

        // ── SCIM: one reason, seven operations ──
        //
        // scim:provision is its own scope precisely because the token holding it is handed to a
        // directory connector and has no business reading clusters or syncing apps. The inverse is
        // just as true: nothing that reads clusters has any business provisioning people. A tool
        // that disables a user account is not a tool a language model should hold.
        //
        // ⚠️ This used to add "and three of these need a request body the shared client cannot
        // send regardless" — a second, weaker reason that is no longer true since SendJsonAsync
        // was added. It is gone rather than corrected: the scope argument above stands on its
        // own, and a reason that depends on a missing capability expires the moment the
        // capability arrives.
        ["GET /scim/v2/ServiceProviderConfig"] = "SCIM is a directory connector's surface, not a model's",
        ["GET /scim/v2/Users"] = "SCIM is a directory connector's surface, not a model's",
        ["POST /scim/v2/Users"] = "SCIM is a directory connector's surface, not a model's",
        ["GET /scim/v2/Users/{}"] = "SCIM is a directory connector's surface, not a model's",
        ["PUT /scim/v2/Users/{}"] = "SCIM is a directory connector's surface, not a model's",
        ["PATCH /scim/v2/Users/{}"] = "SCIM is a directory connector's surface, not a model's",
        ["DELETE /scim/v2/Users/{}"] = "SCIM is a directory connector's surface, not a model's",
    };

    /// <summary>The stub a route parameter is filled with, chosen so it survives URL-escaping unchanged.</summary>
    private const string Stub = "STUB";

    /// <summary>
    /// Captures what a handler requested and answers it, so a tool can be run without a server.
    /// </summary>
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public List<(string Method, string Path)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.Method.Method.ToUpperInvariant(), request.RequestUri!.AbsolutePath));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Every route the MCP server can reach, as "VERB /path" with parameters flattened.
    ///
    /// <para>Arguments are built from each tool's OWN declared schema, so a tool that gains a
    /// required argument is filled in automatically rather than silently dropping out of this list
    /// when <c>RequireString</c> throws.</para>
    /// </summary>
    private static (List<string> Routes, List<string> Failed) McpSurface()
    {
        RecordingTransport transport = new();
        using EntKubeApiClient client = new("http://localhost", "token-not-used", transport: transport);

        List<string> routes = [];
        List<string> failed = [];

        foreach (McpTool tool in EntKubeTools.All)
        {
            int before = transport.Seen.Count;
            try
            {
                tool.Handler(client, StubArgsFor(tool), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failed.Add($"{tool.Name}: {ex.GetType().Name} — {ex.Message}");
                continue;
            }

            if (transport.Seen.Count == before)
            {
                failed.Add($"{tool.Name}: ran without requesting anything");
                continue;
            }

            (string method, string path) = transport.Seen[^1];
            routes.Add($"{method} {path.Replace(Stub, "{}", StringComparison.Ordinal)}");
        }

        return (routes, failed);
    }

    /// <summary>Fills every property the tool's schema declares required with <see cref="Stub"/>.</summary>
    private static JsonObject StubArgsFor(McpTool tool)
    {
        JsonObject args = [];

        if (tool.InputSchema["required"] is JsonArray required)
        {
            foreach (JsonNode? name in required)
            {
                if (name?.GetValue<string>() is { Length: > 0 } key)
                {
                    args[key] = Stub;
                }
            }
        }

        return args;
    }

    /// <summary>
    /// ⚠️ THE MEASURE. Every comparison below subtracts one list from another, and two empty lists
    /// agree perfectly. This is what fails when either side stops being read.
    ///
    /// <para>The MCP side is pinned EXACTLY rather than loosely: "more than fifteen" is satisfied
    /// while a whole verb silently drops out, which is precisely how the CLI ratchet managed to be
    /// blind to PATCH while reporting itself healthy.</para>
    /// </summary>
    [Fact]
    public void Both_surfaces_were_actually_read()
    {
        (List<string> routes, List<string> failed) = McpSurface();

        failed.Should().BeEmpty(
            "every tool must be runnable from its own declared schema, or this ratchet silently "
            + "stops measuring it:\n  " + string.Join("\n  ", failed));

        routes.Should().HaveCount(EntKubeTools.All.Count,
            "each tool must contribute exactly one route, or the comparison below is over a "
            + "partial surface");

        fixture.ApiSurface.Should().HaveCountGreaterThan(15,
            "the API serves around thirty operations; an empty route list would make every "
            + "endpoint below look perfectly covered");
    }

    [Fact]
    public void Every_public_api_route_can_be_reached_from_the_mcp_server()
    {
        List<string> mcp = McpSurface().Routes;

        List<string> unreachable =
            [.. fixture.ApiSurface.Where(r => !mcp.Contains(r) && !NoToolByDesign.ContainsKey(r))];

        unreachable.Should().BeEmpty(
            "an endpoint with no client is indistinguishable, from an operator's seat, from an "
            + "endpoint that does not exist — and worse, because it looks like progress. Add a "
            + "tool to EntKubeTools.All, or name it in NoToolByDesign with the reason:\n  "
            + string.Join("\n  ", unreachable));
    }

    /// <summary>
    /// The other direction, and the worse failure of the two: a tool pointing at a route the API
    /// does not serve looks like a working capability and fails only when a model tries it.
    /// </summary>
    [Fact]
    public void Every_mcp_tool_points_at_a_route_the_api_serves()
    {
        List<string> dead = [.. McpSurface().Routes.Where(r => !fixture.ApiSurface.Contains(r))];

        dead.Should().BeEmpty(
            "these tools request something no route answers, so they fail at the moment a model "
            + "trusts them:\n  " + string.Join("\n  ", dead));
    }

    /// <summary>
    /// The exemption list must not outlive its exemptions — the failure mode of every allow-list
    /// in this repo. An entry naming a route the API no longer serves is a line nobody deletes
    /// unless something fails.
    /// </summary>
    [Fact]
    public void The_exemption_list_names_only_routes_that_still_exist()
    {
        foreach (string exempt in NoToolByDesign.Keys)
        {
            fixture.ApiSurface.Should().Contain(exempt,
                $"'{exempt}' is exempted from needing an MCP tool, but the API no longer serves "
                + "it — delete the exemption rather than leaving it to be trusted");
        }
    }

    /// <summary>Every exemption carries a reason, because a bare allow-list entry explains nothing.</summary>
    [Fact]
    public void Every_exemption_states_why()
    {
        List<string> unexplained =
            [.. NoToolByDesign.Where(e => e.Value.Length < 40).Select(e => e.Key)];

        unexplained.Should().BeEmpty(
            "an exemption with no stated reason is a silent gap with extra steps:\n  "
            + string.Join("\n  ", unexplained));
    }

    /// <summary>
    /// ⚠️ <b>This asserts an EXEMPTION'S REASON, which is the thing a ratchet normally cannot
    /// see.</b> The <c>PUT /cost-rates</c> entry above, and <c>EntKubeApiClient</c>'s note on why
    /// there is no <c>PutAsync</c>, both rest on a claim about what the server does with a PUT that
    /// carries no body. That claim was WRONG when first written — it said the request would be read
    /// as "no fields supplied" and quietly do nothing — and a ratchet asserting only the count would
    /// have kept the wrong reason green forever.
    ///
    /// <para>What actually happens: <c>CostRateInput</c> is a non-nullable body parameter whose
    /// first four members are non-nullable <c>decimal</c>s, so parameter binding refuses the
    /// request before the handler runs. A 400 — loud, and safe.</para>
    /// </summary>
    [Fact]
    public async Task A_bodyless_put_is_refused_rather_than_silently_accepted()
    {
        HttpStatusCode status = await fixture.SendRawAsync(
            "PUT", $"/api/v1/cost-rates/{Guid.NewGuid()}", ApiScopeForCostWrite, body: null);

        status.Should().Be(HttpStatusCode.BadRequest,
            "if this ever becomes a 2xx, a bodyless PUT reaches the handler and the four prices "
            + "take default(decimal) — every price silently zero. ⚠️ This no longer props up the "
            + "exemption's reason, which is now a decision about invoices rather than a claim "
            + "about what the client can send: the refusal matters on its own, for any caller");
    }

    private const string ApiScopeForCostWrite = "config:write";
}
