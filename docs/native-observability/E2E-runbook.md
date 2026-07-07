# Native Observability — End-to-End Runbook

How to stand up and validate the EntKube-native logs + traces pipeline (Phase 2 + Phase 3).
Everything is permissive/community: **OpenTelemetry Collector** (Apache-2.0) → EntKube ingest →
**partitioned Postgres** → EntKube UI. No Loki/Grafana required.

There are two paths:
- **A. Fast UI check (no collector)** — seed sample data straight into the telemetry DB and click
  through the UI. Best first step; proves the query + UI layers.
- **B. Full pipeline** — install the collector, instrument an app, watch real data flow.

---

## 0. Provision the dedicated telemetry database

Logs/traces live in a **separate database** on your management-plane cnpg server (never the
operational `entkube` DB — a log storm must not threaten the control plane).

With CloudNativePG 1.24+ (a `Database` CR):

```yaml
apiVersion: postgresql.cnpg.io/v1
kind: Database
metadata:
  name: entkube-telemetry
spec:
  cluster:
    name: <your-mgmt-cnpg-cluster>
  name: entkube_telemetry
  owner: entkube
```

Or plain SQL against the server: `CREATE DATABASE entkube_telemetry OWNER entkube;`

## 1. Configure EntKube

`appsettings.Production.json`:

```json
"ConnectionStrings": {
  "TelemetryConnection": "Host=<pg>;Port=5432;Database=entkube_telemetry;Username=entkube;Password=…"
},
"Telemetry": {
  "RetentionDays": 14,
  "PublicIngestUrl": "https://<entkube-host>",   // URL clusters reach EntKube at (path 3 below)
  "EnableTextSearchIndex": false                 // set true for fast text + attribute search (adds ingest cost)
}
```

## 2. Start EntKube

On startup `TelemetryMaintenanceService` creates the `logs` and `spans` tables, their indexes, and
the daily partitions (retention window + lookahead), then re-runs every 12 h. Confirm in the logs:
`Telemetry schema ensured (retention 14d).` If you see `Telemetry store disabled`, the connection
string isn't set.

---

## Path A — Fast UI check (seed, no collector)

1. Find a real cluster to attach the data to (run against the **app** DB, not telemetry):
   ```sql
   SELECT "Id","TenantId","Name" FROM "KubernetesClusters";
   ```
2. Seed (run against the **telemetry** DB):
   ```bash
   psql "host=<pg> dbname=entkube_telemetry user=entkube" \
     -v tenant="'<TenantId>'" -v cluster="'<Id>'" \
     -f docs/native-observability/seed-sample-telemetry.sql
   ```
3. Open the tenant → **Logs** tab, pick that cluster, Search: you should see 4 lines, a volume
   histogram, and a **trace** badge on the two lines that carry a trace id.
4. Open the tenant → **Traces** tab: Search shows 2 traces; open `trace-t2` (the error) for the
   waterfall (api → postgres, red bars), pick service `api` for its **RED** overview, and expand
   **Service map** to see `frontend → api` and `api → postgres` edges.
5. Click a log line's **trace** badge → it should switch to the Traces tab and open that waterfall;
   the trace's **Logs for this trace** panel should list the correlated line.

---

## Path B — Full pipeline (collector + instrumented app)

1. **Ingress**: `PublicIngestUrl` must be reachable from managed clusters at
   `POST /ingest/otlp/v1/logs` and `/v1/traces` (bearer-authenticated).
2. **Install** the *EntKube Telemetry Collector* component on a test cluster. In the tenant Logs
   tab's "Send logs to EntKube" panel, copy the **Ingest URL** (`…/ingest/otlp`) and the
   per-cluster **Ingest Token** into the component's form fields.
   - The collector already sinks logs (filelog → OTLP) and accepts app traces on OTLP 4317/4318.
   - Ships to EntKube with `encoding: json` + gzip; the exporter appends `/v1/logs` and `/v1/traces`.
