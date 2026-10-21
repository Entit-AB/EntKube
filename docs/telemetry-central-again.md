# Bringing telemetry back to the management plane

Status: decided; phase 1 in this change. Reverses the relocation described in
[telemetry-in-cluster.md](telemetry-in-cluster.md), which is kept because its reasoning is what
this has to answer rather than ignore.

## 1. What was asked for

> It doesn't make sense to store logs and telemetry data inside the cluster, so move it back into
> its own Postgres database where EntKube is running. Watch the disk: at ~70% usage, offload the
> oldest records to the appointed S3 storage. Which also means sending logs and metrics to EntKube
> again.

## 2. What is already true (measured, not assumed)

The central path was never removed. It coexists with the in-cluster one, and most of this is
already built:

| piece | state today |
|---|---|
| OTLP ingest on the management plane | `/ingest/otlp/v1/logs`, `/ingest/otlp/v1/traces` — live |
| `Telemetry:AcceptDirectIngest` | defaults to **true**; the plane already accepts pushes |
| central store | `SegmentLogService` / `SegmentTraceService` over Lucene segments on the host's disk |
| segment catalogue | `TelemetrySegment` rows in Postgres (`TelemetryDbContext`, via `EfSegmentCatalog`) |
| S3 cold tier | `TelemetrySegmentBlobStore`, with a local-disk fallback |
| seal + retention loop | `SegmentSealService`, running on the management plane |
| query routing | `TelemetryRoute.DecideAsync` picks per cluster, **from the data** |

**So the only reason telemetry lives in clusters is the collector's exporter endpoint**, which
points at `http://entkube-telemetry-indexer.monitoring:8080/ingest/otlp`, plus the two components
that serve it (`entkube-telemetry-indexer`, `entkube-telemetry-query`).

### 2.1 The router needs no change, which is worth knowing before touching it

`TelemetryRoute` asks the node whether it *holds anything* and lets that answer win; configuration
is consulted only when the node is empty. It was written for the forward cutover, and it handles
the reverse for free: once a cluster's components are uninstalled there is no node, so every query
goes to the management-plane store. Nothing has to be re-pointed by hand, and a half-migrated
estate reads correctly throughout.

## 3. Decisions

**Logs and traces: segment files on the host's disk, catalogue in Postgres, S3 as the cold tier.**
Not payload rows in Postgres. The row store was removed in `82ec230` because it "hit a hard wall:
>5s log/trace queries and *too many clients* ingest failures — a row store is the wrong tool for
append-heavy, low-value-per-row observability data". The segment engine was built to answer exactly
that, and it already has the inverted index and the S3 tier the 70% rule needs. Re-hosting it
centrally satisfies "not in the cluster" without repeating the failure.

**In-cluster components: removed now, existing data discarded.** No migration job, no double
write. Historical logs and traces from before the cutover are gone with the volumes.

**Metrics: samples in Postgres — ⚠️ chosen against the recorded history.** See §5.

## 4. Phases

### Phase 1 — cut the collectors back (this change)

1. The `otel-collector` catalog entry ships to the management plane's `/ingest/otlp` again. Its
   description and the endpoint's help text currently assert the opposite policy — "there is no
   management-plane fallback, because a cluster's telemetry is not EntKube's to hold" — and that
   text is the feature, so it is rewritten rather than left to contradict the behaviour.
2. The `entkube-telemetry-indexer` dependency comes off the collector.
3. `entkube-telemetry-indexer` and `entkube-telemetry-query` are retired from the catalog.

Nothing in the query path changes. Nothing in ingest changes. A cluster that still has an indexer
keeps being read from it until it is uninstalled, because the router decides from the data.

### Phase 2 — the disk watermark

**Most of this exists already, in the wrong process.** Measured before designing anything:

| piece | where it is today |
|---|---|
| the pure decision (two watermarks, clamping, unmeasurable = do nothing) | `VolumeBudget` — already in the shared `EntKube.Telemetry` library |
| measuring the filesystem holding the segment directory | `VolumeGuardService.Measure()` — `DriveInfo` on the data path, deliberately not its path root |
| evicting local copies, losing nothing | `SegmentManagerBase.TrimWarmTierAsync` |
| dropping the oldest, proportionally across signals | `DropOldestAsync` + `ObjectStorageBudget.Plan` |
| the host | `VolumeGuardService` in **`EntKube.TelemetryNode`** — the in-cluster node, which is what is being retired |

