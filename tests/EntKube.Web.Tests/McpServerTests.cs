using System.Text.Json.Nodes;
using EntKube.ApiClient;
using EntKube.Mcp;
using FluentAssertions;

namespace EntKube.Web.Tests;

/// <summary>
/// Tests for the EntKube MCP server's protocol behaviour: the handshake, tool listing,
/// the read-only gate on write tools, and the distinction between a JSON-RPC error and
/// a tool error the model is meant to read.
/// </summary>
public class McpServerTests
{
    private static McpServer Server(bool allowWrite = false) =>
        // The client is never reached by these tests: every case here is decided before
        // a request would be made, which is exactly the boundary worth pinning.
        new(new EntKubeApiClient("https://entkube.invalid", "ekp_test"), allowWrite);

    private static JsonObject Request(string method, JsonNode? id, JsonObject? parameters = null)
    {
        JsonObject request = new()
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };

        if (id is not null) request["id"] = id;
        if (parameters is not null) request["params"] = parameters;
        return request;
    }

    private static JsonObject CallTool(string name, JsonObject? arguments = null)
    {
        JsonObject parameters = new() { ["name"] = name };
        if (arguments is not null) parameters["arguments"] = arguments;
        return parameters;
    }

    // ── Handshake ──

    [Fact]
    public async Task Initialize_reports_the_protocol_version_and_server_identity()
    {
        JsonObject? response = await Server().HandleAsync(Request("initialize", 1));

        response.Should().NotBeNull();
        response!["result"]!["protocolVersion"]!.GetValue<string>().Should().Be(McpProtocol.ProtocolVersion);
        response["result"]!["serverInfo"]!["name"]!.GetValue<string>().Should().Be("entkube");
        response["id"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task Initialize_advertises_only_the_tools_capability()
    {
        // Claiming a capability we do not implement makes clients call methods that fail.
        JsonObject? response = await Server().HandleAsync(Request("initialize", 1));

        JsonObject capabilities = response!["result"]!["capabilities"]!.AsObject();
        List<string> advertised = [.. capabilities.Select(kv => kv.Key)];

        advertised.Should().Contain("tools");
        advertised.Should().NotContain("resources");
        advertised.Should().NotContain("prompts");
    }

    [Fact]
    public async Task Initialize_instructions_state_whether_writes_are_possible()
    {
        JsonObject? readOnly = await Server(allowWrite: false).HandleAsync(Request("initialize", 1));
        JsonObject? writable = await Server(allowWrite: true).HandleAsync(Request("initialize", 1));

        readOnly!["result"]!["instructions"]!.GetValue<string>().Should().Contain("read-only");
        writable!["result"]!["instructions"]!.GetValue<string>().Should().Contain("confirm with the user");
    }

    [Fact]
    public async Task A_notification_receives_no_response()
    {
        // Replying to a notification is a protocol violation some clients treat as fatal.
        (await Server().HandleAsync(Request("notifications/initialized", id: null)))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_notification_is_ignored_rather_than_answered()
    {
        // Future protocol revisions add notifications; erroring on them would break clients.
        (await Server().HandleAsync(Request("notifications/somethingNew", id: null)))
            .Should().BeNull();
    }

    [Fact]
    public async Task Ping_is_answered()
    {
        JsonObject? response = await Server().HandleAsync(Request("ping", 7));

        response!["result"].Should().NotBeNull();
        response["id"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task An_unknown_method_with_an_id_returns_method_not_found()
    {
        JsonObject? response = await Server().HandleAsync(Request("does/notExist", 3));

        response!["error"]!["code"]!.GetValue<int>().Should().Be(McpProtocol.MethodNotFound);
    }

    [Fact]
    public async Task A_request_without_a_method_is_an_invalid_request()
    {
        JsonObject request = new() { ["jsonrpc"] = "2.0", ["id"] = 1 };

        JsonObject? response = await Server().HandleAsync(request);

        response!["error"]!["code"]!.GetValue<int>().Should().Be(McpProtocol.InvalidRequest);
    }

    // ── Tool listing and the write gate ──

    [Fact]
    public async Task Read_only_mode_lists_no_write_tools()
    {
        JsonObject? response = await Server(allowWrite: false).HandleAsync(Request("tools/list", 1));

        JsonArray tools = response!["result"]!["tools"]!.AsArray();
        List<string> names = [.. tools.Select(t => t!["name"]!.GetValue<string>())];

        names.Should().Contain("entkube_list_clusters");
        names.Should().NotContain("entkube_sync_deployment");
        names.Should().NotContain("entkube_restart_workload");
    }

    [Fact]
    public async Task Write_mode_lists_the_write_tools()
    {
        JsonObject? response = await Server(allowWrite: true).HandleAsync(Request("tools/list", 1));

        List<string> names = [.. response!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>())];

        names.Should().Contain("entkube_sync_deployment");
        names.Should().Contain("entkube_restart_workload");
    }

    [Fact]
    public async Task Calling_a_hidden_write_tool_explains_how_to_enable_it()
    {
        // "Hidden by configuration" and "does not exist" are different problems, and
        // saying which saves a debugging round trip.
        JsonObject? response = await Server(allowWrite: false)
            .HandleAsync(Request("tools/call", 1, CallTool("entkube_sync_deployment")));

        response!["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        response["result"]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("--allow-write");
    }

    [Fact]
    public async Task Calling_a_tool_that_does_not_exist_says_so()
    {
        JsonObject? response = await Server(allowWrite: true)
            .HandleAsync(Request("tools/call", 1, CallTool("entkube_nonsense")));

        response!["result"]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("Unknown tool");
    }

    [Fact]
    public async Task A_missing_required_argument_returns_a_tool_error_not_a_protocol_error()
    {
        // The model must be able to read the message and retry; a JSON-RPC error ends the turn.
        JsonObject? response = await Server()
            .HandleAsync(Request("tools/call", 1, CallTool("entkube_list_cluster_components")));

        response!["error"].Should().BeNull();
        response["result"]!["isError"]!.GetValue<bool>().Should().BeTrue();
        response["result"]!["content"]![0]!["text"]!.GetValue<string>()
            .Should().Contain("clusterId");
    }

    [Fact]
    public async Task A_tool_call_with_no_name_is_an_invalid_params_error()
    {
        JsonObject? response = await Server().HandleAsync(Request("tools/call", 1, new JsonObject()));

        response!["error"]!["code"]!.GetValue<int>().Should().Be(McpProtocol.InvalidParams);
    }

    // ── Tool catalogue integrity ──

    [Fact]
    public void Every_tool_has_a_name_description_and_object_schema()
    {
        foreach (McpTool tool in EntKubeTools.All)
        {
            tool.Name.Should().StartWith("entkube_");
            tool.Description.Should().NotBeNullOrWhiteSpace();
            tool.InputSchema["type"]!.GetValue<string>().Should().Be("object");
        }
    }

    [Fact]
    public void Tool_names_are_unique()
    {
        EntKubeTools.All.Select(t => t.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_cluster_changing_tool_is_marked_as_a_write()
    {
        // If this ever fails, a mutating tool has become reachable in read-only mode.
        foreach (string name in new[] { "entkube_sync_deployment", "entkube_restart_workload" })
        {
            EntKubeTools.All.Single(t => t.Name == name).IsWrite.Should().BeTrue();
        }
    }

    [Fact]
    public void Write_tool_descriptions_warn_that_they_change_live_clusters()
    {
        foreach (McpTool tool in EntKubeTools.All.Where(t => t.IsWrite && t.Name != "entkube_acknowledge_finding"))
        {
            tool.Description.Should().Contain("CHANGES A LIVE CLUSTER");
        }
    }

    [Fact]
    public void The_supply_chain_tool_states_that_unscanned_is_not_clean()
    {
        // The most dangerous possible misreading of this data, so it belongs in the
        // description the model actually sees.
        EntKubeTools.All.Single(t => t.Name == "entkube_supply_chain")
            .Description.Should().Contain("NOT a clean image");
    }

    // ── Serialization rules of the stdio transport ──

    [Fact]
    public async Task Responses_serialize_to_a_single_line()
    {
        // The stdio transport is newline-delimited: an embedded newline splits one
        // message into two and desynchronises the stream permanently.
        JsonObject? response = await Server(allowWrite: true).HandleAsync(Request("tools/list", 1));

        string json = response!.ToJsonString(McpProtocol.JsonOptions);

        json.Should().NotContain("\n");
        json.Should().NotContain("\r");
    }

    [Fact]
    public void Query_string_building_escapes_values_and_omits_blanks()
    {
        EntKubeApiClient.QueryString(("a", "x y"), ("b", null), ("c", "")).Should().Be("?a=x%20y");
        EntKubeApiClient.QueryString(("a", null)).Should().BeEmpty();
    }
}
