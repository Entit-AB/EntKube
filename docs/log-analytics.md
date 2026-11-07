# Log analytics

Query a cluster's logs with a pipeline, then save the query or alert on it. The language is a small
read-only subset of Kusto, so it will look familiar if you have used Azure Log Analytics.

Open **Observability › Monitoring › Log analytics**, choose a cluster and a time range, and run:

```
logs
| where namespace == "shop" and level >= warn and body has "timeout"
| summarize failures = count() by pod, bin(timestamp, 5m)
| top 10 by failures
```

## Columns

| Column | Type |
|---|---|
| `timestamp` | Time. Set the range beside the query. Use it in `bin()` and `sort` |
| `level` | `debug`, `info`, `warn`, `error` or `fatal`. A line with no detected level sorts below `debug` |
| `namespace`, `pod`, `container`, `body`, `traceId` | Text |

## Stages

| Stage | Example |
|---|---|
| `where` | `where level >= error and not (pod startswith "canary")` |
| `summarize` | `summarize count() by pod` |
| | `summarize pods = dcount(pod) by namespace` |
| | `summarize count() by bin(timestamp, 5m)` |
| | `summarize count() by pattern(body)` |
| `project` | `project timestamp, pod, body` |
| `sort by` | `sort by count_ desc` (`order by` also works) |
| `top` | `top 10 by count_` |
| `take` | `take 100` (`limit` also works) |

A `summarize` or `project` replaces the columns, so later stages see only what they produced.

## Operators

| Operator | Meaning |
|---|---|
| `==`, `!=` | Exact, case-sensitive |
| `=~` | Equal, ignoring case |
| `has`, `!has` | Contains a whole term, ignoring case. `has "time"` does not match `timeout` |
| `contains`, `!contains` | Contains a substring, ignoring case |
| `startswith`, `endswith` | Ignoring case |
| `>=`, `>`, `<=`, `<` | Levels and numbers |

There is no regular-expression operator.

## Log patterns

`pattern(body)` groups lines by shape, so you can see what a log mostly consists of without knowing what
to search for:

```
logs
| where namespace == "shop"
| summarize lines = count() by pattern(body)
| top 20 by lines
```

Numbers, IDs, timestamps, addresses and quoted values become placeholders, so `Order 42 failed` and
`Order 97 failed` are both `Order <n> failed`. Only a line's first line of text counts. The result has a
`pattern` column in place of the column you grouped.

## Partial results

A query reads at most the newest 20,000 lines in the window. If it hits that limit, the result is
marked **Partial** and any count is a lower bound rather than a total.

To get a complete answer, narrow the window or put the conditions that exclude the most lines in the
first `where`. `namespace ==`, `level`, `container ==`, `pod ==`, `pod startswith` and `body has` are
applied while reading, so they let the scan reach further back. Conditions under `or` or `not`, and
`contains`, are checked only after reading, so they use up the limit.

On a cluster that sends logs to Loki rather than to EntKube, `body has` is applied while reading as
Loki's case-sensitive text match, so it can miss a line that differs in case.

## Saved queries

Enter a name and choose **Save**. Saved queries are shared with everyone in the tenant, and saving
under an existing name replaces that query.

## Dashboards

Add a **Log query** panel to a dashboard under **Observability › Monitoring › Dashboards**. It shows a
table, or one large number when the query returns a single value, and follows the dashboard's cluster
and time range. A panel showing a partial result says so.

## Alerting on a query

Under **Support › Alerting › Telemetry alerts**, create a rule of type **Log: query result**.

The rule runs the query over its window every minute and raises an incident when the answer goes
over the threshold. The answer is the single value if the query ends in an aggregate with no `by`,
such as `summarize count()`, and otherwise the number of rows.

Rules read at most 5,000 lines per evaluation. If a rule hits that limit and its count is still under
the threshold, it cannot tell whether things are fine, so an open incident stays open. A rule whose
query no longer parses, or whose logs cannot be read, also holds its state and does not clear.

Alert rules read the EntKube log store directly, so they do not work on Loki-backed clusters.

## API, CLI and MCP

Reading log contents needs the `logs:read` scope. `ops:read` tokens do not include it.

| Surface | Use |
|---|---|
| API | `GET /api/v1/logs/query?clusterId=...&query=...&minutes=60`, up to 7 days |
| CLI | `entkube logs query --cluster <id> --query "logs \| take 10"` |
| MCP | `entkube_query_logs` |

A query that does not parse answers 400 with the reason and position. If the logs cannot be read the
answer is 503, never an empty table.
