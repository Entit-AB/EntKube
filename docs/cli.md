# EntKube CLI

`entkube` is a command-line client for the public API. It talks to the same
`/api/v1` surface as every other client, carrying an ordinary scoped API token —
there is no privileged path, so it can do exactly what the token permits and
nothing more.

## Setup

1. In EntKube, open the tenant → **API tokens** → **New token**, granting only the
   scopes the automation needs.
2. Build (`scripts/build-cli.sh Release`) or use a published binary.
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
upgrades             Components behind their published chart versions
drift                Deployments changed outside EntKube
supply-chain         Running images joined to their vulnerability scans
cost                 Cost run rate by namespace
rollouts             Recent release watches and their verdicts
dr                   Backup and restore posture per cluster
```

Only `deployments sync` and `deployments restart` change anything; everything else
is read-only. Both need the `apps:write` scope, so a read-only token cannot invoke
them even by accident.

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

Note what that means for the sweep-backed commands: `drift`, `supply-chain`, `cost`
and `dr` return **503** when no background sweep has completed yet. That is not the
same as "nothing is wrong", and the CLI exits 1 rather than 0 so a pipeline cannot
mistake an unmeasured fleet for a clean one.
