using System.Net;
using System.Net.WebSockets;
using EntKube.Web.Data;
using EntKube.Agents.Protocol;

namespace EntKube.Web.Services.Agents;

/// <summary>
/// The endpoint egress agents dial in on.
///
/// Deliberately outside the cookie-authenticated application: an agent is a
/// machine holding an enrolment token, not a signed-in user, so this
/// authenticates the token itself and never redirects to a login page.
/// </summary>
public static class AgentEndpoint
{
    /// <summary>
    /// Maps <c>/agent/connect</c>. Anonymous at the framework level because the
    /// token check below is the authentication — a WebSocket handshake cannot
    /// follow an interactive sign-in.
    /// </summary>
    public static void MapAgentEndpoint(this WebApplication app)
    {
        app.Map(AgentProtocol.EndpointPath, async (HttpContext context, AgentRegistry registry,
            ILogger<AgentRegistry> logger) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync(
                    "This endpoint accepts WebSocket connections from the EntKube egress agent.");
                return;
            }

            string? token = context.Request.Headers[AgentProtocol.TokenHeader].FirstOrDefault();
            EgressAgent? agent = await registry.AuthenticateAsync(token ?? "", context.RequestAborted);

            if (agent is null)
            {
                // No detail: an unauthenticated caller learns only that it failed,
                // not whether the token was unknown or the agent disabled.
                logger.LogWarning(
                    "Rejected egress agent connection from {Address}: invalid or disabled token",
                    Describe(context.Connection.RemoteIpAddress));

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsync("Unauthorized.");
                return;
            }

            // Recorded so an operator can confirm the link comes from the network
            // they expect, and see what the agent will actually dial.
            string? reportedAllowlist = context.Request.Headers["X-EntKube-Agent-Allowlist"].FirstOrDefault();

            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

            await registry.RunConnectionAsync(
                agent, socket, Describe(context.Connection.RemoteIpAddress), reportedAllowlist,
                context.RequestAborted);
        }).AllowAnonymous();
    }

    private static string Describe(IPAddress? address) => address?.ToString() ?? "unknown";
}
