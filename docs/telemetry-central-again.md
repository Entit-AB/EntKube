# Bringing telemetry back to the management plane

Status: decided; phases 1 and 2 shipped, phase 3 held (see §5). Reverses the relocation described in
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

### Phase 1 — cut the collectors back (done)

1. The `otel-collector` catalog entry ships to the management plane's `/ingest/otlp` again. Its
   description and the endpoint's help text currently assert the opposite policy — "there is no
   management-plane fallback, because a cluster's telemetry is not EntKube's to hold" — and that
   text is the feature, so it is rewritten rather than left to contradict the behaviour.
2. The `entkube-telemetry-indexer` dependency comes off the collector.
3. `entkube-telemetry-indexer` and `entkube-telemetry-query` are retired from the catalog.

Nothing in the query path changes. Nothing in ingest changes. A cluster that still has an indexer
keeps being read from it until it is uninstalled, because the router decides from the data.

### Phase 2 — the disk watermark (done)

**Most of this existed already, in the wrong process.** Measured before designing anything:

| piece | where it was |
|---|---|
| the pure decision (two watermarks, clamping, unmeasurable = do nothing) | `VolumeBudget` — already in the shared `EntKube.Telemetry` library |
| the thresholds | `SegmentEngineOptions` — already shared |
| measuring the filesystem holding the segment directory | `VolumeGuardService.Measure()` — `DriveInfo` on the data path, deliberately not its path root |
| evicting local copies, losing nothing | `SegmentManagerBase.TrimWarmTierAsync` |
| dropping the oldest, proportionally across signals | `DropOldestAsync` + `ObjectStorageBudget.Plan` |
| the host | `VolumeGuardService` in **`EntKube.TelemetryNode`** — the in-cluster node this programme retires |

The management plane ran five `SegmentSealService` loops
([Program.cs](../src/EntKube.Web/Program.cs)) and **no volume guard at all**. So the guard moved
into `EntKube.Telemetry` beside `VolumeBudget`: "how full is the disk these segments are on?" is
the same question in a cluster and here.

#### What is actually new: sealing under pressure

The old guard could only evict local copies of segments that were **already sealed**. An active
index holding the very records that should be offloaded was untouchable — and the guard's own log
told the operator to go and lower the roll thresholds by hand. Past the high-water mark, cheapest
first:

1. **evict warm copies** — free, the archives are already in object storage
2. **seal the active indexes** — uncompressed Lucene becomes a zstd archive *in the bucket*, and
   the copy left behind is then evictable like any other sealed segment
3. **drop the oldest** — only for managers whose archives are on this very volume

Sealing before dropping, always: that is the difference between *moving* the oldest records to the
assigned storage and *deleting* them. `canSealActive` gates on there being something to seal, so an
idle store still reaches step 3 instead of reporting work it is not doing.

#### Per-tenant storage changes who may be dropped from

`ArchivesAreLocal` moved onto the manager. The guard took one `ISegmentBlobStore`, but the plane
resolves one **per tenant**: one tenant can have object storage assigned while the next falls back
to local disk, so there is no single answer for the host. Dropping targets only managers whose
archives share this volume — deleting a segment whose archive lives in a bucket frees nothing here.

#### Thresholds

70% high / 60% low on the plane, against the node's 85/70, overridable with
`Telemetry:VolumeHighWaterPercent` and `Telemetry:VolumeTargetPercent`. The node measures a
PersistentVolume dedicated to telemetry; this is the management plane's own disk, shared with
Postgres and everything else, so telemetry has to start giving space back sooner. Two watermarks
rather than one because a single threshold oscillates — offload one segment, drop to 69.9%, offload
again next tick.

⚠️ **Offloaded segments stay queryable.** They move to the tier the catalogue already models, and
`TelemetrySegment` keeps `MinTs`/`MaxTs` so a query skips what it does not need. This is a tiering
rule, not an archive — `Sealing_under_pressure_keeps_the_records_readable` asserts a sealed-tier
read after an offload, because a guard that silently destroyed telemetry while reporting that it
had offloaded it is the failure worth testing for.

