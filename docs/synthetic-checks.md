# Synthetic checks

EntKube requests a URL on a schedule and raises an incident when it stops answering as expected, so
you find out a site is down before your customers tell you.

## Creating a check

Open **Observability › Monitoring › Synthetic checks** and choose **New check**.

| Field | Meaning |
|---|---|
| URL | Any public `http` or `https` URL |
| Expected status | The exact status required. Left empty, any 2xx or 3xx passes |
| Expected text | Text the response must contain, within its first 256 KiB. Case-sensitive |
| Every | 1 minute to 1 hour |
| Alert after failures in a row | How many failures raise an incident, so a single blip pages nobody |
| Severity | The incident's severity |
| File incidents against | The cluster the incident belongs to. The request itself always comes from the management plane |

A request times out after 10 seconds. Redirects are not followed: a 302 counts as an answer, so give
the final URL, or set the expected status if you want to check the redirect itself.

Checks send no custom headers, so they cannot log in. Point them at a page that needs no credentials.
A tenant can have 100 checks.

## Alerts

A check's incident goes through the normal pipeline, so maintenance windows, alert routing and
notification channels all apply. It clears when the check next passes.

- Between the first failure and the threshold, nothing changes. An open incident stays open too.
- If checks stop running, the last answer goes stale and the incident stays as it is. A failing check
  that goes quiet does not count as recovered.
- Disabling or deleting a check resolves its open incident.

A check's alert rule is managed with the check, so it does not appear under **Telemetry alerts**.

## What may be checked

Only public addresses can be checked. Loopback, private and link-local addresses, including cloud
metadata endpoints, are refused. The address is checked when you save and again at every connection,
so a name that later starts pointing at an internal address is refused before any request is sent.

The response body is never stored or shown. Only whether it contained the expected text is kept.

An operator running EntKube on a private network can allow internal targets for the whole installation
with `Synthetics:AllowPrivateTargets`. That lets every tenant reach what the management plane can, so
turn it on only when you trust them all.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Synthetics:ResultRetentionDays` | 14 | How long individual runs are kept |
| `Synthetics:AllowPrivateTargets` | `false` | Allow internal addresses, for the whole installation |

Checks are included in backup bundles. Their run history is not.

## API, CLI and MCP

| Surface | Use |
|---|---|
| API | `GET /api/v1/synthetic-checks` (scope `ops:read`) |
| CLI | `entkube synthetics` |
| MCP | `entkube_list_synthetic_checks` |

`uptimePercent` covers the last 24 hours and is null when a check has not run in that time.
