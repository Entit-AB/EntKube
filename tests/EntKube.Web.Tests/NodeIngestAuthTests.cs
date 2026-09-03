using EntKube.Telemetry;
using EntKube.TelemetryNode;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace EntKube.Web.Tests;

/// <summary>
/// How a caller proves it may read from a telemetry node.
///
/// The subtlety here cost a live debugging session. The management plane reaches a node through the
/// Kubernetes API server's proxy, where <c>Authorization</c> is the API server's own credential — set from
/// the kubeconfig by the client library. A caller that overwrites it to carry the node's token is rejected
/// by the API server before the request is ever forwarded, and the resulting 401 looks exactly like the
/// node refusing the token it never saw.
///
/// So the node's credential travels in a header the API server does not interpret and forwards untouched,
/// and that header is checked <i>first</i>.
/// </summary>
public class NodeIngestAuthTests
{
    private const string Expected = "the-node-token";

    private static HttpContext Request(params (string Name, string Value)[] headers)
    {
        DefaultHttpContext ctx = new();
        foreach ((string name, string value) in headers) ctx.Request.Headers[name] = value;
        return ctx;
    }

    [Fact]
    public void The_forwarded_header_authenticates()
    {
        NodeIngest.IsAuthorized(Request((NodeApi.TokenHeader, Expected)), Expected).Should().BeTrue();
    }

    [Fact]
    public void A_foreign_Authorization_header_does_not_defeat_our_own()
    {
        // Exactly the shape the API-server proxy produces: its credential in Authorization, ours alongside.
        // Checking Authorization first would compare against a token never meant for this node and reject
        // a caller whose real credential is sitting in the next header along.
        HttpContext ctx = Request(
            ("Authorization", "Bearer an-api-server-token"),
            (NodeApi.TokenHeader, Expected));

        NodeIngest.IsAuthorized(ctx, Expected).Should().BeTrue();
    }

    [Fact]
    public void Bearer_still_works_for_callers_that_reach_the_node_directly()
    {
        // A querier talking to its indexer inside the cluster has no proxy in between.
        NodeIngest.IsAuthorized(Request(("Authorization", $"Bearer {Expected}")), Expected).Should().BeTrue();
    }

    [Fact]
    public void A_wrong_token_is_refused_in_either_header()
    {
        NodeIngest.IsAuthorized(Request((NodeApi.TokenHeader, "wrong")), Expected).Should().BeFalse();
        NodeIngest.IsAuthorized(Request(("Authorization", "Bearer wrong")), Expected).Should().BeFalse();
    }

    [Fact]
    public void No_credential_is_refused()
    {
        NodeIngest.IsAuthorized(Request(), Expected).Should().BeFalse();
    }

    [Fact]
    public void A_node_with_no_token_configured_refuses_everything()
    {
        // Not "no token means open". An unconfigured node must not become an anonymous read endpoint for
        // raw log bodies just because a value was left blank.
        NodeIngest.IsAuthorized(Request((NodeApi.TokenHeader, "anything")), "").Should().BeFalse();
        NodeIngest.IsAuthorized(Request(), "").Should().BeFalse();
    }
}