The management plane runs five `SegmentSealService` instances (one per signal registry,
[Program.cs:444](../src/EntKube.Web/Program.cs#L444)) and **no volume guard at all**. So the work
is not a new watermark; it is hosting the guard where the data now lives:

1. Move `VolumeGuardService` from `EntKube.TelemetryNode` into `EntKube.Telemetry` beside
   `VolumeBudget`, taking its registries as a list — which it already does.
2. Register it in the management plane over all five registries.
3. Defaults 70% high / 60% low, per the ask (the node's are 85/75, sized for a PV dedicated to
   telemetry; the plane's disk is shared with Postgres and everything else, so it must act sooner).

**One behaviour has to be added, and it is the actual ask.** The node's guard relieves pressure by
evicting *already-sealed* copies, and when the archives are in object storage and the warm tier is
empty it gives up with a log line telling the operator to "lower the segment roll thresholds so
the active index is sealed sooner". On the management plane that is the lever to pull, not to
suggest: **seal under pressure** — `RollAndSealAsync` the active indexes, which moves those bytes
into the assigned bucket, then evict the local copies. "Offload the oldest records onto the
appointed S3 storage" is exactly this step, and today it does not exist in either process.

Order, cheapest first: evict the warm tier → seal the active indexes and evict those → only with
no object storage configured, drop the oldest (which is loss, and already loud).

Two watermarks, not one, because a single threshold at 70% makes the loop oscillate — it offloads
one segment, drops to 69.9%, and offloads again on the next tick. `VolumeBudget.BytesToReclaim`
already aims at the lower target for this reason.

What it measures is the **filesystem holding the segment directory**, not the Postgres data
directory and not the container root: that is the disk that fills, and it is the one an operator
can point at object storage to relieve. `Measure()` already uses `DriveInfo` on the data path
rather than its root, with the reason written down — on Linux the root of `/data/telemetry` is
`/`, so measuring the root reports the image's free space while the mounted volume fills unwatched.

⚠️ **Offloaded segments stay queryable.** They move to the tier the catalogue already models, and
`TelemetrySegment` keeps `MinTs`/`MaxTs` so a query skips what it does not need. This is a tiering
rule, not an archive — a query spanning the boundary reads both, and the federation budget rule
applies: a sealed read and a hot read need different budgets, and a partial answer must fail
rather than return a successful empty one.

### Phase 3 — metrics (held, see §5)

## 5. ⚠️ The metrics decision, recorded with its risk

Metrics are not stored centrally today: `PromMetricsService` reads each cluster's own Prometheus
over PromQL. "Send metrics to EntKube again" was decided as **raw samples in Postgres**, with
downsampling and the same 70% offload.

Two things make that less alarming than it first looks:

- **The query surface is already an abstraction, and already structured.** `IMetricsQuery` takes a
  metric name, a service, a dimension, a range and a bucket count — not PromQL strings. Its own
  doc says the interface was "kept as an interface so the UI depends on an abstraction and an
  alternative backend could be slotted in", and that it "mirrors the old `PgMetricsService` surface
  one-for-one". So the dashboards and the metrics explorer do not have to change, and PromQL is not
  re-implemented. My first reading of this — that PromQL would be lost — was wrong.
- A `PgMetricsService` with this exact surface existed and was deleted in `82ec230`, so the shape
  is known rather than new.

One thing makes it risky, and it is the reason this phase is held rather than started:

- **`82ec230` removed the native metrics table as part of the store that failed.** Metrics are the
  most append-heavy of the three signals. The symptoms it names — ingest failing with "too many
  clients", queries over 5s — are the symptoms a sample table reproduces first.

If the risk lands, two narrower designs keep the decision's intent:

1. **Rollups only, no raw samples.** Ingest aggregates to a fixed resolution (say 30s) per series
   before writing, and `IMetricsQuery`'s bucket count never needs raw. Cardinality is then bounded
   by series × resolution rather than by scrape volume.
2. **Remote-write to a Mimir beside EntKube.** Already a catalog component here, a real TSDB with
   its own S3 tiering, and PromQL keeps working. Metrics stop being per-cluster without a sample
   table.

Phase 3 should pick between (1) and the decision as stated before any schema is written.
