# EntKube CLI

`entkube` is a command-line client for the public API. It talks to the same
`/api/v1` surface as every other client, carrying an ordinary scoped API token —
there is no privileged path, so it can do exactly what the token permits and
nothing more.

## Setup

1. In EntKube, open the tenant → **API tokens** → **New token**, granting only the
   scopes the automation needs.
2. Build (`scripts/release.sh cli`) or use a published binary.
3. Point it at your instance:

```bash
export ENTKUBE_URL=https://entkube.example.com
export ENTKUBE_TOKEN=ekp_...
entkube whoami
```

Both can also be given as `--url` and `--token`. The environment form is preferred
in CI so the token does not appear in the process list or in build logs.

## Commands

```
whoami               Show the tenant and scopes this token has
clusters list        List registered clusters
components list      List components on a cluster (--cluster <id>)
apps list            List applications
deployments list     List deployments (optionally --app <id>)
deployments sync     Apply a deployment's manifests (--id <id>)
deployments restart  Restart one workload (--id <id> --workload <name>)
advisor              Operations Advisor findings
incidents            Alert incidents (--open for unresolved only)
feedback             In-app feedback (--status New|Triaged|Resolved|Dismissed)
logs query           Run a log query (--cluster <id> --query <text> [--minutes <n>])
synthetics           Synthetic checks and their uptime over 24 hours
errors               Error issues (--status Open|Resolved|Ignored)
slos                 Service level objectives and their error budgets
upgrades             Components behind their published chart versions
drift                Deployments changed outside EntKube
supply-chain         Running images joined to their vulnerability scans
cost                 Cost run rate by namespace
cost history         Cost actually incurred over a period (--from, --to, --group-by)
rollouts             Recent release watches and their verdicts
dr                   Backup and restore posture per cluster
vms list             KubeVirt virtual machines on a cluster (--cluster <id>)
vms start            Start a stopped VM (--cluster <id> --namespace <ns> --name <vm>)
vms stop             Stop a VM; the guest is shut down
vms restart          Restart a running VM
vms pause            Freeze a running VM in memory
vms unpause          Resume a paused VM where it was
vms migrate          Live-migrate a running VM to another node
tickets list         Open support tickets, worst first ([--view open|unassigned|reminders|escalated])
tickets show         One ticket with its links and history (--ticket <number>)
tickets tag          Add a tag (--ticket <number> --tag <tag>)
tickets untag        Remove a tag (--ticket <number> --tag <tag>)
tickets link         Link to another ticket (--ticket <number> --to <number> [--role related|parent|child])
tickets unlink       Remove a link (--ticket <number> --link <link-id>)
tickets merge        Merge a duplicate (--ticket <duplicate> --into <original> [--exclude-from-sla --reason <why>])
tickets reminder set    Set a reminder (--ticket <number> --at <UTC time> [--note <text>])
tickets reminder clear  Clear it (--ticket <number>)
tickets note         Add a note; internal unless --customer-visible (--ticket <number> --text <text>)
```

These commands change something; everything else is read-only:

| Commands | Scope |
|---|---|
| `deployments sync`, `deployments restart`, `secrets sync` | `apps:write` |
| `vms start`, `stop`, `restart`, `pause`, `unpause`, `migrate` | `vms:write` |
| `advisor ack` | `ops:write` |
| `cost rates clear` | `config:write` |
| `tickets tag`, `untag`, `link`, `unlink`, `merge`, `reminder set`, `reminder clear`, `note` | `tickets:write` |

`tickets list` and `tickets show` need `tickets:read`. **A ticket carries what customers wrote**,
which for the support agreement includes patient data, so a token with `tickets:read` is a
token that can read it. The MCP server has no ticket tools for that reason.

A read-only token cannot invoke any of them, even by accident. There is no `vms delete`:
deleting a VM destroys the disks it imported, so it stays in the UI, which also refuses it
while the VM has published ports.

## Output

Aligned tables by default, because the common use is a person reading terminal
output. `--json` prints the raw API response for piping into `jq`:

```bash
entkube upgrades --json | jq '.components[] | select(.lag == "Major")'
```

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | The request failed, or the token lacks the required scope |
| 2 | The command was invoked wrongly |
| 3 | `--fail-on-results` was given and rows came back |

`--fail-on-results` is the CI gate: it makes a command exit non-zero when it
returns anything, so a pipeline can fail on a condition without parsing output.

```bash
# Fail the build if anything has drifted from its manifest.
entkube drift --fail-on-results

# Fail if any running image has a critical or high vulnerability.
entkube supply-chain --fail-on-results
```

Note what that means for the sweep-backed commands: `drift`, `supply-chain`, `cost`,
`cost history` and `dr` return **503** when no background sweep has completed yet —
for `cost history`, when the ledger has never recorded anything at all. That is not the
same as "nothing is wrong", and the CLI exits 1 rather than 0 so a pipeline cannot
mistake an unmeasured fleet for a clean one.
