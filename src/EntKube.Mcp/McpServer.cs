using System.Text.Json.Nodes;

namespace EntKube.Mcp;

/// <summary>
/// Handles MCP requests. Kept transport-free — it maps a request object to a response
/// object — so the protocol behaviour can be tested without wiring up stdin and stdout.
/// </summary>
public sealed class McpServer(EntKubeApiClient api, bool allowWrite)
{
    /// <summary>
    /// Handles one JSON-RPC message. Returns null for notifications, which by definition
    /// take no response — replying to one is a protocol violation that some clients treat
    /// as a fatal error.
    /// </summary>
    public async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken ct = default)
    {
        JsonNode? id = request["id"];
        string? method = request["method"]?.GetValue<string>();

        if (method is null)
        {
            return McpProtocol.Error(id, McpProtocol.InvalidRequest, "Missing 'method'.");
        }

        // A message without an id is a notification.
        bool isNotification = id is null;

        switch (method)
        {
            case "initialize":
                return McpProtocol.Result(id, new JsonObject
                {
                    ["protocolVersion"] = McpProtocol.ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        // Tools only: this server exposes no resources or prompts, and
                        // claiming capabilities we do not implement makes clients call
                        // methods that then fail.
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "entkube",
                        ["version"] = "1.0.0",
                    },
                    ["instructions"] =
                        "EntKube is a multi-tenant Kubernetes control plane. This connection is bound "
                        + "to one tenant by its API token. Start with entkube_advisor_findings for an "
                        + "overview of what needs attention. "
                        + (allowWrite
                            ? "Write tools are enabled: they change live clusters, so confirm with the "
                              + "user before calling them."
                            : "This server is read-only; no tool here can change a cluster."),
                });

            // Notifications: acknowledge by doing nothing and returning no response.
            case "notifications/initialized":
            case "notifications/cancelled":
                return null;

            case "ping":
                return isNotification ? null : McpProtocol.Result(id, new JsonObject());

            case "tools/list":
                return McpProtocol.Result(id, new JsonObject
                {
                    ["tools"] = EntKubeTools.Describe(allowWrite),
                });

            case "tools/call":
                return await CallToolAsync(id, request["params"] as JsonObject, ct);

            default:
                // Unknown notification: stay silent rather than erroring, since the client
                // is not listening for a reply and future revisions add notifications.
                return isNotification
                    ? null
                    : McpProtocol.Error(id, McpProtocol.MethodNotFound, $"Unknown method '{method}'.");
        }
    }

    private async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken ct)
    {
        string? name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return McpProtocol.Error(id, McpProtocol.InvalidParams, "Missing tool name.");
        }

        McpTool? tool = EntKubeTools.Find(name, allowWrite);
        if (tool is null)
        {
            // Distinguish "hidden because read-only" from "does not exist": the first is a
            // configuration the user can change, and saying so saves a debugging round trip.
            bool existsButHidden = EntKubeTools.All.Any(t => t.Name == name);
            return McpProtocol.Result(id, McpProtocol.ToolResult(
                existsButHidden
                    ? $"The tool '{name}' changes cluster state and this EntKube MCP server is "
                      + "running in read-only mode. Restart it with --allow-write to enable it."
                    : $"Unknown tool '{name}'.",
                isError: true));
        }

        JsonObject? arguments = parameters?["arguments"] as JsonObject;

        try
        {
            ApiResult result = await tool.Handler(api, arguments, ct);
            return McpProtocol.Result(id, McpProtocol.ToolResult(result.Body, isError: !result.Success));
        }
        catch (ArgumentException ex)
        {
            // A missing argument is the model's mistake to correct, so it comes back as a
            // tool error it can read and retry — not a JSON-RPC error that ends the turn.
            return McpProtocol.Result(id, McpProtocol.ToolResult(ex.Message, isError: true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpProtocol.Result(id, McpProtocol.ToolResult(
                $"The '{name}' tool failed: {ex.Message}", isError: true));
        }
    }
}