3. **Traces**: point an instrumented app's OTLP exporter at the in-cluster collector
   (`otel-collector.monitoring:4317`), or generate some with
   `telemetrygen traces --otlp-endpoint <collector>:4317 --otlp-insecure --traces 20`.
   - **Zero-code option**: install the *EntKube Trace Auto-Instrumentation (eBPF)* component
     (`otel-ebpf`) on the cluster. It runs OpenTelemetry eBPF Instrumentation (OBI) as a
     privileged DaemonSet that synthesizes HTTP/gRPC spans + RED metrics for every workload
     and ships them to the collector — no app changes, no mesh. Requires kernel 5.8+ (5.17+
     for cross-service context propagation) and a `monitoring` namespace that permits
     privileged pods.
4. Watch rows land (`SELECT count(*) FROM logs;` / `FROM spans;`) and appear in the UI.

---

## Customer portal access

Customers see telemetry for **their own apps only** in the customer portal (`/portal`), scoped by
the namespaces of each app's deployments (a namespace belongs to exactly one customer). Panels:

- **Logs** and **Monitoring** (status/SLA/incidents) — available to any customer user (Viewer+).
- **Traces & Metrics** (APM traces, RED metrics, service map) and **RUM** — gated to
  **Operator+** on the customer, since they expose deeper request-level telemetry. A Viewer keeps
  status + logs only. (Threshold is `CustomerAccessRole.Operator` in `CustomerPortal.razor`.)

**RUM requires an App owner.** A RUM site only appears in the customer portal when it is assigned
an **App owner** (its `AppId`). Set it in the tenant **RUM sites** admin (App owner dropdown) when
creating or editing the site — the owning app determines which customer sees it. Sites with no app
owner are admin-managed and never surface in the portal.

## Verification checklist

- [ ] Logs: namespace/pod dropdowns populate; lines render; **volume histogram** shows.
- [ ] Logs: **severity** filter ("Error + Fatal") re-queries server-side (not just a client slice).
- [ ] Logs: **attribute** filter (key `http.status_code`, value `500`) narrows results (native only).
- [ ] Traces: search by service/min-duration/errors-only; **waterfall** indents by span depth.
- [ ] Traces: **RED** overview (requests / error-rate / avg / p95) for a selected service.
- [ ] Traces: **Service map** lists caller→callee edges with calls/errors/avg.
- [ ] Correlation: log **trace** badge → opens the trace; trace **Logs for this trace** → the lines.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Ingest returns **401** | Wrong/blank ingest token, or `Vault:RootKey` changed (invalidates tokens). |
| Ingest returns **400** | Collector not using `encoding: json` (protobuf isn't parsed), or malformed payload (dropped, by design). |
| Ingest returns **503** | `TelemetryConnection` not configured. |
| Ingest returns **413** | Batch decompresses > 64 MiB — lower collector batch size. |
| Logs land but **dropdowns empty** on a Loki cluster | That's the Loki OTLP label mapping (`loki` entry `limits_config.otlp_config`), unrelated to the native store. |
| **Traces tab** says "no traces" | No spans for the cluster yet — instrument an app, install the `otel-ebpf` component, or seed (Path A). |
| Text/attribute search slow | Set `Telemetry:EnableTextSearchIndex: true` (builds pg_trgm + JSONB GIN indexes; costs ingest throughput). |
| Customer can't see **Traces & Metrics** button in the portal | Their `CustomerAccessRole` is Viewer — grant **Operator+** on that customer. |
| A **RUM site** doesn't appear in the customer portal | The site has no **App owner** — set it in the tenant RUM sites admin (App owner dropdown). |
| Customer portal traces/metrics **empty** for an app | The app's deployment has no synced namespace yet (scoping needs it), or no telemetry for that namespace. |

Notes: timestamps are clamped into the partition range on write (a stray/1970/future ts can't wedge
the batch); a malformed payload returns 400 (dropped) so the collector never retry-loops.
