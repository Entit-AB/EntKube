# EntKube MCP server

`entkube-mcp` exposes an EntKube tenant to any Model Context Protocol client —
Claude Desktop, Claude Code, or any MCP-capable agent — so a model can answer
questions about the fleet and, optionally, act on it.

It is an ordinary API client. It holds a scoped API token and talks to the same
public `/api/v1` surface any other integration would use; it has no privileged
path into EntKube. Whatever the token cannot do, the model cannot do.

## Setup

1. In EntKube, open the tenant → **API tokens** → **New token**.
2. Grant only the scopes the assistant needs. `ops:read` and `fleet:read` cover
   every read-only tool; add `apps:write` and `ops:write` only if you intend to
   let the model act.
3. Copy the token — it is shown once.
4. Build the server (`scripts/build-mcp.sh Release`) or use a published binary.
5. Point your MCP client at it:

```json
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
```

The token is passed by environment variable rather than as an argument so it does
not appear in the process list.

## Two independent gates

The token's scopes are the real authority and are enforced server-side by EntKube.

On top of that, **the MCP server is read-only unless started with `--allow-write`**.
Write tools are then not merely refused — they are absent from `tools/list`, so a
model never plans around a capability it does not have.

This is deliberate defence in depth: the caller is a language model rather than a
script whose behaviour someone reviewed, so handing the server a broadly-scoped
token should not by itself let a model change a cluster.

```json
"args": ["--allow-write"]
```

## Tools

Read-only (10):

| Tool | What it answers |
|---|---|
| `entkube_whoami` | Which tenant and scopes this connection has |
| `entkube_list_clusters` | Registered clusters, environment, status |
| `entkube_list_cluster_components` | Components on one cluster, with chart versions |
| `entkube_list_apps` | Applications and their owning customer |
| `entkube_list_deployments` | Deployments with sync and health status |
| `entkube_advisor_findings` | The Operations Advisor feed — best starting point |
| `entkube_list_incidents` | Alert incidents, optionally open only |
| `entkube_upgrades` | Components behind their published chart versions |
| `entkube_drift` | Deployments changed outside EntKube |
| `entkube_supply_chain` | Running images joined to vulnerability scans |

Write, requiring `--allow-write` (3):

| Tool | Effect |
|---|---|
| `entkube_acknowledge_finding` | Marks an advisor finding as handled. Changes no cluster. |
| `entkube_sync_deployment` | **Applies manifests to a live cluster.** Overwrites out-of-band edits. |
| `entkube_restart_workload` | **Rolling-restarts a Deployment on a live cluster.** |

## Notes on behaviour

- **`503` is not an error.** `entkube_drift` and `entkube_supply_chain` return 503
  when no background sweep has completed. That means *unknown*, not *clean*, and
  the tool descriptions say so — the model is told to ask again later rather than
  to report all-clear.
- **An unscanned image is not a clean image.** The supply-chain tool description
  states this explicitly, because it is the most dangerous available misreading.
- **Argument mistakes come back as tool errors, not protocol errors**, so the model
  can read the message and retry rather than losing the turn.
- **Diagnostics go to stderr, never stdout.** stdout carries the JSON-RPC stream;
  anything else written there corrupts it and the client drops the connection.

## Protocol

MCP revision `2024-11-05` over the stdio transport: newline-delimited JSON-RPC 2.0,
one message per line. Only the `tools` capability is advertised — the server exposes
no resources or prompts, and claiming capabilities it does not implement would make
clients call methods that fail.
