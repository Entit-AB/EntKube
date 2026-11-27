# Error tracking

EntKube reads your error logs and browser errors and groups them into issues, so a fault that happened
ten thousand times is one line in a list rather than ten thousand lines in a log.

Open **Observability › Monitoring › Error tracking**. It needs no setup: every cluster sending logs to
EntKube and every enabled RUM site is scanned once a minute, and a new error appears within a couple
of minutes.

## How errors are grouped

An issue is one message shape. Before comparing, EntKube replaces what varies between occurrences:
IDs, numbers, timestamps, IP addresses, hex values and quoted strings. These two lines are one issue,
titled `Order <n> failed for user "<str>" at <ip>`:

```
Order 1042 failed for user "anna" at 10.0.0.4:8080
Order 7731 failed for user "erik" at 10.0.0.9:8080
```

Only the first line of a message counts, because later stack frames change with every release.

Log issues are also kept apart by namespace and workload, so the same message from two services is two
issues. A workload is the pod name without its generated suffix, so an issue carries across replicas
and rollouts. Log issues come from lines at `error` or `fatal` level.

Numbers are replaced too, so `HTTP 500` and `HTTP 503` share an issue. The latest occurrence is shown
in full on the issue.

## Working with issues

| Status | Meaning |
|---|---|
| **Open** | Needs attention |
| **Resolved** | You believe it is fixed. If it happens again after you resolve it, it reopens and is marked **Regressed** |
| **Ignored** | Known and accepted. It keeps counting and never reopens by itself |

A line that happened before you pressed Resolve does not reopen the issue, even if it is scanned afterwards.

## Alerts

On the tenant page, under **Operations › Alerting › Telemetry alerts**, create a rule of type **Errors: new or regressed
issues**. It raises an incident when more than the threshold of issues appear or come back within the
rule's window. A threshold of 0 alerts on the first new issue. Browser issues belong to a site rather
than a cluster, so these rules cover log issues only.

## What the counts mean

Each pass reads at most 5,000 error lines, and at most 200 distinct browser messages. If a pass reaches
that limit the count is a lower bound, and it is shown with a `+`. That mark stays on the issue, because
one undercount makes the running total a lower bound for good.

A line reaching the store more than 30 seconds after it happened can be missed, because a window is
scanned once. Issues are not created on Loki-backed clusters, because the scan reads the EntKube log
store directly.

A cluster or site can hold 2,000 issues. Past that, new shapes are not recorded until old ones age out,
which usually means a message contains something the grouping cannot recognise as varying.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `ErrorTracking:RetentionDays` | 90 | Issues not seen for this long are deleted, whatever their status |

Issues are included in backup bundles, so what you resolved and ignored travels with a server move.

## API, CLI and MCP

| Surface | Use |
|---|---|
| API | `GET /api/v1/error-issues?status=Open` (scope `ops:read`) |
| CLI | `entkube errors --status Open` |
| MCP | `entkube_list_error_issues` |

Each issue includes a sample of its message, so `ops:read` does reach the text of error lines.
