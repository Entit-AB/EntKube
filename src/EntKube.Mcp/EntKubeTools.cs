using System.Text.Json.Nodes;

namespace EntKube.Mcp;

/// <summary>One MCP tool: its schema, whether it mutates, and how to run it.</summary>
public sealed record McpTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject InputSchema { get; init; }

    /// <summary>
    /// True when the tool changes cluster state. Write tools are hidden entirely unless
    /// the server was started with --allow-write.
    /// </summary>
    public bool IsWrite { get; init; }

    public required Func<EntKubeApiClient, JsonObject?, CancellationToken, Task<ApiResult>> Handler { get; init; }
}

/// <summary>
/// The EntKube tool catalogue exposed over MCP.
///
/// Two independent gates stand between a model and a cluster change. The API token's
/// scopes are the real authority and are enforced server-side. On top of that, this
/// server hides every write tool unless explicitly started with --allow-write — so
/// handing it a broadly-scoped token does not, by itself, let a model mutate anything.
/// Defence in depth, because the caller here is a language model rather than a script
/// whose behaviour someone reviewed.
/// </summary>
public static class EntKubeTools
{
    private static JsonObject NoArgs() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private static JsonObject ObjectSchema(JsonObject properties, params string[] required)
    {
        JsonObject schema = new()
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Length > 0)
        {
            schema["required"] = new JsonArray([.. required.Select(r => (JsonNode)r!)]);
        }

