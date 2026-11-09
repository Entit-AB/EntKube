# Service level objectives

An objective promises a share of good events over a rolling window, such as 99.9% of checkout requests
without an error over 30 days. What the target allows to go wrong is the error budget, and EntKube shows
how much of it is spent and how fast.

These are engineering targets, owned by the team that runs a service. The availability figure your
support agreement is invoiced against is separate, under **Support › SLA targets**.

Open **Observability › Monitoring › SLOs** and choose **New objective**.

## What can be measured

| Measures | A good event is | Source |
|---|---|---|
| Service availability | An inbound request that did not error | Traces |
| Service latency | A 5-minute slice in which the service's p95 was at or under your threshold. Slices with no traffic do not count | Traces |
| Synthetic uptime | A run of a synthetic check that passed | Synthetic checks |

Latency is measured in time slices rather than requests, because traces are summarised per slice. A slice
with a slow p95 counts as bad even if most requests in it were fast.

If traces are sampled, availability is measured over the sampled requests. That is usually an accurate
share, but not an exact count.

## Reading the figures

| Figure | Meaning |
|---|---|
| Attained | Good events as a share of all events in the window |
| Budget spent | Bad events as a share of the bad events the target allows. 100% exactly still meets the target; over 100% is a breach |
| Burn rate | Today's bad share against the share the target allows. Above 1× spends the budget faster than the window can afford. Early in a day with no events yet, it uses yesterday |

With no events in the window, the figures are blank rather than 0% or 100%.

## Alerts

Set **Alert when budget spent** to raise an incident once that much of the budget is gone, for example 50%
for a warning before the target is missed. The incident is filed against the service's cluster, or the
synthetic check's. Without events the alert holds its state, because a service with no traffic has not
met its target.

## How it is counted

A sweep records good and total events per UTC day, every 5 minutes. Today and yesterday are recomputed
on each pass, so late-arriving data is counted. Older days are computed once and then kept, which lets a
window run longer than the raw data is kept: synthetic results are kept for 14 days and traces for 30.

A new objective fills in past days only as far back as that data reaches, so a 90-day window starts out
covering fewer days. Changing what an objective measures clears its history; renaming it does not.

## API, CLI and MCP

| Surface | Use |
|---|---|
| API | `GET /api/v1/slos` (scope `ops:read`) |
| CLI | `entkube slos` |
| MCP | `entkube_list_slos` |
