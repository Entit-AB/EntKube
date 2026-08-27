# In-cluster telemetry: indexer and query components

Status: all phases complete, with one deliberate change of plan in Phase 5 (see §5.6). The chart and
image are NOT yet published, which is the only thing standing between this and a working install.

Today every log line and every span from every managed cluster is shipped across the
WAN into the single EntKube management-plane process, which indexes it, stores it, and
answers every query about it. This document describes moving the index and the query
engine *into each cluster* as two managed catalog components, leaving the management
plane as a thin federating client.

## 1. Why the current shape is slow

Three separate causes, all measurable in the code as it stands.

### 1.1 One process owns ingest, storage, query and the UI

`otel-collector` (DaemonSet, per cluster) exports OTLP/HTTP to a public EntKube URL —
see the `otlphttp/entkube` exporter in the catalog entry at
[ComponentCatalog.cs:1667](../src/EntKube.Web/Services/ComponentCatalog.cs#L1667). That
lands on `/ingest/otlp/v1/logs` and `/ingest/otlp/v1/traces`
([Program.cs:741](../src/EntKube.Web/Program.cs#L741)) and is written by
`SegmentTelemetryStore` into Lucene indexes on the management plane's own disk.

So the management plane carries the full log volume of every cluster of every tenant
over the internet, indexes it, and serves the Blazor UI — on one CPU budget, one disk,
one page cache. Ingest bursts and heavy queries contend directly.

### 1.2 Segments cannot be pruned by cluster

`TelemetrySegment` — the catalog row that lets a query skip a segment without reading it
— carries `TenantId`, `Signal`, `MinTs`, `MaxTs`, but **no `ClusterId`**
([TelemetrySegment.cs](../src/EntKube.Web/Data/TelemetrySegment.cs)). Cluster is only a
field *inside* each Lucene document.

A query for one cluster's logs therefore opens and searches every segment in the time
window belonging to that tenant — across all of its clusters — and discards most of what
it reads. A tenant with ten clusters does roughly ten times the work it needs to for
every single-cluster query. This is structural: it cannot be fixed by tuning.

### 1.3 Every metrics query pays a fresh TLS handshake

Metrics are *not* stored in EntKube — `PromMetricsService` reads the cluster's own
Prometheus over PromQL. But the access path re-creates the Kubernetes client per call:

- `PrometheusService` — [line 2249](../src/EntKube.Web/Services/PrometheusService.cs#L2249): `BuildConfigFromConfigFile(...)` then `new Kubernetes(config)`
- `LokiService.WithLokiAsync` — [line 495](../src/EntKube.Web/Services/LokiService.cs#L495): the same, inside the per-query helper

Each call is a new `HttpClient`, so a new TLS handshake to a remote API server, and then
a proxy hop through `kube-apiserver` to the target pod. An eight-panel dashboard is
eight handshakes and eight proxy hops before a single byte of data moves. This is the
single cheapest thing on this list to fix and it is independent of everything else.

## 2. What we are not building

Two named failure modes to steer away from:

- **Loki's "only storing".** Loki indexes labels only; matching on message content is a
  brute-force scan of chunks. We keep a real inverted index per segment — the schema
  already exists in `LogSegmentSchema` — so `namespace`, `pod`, `container`, `severity`
  and `trace_id` are term lookups and the message body is a tokenised full-text field.
- **Elasticsearch's "endlessly complicated indexing".** No cluster membership protocol,
  no shard allocation, no rebalancing, no dynamic mapping and therefore no mapping
  explosion. A segment is an immutable file with a **fixed schema**. The only distributed
  state is a small catalog answering "which segments exist and what time range does each
  cover". Scaling reads means adding querier replicas; nothing reshards.

The engine that does this is already written and tested — roughly 3,800 lines under
[src/EntKube.Web/Services/Telemetry/](../src/EntKube.Web/Services/Telemetry/), with
Lucene.NET 4.8, zstd segment archives, tiered log retention by severity, trace head
sampling, and an LRU reader cache. **We are relocating it, not replacing it.**

## 3. Target architecture

```
  ┌─ managed cluster ────────────────────────────────────────────────┐
  │                                                                  │
  │  otel-collector (DaemonSet)  ──OTLP──▶  entkube-telemetry-indexer│
  │  otel-ebpf (DaemonSet)       ──OTLP──▶    (StatefulSet + PV)     │
  │                                              │      ▲            │
  │                                       seal   │      │ hot+warm   │
  │                                              ▼      │ scan       │
  │                                          object storage (cold)   │
  │                                              │      │            │
  │                                              ▼      │            │
  │  Prometheus ◀──────────────  entkube-telemetry-query ────────────┤
  │                               (Deployment ×N + cache PV)         │
  └──────────────────────────────────┬───────────────────────────────┘
                                     │ one HTTPS request per user query
                            ┌────────▼─────────┐
                            │  EntKube.Web     │  fan-out + merge across clusters
                            └──────────────────┘
```

Both components run the **same image**, `entit.azurecr.io/entkube-telemetry`, with a
role flag. Two catalog entries so read and write scale independently and a heavy query
can never stall ingest.

### 3.1 `entkube-telemetry-indexer` — the write path

StatefulSet, one PV, OTLP receivers on 4317/4318. Owns hot and warm.

| Tier | Where | Lifetime | Purpose |
|---|---|---|---|
| **Hot** | Active Lucene `FSDirectory` on the PV | until roll (1M docs / 1h) | near-real-time; `SearcherManager` makes writes visible without reopening |
| **Warm** | Sealed `.tar.zst` segments kept on the PV | `WarmRetentionDays` (default 3), bounded by `WarmMaxBytes` | the last few days — the overwhelming majority of queries — never touch object storage |
| **Cold** | Same archives in the tenant's S3 bucket | `RetentionDays` (default 90) | durable; fetched on demand |

Sealing already works this way (`SegmentSealService` → `SegmentArchive.PackAsync` → blob
store). What is new is that **warm is an explicit, bounded tier**. Today
[`SegmentCache`](../src/EntKube.Web/Services/Telemetry/SegmentCache.cs) only evicts when
retention deletes a segment — its own comment notes the missing "future LRU disk cap" —
so on a busy cluster the local copy grows to the entire 90-day window or fills the disk.
Phase 2 adds size- and age-bounded LRU eviction, which is what turns an accidental cache
into a designed warm tier.

Retention controls that already exist and carry over unchanged: `TieredLogRetention`
(WARN+ kept for the full window, DEBUG/INFO for `VerboseLogRetentionDays`, default 14),
`RawSpanRetentionDays` (default 30, trace *summaries* still keep the full window), and
`TraceSampleRatePercent` with always-keep for error and slow traces.

### 3.2 `entkube-telemetry-query` — the read path

Deployment, N replicas, a cache PV each, no OTLP receivers. Exposes one HTTP API:

- `/api/logs/*`, `/api/traces/*`, `/api/rum/*` — the existing `ILogBackend`,
  `ITraceQueryService` and `IRumQueryService` surfaces rendered as REST
- `/api/metrics/*` — a caching passthrough to the cluster's Prometheus (§3.3)

A query is planned, not scanned blindly: prune the catalog to overlapping segments, ask
the indexer for hot and warm, fetch cold from S3 into the local LRU cache, search all of
it in parallel, merge and limit. This is the querier↔ingester split, and it is the same
plan `SegmentManagerBase.QueryAsync` already executes — it just runs next to the data.

**The segment catalog** moves out of the management-plane Postgres. The engine gets an
`ISegmentCatalog` interface with two implementations: the existing EF one (management
plane, for already-stored segments) and a SQLite-on-PV one for the in-cluster daemon,
snapshotted to object storage periodically so a lost PV can be rebuilt from the bucket.
The in-cluster catalog gains the `ClusterId` column §1.2 is missing — though per-cluster
indexing makes it nearly redundant, since a cluster's indexer only ever holds its own data.

### 3.3 Metrics stay in Prometheus

No new time-series database. Combined with fixing §1.3, a dashboard becomes one pooled
connection and a handful of cached queries instead of N handshakes and N proxy hops.

The original plan also had the querier *fronting* Prometheus so EntKube had a single
endpoint per cluster for every signal. That part was dropped once there was enough built
to evaluate it — see §5.6.

### 3.4 Management-plane changes

`LogQueryService` already routes per cluster between the native backend and Loki. It
gains a third: `RemoteQueryLogBackend`, talking to the in-cluster querier. Precedence per
cluster becomes **in-cluster querier → local segments (legacy) → Loki**. Traces and RUM
follow the same pattern. Tenant-wide views fan out across clusters and merge.

Reaching the querier reuses the API-server proxy that `LokiService` uses — with pooled
clients — and prefers a direct EntKube-managed route with mTLS where the cluster has one.

## 4. Migration

None required, and nothing is thrown away.

Segments already in management-plane storage stay readable through the existing local
backend for their full retention window; the two backends coexist per cluster. Switching
a cluster over is: install the two components, repoint that cluster's `otel-collector`
exporter from the public EntKube URL to `entkube-telemetry-indexer.<ns>:4318`, and set
the cluster's log/trace backend to remote. Rolling back is repointing the exporter.

## 5. Phases

| Phase | Work | Risk |
|---|---|---|
| **0** ✅ | Pool Kubernetes clients per cluster instead of per call (§1.3) — `KubernetesProxyClientPool`, wired into `PrometheusService` and `LokiService`. PromQL result caching still outstanding | Low, independent, immediate win |
| **1** ✅ | `ISegmentCatalog` + `IClusterTenantResolver` seams, then the engine moved to `src/EntKube.Telemetry/` — 34 files, no ASP.NET, no EF Core. The OTLP/JSON parsers moved with it (both hosts parse) | Mechanical but wide — touches every telemetry call site |
| **2** ✅ | `src/EntKube.TelemetryNode/` host: OTLP receivers, log query REST API, role flag, `SqliteSegmentCatalog`, Dockerfile — plus the bounded warm tier in the engine, which the management plane gets too | New code, self-contained |
| **3** ✅ | `charts/entkube-telemetry` (indexer StatefulSet, optional querier StatefulSet, Secret, Services) + both catalog entries. `EntKubeTelemetryService` wires the tenant's `StorageLink` and both tokens in, following the `TempoService` pattern | Follows existing catalog patterns |
| **4** ✅ | `TelemetryNodeClient` + `NodeLogBackend`/`NodeTraceService` + `ClusterRouted*` routing; `otel-collector` now defaults to an in-cluster indexer when the cluster has one. Cross-cluster fan-out not needed — the viewers are per-cluster | Touches the UI read paths |
| **5** ◐ | PromQL result cache with single-flight, in the management plane. The node-side metrics passthrough was **dropped** — it would have added a hop without removing one (§5.6) | Small once Phase 2 exists |

Phase 0 is worth doing regardless of whether the rest ships.

### 5.1 Naming debts taken on deliberately

The engine assembly declares three groups of types under namespaces that no longer match where they live.
This was a considered trade, not an oversight:

- `KubernetesOperationResult<T>`, `LokiLogStream`/`LokiLogEntry`/`LogLevel`, `TimeSeriesDataPoint` and the
  telemetry DTOs keep the `EntKube.Web.Services` namespace. They are returned by every engine query surface
  *and* used by several hundred call sites across `EntKube.Web`; renaming them is a mechanical pass that
  would have buried the actual move in diff noise. None of the three names is accurate any more —
  the result type is not Kubernetes-specific, and the log stream is only "Loki" by history.
- `TelemetrySegment` keeps `EntKube.Web.Data` for a harder reason: EF Core's model snapshots identify
  entities by full type name, so renaming it would make the next scaffolded migration read as "entity
  dropped, new entity added" across all three providers.

`EntKube.Telemetry` also carries a global `using` alias for `LogLevel`, because telemetry's severity enum
collides by name with `Microsoft.Extensions.Logging.LogLevel`. Inside `EntKube.Web` the engine's consumers
happened to sit in the same namespace as the type so it simply won; in the library it arrives via a using
and needed the tie broken.

### 5.2 How the read path splits across two pods

The obstacle was that the two tiers are not equally reachable. Sealed segments are immutable archives in a
bucket, readable by any pod with credentials; the **hot** active index is an open Lucene writer that exists
only inside the process appending to it. So neither pod can answer a query alone.

`SegmentScope` (`All` / `Hot` / `Sealed`) threads through the single `SegmentManagerBase.QueryAsync` that
every read already funnels into, so log search, histograms, label discovery, traces and RUM all inherit the
split without knowing about it. On top of that:

- the **indexer** exposes `/internal/logs/*` bound to a `Hot`-scoped backend, and `/internal/segments`,
  which hands out the catalog — object keys and time bounds, not data;
- the **querier** runs a `Sealed`-scoped backend over segments it fetches from object storage itself,
  reads the segment list through `RemoteSegmentCatalog`, and merges the indexer's hot results in
  `FederatedLogBackend`.

The scope is fixed by wiring, never by a request parameter: a caller cannot ask the indexer's public route
for hot-only results, and the querier cannot accidentally double-count sealed segments it already searched.

**Partial results beat failures.** If the indexer is restarting, its hot tier is a few unsealed minutes
while the sealed history is hours to months — so a failing half is logged and the other half returned.
Failing the whole query would turn a rolling restart into a total log outage. Only both halves failing is
an error, and it names both causes, because "no logs" and "logs unreachable" look identical in a UI.

**Configure the querier with `WarmRetentionDays` equal to `RetentionDays`.** Its local disk is a
read-through LRU cache, not a recency window: ageing it by event time would download last month's segments
to answer a query and then immediately evict them as "old", so the next identical query pays the download
again. `WarmMaxBytes` and LRU do the bounding, driven by `SegmentCacheTrimService` — the querier never
seals and never runs retention, since a second process doing either would race the indexer.

## 5.3 Verified end to end

The indexer was run locally and driven through the whole path:

- OTLP/JSON accepted plain and gzipped; rejected with 401 unauthenticated and with a wrong token
- logs indexed and returned by full-text search (`payment gateway timeout` found by the word `gateway`)
- label discovery (`/namespaces`, `/pods`) populated from the resource attributes
- severity filtering correct: the WARN+ query returned only the error, the unfiltered query returned both
  lines — i.e. the DEBUG/INFO tier is written separately and unioned back in at query time
- histogram bucketed with error counts
- SQLite catalog created in WAL mode with the covering index

Both catalog implementations are held to one shared contract suite (`SegmentCatalogContractTests`), so the
in-cluster path cannot quietly diverge from the management plane's.

Then both roles were run together, sharing one object-storage location:

- with data only in the hot tier, the querier returned it — proving the hot-tier federation
- the indexer was stopped, sealing its active index to object storage and cataloguing it, and restarted
  with an empty hot tier; the querier then fetched the segment list over HTTP, downloaded the archive from
  the bucket into its own cache, and answered from it
- with one sealed event and one hot event, the querier returned **two** entries in **one** stream,
  newest-first; the histogram summed to 2, not 3; and `limit: 1` returned only the newest

That last case is the one that matters: it is what proves the merge neither duplicates rows across tiers
nor splits one pod's lines into two streams.

Traces were then driven the same way — service discovery, the trace list, and the service map returned
identical results through both roles.

### 5.3.1 Not every aggregate can be merged

Traces forced a distinction the log surface did not. Counts and sums combine exactly: a service-map edge
with 10 calls in one tier and 5 in the other is 15 calls, and its average latency recombines as a
call-weighted mean. **Percentiles do not.** There is no function of two p95s that yields the p95 of the
union — you need the underlying distribution, which is precisely what an aggregate has discarded. Merging
them anyway would produce a number that looks plausible and moves when the data moves, and latency SLOs
and alert thresholds are built on exactly these numbers.

So `GetServiceRedAsync` and `GetServiceStatsAsync` are **delegated whole** to the indexer's all-tier route
rather than merged, and the running system was checked to confirm it: the querier's calls land on
`api/traces/red` and `api/traces/stats`, not `internal/traces`, and return p95 values identical to the
indexer's. That costs the indexer a cold-segment scan for those two query types, which is the honest price
of a correct number.

RUM has no in-cluster path at all and needs none: the RUM snippet derives its endpoint from its own origin,
so browser telemetry goes to the management plane and never reaches a cluster node.

## 5.4 The chart, and what installs it

`charts/entkube-telemetry` renders both roles from one set of values, which is what keeps their
configuration honestly identical — a retention value learned on the indexer means the same thing on the
querier. Two differences are deliberate and template-enforced rather than left to the operator:

- **`indexer.replicas` is hard-coded to 1.** The engine is single-writer by design: one `IndexWriter` per
  signal, no cross-node coordination. A second indexer would corrupt the index and race the first over the
  same catalog and the same object keys. Read capacity is the querier's job.
- **The querier's `WarmRetentionDays` is set to the full `retentionDays`**, disabling the age rule for it,
  because its disk is a read-through cache rather than a recency window (§5.2).

The two catalog entries install the same chart with different values. `EntKubeTelemetryService` fills in
what an operator should not have to type:

- **identity** — the tenant and cluster ids the node refuses to start without;
- **the ingest token** — deliberately the cluster's *existing* `IngestTokenService` token, the same value
  the collector already holds, so repointing the collector at the in-cluster indexer is only an endpoint
  change and nothing is re-copied;
- **the query token** — freshly random, and shared between the two components on a cluster (the querier
  authenticates to the indexer with it, so minting independently would leave them unable to talk). It
  grants read access to raw log bodies, so it is never derived from anything guessable;
- **the bucket** — from the tenant's selected `StorageLink`, credentials into the vault and injected at
  install time through hidden fields, exactly as Loki/Mimir/Tempo/Velero do.

`EntKubeTelemetryCatalogTests` checks every form field's `YamlPath` against the chart's own `values.yaml`.
That mismatch is otherwise silent: a value written to a key the chart does not define is accepted and
ignored, so an operator sets 90-day retention, the install succeeds, and the node runs on the default.

**Not yet published.** `HelmRepoUrl` points at `oci://entit.azurecr.io/helm`; until the chart is pushed
there the entries cannot install. Both artifacts are built and published by one script, which also refuses
to release when the chart version, the image tag it deploys, and the version the catalog pins disagree:

    scripts/build-telemetry.sh            # build and verify, publish nothing
    scripts/build-telemetry.sh --push     # after: az acr login --name entit

CI does the same via `.github/workflows/release-telemetry.yml`, on a `telemetry-v*` tag. See
[docs/releasing.md](releasing.md) for why this one is tag-driven rather than push-driven.

## 5.5 How the management plane reads a cluster

`TelemetryNodeClient` reaches a node through the API server's **service proxy** — the same route
`LokiService` and `PrometheusService` take — so reading a cluster's logs needs no ingress, no public
hostname and no extra certificate. It uses the pooled client from §1.3, so a page of panels shares one
connection rather than handshaking per panel.

The node is located by **label**, not by a name derived from the Helm release: release names are the
operator's choice, the chart's labels are not. A querier is preferred when one is deployed — that is the
reason to deploy it — and the indexer answers otherwise.

Routing sits *behind* `LogQueryService`'s existing native-vs-Loki decision rather than beside it, as
`ClusterRoutedLogBackend` / `ClusterRoutedTraceService`. The viewers still inject one `ILogBackend`, so
nothing above changed. The rule is deliberately just **in-cluster if present, local otherwise**:

- cutting a cluster over is an install; rolling back is an uninstall
- there is no flag to remember and no state where both stores are consulted
- clusters not cut over keep reading the management plane's store for its full retention

Both the endpoint resolution (60s) and the routing decision (30s) are memoized, because the negative case —
a cluster with *no* node — would otherwise cost a Service lookup against a remote API server on every
method of every panel, which is exactly the per-call round-trip this work exists to remove.

Finally, `TelemetryIngestDefaults` now prefers the cluster's own indexer when filling in a collector's
ingest URL. A deployment that never exposed EntKube publicly could not run native telemetry at all before;
it can now, because the collector no longer has to reach the internet. An endpoint the operator typed is
still never overridden.

## 5.6 Why the node does not front Prometheus

The plan said the querier should proxy metrics so the management plane had one endpoint per cluster for
every signal. Building the rest first made it clear that would not pay for itself.

The path today is: management plane → API server proxy → Prometheus. Routing through the node makes it
management plane → API server proxy → node → Prometheus. **The API-server hop is still there** — that is
how the management plane reaches anything inside a cluster — so the passthrough adds a hop and removes
none. "One endpoint" is not worth putting another component on the metrics path that can fail.

What *was* worth building is the caching, and it belongs in the management plane, where it also helps every
cluster that has no telemetry node at all. `PromQueryCache` sits in front of `PrometheusService`'s range
and label-value queries:

- **Single-flight is the important half.** A dashboard renders its panels concurrently and several ask the
  same question; on a cold cache a plain TTL cache helps not at all, because all of them start before any
  finishes. Collapsing them onto one in-flight request turns eight WAN round-trips into one.
- The TTL (10s, `Metrics:QueryCacheSeconds`) is shorter than a typical scrape interval, so nothing is
  staler than a fresh query would have been anyway.
- Range queries are keyed on the query and window, never on the `end` timestamp — that moves every second,
  which would make every call a distinct key and cache nothing.
- Failures are never cached, and a caller joining a failing shared fetch gets that error rather than
  starting its own retry. Holding a failure turns a transient blip into a sticky outage; retrying per
  caller multiplies a bad query by the number of panels.

One case would justify a passthrough later: a cluster whose NetworkPolicy stops the API server reaching
Prometheus but allows a pod in the monitoring namespace to. Nothing else in the current design needs it.

## 5.7 Installing from an OCI registry

The chart is published to an OCI registry, not a classic chart repository, and the two are installed
differently. `helm repo add` on an `oci://` URL fails outright — *"not a valid chart repository or cannot
be reached … invalid reference"* — because a registry is not a repository index. A chart there is
addressed directly, as `oci://<registry>/<path>/<chart>` with `--version`.

`ComponentLifecycleService` added a repo for every component carrying a repo URL, which broke these two
entries specifically. It now detects the `oci://` prefix, skips the add, and leaves the chart reference
alone — that reference was already in the correct form.

Two authentication surfaces follow from a private registry, and they are independent:

- **EntKube pulls the chart.** Configure `Helm__Registries__<host_with_underscores>__Username` /
  `__Password` and EntKube runs `helm registry login` before an OCI install. Unset means an anonymous
  pull is attempted rather than a refusal, since public registries need no login.
- **The cluster pulls the image.** The kubelet in the *managed* cluster fetches it, so EntKube's own
  credentials are irrelevant. Create an image-pull Secret in the release namespace and name it in the
  component's **Image Pull Secret** field, or the install succeeds and the pods sit in `ImagePullBackOff`.

See [docs/releasing.md](releasing.md) for the exact commands.

## 5.8 Installing it on a cluster, in order

The order is forced by a dependency and is easy to get wrong:

1. **Install the EntKube Telemetry Collector first.** The indexer declares it as a dependency, so it
   always goes first. At this point there is no indexer, so the collector is pointed at the management
   plane's public ingest URL — which is correct for that moment.
2. **Install the EntKube Telemetry Indexer.** Identity, tokens, the bucket and the image-pull Secret are
   all filled in for you. The pull Secret is created in the release namespace from EntKube's configured
   registry credentials (`Helm__Registries__*` in docker-compose) — the **cluster's** kubelet does that
   pull, so EntKube's own session cannot serve it. The collector needs no equivalent because its image
   (`otel/opentelemetry-collector-contrib`) is public on Docker Hub.
3. **Re-apply the collector.** This is the step that is easy to miss. Repointing cannot happen when the
   collector is registered — the indexer does not exist yet — so it happens on install: re-applying the
   collector moves its exporter to `http://<release>-entkube-telemetry-indexer.<ns>:8080/ingest/otlp`.
   Until then the collector keeps shipping to the management plane and the indexer receives nothing.
4. *(Optional)* Install the Query component when read load justifies it.

The collector still asks for an ingest URL and a token afterwards, and both are still meaningful — the URL
is simply the in-cluster indexer rather than EntKube, and the indexer validates the **same** token, so
switching destinations changes only the URL. A URL an operator typed themselves is never repointed; only
one EntKube generated is.

## 6. Open items

- **Indexer HA.** The engine is single-writer by design — one `IndexWriter` per signal,
  no cross-node coordination. One indexer per cluster is therefore the supported shape;
  a rolling restart is a short ingest gap, absorbed by the collector's retry queue.
  Multi-writer sharding is deliberately out of scope.
- **Authentication** between the collector and the indexer, and between EntKube and the
  querier. In-cluster the current bearer-token scheme is weaker than it needs to be —
  a ServiceAccount token or mesh mTLS is the natural replacement.
- **Chart hosting.** The catalog assumes a reachable `HelmRepoUrl`; we need to publish
  one for EntKube-authored charts, or use `ComponentType = "Manifest"` as the
  `letsencrypt-issuer` entry does.