        return schema;
    }

    private static JsonObject StringProp(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    /// <summary>Every tool this server can expose, write tools included.</summary>
    public static IReadOnlyList<McpTool> All { get; } =
    [
        new McpTool
        {
            Name = "entkube_whoami",
            Description = "Show which EntKube tenant this connection is bound to and which "
                        + "scopes the API token holds. Use this first when a call is refused, "
                        + "to see whether the token simply lacks the scope.",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/whoami", ct),
        },

        new McpTool
        {
            Name = "entkube_list_clusters",
            Description = "List the Kubernetes clusters registered in this tenant, with their "
                        + "environment, provisioning status and component count.",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/clusters", ct),
        },

        new McpTool
        {
            Name = "entkube_list_cluster_components",
            Description = "List the components (Helm releases and manifests) installed on one "
                        + "cluster, with chart versions and install status.",
            InputSchema = ObjectSchema(
                new JsonObject { ["clusterId"] = StringProp("Cluster id (GUID) from entkube_list_clusters.") },
                "clusterId"),
            Handler = (api, args, ct) =>
                api.GetAsync($"/api/v1/clusters/{EntKubeApiClient.RequireString(args, "clusterId")}/components", ct),
        },

        new McpTool
        {
            Name = "entkube_list_apps",
            Description = "List the applications in this tenant, with their owning customer "
                        + "and deployment count.",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/apps", ct),
        },

        new McpTool
        {
            Name = "entkube_list_deployments",
            Description = "List deployments with their sync and health status, target cluster and "
                        + "namespace. Optionally filter to one app.",
            InputSchema = ObjectSchema(new JsonObject
            {
                ["appId"] = StringProp("Optional app id (GUID) to filter by."),
            }),
            Handler = (api, args, ct) => api.GetAsync(
                "/api/v1/deployments" + EntKubeApiClient.QueryString(
                    ("appId", EntKubeApiClient.OptionalString(args, "appId"))), ct),
        },

        new McpTool
        {
            Name = "entkube_advisor_findings",
            Description = "The Operations Advisor feed: what needs doing across security, "
                        + "reliability, data protection, capacity, maintenance and supply chain, "
                        + "bucketed by time-to-impact. The best single starting point for "
                        + "\"what is wrong right now\".",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/advisor/findings", ct),
        },

        new McpTool
        {
            Name = "entkube_list_incidents",
            Description = "List alert incidents, newest first. Set open=true for unresolved ones only.",
            InputSchema = ObjectSchema(new JsonObject
            {
                ["open"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "When true, return only incidents that are not resolved.",
                },
            }),
            Handler = (api, args, ct) => api.GetAsync(
                "/api/v1/incidents" + EntKubeApiClient.QueryString(
                    ("open", EntKubeApiClient.OptionalString(args, "open"))), ct),
        },

        new McpTool
        {
            Name = "entkube_upgrades",
            Description = "Which installed components are behind the versions their Helm "
                        + "repositories publish, and by how much (patch/minor/major).",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/upgrades", ct),
        },

        new McpTool
        {
            Name = "entkube_drift",
            Description = "Deployments whose live cluster state no longer matches the manifests "
                        + "EntKube applied — i.e. changed outside EntKube. Returns 503 if no "
                        + "drift sweep has completed yet, which means \"unknown\", not \"clean\".",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/drift", ct),
        },

        new McpTool
        {
            Name = "entkube_supply_chain",
            Description = "Running container images joined to their registry vulnerability scans. "
                        + "Note that an image reported as 'Unscanned' is one whose status is "
                        + "unknown — it is NOT a clean image. Returns 503 if no sweep has run yet.",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/supply-chain", ct),
        },

        new McpTool
        {
            Name = "entkube_cost",
            Description = "What the fleet costs at the current rate of consumption, broken down "
                        + "by customer, environment and namespace. These are run-rate projections "
                        + "over a 730-hour month, not a historical bill — they reflect what is "
                        + "reserved right now. Returns 503 if no calculation has run yet.",
            InputSchema = NoArgs(),
            Handler = (api, _, ct) => api.GetAsync("/api/v1/cost", ct),
        },

        new McpTool
        {
            Name = "entkube_acknowledge_finding",
            Description = "Acknowledge an Operations Advisor finding, marking it as being handled. "
                        + "Does not change any cluster.",
            IsWrite = true,
            InputSchema = ObjectSchema(
                new JsonObject { ["findingId"] = StringProp("Finding id from entkube_advisor_findings.") },
                "findingId"),
            Handler = (api, args, ct) => api.PostAsync(
                "/api/v1/advisor/findings/"
                + Uri.EscapeDataString(EntKubeApiClient.RequireString(args, "findingId"))
                + "/acknowledge", ct),
        },

        new McpTool
        {
            Name = "entkube_sync_deployment",
            Description = "Apply a deployment's manifests to its cluster. THIS CHANGES A LIVE "
                        + "CLUSTER — it will overwrite any out-of-band edits to the deployment's "
                        + "resources. Check entkube_drift first if you are unsure whether "
                        + "someone has changed them deliberately.",
            IsWrite = true,
            InputSchema = ObjectSchema(
                new JsonObject { ["deploymentId"] = StringProp("Deployment id (GUID) from entkube_list_deployments.") },
                "deploymentId"),
            Handler = (api, args, ct) => api.PostAsync(
                $"/api/v1/deployments/{EntKubeApiClient.RequireString(args, "deploymentId")}/sync", ct),
        },

        new McpTool
        {
            Name = "entkube_restart_workload",
            Description = "Trigger a rolling restart of one Kubernetes Deployment inside an "
                        + "EntKube deployment. THIS CHANGES A LIVE CLUSTER.",
            IsWrite = true,
            InputSchema = ObjectSchema(
                new JsonObject
                {
                    ["deploymentId"] = StringProp("Deployment id (GUID) from entkube_list_deployments."),
                    ["workload"] = StringProp("Name of the Kubernetes Deployment to restart."),
                },
                "deploymentId", "workload"),
            Handler = (api, args, ct) => api.PostAsync(
                $"/api/v1/deployments/{EntKubeApiClient.RequireString(args, "deploymentId")}/restart"
                + EntKubeApiClient.QueryString(("workload", EntKubeApiClient.RequireString(args, "workload"))), ct),
        },
    ];

    /// <summary>
    /// The tools visible for this server's configuration. Write tools are omitted entirely
    /// rather than advertised-and-refused, so a model never plans around a capability it
    /// does not have.
    /// </summary>
    public static IReadOnlyList<McpTool> Visible(bool allowWrite) =>
        allowWrite ? All : [.. All.Where(t => !t.IsWrite)];

    public static McpTool? Find(string name, bool allowWrite) =>
        Visible(allowWrite).FirstOrDefault(t => t.Name == name);

    /// <summary>Renders the visible tools as the MCP tools/list payload.</summary>
    public static JsonArray Describe(bool allowWrite)
    {
        JsonArray tools = [];
        foreach (McpTool tool in Visible(allowWrite))
        {
            tools.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }

        return tools;
    }
}
