-- Sample telemetry seed for the EntKube native-observability end-to-end test.
--
-- Populates the `logs` and `spans` tables (created by the app on startup) with a few realistic
-- traces + correlated logs, so you can exercise the Logs and Traces tabs WITHOUT a live collector.
--
-- Prereqs: the app has started at least once against the telemetry DB (so the tables + today's
-- partitions exist). Set :tenant and :cluster to a REAL (tenant_id, cluster_id) pair — find one in
-- the app (operational) DB with:  SELECT "Id","TenantId","Name" FROM "KubernetesClusters";
--
-- Run:
--   psql "host=<pg> dbname=entkube_telemetry user=entkube" \
--     -v tenant="'<CLUSTER_TENANT_UUID>'" -v cluster="'<CLUSTER_UUID>'" \
--     -f seed-sample-telemetry.sql

\if :{?tenant}
\else
  \echo '>> ERROR: pass -v tenant="''<uuid>''" and -v cluster="''<uuid>''"'
  \quit
\endif

-- ── Traces ──
-- severity/status_code notes: log severity = LogLevel enum (Info=2, Warn=3, Error=4);
-- span status_code = OTLP (0 unset, 1 ok, 2 error); span kind = OTLP (2 SERVER, 3 CLIENT).

-- T1: frontend → api → postgres (healthy)
INSERT INTO spans (ts, tenant_id, cluster_id, trace_id, span_id, parent_span_id, name, service, kind, duration_ms, status_code, namespace, pod, attributes) VALUES
 (now() - interval '30 seconds',                            :tenant, :cluster, 'trace-t1', 'span-1', NULL,     'GET /checkout',   'frontend', 2, 210, 1, 'shop', 'frontend-abc', '{"http.method":"GET"}'),
 (now() - interval '30 seconds' + interval '5 milliseconds', :tenant, :cluster, 'trace-t1', 'span-2', 'span-1', 'POST /orders',    'api',      3, 160, 1, 'shop', 'api-def',      '{"http.method":"POST"}'),
 (now() - interval '30 seconds' + interval '25 milliseconds',:tenant, :cluster, 'trace-t1', 'span-3', 'span-2', 'SELECT orders',   'postgres', 3, 40,  1, 'shop', 'pg-0',         '{"db.system":"postgresql"}');

-- T2: api → postgres (error)
INSERT INTO spans (ts, tenant_id, cluster_id, trace_id, span_id, parent_span_id, name, service, kind, duration_ms, status_code, namespace, pod, attributes) VALUES
 (now() - interval '15 seconds',                             :tenant, :cluster, 'trace-t2', 'span-4', NULL,     'POST /orders',    'api',      2, 520, 2, 'shop', 'api-def', '{"http.status_code":"500"}'),
 (now() - interval '15 seconds' + interval '10 milliseconds', :tenant, :cluster, 'trace-t2', 'span-5', 'span-4', 'INSERT orders',   'postgres', 3, 480, 2, 'shop', 'pg-0',    '{"db.system":"postgresql","error":"deadlock"}');

-- ── Logs ── (the first two carry the trace ids above, for log↔trace correlation)
INSERT INTO logs (ts, tenant_id, cluster_id, namespace, pod, container, severity, body, trace_id, attributes) VALUES
 (now() - interval '30 seconds', :tenant, :cluster, 'shop', 'api-def',      'api',      2, 'handling POST /orders',                  'trace-t1', '{"http.status_code":"200"}'),
 (now() - interval '15 seconds', :tenant, :cluster, 'shop', 'api-def',      'api',      4, 'order insert failed: deadlock detected',  'trace-t2', '{"http.status_code":"500"}'),
 (now() - interval '10 seconds', :tenant, :cluster, 'shop', 'frontend-abc', 'frontend', 2, 'rendered checkout page',                  NULL,       NULL),
 (now() - interval '5 seconds',  :tenant, :cluster, 'shop', 'pg-0',         'postgres', 3, 'slow query 480ms on orders',              NULL,       NULL);

\echo '>> Seeded 5 spans (2 traces) + 4 logs. Open the tenant Logs and Traces tabs for this cluster.'
