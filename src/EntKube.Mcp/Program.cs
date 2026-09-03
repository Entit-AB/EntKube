using System.Text;
using System.Text.Json.Nodes;

using EntKube.ApiClient;
namespace EntKube.Mcp;

/// <summary>
/// EntKube's Model Context Protocol server: exposes the EntKube fleet to an MCP client
/// (Claude Desktop, Claude Code, any MCP-capable agent) over stdio.
///
/// Configuration is by environment variable so a token never appears in a process list:
///   ENTKUBE_URL    base URL, e.g. https://entkube.example.com
///   ENTKUBE_TOKEN  a scoped API token (ekp_…) created in the tenant's API tokens tab
///
/// Flags:
///   --allow-write  expose the tools that change live clusters (off by default)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        string? baseUrl = Environment.GetEnvironmentVariable("ENTKUBE_URL");
        string? token = Environment.GetEnvironmentVariable("ENTKUBE_TOKEN");

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
        {
            // stderr, never stdout: stdout carries the JSON-RPC stream and anything else
            // written there corrupts it.
            Console.Error.WriteLine("entkube-mcp: ENTKUBE_URL and ENTKUBE_TOKEN must both be set.");
            PrintUsage();
            return 1;
        }

        bool allowWrite = args.Contains("--allow-write");

        using EntKubeApiClient api = new(baseUrl, token);
        McpServer server = new(api, allowWrite);

        Console.Error.WriteLine(
            $"entkube-mcp: connected to {baseUrl} ({(allowWrite ? "read-write" : "read-only")}), "
            + $"{EntKubeTools.Visible(allowWrite).Count} tools available.");

        return await RunStdioAsync(server);
    }

    /// <summary>
    /// The MCP stdio transport: one JSON-RPC message per line, in and out.
    ///
    /// Nothing but JSON-RPC may ever reach stdout — a stray Console.WriteLine anywhere in
    /// this process corrupts the stream and the client drops the connection with no useful
    /// diagnostic. All diagnostics go to stderr for that reason.
    /// </summary>
    private static async Task<int> RunStdioAsync(McpServer server)
    {
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        // An explicit writer with AutoFlush: a buffered response that is never flushed
        // looks to the client exactly like a hung server.
        await using StreamWriter stdout = new(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        using StreamReader stdin = new(Console.OpenStandardInput(), Encoding.UTF8);

        while (!shutdown.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await stdin.ReadLineAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // End of stream: the client closed the pipe, which is the normal way an MCP
            // server is shut down.
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch (System.Text.Json.JsonException ex)
            {
                await WriteAsync(stdout, McpProtocol.Error(null, McpProtocol.ParseError, ex.Message));
                continue;
            }

            if (request is null)
            {
                await WriteAsync(stdout, McpProtocol.Error(
                    null, McpProtocol.InvalidRequest, "Expected a JSON-RPC object."));
                continue;
            }

            JsonObject? response;
            try
            {
                response = await server.HandleAsync(request, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad request must not take the server down: the client would lose the
                // whole session over a single malformed call.
                Console.Error.WriteLine($"entkube-mcp: unhandled error: {ex}");
                response = McpProtocol.Error(request["id"], McpProtocol.InternalError, ex.Message);
            }

            if (response is not null)
            {
                await WriteAsync(stdout, response);
            }
        }

        return 0;
    }

    private static async Task WriteAsync(StreamWriter stdout, JsonObject message) =>
        await stdout.WriteLineAsync(message.ToJsonString(McpProtocol.JsonOptions));

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""

            entkube-mcp — Model Context Protocol server for EntKube

            Environment:
              ENTKUBE_URL      Base URL of the EntKube instance (https://entkube.example.com)
              ENTKUBE_TOKEN    Scoped API token (ekp_…), created under a tenant's API tokens tab

            Options:
              --allow-write    Expose tools that change live clusters. Off by default.
              --help           Show this message.

            The token's scopes are the real authority: this server cannot do anything the
            token is not permitted to do. --allow-write is a second, local gate on top.

            Example MCP client configuration:
              {
                "mcpServers": {
                  "entkube": {
                    "command": "/usr/local/bin/entkube-mcp",
                    "env": {
                      "ENTKUBE_URL": "https://entkube.example.com",
                      "ENTKUBE_TOKEN": "ekp_..."
                    }
                  }
                }
              }

            """);
    }
}
