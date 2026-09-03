using System.Text.Json;
using System.Text.Json.Nodes;

namespace EntKube.Mcp;

/// <summary>
/// Minimal JSON-RPC 2.0 plumbing for the Model Context Protocol's stdio transport.
///
/// Hand-rolled rather than taking a dependency: the stdio transport is newline-delimited
/// JSON-RPC with a handful of methods, and the one rule that actually matters — never
/// write anything but a JSON-RPC message to stdout — is easier to guarantee when we own
/// the writer than when a library shares it.
/// </summary>
public static class McpProtocol
{
    /// <summary>
    /// Protocol revision this server implements. Reported in the initialize response; a
    /// client asking for a different revision is answered with this one, which the spec
    /// allows and clients handle by negotiating down.
    /// </summary>
    public const string ProtocolVersion = "2024-11-05";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Messages must not contain embedded newlines on the stdio transport, so never
        // pretty-print: one message is exactly one line.
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // JSON-RPC error codes. The first four are from the JSON-RPC 2.0 spec.
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>Builds a successful JSON-RPC response for the given request id.</summary>
    public static JsonObject Result(JsonNode? id, JsonNode? result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result ?? new JsonObject(),
    };

    /// <summary>Builds a JSON-RPC error response for the given request id.</summary>
    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    /// <summary>
    /// Wraps text as an MCP tool result. <paramref name="isError"/> marks a failure the
    /// model should see and can react to — as distinct from a protocol-level JSON-RPC
    /// error, which means the call itself was malformed.
    /// </summary>
    public static JsonObject ToolResult(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        }),
        ["isError"] = isError,
    };
}