### Phase 3 — metrics: rollups, not raw samples

§5 below recorded the decision as **raw samples in Postgres**, held, with two narrower designs to
fall back on if the risk landed. Phase 3 took the first of them — **rollups only** — *before* any
schema was written rather than after measuring a failure, because the measurement that would have
justified raw samples is the one that broke the previous store. There is nothing to learn from
reproducing it.

#### What is stored

One `MetricRollups` row per (tenant, cluster, name, service, namespace, pod, bucket, resolution),
carrying `Count`/`Sum`/`Min`/`Max`/`Last`. `Telemetry:MetricsResolutionSeconds` (default 30) is the
dial, and the unique index over that tuple is also the upsert key — a collector that retries on a
5xx, or two collectors reporting the same scope, merge into the row instead of doubling every
reading of that bucket. Cardinality is bounded by series × resolution rather than by scrape volume,
which is the whole difference from the store `82ec230` deleted.

#### Four slices, kept separate on purpose

1. The store and the resolution dial (#329).
2. The OTLP endpoint, the parser and the write path (#331 — #330 carried this and was merged into an
   already-merged branch, so it never reached main, and was re-cut against main).
3. The series semantics (#332): the two PromQL rules reproduced against known arithmetic.
4. The query: `RollupMetricsQuery`, an `IMetricsQuery` over the store.

⚠️ **None of it is routed.** `PromMetricsService` is still the only implementation anything
resolves, `Telemetry:AcceptMetricsIngest` defaults to false, and `RollupMetricsQuery` is not even
registered. Routing is its own change deliberately: it is the moment a dashboard's numbers change
source, and a mistake there does not fail — it draws a different graph, with no error anywhere.

#### What the query has to decide that Prometheus decided for it

- **The tenant predicate is the only boundary there is.** `PromMetricsService` reaches a cluster's
  own Prometheus, so it is bounded by the cluster it found; this store holds every tenant's rows in
  one table. Each of the five methods is therefore tested against a *populated* neighbouring
  tenant, not an empty database — a missing predicate returns a plausible answer rather than an
  error.
- **The pod scope arrives as a POSIX regex** (`^(workload-a|workload-b)-`, built for PromQL) and is
  applied as a prefix match, which is what that pattern means for every case the product generates.
  No provider can translate it to SQL, and evaluating it in memory would mean loading every row the
  cluster has first. A pattern the parser cannot read scopes **nothing** rather than everything:
  too many pods in a chart is visible and can be narrowed, too few is indistinguishable from the
  workload having no metrics.
- **A counter's window is read one bucket wider than it is plotted**, because the first bucket has
  no rate without a predecessor — otherwise every counter chart opens with a blank first interval,
  at every range, which reads as missing data. A gauge's window is *not* widened, and that is the
  only thing keeping a point from being drawn before the operator's own left edge: `topk` orders the
  broken-down series by their peak before any after-the-fact filter could run, so not reading the
  bucket is strictly better than discarding it later.

#### A timezone shift the coarse path was hiding

`MetricRollupFold.BucketFor` converted any non-UTC timestamp with `ToUniversalTime()`, which treats
an **Unspecified** one as local and applies the host's offset. Rows come back Unspecified from any
provider that does not store a kind — SQLite keeps timestamps as TEXT — so a read went looking for
buckets two hours in the past and found none. It was invisible until this phase because
`MetricSeriesBuilder.ToStep` skips the bucketing entirely when the caller's step equals the stored
resolution: only a **coarser** step reaches it. Only `DateTimeKind.Local` is converted now, and the
query stamps UTC on rows as they leave the store.


## 5. ⚠️ The metrics decision as it was recorded, and the risk it was held for

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

**Phase 3 picked (1).** Recorded here rather than rewritten, because the reasoning above is what
the choice was made against and a note that only states its conclusion cannot be checked later.
