# MariaDB (MySQL-compatible) as a managed database service

Status: design, 2026-11-20. Revised twice: first after a verification pass against the operator's docs and source, then after the kind spike (PR 0) on mariadb-operator 26.10.2 with MariaDB 12.3.3 (11.8.9 for one check) and MaxScale 23.08.13.
Owner module: DataServices.
Supersedes: none. This is a new engine beside CNPG and MongoDB.

Verification conventions:
- Repo paths, types and methods named below were checked against the tree. The verification pass corrected several names, line numbers and claims about CNPG/Mongo, and those corrections are already applied here. The spike did not touch the repo, so repo facts are unchanged by it.
- **[verified on kind, 26.10.2]** marks an operator fact observed on a running mariadb-operator 26.10.2 on 2026-11-20. Evidence excerpts are in §A.
- **[refuted on kind]** marks an earlier claim or plan that the spike disproved. The text says what changed as a result.
- **[cited]** marks a fact from the mariadb-operator 26.10.2 docs or source (the `docs/` and `api/v1alpha1/` trees at tag `26.10.2`) that the spike did not observe. Code may be designed around it.
- **[verify]** marks what is still unsettled: either the spike was inconclusive (the text says what was tried) or the spike did not cover it. PR 0b (§10) settles each one before the PR that depends on it.
- **[user decision, 2026-11-20]** marks the user's final answers to open decisions 1–3.

Spike environment limits, which bound what "verified" means: kind with 3 workers (plus a control-plane node tolerated once), a `local-path` StorageClass that cannot expand volumes, a SeaweedFS S3 that auto-creates missing buckets, kindnet NetworkPolicy (which does not cut established connections), and no Prometheus or metrics-server.

---

## 1. Summary

EntKube gets a first-class **"MariaDB (MySQL-compatible)"** database service **[user decision, 2026-11-20]**. It is built on the community **mariadb-operator** (MIT, `k8s.mariadb.com/v1alpha1`), pinned to **26.10.2**.

It is managed the same way as CNPG and MongoDB:
- operator detection and the catalog entry;
- create, import, delete and unregister;
- live detail, drift and re-apply;
- storage expansion, resize, scale, upgrade, restart and switchover;
- databases, users and credentials in the vault;
- app bindings, the portal and governance;
- scheduled physical backups to an S3 StorageLink, continuous binlog PITR, and restore to a new cluster (on the same or another Kubernetes cluster);
- Prometheus metrics with scrape diagnosis;
- advisor findings, the app-state backup bundle, blueprints, and a read-only status/backup poller.

**Default topology [user decision, 2026-11-20]:**
- 3 MariaDB replicas with semi-synchronous replication (`AfterSync`) and binlog PITR.
- **MaxScale 23.08 (2 replicas) in front.** It is the application endpoint, and its `mariadbmon` monitor owns failover.
- PDBs for both MariaDB and MaxScale.
- Replicas-first rolling updates.
- Replica recovery from a replica-seed template.

Apps connect to `<name>-maxscale.<ns>.svc.cluster.local:3306` (readwritesplit with transaction replay). Read-only traffic uses port 3308 on the same Service. TLS is on at the MaxScale listener from creation.

Standalone (1 pod, development) has no MaxScale. Replication with MaxScale switched off at create remains possible and uses the operator's own failover (§3.1).

**Three caveats on the default:**
1. **Replication is alpha in the operator's own API docs, which point to Galera for "more production-ready HA" [cited].** EntKube chooses replication for point-in-time restore (§3.1). That is EntKube's trade-off, confirmed by the user **[user decision, 2026-11-20]**, not the operator's recommendation, and the UI and docs say so.
2. **MaxScale ON matches the operator's own production recommendation** (its replication docs recommend MaxScale for switchover and failover [cited]), and the spike confirmed what it buys:
   - a planned switchover through MaxScale was transparent to an open session (0 errors, a 2.2 s stall) **[verified on kind, 26.10.2]**;
   - apps get one endpoint.
3. **Known blocker on 26.10.2 [refuted on kind].** On a hard primary failure (pod gone and its replacement unreachable, as on node loss), MaxScale promoted a replica, and **the operator demoted it again within 3 s**. This was reproduced 2 out of 2 times, once with 2 MaxScale replicas and once with 1. The consequences:
   - the cluster stayed write-unavailable for the whole outage;
   - writes acknowledged on the briefly promoted node were lost (1 and 19 rows);
   - replication broke with errant GTIDs.

   Without MaxScale, the operator's own failover took 2–11 s with no data loss **[verified on kind, 26.10.2]**.

   This design builds MaxScale ON as decided. It files the upstream issue in PR 0b and holds **release gate G1** (§3.3.9): MaxScale-fronted clusters keep the "Preview" badge, and the UI does not call them HA, until a hard-failure test passes on the pinned operator. Open question Q6 asks the user to re-confirm the default with this evidence. EntKube never flips the default on its own.

Galera is a later opt-in (PR 13).

The main structural difference from CNPG and Mongo: databases, users, grants and password changes are **declarative CRs (`Database`/`User`/`Grant`)**. Passwords live in Secrets, so EntKube runs **no write SQL over kubectl-exec** and puts **no password on any argv**. Every write therefore goes through the change gate and is audited.

The single exec path is a read, used for:
- rotation verification (a hash comparison, §9.2; this settles Q5);
- database discovery on imported clusters;
- drill verification.

The design copies CNPG's shape and leaves CNPG and Mongo behaviour untouched. It also deliberately avoids repeating the known CNPG/Mongo defects listed in §11.3.

---

## 2. Decision: engine and operator

### 2.1 Decision
- **Engine [user decision, 2026-11-20]:** MariaDB only, for now. Oracle MySQL comes later as a separate engine.
  - MariaDB LTS. The default is 12.3 LTS (GA 2026-05-28, supported to 2029-06); 11.8 LTS is offered as an option.
  - The version list lives in one place, `MariaDbVersions` (CNPG's 14–18 list is hard-coded in Razor).
  - Images are pinned to a **patch** tag (`mariadb:12.3.3`, `mariadb:11.8.9`).
    - The operator's default image is `mariadb:12.3.3` (`MARIADB_DEFAULT_VERSION 12.3` in `mariadb-operator-env`) **[verified on kind, 26.10.2]**.
    - Both `mariadb:12.3` and `docker-registry1.mariadb.com/library/mariadb:12.3` pulled and ran **[verified on kind, 26.10.2]**.
    - A floating minor tag would let any pod restart pull a new patch outside a gated upgrade, so EntKube does not use one.
- **Operator:** `mariadb-operator` 26.10.2. Never use 26.10.0, which its own release notes say to skip.
- **Product label:** "MariaDB (MySQL-compatible)". Apps connect with ordinary MySQL-protocol drivers (`mysql://`, `jdbc:mariadb:`/`jdbc:mysql:`, MySqlConnector, mysql2, PyMySQL, go-sql-driver/mysql).
- **Not promised:** Oracle-MySQL-only semantics. That means no Group Replication / InnoDB Cluster, and no MySQL 8.4/9.x/26.x system-schema or JSON behaviour. GTID formats, backup tools and HA stacks have diverged between the two engines.
  - **`caching_sha2_password` is not offered by default, but it works [verified on kind, 26.10.2] on 12.3.3 and 11.8.9.** It needs three things:
    - the server plugin loaded through `myCnf` `plugin_load_add = auth_mysql_sha2`, which is not loaded by default and restarts the pods;
    - a `User` with `passwordPlugin`, whose `pluginArgSecretKeyRef` holds the **70-character hash** `$A$005$<20-char salt><43 chars>` (SHA-256 crypt, 5000 rounds). A plaintext argument fails with Error 1372. EntKube can compute the hash in C# with an **alphanumeric** salt, because the operator interpolates the argument unescaped;
    - CA_CERT in the credentials, because MariaDB 11.4+ clients trust a self-signed certificate via the password only for native/ed25519 accounts.

    MySQL 8.4 clients work with `ssl-mode=REQUIRED`. It stays a per-user opt-in (PR 15). Whether it works **through MaxScale 23.08** is **[verify]** in PR 0b.
  - mariadb-operator supports only official MariaDB images **[cited]**, so it cannot also manage Oracle MySQL. Whether any other operator manages both engines was not established; it does not change this decision.
- **Naming:** entities, services and keys say `MariaDb`, never `MySql`, so they stay truthful the day an Oracle-MySQL engine arrives. That engine would be a **second, separate engine**, most likely on the Percona XtraDB Cluster operator. It would get its own entities and service, the way CNPG and Mongo are separate.

### 2.2 Why mariadb-operator
It is the only MySQL-family operator surveyed that covers every mechanism the CNPG service relies on:

| CNPG mechanism EntKube uses | mariadb-operator equivalent |
|---|---|
| `postgresql.cnpg.io/v1 Cluster`, operator failover | `MariaDB` CR. Failover belongs to MaxScale `mariadbmon` when `maxScaleRef` is set, otherwise to the operator (`replication.primary.autoFailover`). Switchover is MaxScale `spec.primaryServer`, or `replication.primary.podIndex` without MaxScale **[verified on kind, 26.10.2]** |
| Barman `ObjectStore` + `ScheduledBackup`, retention | `PhysicalBackup` (mariadb-backup → S3) with `schedule.cron` and `maxRetention` |
| WAL archive + `recoveryTarget.targetTime` | `PointInTimeRecovery` (binlog archive → S3) + `bootstrapFrom.targetRecoveryTime` |
| `bootstrap.recovery` into a new cluster | `bootstrapFrom` (`backupRef` / `s3` / `pointInTimeRecoveryRef`), new instance only |
| `monitoring.enablePodMonitor` | `metrics.enabled` + `metrics.serviceMonitor.prometheusRelease` (a per-cluster exporter Deployment, scraped through `/probe`) |
| `CREATE ROLE/DATABASE` via `kubectl exec psql` | Declarative `Database` / `User` / `Grant` CRs with `passwordSecretKeyRef` |

### 2.3 Alternatives rejected
| Operator | Why not |
|---|---|
| Percona XtraDB Cluster (PXC) 1.20 | MySQL engine with mature PITR. Monitoring is PMM-first: PMM is the primary path, although since 1.17 the binlog collector exposes Prometheus metrics and custom sidecars are possible, so "PMM-only" would overstate it. Its `users[]` block is add-only **[unverified]**, so we would be back to exec SQL for user lifecycle. (Backup-job TTL is **not** a reason: `backup.ttlSecondsAfterFinished` is opt-in, and each backup is recorded as a `PerconaXtraDBClusterBackup` object whose status survives Job deletion.) Kept as the candidate for a future Oracle-MySQL engine. |
| Percona Server for MySQL operator 1.3 | Group Replication is GA, but PITR and async mode are tech preview. |
| Oracle MySQL Operator | Its docs describe logical backups only (`dumpInstance` via MySQL Shell; clone is for initDB only) and document no PITR or retention. "No user CRDs" and "open metrics-sidecar bugs" were not verified and are not relied on. |
| MOCO | MySQL 8.x only, does not delete old backup files (no retention), and is a Cybozu project. It can restore to an arbitrary time, but binlogs are uploaded only at each backup run, so the recoverable window ends at the last backup (RPO equals the backup interval). |
| Vitess | Sharding platform; wrong scale and model for per-app databases. |

---

## 3. HA topologies

### 3.1 Offered topologies
The topology and the MaxScale choice are both fixed at create. Switching topology on a live cluster is refused, because the operator does not support it. Adding or removing MaxScale on a live cluster is also refused in the first release (§3.3.10).

| Topology | Pods | Default | Failover | PITR | UI label |
|---|---|---|---|---|---|
| **Replication (semi-sync) + MaxScale** | 3 MariaDB (allowed 2, 3, 5) + 2 MaxScale | **yes** [user decision] | MaxScale `mariadbmon` (`auto_failover`, `auto_rejoin`). The operator's failover is disabled while `maxScaleRef` is set **[verified on kind, 26.10.2]**. Hard failover is defective on 26.10.2 (§3.3.9) | **yes** | "HA (Preview: failover defect under test)" until G1 passes, then "HA (recommended)" — see the caveats in §1 |
| Replication (semi-sync), MaxScale off | 3 (allowed 2, 3, 5) | – | Operator: promotes the Ready replica with the highest `gtid_current_pos` (lowest index on a tie), repoints the others and the `-primary` Service. Measured at 2 s and 11 s **[verified on kind, 26.10.2]** | **yes** | "HA without proxy: apps reconnect on failover" |
| Standalone | 1 | – | none | **no.** The operator **admits** `pointInTimeRecoveryRef` on a standalone and silently does nothing: no agent, `log_bin=0`, no error, `lastRecoverableTime` empty forever **[verified on kind, 26.10.2]**. EntKube refuses it itself. | "Development, not HA" |
| Galera | 3 or 5 (odd only, enforced by a CRD CEL rule unless `replicasAllowEvenNumber` **[verified on kind, 26.10.2]**) | – (PR 13) | Multi-primary; quorum; agent sidecar on 5555. MaxScale (`galeramon`) is the HA default per decision 3, but it is untested **[verify]** in the PR 13 spike | **no** | "Multi-primary, no point-in-time restore" with warning |

Replication is the default rather than Galera because data safety has two axes. Galera wins on in-cluster durability. Only PITR helps with logical damage: a bad migration, `DROP TABLE` or ransomware. Galera replicates a `DROP TABLE` to every node in milliseconds.

A Galera full outage where every pod had a valid seqno recovered automatically in about 4 minutes through per-pod `<pod>-recovery` Jobs **[verified on kind, 26.10.2]**. Per the docs, a human must run `forceClusterBootstrapInPod` when every recovered seqno is -1 **[cited; not reproduced]**.

**Caveat.** This ranks PITR above the operator's own maturity labels. The operator calls replication alpha and steers production HA to Galera **[cited]**. "HA (recommended)" is therefore EntKube's recommendation. The UI help text says that replication is chosen for point-in-time restore and that the operator marks it alpha. The user confirmed this trade-off (decision 2).

### 3.2 Generated spec: the `MariaDB` CR (replication default)
Everything below was observed on kind unless marked otherwise. The builder applies every CR with **strict server-side field validation** (`--validate=strict`). A non-strict apply silently prunes unknown fields: an embedded `spec.maxScale` was dropped without error, and no proxy was created **[verified on kind, 26.10.2]**. Whether `IClusterClient`'s apply already passes strict validation was not checked; PR 3 adds it if not.

- `image: mariadb:<patch>` from `MariaDbVersions`.
- `replicas: 3`.
- `replication.enabled: true`, `semiSyncEnabled: true`, `semiSyncWaitPoint: AfterSync`, `gtidStrictMode: true` **[verified on kind, 26.10.2: `Rpl_semi_sync_master_status ON` with 2 clients, `AFTER_SYNC`]**.
- `replication.primary.autoFailover: true`. With MaxScale it is inert, because the operator's pod-replication controller returns early when `maxScaleRef` is set **[verified on kind, 26.10.2]**. It is still emitted so that a cluster whose MaxScale is ever removed fails over again.
- **Never emit `replication.primary.podIndex`.** Without MaxScale, the operator rewrites it in **spec** on every failover, so re-applying the original index would trigger an unwanted switchover **[verified on kind, 26.10.2]**. With MaxScale it is never updated and is ignored. Drift excludes it in both cases.
- **The replica template is required for every Replication cluster [verified on kind, 26.10.2].** The builder always emits `replication.replica.bootstrapFrom.physicalBackupTemplateRef: {name: <name>-replica-template}` and `replication.replica.recovery.enabled: true`. Without a template, each of these was observed:
  - the webhook rejects `recovery.enabled` (`'bootstrapFrom' must be set when 'recovery' is enabled`);
  - scale-out fails with `ScaleOutError … replication.replica.bootstrapFrom is nil`;
  - an old primary that rejoins with IO error 1236 (diverged GTID) **crash-loops on its startup probe forever**, so each failover can permanently cost a member.

  With the template, the old primary was re-seeded from S3 and was a healthy replica about 33 s after failover. The template is the `PhysicalBackup` `<name>-replica-template`:
  - `schedule.suspend: true`, `target: PreferReplica`, `mariaDbRef.waitForIt: false`, `compression: zstd`, `podAffinity: true`, `timeout: 1h`;
  - **its own prefix** `mariadb/<clusterId:N>/replica-seed/` with `maxRetention: 168h` and a bucket lifecycle rule (§3.5 item 6, Q3).

  The operator clones the template as a transient `<name>-pb-scaleout` or `<name>-pb-recovery`, runs it against a Ready replica, seeds the new PVC through `<name>-<i>-pb-init`, and deletes the transient objects about 20 s after success. The S3 object stays **[verified on kind, 26.10.2]**. The template's Jobs carry `sidecar.istio.io/inject: "false"` through `podMetadata`.

  Where no StorageLink exists ("no backups" ticked), the template uses `storage.persistentVolumeClaim`. The operator deletes the PVC it provisioned for the transient object. This was read from source and **not run [verify, PR 0b]**. If it does not work, "no backups" is refused for Replication (decision 8).
- `rootPasswordSecretKeyRef: {name: <name>-root, key: password}`. The value never appears in the CR.
  - The field is **immutable**: the webhook rejects adding `generate: true` later.
  - The operator never creates a missing Secret unless `generate: true`. In that case the CR waits at `Ready=False/Failed "Error reconciling Secret …"` and recovers on its own about 37 s after the Secret appears **[verified on kind, 26.10.2]**.
  - EntKube keeps the "Secret before the CR" order and never uses `generate: true`, which creates a Secret with no labels and no owner.
- `storage.size`, optional `storageClassName`, `resizeInUseVolumes: true`.
  - **EntKube does not emit `storage.volumeClaimTemplate`.** When `storage.size` is set, the defaulting webhook replaces the template with `accessModes: [ReadWriteOnce]` and silently drops a user-supplied `ReadWriteOncePod` **[verified on kind, 26.10.2]**. PVCs are therefore RWO; RWOP is not offered (§11.1).
  - `pvcRetentionPolicy` stays at the operator default, which is `Retain` for both `whenDeleted` and `whenScaled` **[verified on kind, 26.10.2]**. Scale-in deletes the PVC explicitly (§4.1 row 9b).
- `updateStrategy.type: ReplicasFirstPrimaryLast` and **always** `mariadbAutoUpgradeEnabled: true`. It must already be set before any major-version image bump, so EntKube never emits a manifest without it.
  - The StatefulSet uses `OnDelete`, and the operator drives the rollout itself: replicas one at a time, then a switchover, then the old primary **[verified on kind, 26.10.2]**.
- **Anti-affinity:** EntKube writes an explicit `podAntiAffinity` on `kubernetes.io/hostname` with `labelSelector.matchLabels {app.kubernetes.io/instance: <name>, app.kubernetes.io/name: mariadb}`. The operator passes it to the StatefulSet verbatim **[verified on kind, 26.10.2]**.
  - When schedulable nodes ≥ replicas: **required**.
  - When there are fewer nodes: create is refused unless the operator ticks "Allow replicas on the same node (not node-fault-tolerant)". EntKube then writes *preferred* and stores `HaDegradedReason`, which the advisor surfaces.
  - `antiAffinityEnabled: true` is **not** used, although it produces exactly the required-by-hostname rule **[verified on kind, 26.10.2]**. It cannot express the preferred case, the webhook writes the expanded rule back into spec, and on MaxScale the same field is immutable and also repels MariaDB pods (§3.3.7).
  - The node count comes from `IClusterClient.GetJsonAsync("nodes", "")`, a read.
- Zone spread: `topologySpreadConstraints` on `topology.kubernetes.io/zone`, `whenUnsatisfiable: ScheduleAnyway`.
- **PDB: emit nothing.** For replication, the operator creates PDB `<name>` with `minAvailable: 50%` and selector instance+name by default, even when `spec.podDisruptionBudget` is unset. With 3 pods, 1 disruption is allowed **[verified on kind, 26.10.2]**. Whether a standalone gets a PDB was not checked **[verify]**. Init Job pods (`<name>-<i>-pb-init`) match the PDB selector and produce harmless `CalculateExpectedPodCountFailed` warnings during scale-out **[verified on kind, 26.10.2]**.
- `metrics.enabled: true`, plus the following (the operator runs a separate exporter Deployment, §9.5):
  - `metrics.exporter.args: [--collect.binlog_size]`. The args are appended to the defaults; without the flag there is no binlog-size series **[verified on kind, 26.10.2]**.
  - `metrics.exporter.resources`.
  - `metrics.exporter.podMetadata`, carrying the Istio label.
  - `metrics.serviceMonitor.prometheusRelease: <kube-prometheus-stack release>`, read through `ICatalogApi`, plus explicit `interval: 30s` and `scrapeTimeout: 10s`.
  - The `release` label and `jobLabel` are written **only when the ServiceMonitor is created** **[verified on kind, 26.10.2]** (§9.5 has the repair).
- Resources use Mongo's rule: limit-only input gets a modest request floor.
- `myCnf` defaults:
  - `innodb_buffer_pool_size` ≈ 50% of the memory limit, `binlog_format=ROW`, `sync_binlog=1`, `innodb_flush_log_at_trx_commit=1`, `max_connections=500`;
  - **`max_binlog_size=64M`**: the active binlog is never archived, so its size bounds the RPO (§3.5);
  - `log_slave_updates` is open question Q7.

  Editing them is deferred.
- `tls.enabled: true`. TLS is on by default even with no `tls` block **[verified on kind, 26.10.2]**. `tls.required` follows `TlsRequired` (decision 7).
- `podMetadata` carries `sidecar.istio.io/inject: "false"` for the MariaDB pods. The operator never adds it on any object **[verified on kind, 26.10.2]**. The annotation form on `spec.podMetadata` and on PhysicalBackup `podMetadata` reached the pods **[verified on kind, 26.10.2]**; the label form is Istio's current form, and the builder emits both. Restore Jobs take it through `bootstrapFrom.restoreJob.metadata` and Galera Jobs through `galera.recovery.job.metadata` / `galera.initJob.metadata` **[cited]**. Whether EntKube database namespaces are injected at all is still **[verify]**.
- `maxScaleRef: {name: <name>-maxscale}` when MaxScale is on (§3.3).
- `spec.env` is **not** emitted. `MARIADB_ROOT_PASSWORD` is already injected, and `spec.env` would reach the init and agent containers too **[verified on kind, 26.10.2]**. `enableServiceLinks: false` would cut the noisy per-Service env vars, but whether the field exists in the CRD is **[verify]**.
- The data-plane agent sidecar runs in the replication topology too. Its args are `agent replication --addr=:5555 --probe-addr=:5566 --kubernetes-auth`, the mariadb container's probes are HTTP against 5566, and pods show 2/2 **[verified on kind, 26.10.2]**.

**There is one builder: `MariaDbManifests.Cluster(spec, bootstrapFrom?)`, plus `MariaDbManifests.MaxScale(spec)` emitted from the same spec.**
- Restore and cut-over clusters differ from a fresh create only in the top-level `bootstrapFrom`.
- Drill clusters differ in `bootstrapFrom`, and also have metrics off and MaxScale off: a drill verifies data, not the proxy, and metrics turned off later leave objects behind (§9.5).
- The replica-template PhysicalBackup is emitted for every replication cluster, including restored ones.

This avoids CNPG's `BuildRestoreManifest` divergence, where restored clusters lose their HA settings.

### 3.3 MaxScale (default for HA clusters)

#### 3.3.1 How it is attached
- Only a **separate `MaxScale` CR** works in 26.10.2. Its `spec.mariaDbRef` points at the MariaDB, and the MariaDB's `spec.maxScaleRef {name}` points back. The embedded `MariaDB.spec.maxScale` no longer exists in the CRD: a strict apply rejects it, and a non-strict apply drops it silently **[verified on kind, 26.10.2]**.
- Name: `<name>-maxscale`.
- **Create order:** the MariaDB (with `maxScaleRef`) and the MaxScale CR are applied back to back in the same operation. This is the order the spike's manifests used. While `maxScaleRef` names a MaxScale that does not exist, the MariaDB shows `Ready=False/Failed` (ref NotFound), and **no failover exists at all**, because the operator's own failover is off whenever the ref is set **[verified on kind, 26.10.2]**. Whether applying MaxScale *first* also works was not tested **[verify, PR 0b]**.
- No `Connection` CR is created; `spec.connection` is an optional template that EntKube does not use **[verified on kind, 26.10.2]**.

#### 3.3.2 Generated spec
- `replicas: 2`. The operator docs recommend 2, the scale subresource works, and the StatefulSet `<name>-maxscale` uses `Parallel` **[verified on kind, 26.10.2]**.
- `image: mariadb/maxscale:23.08.13-2`, **always explicit**, never the operator default (§3.3.8).
- `services`:
  - `rw-router` (`readwritesplit`) on 3306, with `transaction_replay: "true"`, `transaction_replay_attempts: "10"`, `transaction_replay_timeout: "5s"`, `max_slave_connections: "255"`, `max_replication_lag: "3s"`, `master_accept_reads: "true"`, and listener param `connection_metadata: "tx_isolation=auto"`;
  - `rconn-slave-router` (`readconnroute`, `router_options: slave`, `max_replication_lag: "3s"`) on 3308.

  Both were verified as the spike's working form. The spike's third listener (3307, master `readconnroute`) is not emitted, because readwritesplit already covers primary traffic. If `services` is omitted, the operator defaults to only a readwritesplit listener on 3306 **[verified on kind, 26.10.2]**.
- `monitor`: `interval: 2s`, `cooperativeMonitoring: majority_of_all`, and params `auto_failover: "true"`, `auto_rejoin: "true"`, `switchover_on_low_disk_space: "true"`. MaxScale starts failover after 4 failed monitor ticks **[verified on kind, 26.10.2]**.
- `admin: {port: 8989, guiEnabled: false}`. With the GUI off, no `-gui` Service is created **[verified on kind, 26.10.2]**.
- `tls` at **create** time (§3.3.6).
- `affinity.podAntiAffinity`: required on `kubernetes.io/hostname` among MaxScale pods only (`app.kubernetes.io/instance: <name>-maxscale`, `app.kubernetes.io/name: maxscale`). Preferred in the degraded-HA case (§3.3.7).
- `metrics.enabled: true` (§3.3.5 and §9.5).
- `resources`: requests `100m`/`128Mi`, limit `512Mi` (the spike's working values; observed peak 58–67 MiB, §3.3.7).
- `podMetadata` with `sidecar.istio.io/inject: "false"`. That the MaxScale CR's field is named `podMetadata` is **[verify]**.
- **Never emit `spec.primaryServer`.** Only the switchover action writes it (§3.3.4), and drift excludes it.

#### 3.3.3 Failover ownership
- **MaxScale's `mariadbmon` performs failover.** While `maxScaleRef` is set, the operator's pod-replication controller is disabled (`PodReplicationController.shouldReconcile` returns false when `IsMaxScaleEnabled()`), even though `autoFailover` stays `true` in spec **[verified on kind, 26.10.2]**.
- **The operator's replication reconciler still runs.** It configures replicas and `read_only`. It takes the primary from MaxScale's live REST API (the monitor's `diagnostics.primary`), and falls back to `status.currentPrimaryPodIndex` when that answer is unavailable **[verified on kind, 26.10.2, from source and logs]**. That fallback is where the defect in §3.3.9 enters.
- `MariaDB.status.currentPrimary`, `status.replication.roles` and the `<name>-primary` selector follow MaxScale within about 2 s on a switchover **[verified on kind, 26.10.2]**. `spec.replication.primary.podIndex` is never updated: it stayed 0 while the primary was `mdb-1`.
- **A plain primary pod delete does not fail over** **[verified on kind, 26.10.2]**. The pod is back within MaxScale's 4-tick failcount (2 s interval), but there is still an outage:
  - open sessions are closed (`ERROR 2013`, then `2006`). Transaction replay cannot help, because there is no other primary;
  - new connections could not write for about 13 s, because the returning primary waits out the semi-sync timeout (`rpl_semi_sync_master_timeout=10000`) while replicas reconnect only after `Connect_Retry=60s`;
  - semi-sync then ran async for about 47 s.

  "Transparent reconnection" therefore holds for **planned switchovers only**, not for primary restarts.

#### 3.3.4 Switchover
- With MaxScale, patch `MaxScale.spec.primaryServer = <pod name>` (server names equal pod names).
  - Conditions on the MaxScale CR: `PrimarySwitched=False/SwitchPrimary "Switching primary to '<pod>'"`, then `PrimarySwitched=True/SwitchPrimary "Switchover complete"`, plus event `MaxScalePrimaryServerChanged` **[verified on kind, 26.10.2]**.
  - **Completion** is `PrimarySwitched=True` with `"Switchover complete"` **and** `MariaDB.status.currentPrimary == <pod>`.
  - An open session saw 0 errors and a 2.2 s stall **[verified on kind, 26.10.2]**.
  - MariaDB `podIndex` is ignored while `maxScaleRef` is set (`switchover.go` returns early) **[verified on kind, 26.10.2]**.
- Without MaxScale, patch `MariaDB.spec.replication.primary.podIndex`.
  - The steps run `PrimaryLock` (FLUSH TABLES WITH READ LOCK) → `PrimaryReadonly` → `ReplicaSync` → `PrimaryNew` → `ReplicaConn` → `PrimaryToReplica` → `PrimarySwitched`. A clean run took about 80 ms, with no restarts **[verified on kind, 26.10.2]**.
  - **Completion** is `PrimarySwitched=True` **and** `status.currentPrimaryPodIndex == requested`, never `Ready` alone.
  - The conditions read identically for a failover. Only the Events tell them apart: `PrimaryLock` appears only on a planned switchover.
- Both are a `DeclareConsequence` naming the target pod. Cancelling the non-MaxScale form means setting `podIndex` back **[cited]**.

#### 3.3.5 Credentials and Secrets MaxScale uses
- With `auth.generate` (the default under `mariaDbRef`), the operator creates these Secrets, each with key `password`, **no ownerReference and no labels** **[verified on kind, 26.10.2]**:
  - `<name>-maxscale-admin` (REST admin user `mariadb-operator`; the default admin is deleted);
  - `-client` (`SELECT, INSERT, UPDATE, DELETE ON *.*`);
  - `-server` (`SHOW DATABASES ON *.*`, `SELECT ON mysql.*`);
  - `-monitor` (SUPER, REPLICATION SLAVE ADMIN, READ_ONLY ADMIN, BINLOG ADMIN, CONNECTION ADMIN and others `ON *.*`; also used as `mariadbmon`'s `replication_user`);
  - `-sync` (on `mysql.maxscale_config`);
  - `-metrics` (REST user `metrics`).
- The SQL users are operator-owned `User`/`Grant` CRs named `<name>-maxscale-{client,server,monitor,sync}`.
- No password appears in any CR spec or ConfigMap.
- **The MaxScale PVC stores the server and monitor passwords in plaintext** (`/var/lib/maxscale/maxscale.cnf.d/*.cnf`, mode 0644, no `.secrets` key) **[verified on kind, 26.10.2]**. That is a §9.7 risk.
- EntKube's rules:
  - **Leave these to the operator.** They are not copied into the vault, never synced to app namespaces, and never shown. `<name>-maxscale-client` is a cross-database DML credential and **must never be handed to apps**.
  - **Apps authenticate through MaxScale with their own per-database users** (verified working through readwritesplit).
  - `readwritesplit` opens one backend connection **per server** for each client session (`max_slave_connections: 255`). An app user's `maxUserConnections` therefore applies per server and must cover the app's whole pool (§9.6).
  - Delete removes the six Secrets explicitly, because they survive MaxScale deletion (§4.1 row 6).
  - **Monitor-user hazard.** A replica that MaxScale redirected during a failover or rejoin keeps replicating as `Master_User=<name>-maxscale-monitor`. When MaxScale is removed, that user is dropped, the replica's next reconnect fails (`Access denied`), and the operator does not repair it, even after `maxScaleRef: null` and a pod restart **[verified on kind, 26.10.2]**. This is the reason for §3.3.10.

#### 3.3.6 TLS
- **With operator defaults, MaxScale TLS is off on both legs.** MaxScale `tls.enabled` defaults on only when the MariaDB has TLS enabled **and required**. A default-flag 12.3 client then connected through MaxScale **in plaintext** **[verified on kind, 26.10.2]**. Client behaviour against a non-TLS listener was inconsistent across runs, so apps must set the TLS mode explicitly.
- **The builder always emits, at create time:**
  - `tls.enabled: true`;
  - `tls.serverCASecretRef: {name: <name>-ca-bundle}`;
  - `tls.serverCertSecretRef: {name: <name>-client-cert}`.

  Without the two refs, reconcile fails with `Ready=False "spec.tls.serverCASecretRef and spec.tls.ServerCertSecretRef fields must be set"`. With them, the operator creates `<name>-maxscale-ca`, `-ca-bundle` (MaxScale CA plus MariaDB CA), `-listener-cert` and `-admin-cert`. The CA lasts 3 years and leaf certificates 90 days. The result: listener and server legs at TLSv1.3, the admin API on HTTPS, verify-full working, and **plaintext clients refused** **[verified on kind, 26.10.2]**.
- **Listener TLS must be set at creation.** Enabling TLS on an existing MaxScale secured the server leg and the admin API, but existing listeners stayed `ssl=false` **[verified on kind, 26.10.2]**. Changing MaxScale TLS later therefore means recreating MaxScale, which §3.3.10 refuses in the first release.
- The listener certificate SANs cover `<name>-maxscale`, `<name>-maxscale.<ns>[.svc[.cluster.local]]` and `*.<name>-maxscale-internal…`, so `sslMode=verify-full` against the Service FQDN works **[verified on kind, 26.10.2]**.
- **Apps behind MaxScale get CA_CERT from `<name>-maxscale-ca-bundle` (`ca.crt`), not from the MariaDB CA.**
- MaxScale → MariaDB does not verify the peer by default (`verifyPeerCertificate`/`verifyPeerHost` false) **[verified on kind, 26.10.2]**. This is in-namespace traffic, and NetworkPolicy (§9.5) is the control.
- Whether MaxScale works with MariaDB `tls.required: true` was not run (the spike ran with `required: false`) **[verify, PR 0b]**.

#### 3.3.7 PDB, placement and footprint
- The operator creates PDB `<name>-maxscale` with `minAvailable: 50%`. **With `replicas: 1` that allows 0 disruptions and blocks node drains** **[verified on kind, 26.10.2]**. EntKube always creates 2, and the advisor flags imported MaxScales with 1 (`mariadb-maxscale-single-replica`).
- **Never use MaxScale `affinity.antiAffinityEnabled`.** It is **immutable** (changing it means delete and recreate), and it produces a required rule against **both** the MaxScale and the MariaDB instance. 3 MariaDB + 2 MaxScale then need 5 nodes; on 3 workers, both MaxScale pods stayed Pending **[verified on kind, 26.10.2]**. EntKube's explicit rule repels only MaxScale pods, so MaxScale co-locates with MariaDB pods.
- The node-count check for create is: nodes ≥ max(MariaDB replicas, 2). Otherwise the same degraded-HA tick applies, and EntKube writes preferred anti-affinity for both.
- Footprint per MaxScale pod **[verified on kind, 26.10.2]**:
  - `memory.current` was 24.7 MiB, with a peak of 58–67 MiB, after 30 min of light load and failover tests;
  - `query_classifier_cache_size` is set to 15% of the memory limit, so the limit drives the cache;
  - each pod gets a 100Mi PVC (`config.volumeClaimTemplate` default);
  - the MaxScale exporter Deployment used 22.8 MiB;
  - CPU was not measured.
- Config sync: the operator sets `config_sync_cluster`, but `mysql.maxscale_config` stayed empty because the operator configures each pod through its REST API **[verified on kind, 26.10.2]**. The GUI Service, when enabled, targets only pod `-0` **[verified on kind, 26.10.2]**.

#### 3.3.8 Licence, version, and the bump rule
**Facts [verified on kind, 26.10.2]:**
- Operator 26.10.2 defaults to `mariadb/maxscale:23.08.13-2` (`RELATED_IMAGE_MAXSCALE`; the binary reports MaxScale 23.08.13 on Rocky Linux 8.10). The exporter is `mariadb/maxscale-prometheus-exporter-ubi:v0.0.1` (`RELATED_IMAGE_EXPORTER_MAXSCALE`).
- The image ships `LICENSE.TXT` with: BSL 1.1; Licensed Work "MariaDB MaxScale version 23.08"; Additional Use Grant "less than three server instances in production"; Change Date 2026-09-21; Change License GPLv2 or later.
- The RPM header still says "MariaDB BSL 1.1", and the image has no OCI licence labels.
- Under the BSL's own Change Date clause, **23.08 is GPLv2+ as of 2026-09-21**, even though the shipped files still say BSL. The operator's `maxscale.md` warning that 23.08 is BSL is stale.
- **MaxScale 24.08 is not covered.** `LICENSE2408.TXT` has the same "less than three server instances in production" grant, with Change Date 2027-09-09. **EntKube's default (3 servers) is outside that grant**, so 24.08+ would need a commercial licence until its Change Date.
- A newer 23.08 maintenance release exists: 23.08.15 (2026-09-17; 23.08.14 was 2026-06-15). The operator does not pin it.
- The **23.08 end-of-support date is unknown**. MariaDB's engineering-policy documents found list 22.08 (EOL 2026-01-01) and 23.02 (EOL 2027-01-01), but have no 23.08 row **[verify]**.

**Rules:**
1. EntKube pins the MaxScale image itself (`MariaDbOperator.MaxScaleImage`), always explicit in `MaxScale.spec.image`, never inherited from the operator's `RELATED_IMAGE_MAXSCALE`. An operator bump can therefore never move MaxScale.
2. **Only 23.08.x tags are allowed.** A unit test (`MaxScaleImagePinTests`) fails the build for any MaxScale tag that does not start with `23.08.`. The advisor raises `mariadb-maxscale-image:{id}` for imported clusters whose MaxScale image is outside 23.08.
3. **A bump within 23.08** (for example 23.08.13-2 → 23.08.15) is a catalog-level change. It needs:
   - the PR 0b MaxScale test set re-run on kind (switchover transparency, pod-delete, hard failure, TLS, metrics);
   - a builder snapshot diff;
   - a release note.

   Existing clusters pick it up only through the gated re-apply (rolling, one ack).
4. **Leaving 23.08** (to 24.08 or later) needs a user decision on licensing. It can be licence-free only from that version's Change Date (2027-09-09 for 24.08), and it needs the same re-test. The same decision is needed if 23.08 reaches end of support first.
5. Every operator bump checks that `RELATED_IMAGE_MAXSCALE` and the MaxScale CRD did not change in a way the pinned image cannot serve, and re-runs the §3.3.9 hard-failure test.
6. The third-party notices state MaxScale 23.08 as GPLv2+ via the BSL Change Date, and quote the shipped `LICENSE.TXT`.

#### 3.3.9 Known defect on 26.10.2 and release gate G1
**Observed [refuted on kind]** (the claim that MaxScale delegates HA cleanly; reproduced twice, once with 2 MaxScale replicas and once with 1):
1. The primary was black-holed (NetworkPolicy plus pod delete, which is closer to node loss than to a crash). MaxScale began "Performing automatic failover" at about +14 s.
2. `STOP SLAVE` on the candidate failed for about 50 s, because its IO thread was stuck connecting to the dead primary. Failover completed at about +68 s.
3. Within 2–3 s, the operator logged "MaxScale primary server changed", then **"Configuring replica mdb-1"**. It re-pointed the newly promoted primary at the dead pod and turned `read_only` back on.
4. MaxScale then reported "mdb-1 is no longer valid because it is in read-only mode, but there is no valid alternative".
5. The cluster stayed **write-unavailable for the whole isolation** (3 min in run 2). It recovered only when the old primary returned.
6. **Acknowledged writes were lost**: 1 row in run 1 and 19 rows in run 2. The promoted node kept errant GTIDs, broke with error 1236, and needed a manual repair.
7. Another replica was left `read_only=OFF` while replicating as `<name>-maxscale-monitor`.
8. Throughout, MariaDB `status.currentPrimary` stayed on the dead pod, and MaxScale showed `Ready=False/MaxScaleNotReady`.

kindnet does not cut established connections, so a policy alone could not simulate the partition. The defect itself (the operator undoing MaxScale's promotion) does not depend on how the failure was simulated.

**What EntKube does:**
- PR 0b files an upstream issue with `status-failover3.log` / `status-failover4.log`, and adds the hard-failure test to the spike harness. The test runs a writer through MaxScale, black-holes and deletes the primary, and asserts all three of the following:
  - writes resume within a bound;
  - no write acknowledged during the outage is lost;
  - no member is left with errant GTIDs.
- **Release gate G1:** MaxScale-fronted clusters carry the "Preview" badge, and the create form says "automatic failover on node loss is under test" with a link to this section, until G1 passes on the pinned operator version. G1 is re-run on every operator bump.
- If G1 has not passed by the time PR 7 removes the general Preview badge, the user decides between keeping the MaxScale default with the documented failure mode or defaulting it off temporarily (Q6). EntKube never flips the default on its own.
- The advisor makes the failure visible instead of silent: `mariadb-maxscale-primary-mismatch` fires when MaxScale's primary differs from `MariaDB.status.currentPrimary`, or when MaxScale reports a read-only primary, for more than 2 minutes (§8 row 18).
- Hypotheses to put in the upstream issue (not facts): the operator's fallback to `status.currentPrimaryPodIndex` when MaxScale's API is briefly unavailable (the cooperative lock was lost and reacquired at the moment of promotion), and `log_slave_updates` being off (Q7).

#### 3.3.10 Adding or removing MaxScale on a live cluster
Both are refused in the first release:
- **Removing** strands any replica that replicates as `<name>-maxscale-monitor` (§3.3.5). It leaves six unowned Secrets. And while `maxScaleRef` points at a missing MaxScale, there is no failover at all **[verified on kind, 26.10.2]**.
- **Adding** later cannot give the listeners TLS unless MaxScale is created with it, and it changes every app's HOST and CA_CERT.

A later PR (15) can add both, with a pre-flight read of `SHOW SLAVE STATUS` `Master_User` on each replica (an exec Read) and a documented `CHANGE MASTER TO MASTER_USER='repl'` repair. Deleting the whole cluster removes MaxScale first (§4.1 row 6).

### 3.4 Endpoints
| Cluster | HOST:PORT (writes and general use) | READ_HOST:READ_PORT | CA_CERT source |
|---|---|---|---|
| Replication + MaxScale | `<name>-maxscale.<ns>.svc.cluster.local:3306` (readwritesplit, transaction replay) | `<name>-maxscale.<ns>.svc.cluster.local:3308` (readconnroute slave; lagged replicas over 3 s excluded) | `<name>-maxscale-ca-bundle` `ca.crt`, always (the listener refuses plaintext) |
| Replication, MaxScale off | `<name>-primary.<ns>.svc.cluster.local:3306` | `<name>-secondary.<ns>.svc.cluster.local:3306` | `<name>-ca-bundle` `ca.crt` |
| Standalone | `<name>.<ns>.svc.cluster.local:3306` | same as HOST | `<name>-ca-bundle` `ca.crt` |

Facts behind the table **[verified on kind, 26.10.2]**:
- **MariaDB Services.** Each replication MariaDB gets five ClusterIP Services owned by the CR:
  - `<name>` (all pods);
  - `<name>-primary` (selector adds `statefulset.kubernetes.io/pod-name=<currentPrimary>`, rewritten on failover and switchover);
  - `<name>-secondary` (no selector; the operator manages its EndpointSlice with replicas only, and a broken replica was listed `ready=false`);
  - `<name>-internal` (headless, `publishNotReadyAddresses`, ports 3306/5555/5566; the StatefulSet's serviceName);
  - `<name>-metrics` (9104, the exporter Deployment).

  Without MaxScale, `-primary` pointed at a dead pod for up to 11 s during a failover. Whether `-secondary` drops a *lagging* (as opposed to broken) replica was not observed **[verify]**.
- **MaxScale Services:**
  - `<name>-maxscale` (ports `admin` 8989 plus one per listener, named `<service>-listener`);
  - `<name>-maxscale-internal` (headless, no ports);
  - `<name>-maxscale-metrics` (9105).

  **The admin API (8989) shares the app-facing Service**, so NetworkPolicy must open only the listener ports to apps (§9.5).

`READ_PORT` is a new vault key, because the read port differs from `PORT` behind MaxScale. Applications on MaxScale clusters must connect with TLS; the connection docs say so, and also that an open session is still dropped when the primary pod restarts (§3.3.3).

### 3.5 What "as HA as possible" means (acceptance checklist)
1. Three MariaDB members on three nodes (required anti-affinity), two MaxScale pods on two nodes, soft zone spread, and both PDBs (operator-created).
2. Semi-sync `AfterSync`: a commit acknowledged to the app exists on a replica.
   - If the acknowledgement times out (10 s), MariaDB falls back to async. MaxScale logged "Semi-synchronous replication … stopped working" for about 47 s after a routine primary restart **[verified on kind, 26.10.2]**.
   - The advisor surfaces sustained async (PR 9), **debounced** to more than 5 minutes so routine restarts do not fire it.
3. Automatic failover by MaxScale, subject to G1 (§3.3.9). Switchover is an explicit, gated EntKube action, and it is transparent through MaxScale.
4. Replicas-first-primary-last rollouts, with a switchover before the primary restarts.
5. Replica recovery is always enabled, backed by the replica-template PhysicalBackup (§3.2). A diverged member is re-seeded instead of crash-looping.
6. Off-cluster backups:
   - A `PhysicalBackup` plus a binlog archive to an S3 StorageLink. **A StorageLink is required to create a Replication or Galera cluster** unless the operator ticks "no backups (not production)", which raises a standing advisor finding (and see decision 8).
   - Prefixes are keyed by cluster **Id**: `mariadb/<clusterId:N>/physical/`, `mariadb/<clusterId:N>/binlog/` and `mariadb/<clusterId:N>/replica-seed/`.
     - A re-created same-name cluster never shares an archive, and PITR's empty-prefix-at-first-archival rule holds **[verified on kind, 26.10.2]**.
     - The seed prefix is separate because `maxRetention` prunes **every** `physicalbackup-*` object in a prefix, whoever wrote it, and object keys carry no CR name (§4.1 row 15).
7. **RPO is stated honestly.** Binlog archival runs every **10 minutes (hard-coded, not configurable)** on the current primary's agent, and **never uploads the active binlog** **[verified on kind, 26.10.2]**. RPO is therefore 10 minutes plus the time to fill `max_binlog_size` (64M). On a quiet cluster that can be hours. The UI shows the measured archive lag (§4.1 row 16); a time-based flush is Q8.
8. Restore into **another** Kubernetes cluster of the tenant is a first-class action. That is the cluster-loss story; Velero PVC snapshots of a live database are not.
9. Status and backup history are recorded by a **read-only poller**, so the advisor is right about clusters nobody opens.
10. Restores are proven by **restore drills**: manual in PR 12, opt-in scheduled later.
11. G1 passes on the pinned operator (§3.3.9).

**Measured behaviour [verified on kind, 26.10.2]:**

| Event | Without MaxScale | With MaxScale |
|---|---|---|
| Planned switchover | about 80 ms; writes blocked by `FLUSH TABLES WITH READ LOCK` during it | 2.2 s stall; 0 errors on an open session (transaction replay) |
| Rolling restart / update (3 pods) | about 1 min 41 s total; about 10 s of write lock during the built-in switchover | switches over through MaxScale (stall during rollout not measured) |
| Primary pod deleted, returns at once | failover in 2 s (run 1) or 11 s (run 2, Error 1948 then a 10 s `syncTimeout` retry); old primary re-seeded from S3 in about 33 s (run 1) or rejoined cleanly (run 2); no data loss | no failover; open sessions killed; about 13 s write gap for new connections; about 47 s async |
| Primary unreachable (node loss) | not tested separately | **failed**: write outage for the whole outage, 1–19 acknowledged writes lost, errant GTIDs (§3.3.9) |
| Galera full outage | – | auto-recovered in about 4 min (Galera, no MaxScale) |

---

## 4. Parity matrix

Status key:
- **direct**: the same capability, via a native operator mechanism.
- **adapted**: the same outcome by a different mechanism.
- **improved**: also fixes a known CNPG/Mongo gap.
- **deferred**: possible, scheduled later.
- **n/a**: not applicable to this engine.
- **not possible**: the operator cannot do it.

The PR column refers to §10.

### 4.1 Every inventoried CNPG feature
| # | CNPG feature | MariaDB mechanism | Status | PR |
|---|---|---|---|---|
| 1 | Operator availability detection (`DatabaseService.GetOperatorStatusAsync`, `GetCnpgEnabledClustersAsync`, re-check in `CreateClusterAsync`) | `DatabaseOperatorStatus.MariaDbAvailable`, `GetMariaDbEnabledClustersAsync`. **One** static predicate `MariaDbOperator.IsInstalled(InstalledComponent)` is used by both `DatabaseService` and `MariaDbService.CreateClusterAsync` (via `ICatalogApi.GetComponentsForClusterAsync(tenantId, clusterId)`, Status Installed). | improved: CNPG's list accepts ReleaseName `cnpg` but create does not | 1 |
| 2 | Catalog `cloudnative-pg` + Barman subchart | Two entries: `mariadb-operator-crds` (detected by `mariadbs.k8s.mariadb.com`) and `mariadb-operator` (pinned 26.10.2, `Dependencies = ["mariadb-operator-crds"]`, `ConfiguredIn = ServicesSection("databases", …)`, detected by Helm release). The operator chart's own `crds.enabled` defaults to false **[cited]**. A `subchart:` field is **not** usable for the CRDs: `ComponentLifecycleService.InstallSubchartsAsync` installs subcharts after the parent. **Operator metrics:** chart values `metrics.enabled: true` and `metrics.serviceMonitor.additionalLabels.release: <kps release>`. The chart's three ServiceMonitors (`mariadb-operator`, `-webhook`, `-cert-controller`, port `metrics` 8080) carry no `release` label otherwise, and expose only controller-runtime metrics, useful for reconcile-error alerts only **[verified on kind, 26.10.2]**. **MaxScale is not a catalog component.** The operator runs it per cluster, and EntKube pins its image in the builder (§3.3.8). **CRD retention is not free:** the CRDs chart has no keep option (empty `values.yaml`, no `helm.sh/resource-policy: keep` on its 12 CRDs) **[cited]**, and a `helm uninstall` of it deletes the CRDs and cascade-deletes every CR, i.e. every MariaDB cluster **[verified on kind, 26.10.2]**: `helm uninstall mariadb-operator-crds` with 10 `MariaDB` CRs present removed all 12 CRDs within 60 s, and with them every `MariaDB` CR, all 9 StatefulSets, their pods and 39 of 77 Secrets in those namespaces; 32 of 34 PVCs survived, so the data is orphaned rather than wiped. (`keepCRDs` at `ComponentCatalog.cs:2758` is a value of the snapshot-controller chart, not a catalog mechanism.) EntKube must therefore either refuse uninstalling `mariadb-operator-crds` while any `MariaDB` CR exists, or add the keep annotation through a post-render step. No such uninstall guard or post-render hook exists in `ComponentCatalog.cs`/`ComponentLifecycleService.cs` today; which one to build is open question Q1. | adapted | 1 |
| 3 | Create managed cluster | `CreateClusterAsync`: row (Creating) → one `gate.BeginOperation("create a MariaDB cluster")` → EnsureNamespace → `<name>-root` Secret (vault value, label `k8s.mariadb.com/watch`) → `<name>-s3-credentials` → `MariaDB` CR (with `maxScaleRef`) → `MaxScale` CR → replica-template `PhysicalBackup` → scheduled `PhysicalBackup` → `PointInTimeRecovery`. Each CR is a separate apply with **strict field validation**, so the error is precise. Creation reached `Ready=True` about 37 s after apply without MaxScale **[verified on kind, 26.10.2]**; between the MariaDB and MaxScale applies, the MariaDB reports a transient `Ready=False/Failed` (ref NotFound). A schedule without a StorageLink is refused. PITR on Standalone is refused (§3.1). The StorageLink is loaded tenant-scoped and the **validated** id is stored. | direct (+fixes CNPG storing the caller's id) | 3 / 6 |
| 4 | HA settings in spec | §3.2 and §3.3, in the single builder | improved | 3 |
| 5 | Import existing CNPG cluster | `ImportClusterAsync`: read `mariadb.k8s.mariadb.com/<name>`, parse image, replicas, storage and topology, and `spec.maxScaleRef`. If the ref is set, read the MaxScale CR (image, replicas, listeners, TLS) and record it with `MaxScaleEnabled=true`. List its `Database`/`User` CRs, **excluding CRs owned by a MariaDB or MaxScale** (`<name>-mariadb-sys`, `<name>-mariadb-sys-global-priv`, `<name>-metrics`, `<name>-maxscale-*`) **[verified on kind, 26.10.2]**. `IsExternal=true`. Root password pulled from `rootPasswordSecretKeyRef` into the vault. Nothing is applied. The Kubernetes cluster is checked to belong to the tenant. The advisor flags an imported MaxScale on 1 replica or outside 23.08. | direct | 10 |
| 6 | Delete / unregister | Delete in one operation. **Order:** the `MaxScale` CR first, waiting for its finalizer `maxscale.k8s.mariadb.com/finalizer`, which drops its SQL users and therefore needs the MariaDB alive; whether it hangs once the MariaDB is gone is **[verify]**. Then the `PhysicalBackup`s (including the replica template), the `PointInTimeRecovery`, the `Database`/`User`/`Grant` CRs, and the `MariaDB`. MariaDB deletion has no finalizer and garbage-collects the StatefulSet, pods, Services, EndpointSlices, PDB, ConfigMaps, SA/Role/RoleBinding and the four TLS Secrets **[verified on kind, 26.10.2]**. **EntKube then deletes what the operator leaves behind [verified on kind, 26.10.2]:** Secrets `<name>-repl-password` and `<name>-metrics-password` (no owner); the six `<name>-maxscale-{admin,client,server,monitor,sync,metrics}` Secrets (no owner); `<name>-root`, `<name>-s3-credentials` and the per-database credential Secrets; any orphan `<name>-<i>-pb-init` Job; the cluster-scoped **ClusterRoleBinding `<name>-<ns>-<first 8 chars of the MariaDB uid>:auth-delegator`**, which Kubernetes never garbage-collects (its ownerReference to a namespaced object is invalid, `OwnerRefInvalidNamespace`). The uid comes from `MariaDbCluster.CrUid`, recorded by the poller, or from the CR before deletion. If EntKube's credentials cannot delete ClusterRoleBindings, the leftover report names it. Binding keys in app namespaces are deleted. PVCs `storage-<name>-*` are deleted only with an explicit "also delete volumes" toggle (default `Retain`); MaxScale's own PVCs go with the MaxScale CR. S3 data is kept (the UI says so). Returns a `KubernetesOperationResult` listing everything left behind instead of swallowing errors. Unregister touches rows and vault only. | improved: CNPG leaves Secrets and binding keys behind | 3 |
| 7 | Live detail + status reconcile (on view only) | `GetClusterDetailAsync` reads the fields below **[verified on kind, 26.10.2]**. Reads apply nothing. Status, LastError, CurrentPrimary, ReadyReplicas and the MaxScale fields are also written by the poller (§6.3). <ul><li>CR conditions (`Ready`, `Updated`, `ReplicationConfigured`, `PrimarySwitched`, `ReplicaRecovered`, `ScaledOut`, `StorageResized`, `BinlogsArchived`; no `observedGeneration`).</li><li>`status.currentPrimary`, `currentPrimaryPodIndex`, `currentPrimaryFailingSince`, `replicas`, `scaleOutInitialIndex`.</li><li>`replication.roles`, and `replication.replicas[*]` (lag, IO/SQL threads and errnos).</li><li>`tls.*.notAfter`.</li><li>Pods by `app.kubernetes.io/instance=<name>,app.kubernetes.io/name=mariadb`, with role from `k8s.mariadb.com/role`.</li><li>MaxScale pods by `app.kubernetes.io/instance=<name>-maxscale,app.kubernetes.io/name=maxscale`.</li><li>MaxScale CR conditions and primary server.</li></ul> | improved | 3 |
| 8 | Drift detection + re-apply (CNPG: private `DetectManifestDriftAsync` called from `GetClusterDetailAsync`; re-apply via `ReapplyClusterSpecAsync` / `ReapplyClusterManifestsAsync`) | A private `DetectManifestDriftAsync` called from `GetClusterDetailAsync`, as CNPG does, using `IClusterClient.DiffManifestAsync` and `DiffOutcome.Matches/Differs/Unknown` (`Services/Clusters/ManifestDiff.cs`); public `ReapplyAsync` in one operation. Works without a StorageLink. **The operator mutates spec heavily**, so drift compares only fields EntKube emits, and the builder never emits these **[verified on kind, 26.10.2]**: <ul><li>`replication.primary.podIndex`;</li><li>`replication.agent`, `initContainer`, `replPasswordSecretKeyRef`, `syncTimeout`, `gtid`, `standaloneProbes`, `autoFailoverDelay`;</li><li>`affinity` expanded from `antiAffinityEnabled`;</li><li>`storage.volumeClaimTemplate`, `waitForVolumeResize`;</li><li>`metrics.exporter`/`passwordSecretKeyRef`/`username`;</li><li>MaxScale `spec.primaryServer`.</li></ul> Re-apply also annotates every EntKube `User`/`Grant` CR with `entkube.io/reconciled-at`, forcing an immediate SQL reconcile; otherwise manual drift persists for the operator's 10–11 h requeue (§9.6). | direct | 8 |
| 9 | Live storage + online expansion | `MariaDbStorage` (pure, reuses `CnpgStorage.ToBytes/IsValidSize/Largest/ReadClaims/ReadAllowVolumeExpansion`, as `MongoStorage` does). `ExpandStorageAsync` patches only `spec.storage.size`. Refusals: not larger; unreadable; **StorageClass without `allowVolumeExpansion: true`**; `resizeInUseVolumes:false`. On a class that cannot expand, the API server refused the PVC patch, and the CR sat at `Ready=False/Failed "Error reconciling Storage …"` with **no Kubernetes Event**. The webhook forbids shrinking, so the size could not be reverted, and the only escape (`resizeInUseVolumes: false`) made the operator recreate the StatefulSet and report **`StorageResized=True` while every PVC was unchanged** **[verified on kind, 26.10.2]**. Completion is therefore checked from **PVC `status.capacity`**, never from `StorageResized`, and a mismatch raises `mariadb-storage-not-expanded:{id}`. `AlignStorageSizeAsync` runs before every manifest build. | direct | 8 |
| 9b | (new) Scale-in volume cleanup | Scale-in keeps `storage-<name>-<i>` (`whenScaled: Retain`), and **the next scale-out then fails** with `ScaleOutError "storage PVCs already exist"` **[verified on kind, 26.10.2]**. `ScaleReplicasAsync` deletes the PVC (`DeclareConsequence`) once `status.replicas` reaches the new count. Emitting `pvcRetentionPolicy.whenScaled: Delete` instead was accepted and reached the StatefulSet, but a scale-in with it was not run **[verify, PR 0b]**. | improved | 8 |
| 10 | Minor upgrade | Image bump within the major, in one operation; ReplicasFirstPrimaryLast. Rolls every pod with a switchover (about 10 s of write lock without MaxScale; through MaxScale with MaxScale) **[verified on kind, 26.10.2]**. | direct (+BeginOperation CNPG lacks) | 8 |
| 11 | Major upgrade (CNPG's `MajorUpgradeAsync` backup→recover→rename is unsafe) | In place: `mariadbAutoUpgradeEnabled` is already set, then the image is bumped and the operator runs `mariadb-upgrade` **[cited; not exercised]**. Precondition: a completed backup < 24 h old, or an explicit override. Downgrade is refused. | improved | 8 |
| 12 | Rolling restart | Patch `spec.podMetadata.annotations["entkube.io/restarted-at"]` **[verified on kind, 26.10.2]**: `Updated=False/PendingUpdate` → `Updating`, replicas one by one, then a switchover, then the old primary; about 1 min 41 s for 3 pods. The operator has no restart annotation, and `kubectl rollout restart` does not apply (the StatefulSet is `OnDelete` and operator-owned). The confirm dialog says writes block for about 10 s without MaxScale (`replica.syncTimeout` 10 s). | direct | 8 |
| 13 | Backup configuration (schedule, retention, MaxBackups) | `UpdateBackupSettingsAsync` on the scheduled `PhysicalBackup` `<name>-backup` (details below). A StorageLink can be attached or changed after create. **MaxBackups is not modelled** (decision 6). <ul><li>`schedule.cron` (**5-field, validated with Cronos**, already referenced in `EntKube.Web.csproj`).</li><li>`schedule.immediate: true` on create. It runs once the MariaDB is Ready (`waitForIt` defaults to true), 40 s after creation in the spike, and a missed tick is caught up as soon as the MariaDB is Ready again **[verified on kind, 26.10.2]**.</li><li>`maxRetention = RetentionDays×24h`, `target: PreferReplica` (logged "Using replica as PhysicalBackup target" **[verified on kind, 26.10.2]**), `compression: zstd`, `podMetadata` with the Istio label, and `timeout: 1h`.</li><li>Explicit `successfulJobsHistoryLimit`/`failedJobsHistoryLimit` of 5 each (defaults 5 **[verified on kind, 26.10.2]**).</li><li>Prefix `mariadb/<clusterId:N>/physical/`. **Every writer to that prefix carries the same `maxRetention`** (§4.1 row 15).</li><li>`PhysicalBackupSchedule` has only `cron`, `suspend`, `immediate` and `onDemand` **[cited]**; there is no "on primary change" trigger. Clearing the schedule sets `schedule.suspend: true`.</li></ul> | adapted | 6 |
| 14 | On-demand backup | **Q2 answered:** set `schedule.onDemand` on `<name>-backup` to a new identifier. It ran within about 1 s, is tracked in `status.lastScheduleOnDemand`, shares the scheduled object's Job naming and history limits, and creates no object sprawl **[verified on kind, 26.10.2]**. **`onDemand` is ignored while `schedule.suspend: true`** (no Job, no status change) **[verified on kind, 26.10.2]**. On a suspended schedule, EntKube instead creates a one-shot `PhysicalBackup` `<name>-od<6 base36>` with the **same prefix and `maxRetention`**, so that it neither prunes nor is pruned differently. "Backup now" is serialized per cluster, because two backups in the same second in one prefix overwrite each other (the key has second resolution). One operation either way. | direct | 6 |
| 15 | Backup listing / history / cleanup | The poller upserts `MariaDbBackups` from PhysicalBackup status and its Jobs. **Facts [verified on kind, 26.10.2]:** <ul><li>There is no CronJob. Jobs are `<pb>-<yyyyMMddHHmmss>` (UTC) and carry **no** operator labels; they are linked only by `ownerReferences` (kind `PhysicalBackup`, controller).</li><li>The S3 key is `<prefix>/physicalbackup-<yyyyMMddHHmmss>.xb.zst`, with the same timestamp as the Job and **no CR name**.</li><li>Pruning runs inside each successful Job and deletes **every** `physicalbackup-*` object in the prefix older than *that Job's* `maxRetention`. A one-shot with 720 h and a seed object were both deleted by a 20 m schedule. No pruning happens while backups are paused.</li></ul> The row's `Name` is `physicalbackup-<ts>`, and `ObjectKey` is the full key that the restore uses. Status comes from `BackupHistoryPolicy` mapping the `Complete` condition **by reason** (§6.3), because **`Complete` is `True` on failure too** (`JobFailed`). Seed backups live in their own prefix and are **excluded** from history and restore choices (Q3 answered). Cleanup is the operator's `maxRetention` plus a bucket lifecycle rule; EntKube never deletes S3 objects. | improved | 6 |
| 16 | PITR into a new cluster | `RestoreAsync`: a new `MariaDB` built by the single builder, plus `bootstrapFrom`: either `pointInTimeRecoveryRef` with `targetRecoveryTime`, or `backupRef {kind: PhysicalBackup}` with an **explicit** `targetRecoveryTime` equal to the chosen backup's timestamp (without one, the newest object in the prefix wins, from any writer). Cross-cluster restores use `bootstrapFrom.s3` with `backupContentType: Physical`, which is required (otherwise Logical is assumed). All three paths restored correctly; PITR into a new 3-replica semi-sync cluster brought back a dropped table and excluded the `DROP` (about 62 s, with binlog replay about 7 s) **[verified on kind, 26.10.2]**. With `strictMode: true`, a target after the last recoverable time fails fast with `InitError` and creates no pods **[verified on kind, 26.10.2]**. **Recoverable window:** `PointInTimeRecovery.status` has **only** `lastRecoverableTime`, which goes stale whenever the newest base backup is past the last archived binlog **[verified on kind, 26.10.2]**. EntKube therefore computes the window as the oldest retained `physicalbackup-*` object (S3 listing) through the latest of `MariaDB.status.pointInTimeRecovery.lastArchivedTime` and the S3 `index.yaml`. It clamps targets to that window and still sends `strictMode: true`. Cross-cluster PITR needs a `PointInTimeRecovery` and a suspended `PhysicalBackup` on the target cluster that point at the source prefixes. That mechanism is **[verify, PR 0b]**; the same-cluster PITR and the cross-cluster-capable `bootstrapFrom.s3` were verified. Root password: the backup-time value goes into the new root Secret **before** the CR (§9.1). The restored cluster gets its own MaxScale (whether MaxScale behaves on a `bootstrapFrom` cluster is **[verify, PR 0b]**). DB rows and vault credentials are recreated. Init and PITR Jobs (`<name>-<i>-pb-init`, `<name>-pitr`) are deleted on success, so the poller captures their status and log tail while they run. | direct (+fixes CNPG's missing DB rows) | 7 |
| 17 | Restore in place (CNPG: `RestoreInPlaceAsync`) | **Not possible** physically (new instance only). Replacement: **restore & cut over**. EntKube restores to `<base>-r<6 base36>`, then, once it is Ready, runs one operation that moves the `MariaDbDatabase` rows, rewrites HOST/READ_HOST/READ_PORT **and CA_CERT** in the vault (a MaxScale cluster has its own CA), and re-syncs every binding. Apps that pin the CA outside the synced key must be told. The old cluster is marked `Superseded` and kept. | adapted | 7 |
| 18 | Create / drop logical database (CNPG: `CreateDatabaseAsync` / `DeleteDatabaseAsync`) | `Database` + `User` (`passwordSecretKeyRef`) + `Grant` CRs in one operation, all `cleanupPolicy: Skip` (the operator default is **`Delete`**, which ran `DROP DATABASE` on a database holding data **[verified on kind, 26.10.2]**). <ul><li>**Always explicit** on `Database`: `characterSet: utf8mb4` plus `collate`. The CRD defaults to `utf8`/`utf8_general_ci`, stored as utf8mb3, and both fields are immutable **[verified on kind, 26.10.2]**.</li><li>**Always explicit** on `User`: `maxUserConnections`. The CRD default is **10**, and 0 means unlimited **[verified on kind, 26.10.2]**.</li><li>**Always explicit** on `Grant`: `database`, `table: "*"`, `host: "%"`, `grantOption: false`. The CRD defaults `database`/`table` to `*`, so a Grant without a database is **global `*.*`** **[verified on kind, 26.10.2]**.</li><li>Drop: typed-name confirm → `DeclareConsequence` → patch to `Delete` only when "drop data" is ticked → delete CRs, Secret keys, binding keys, vault and row.</li></ul> | improved: no exec, gated, audited | 4 |
| 19 | Discover / import existing databases | List `Database` CRs, excluding operator-owned ones (row 5); on imported clusters run `SHOW DATABASES` through the read-only exec verb (§9.3). Import writes row + vault from operator-supplied credentials with `IsAdopted=true`, so rotate/drop are disabled for users EntKube did not create. Adopting an existing database or user with a `Skip` CR works: data is kept, and the user is ALTERed to the Secret's password **[verified on kind, 26.10.2]**. | adapted | 10 |
| 20 | Credentials view / sync / rotation / superuser custody (CNPG: `FetchSuperuserCredentialsAsync`) | Vault keys HOST, READ_HOST, PORT, READ_PORT, DATABASE, USERNAME, PASSWORD, DATABASE_URL, JDBC_URL, and CA_CERT (always on MaxScale clusters; when TLS is required otherwise). Sync via `SharedSecretKeys.WriteAsync(…, Owners.Database, …)`. Rotation per §9.2 (Q5 settled: hash check). Root in vault `ROOT_PASSWORD` (not synced to apps). | improved: rotation always re-syncs and rolls back | 4 |
| 21 | In-DB helpers (ownership, Liquibase lock, Keycloak realm URL, golang-migrate) | n/a until a catalog component uses MariaDB | deferred | – |
| 22 | App bindings + customer portal | `AddMariaDbDatabaseBindingAsync` (both ends tenant-checked); portal `DbKind.MariaDb` | direct | 5 |
| 23 | App-wide secret sync (`POST /apps/{appId}/secrets/sync`, CLI `secrets sync`) | `DatabaseBindingTarget.MariaDb` arm in `AppSecretSyncService` | direct | 5 |
| 24 | Prometheus panel + scrape diagnosis (CNPG: `GetCnpgClusterMetricsAsync`, `DiagnoseCnpgScrapeAsync`, PodMonitor-based) | `PrometheusService.GetMariaDbClusterMetricsAsync` (MariaDB and MaxScale series, §9.5). Scrape diagnosis **reuses the existing generic `PrometheusService.DiagnoseServiceMonitorScrapeAsync(tenantId, clusterId, ns, monitorNameHint, absentRemedy)`** for both `<name>-metrics` and `<name>-maxscale-metrics`; it already checks a ServiceMonitor against `serviceMonitorSelector` and `serviceMonitorNamespaceSelector` for RabbitMQ and Keycloak. It must compare the **live** ServiceMonitor's `release` label, not the CR spec (§9.5). No MariaDB-specific diagnose method. New `MariaDbMonitoringPanel` with `TenantId`. Selectors use `namespace`, `service`, and `target` (the MariaDB pod), **never `pod`**, which names the exporter pod **[verified on kind, 26.10.2 for the raw series; the Prometheus-added labels are inferred, §9.5]**. | improved | 9 |
| 25 | Advisor backup findings | `EvaluateScheduledBackup(…, "MariaDB", "mariadb", …, latestFailError)`. The error passed is the best available: a failed Job's pod is deleted, so the S3 error text is lost unless the poller caught the container log while the pod was in BackOff; otherwise it is the Job condition `BackoffLimitExceeded` **[verified on kind, 26.10.2]**. Plus the cluster, MaxScale, PITR and drill findings listed in §8 row 18. | improved | 3 / 6 / 9 / 12 |
| 26 | App-state backup bundle | `MariaDbClusters`, `MariaDbDatabases` bundled; `MariaDbBackup` deliberately excluded; VaultSecretRecord tail fields; `CurrentVersion` 22 → 23 (re-measure on the merge result) | direct | 2 |
| 27 | Blueprint capture + bootstrap | Service kind `"mariadb"` in all five places + key-parity test + tenant predicate in `ServiceInstanceExistsAsync` | improved | 11 |
| 28 | Component DB wiring (`FormFieldType.CnpgDatabase`) | `FormFieldType.MariaDbDatabase` | deferred | – |
| 29 | Connectivity edges | `{ MariaDbDatabaseId: not null }` gives `("MariaDB", 3306)`. On MaxScale clusters the edge targets the **MaxScale pods** on 3306 and 3308, never 8989. Explicit arms for every engine (§8, row 11). | improved | 5 |
| 30 | Deployment-import detection | `DetectMariaDb` (`mysql://`, `mariadb://`, `jdbc:mysql:`/`jdbc:mariadb:`, `MYSQL_*`, `MARIADB_*`, port 3306). `DetectPostgres` does not reference port 3306; the problem is its generic `DB_HOST`/`DB_NAME`/`DB_USER`/`DB_PORT`/`PORT` key-group heuristic (`DeploymentImportService.cs` ~1370–1400), which classifies any such app, including a MySQL one, as Postgres regardless of port. `DetectMariaDb` must run first, or the generic heuristic must stop claiming apps whose port or URL scheme is MySQL. | deferred | 15 |
| 31 | Registered (vanilla) Postgres instances | Registered external MariaDB/MySQL via the operator's `ExternalMariaDB` CR (unresearched) | deferred | – |
| 32 | Registered Postgres databases and credentials | Same `ExternalMariaDB` path | deferred | – |
| 33 | Registered pg_dump to S3 / restore to CNPG / migrate to CNPG | Logical `Backup`/`Restore` CRs; import Job with an owned Secret | deferred | 15 |
| 34 | Exec plumbing (ChangeIntent, stdin, 0600 kubeconfig) | One read-only verb pair, §9.3 | direct | 4 |
| 35 | Data model, migrations ×3, module wiring | §5 | direct | 2 |

### 4.2 Every inventoried MongoDB feature
| Mongo feature | MariaDB mechanism | Status | PR |
|---|---|---|---|
| Operator detection (alias-asymmetric predicate) | Shared predicate, tested with a matching and a near-miss component per alias in `OperatorStatusViaContractTests` | direct | 1 |
| Create managed cluster | §4.1 row 3 | direct | 3 |
| Replica-set topology 1/3/5 | Replicas 1/2/3/5; Galera odd only (CRD CEL **[verified on kind, 26.10.2]**) | direct | 3 / 13 |
| Admin credential custody (generated, vault + Secret applied before the CR, re-import; Mongo: `FetchAdminPasswordAsync`) | Root password: same flow (`<name>-root` before the `MariaDB` CR); "Fetch root password" (`FetchRootPasswordAsync`) re-imports | direct | 3 |
| SA/Role for the agent in the target namespace | The operator creates ServiceAccount, Role and RoleBinding `<name>` per replication cluster, plus a ClusterRoleBinding `<name>-<ns>-<uid8>:auth-delegator` to `system:auth-delegator` (TokenReview for the agent's `kubernetesAuth`; only the `mariadb-operator` ServiceAccount is trusted). The ClusterRoleBinding **leaks on every delete**, and every re-create of the same name adds another, because the name carries the uid **[verified on kind, 26.10.2]**. A standalone gets none **[verified on kind, 26.10.2]**. EntKube deletes it on cluster delete (§4.1 row 6). | adapted | 3 |
| Delete / unregister | §4.1 row 6 | improved | 3 |
| Register external cluster + "Credentials & Backup Settings" panel | Import (row 5) + backup settings on imported clusters (CNPG cannot). **No** plaintext `ExternalUri` column. | adapted | 10 |
| Live detail + status reconcile | §4.1 row 7 | improved | 3 |
| Live storage + resize CPU/memory/storage (Mongo: `ResizeClusterAsync`) | Row 9 + `ResizeClusterAsync` (resources re-apply, request floors; MaxScale resources too). Changing pod-template fields such as tolerations rolls every pod like a restart **[verified on kind, 26.10.2]**. | direct | 8 |
| Members fixed after create (gap) | `ScaleReplicasAsync` patches `replicas` (details below). Galera odd only. <ul><li>Scale-out takes a **new** PhysicalBackup from the template against a Ready replica and seeds the new PVC: `ScaledOut=False/ScalingOut` and `status.scaleOutInitialIndex`, then Ready with all data, about 70 s for 3→4 **[verified on kind, 26.10.2]**.</li><li>**Preconditions, all refused before any write:** <ul><li>the template is configured;</li><li>a replica is Ready;</li><li>**no PVC `storage-<name>-<i>` exists for i ≥ current replicas**, and no `<name>-<i>-pb-init` Job exists. EntKube offers to delete leftovers (`DeclareConsequence`);</li><li>**replicas+1 ≤ schedulable nodes under required anti-affinity**. Otherwise the pb-init pod stays Pending (`FailedScheduling`) and the CR sits in `ScalingOut` indefinitely **[verified on kind, 26.10.2]**.</li></ul></li><li>**Cancel** restores `spec.replicas`, then deletes the orphan `pb-init` Job and Pending PVC. A cancel reported `ScaledOut=True "Scaled out"` and left both behind. The orphan Job later **ran on its own** and bound the stale PVC to a node that already hosted a member **[verified on kind, 26.10.2]**.</li><li>Whether the operator adds the new server to MaxScale's monitor and services is **[verify, PR 0b]**.</li></ul> | improved | 8 |
| Version upgrade; external = patch `spec.version` only | Rows 10/11; for `IsExternal`, patch `spec.image` only, never a full re-apply | direct | 8 |
| Rolling restart through the CR | Row 12 | direct | 8 |
| Database + owner user lifecycle (mongosh + sentinel) | CRs. Success is the single condition `Ready=True/Created` (message `Created`); failure is `Ready=False/Failed`, whose message carries the SQL error code (for example `Error 1133`, `Error 1064`). There is no `observedGeneration`. Failures retry with exponential backoff and recovered about 2 s after a missing Secret appeared. A CR that never succeeded has no finalizer **[verified on kind, 26.10.2]**. Read by a bounded wait or by the poller. | adapted | 4 |
| Live listDatabases on every view | Database CRs are the inventory; exec `SHOW DATABASES` only on an explicit "Discover"; no auto-insert (avoids Mongo's `Owner = dbName` defect) | adapted | 10 |
| Credentials view / K8s sync / bindings | Row 20. The cluster-namespace Secret also goes through `SharedSecretKeys`, not hand-built YAML. | direct | 4 / 5 |
| Password rotation (+ portal re-sync) | §9.2; always re-syncs | improved | 4 |
| On-demand backup (mongodump Job) | Row 14 (physical); logical on-demand deferred | direct | 6 |
| Scheduled backups + reality check (Mongo: `GetScheduledBackupStatusAsync`) | `GetScheduledBackupStatusAsync` reads the PhysicalBackup's `spec.schedule.suspend` and its status: `lastScheduleTime`, `nextScheduleTime` (set only after the first cron tick, and left stale while paused), `lastScheduleCheckTime`, `lastScheduleOnDemand` and `conditions[type=Complete]`. It also reads the Jobs it owns (by `ownerReferences`, since they have no labels) **[verified on kind, 26.10.2]**. There is **no CronJob** **[verified on kind, 26.10.2]**. Shown as "configured vs running", including "**paused: MariaDB not ready**" (reason `Failed`, which is not a backup failure). Schedule-without-link refused in **every** path, including imported-cluster settings (Mongo regressed there). | direct | 6 |
| History reconcile, stale marking, S3 prune Job | Poller + `BackupHistoryPolicy`. A Job running past `timeout` is deleted together with its pod, leaving **no event and no condition**; status falls back to `JobNotComplete`. The timeout is enforced only while the MariaDB is Ready **[verified on kind, 26.10.2]**. A Running row whose Job vanished maps to **Unknown**/**Expired**, never Completed. No EntKube S3 prune Job. Restore rows never written into the backups table. | improved: fixes three Mongo defects | 6 |
| Restore in place (`mongorestore --drop`) | Row 17 (cut-over). Single-database logical restore deferred. | adapted | 7 / 15 |
| Restore to a new cluster | `bootstrapFrom`, sized from the live source | direct | 7 |
| Restore to external URI | Logical backup + Job with the URI in a Secret owned by the Job | deferred | 15 |
| Restore from Atlas → "import from external MySQL/MariaDB" | `mariadb-dump --single-transaction` Job with credentials in an owned Secret | deferred | 15 |
| Per-database migration between clusters | Logical `Backup{databases:[db]}` → `Restore{database}`, plus CRs, vault and rows on the target | deferred | 15 |
| External → managed migration | Same logical path | deferred | 15 |
| mongosh exec plumbing (`{cluster}-0`, password on argv) | Read-only verb; pod = `status.currentPrimary` or any Ready pod; root password on **stdin**, never argv or pod env (§9.3) | improved | 4 |
| Change gate and custody | §8 rows 4–7 | improved | all |
| Customer portal panel | Row 22 | direct | 5 |
| Governance allowlists, vault scope, binding picker, connectivity | `AppAllowedDatabase.MariaDbDatabaseId`; governance filtered by tenant **and environment** (Mongo filters by tenant only); VaultTab scope; picker includes imported clusters | direct | 5 |
| Advisor backup findings (passes null error) | Row 25, with the best available error | improved | 6 |
| App-state bundle coverage | Row 26 | direct | 2 |
| Data model / migrations | §5 | direct | 2 |
| Public API / CLI (absent) | §7 | deferred | 14 |
| Metrics (absent) | Row 24 | improved | 9 |

### 4.3 Beyond parity
- Switchover, transparent to open sessions through MaxScale.
- A single application endpoint with transaction replay (MaxScale).
- `READ_HOST`/`READ_PORT`.
- Replica scaling, with leftover-PVC cleanup.
- Replica recovery (the operator re-seeds a diverged replica from the template).
- Explicit degraded-HA flag.
- PITR window display computed from S3 and status, not from the stale PITR field.
- Cross-cluster restore.
- Restore & cut-over.
- Root-password version per backup.
- Restore drills.
- Read-only status/backup poller.
- TLS end to end, including through MaxScale.
- Galera (PR 13).

---

## 5. Data model

All entities are owned by `Module.DataServices`, with explicit `ToTable` in `Data/Modules/DataServicesModel.cs`. DbSets go on **both** `DataServicesDbContext` and `ApplicationDbContext`; `OperationsAdvisor` and `BackupService` read through the latter.

### 5.1 `MariaDbCluster` → `MariaDbClusters`
| Column | Type | Notes |
|---|---|---|
| Id, TenantId, KubernetesClusterId | Guid | Tenant **Cascade**, KubernetesCluster **Cascade**. Copy the CNPG `onDelete` choices exactly, including the SQL Server variant created in `20260518072630_AddCnpgRetentionDays.cs`, then prove the cascade on the generated SQL. |
| Name | string(36) | DNS-1123. User-entered names are capped at **28**, so derived cluster names (`<base>-r<6 base36>` for restores, `<base>-d<6 base36>` for drills) stay ≤ 36. The cap comes from operator Job names: `<name>-pb-scaleout-<yyyyMMddHHmmss>` and `<name>-pb-recovery-<…>` add 27 characters **[verified naming on kind, 26.10.2]**, and a Job name becomes the `job-name` label, which Kubernetes caps at 63. Whether the operator truncates over-long names was not tested. Other derived names fit: `<name>-backup-<ts>` (+22), `<name>-maxscale-metrics` (+17), `<name>-od<6>-<ts>` (+24), `<name>-replica-template` (+17). (The previous cap of 40 was wrong: 40 + 27 > 63.) `<base>` is the name with any trailing `-[rd][a-z0-9]{6}` stripped, so restoring a restored cluster does not grow the name. |
| Namespace | string(63) | |
| MariaDbVersion | string(20) | Patch-level, e.g. `12.3.3`. |
| Topology | enum Standalone/Replication/Galera | Immutable. Stored as **int**, like CNPG's enums (EF default; `DataServicesModel.cs` has no `HasConversion` for `CnpgClusterStatus`, and the CNPG migration stores `Status` as int). With int storage the `(int)`-cast hazard (which concerns string-stored enums) does not arise; if string storage is ever chosen instead, never `(int)` it in an EF expression. |
| Replicas | int | |
| MaxScaleEnabled | bool | Fixed at create (§3.3.10). Default `true` for Replication/Galera, always `false` for Standalone. |
| MaxScaleReplicas | int = 2 | **Hand-correct `defaultValue: 2` in all three migrations.** |
| MaxScaleImage | string(200)? | The image applied (for example `mariadb/maxscale:23.08.13-2`). Re-apply moves it to the current pin. |
| StorageSize | string(20) | |
| StorageClassName | string(253)? | |
| CpuRequest, CpuLimit, MemoryRequest, MemoryLimit | string(20)? | |
| MaxScaleCpuRequest, MaxScaleMemoryRequest, MaxScaleMemoryLimit | string(20)? | Defaults 100m / 128Mi / 512Mi (§3.3.2). |
| StorageLinkId | Guid? | **SetNull** |
| BackupSchedule | string(100)? | 5-field cron |
| RetentionDays | int = 30 | **Hand-correct `defaultValue: 30` in all three migrations.** |
| PitrEnabled | bool | Refused for Standalone (§3.1). |
| TlsRequired | bool | |
| IsExternal | bool | |
| HaDegradedReason | string(500)? | |
| Status | enum Creating/Running/Degraded/Updating/Restoring/Superseded/Failed/Deleting | Stored as int (see Topology). `Upgrading` became `Updating`: it covers upgrades, restarts, scale and resize, as the operator's reasons do (§6.3). |
| LastError | string(2000)? | Written, unlike CNPG's dead field. Carries the condition message, which includes SQL error codes. |
| CurrentPrimary | string(253)? | `status.currentPrimary`, observed by the poller. |
| ReadyReplicas | int? | Observed by the poller. |
| MaxScaleReadyReplicas | int? | Observed by the poller. |
| MaxScalePrimary | string(253)? | MaxScale's view of the primary, observed by the poller. A mismatch with `CurrentPrimary` drives `mariadb-maxscale-primary-mismatch`. |
| MaxScaleLastError | string(2000)? | MaxScale `Ready=False` message (for example `"Server mdb-0 not ready"`). |
| PitrLastArchivedAt | DateTime? | `MariaDB.status.pointInTimeRecovery.lastArchivedTime`. UTC, Kind-normalized on read. |
| PitrLastArchivedGtid | string(200)? | Compared with the replicas' `gtidCurrentPos` for the lag finding (§9.5). |
| CrUid | string(36)? | The `MariaDB` CR's uid, observed by the poller. Used to name the leaked auth-delegator ClusterRoleBinding on delete. |
| LastObservedAt | DateTime? | UTC, Kind-normalized on read. |
| RestoredFromClusterId | Guid? | No FK; provenance. |
| RestoreTargetTime | DateTime? | No FK; provenance. |
| CreatedAt | DateTime | |

Unique index on `(KubernetesClusterId, Name, Namespace)`.

### 5.2 `MariaDbDatabase` → `MariaDbDatabases`
Columns: Id, MariaDbClusterId (Cascade), Name(64), OwnerUser(80), CharacterSet(32) (`utf8mb4`), Collation(64) (default `utf8mb4_uca1400_ai_ci`, the server default on 11.8/12.3; the CR path was verified with `utf8mb4_unicode_ci`), MaxUserConnections (int = 100; **hand-correct the default in all three migrations**; §9.6), Status (Creating/Ready/Failed/Pending, int), LastError(2000)?, IsAdopted, CreatedAt.

Unique index on `(MariaDbClusterId, Name)`. Nav: `DatabaseBindings`, as on `CnpgDatabase` and `MongoDatabase`. There is no allow-list inverse navigation: `AppAllowedDatabase` FKs are configured `.WithMany()` with no inverse (`DeliveryModel.cs` ~680–692), and the MariaDB FK follows suit.

### 5.3 `MariaDbBackup` → `MariaDbBackups`
Columns:
- Id, MariaDbClusterId (Cascade)
- Name(253): `physicalbackup-<yyyyMMddHHmmss>`, the timestamp the restore passes as `targetRecoveryTime`
- ObjectKey(512): the full S3 key (`<prefix>/physicalbackup-<ts>.xb.zst`)
- JobName(63)?: the `<pb>-<ts>` Job, while it exists
- Kind (Physical/Logical), Trigger (Scheduled/OnDemand/PreUpgrade/Drill)
- Status (Running/Completed/Failed/Unknown/Expired)
- StartedAt, CompletedAt, SizeBytes?, Error(2000)?, ObservedAt
- **RootPasswordSecretVersionId** (Guid?): the vault version that restores this backup

Unique index on `(MariaDbClusterId, Name)`. Restore events are **never** written here. Template-driven replica-seed backups are **not** recorded (Q3): they live in their own prefix, their transient objects disappear about 20 s after success, and they are never offered as restore points. `ReplicaSeed` was removed from `Trigger`.

### 5.4 `MariaDbRestoreDrill` → `MariaDbRestoreDrills` (PR 12)
Columns: Id, MariaDbClusterId (Cascade), Status, StartedAt, CompletedAt, TargetTime, RecoveredToTime, RecoverySeconds, ScratchName(36), Verification(4000), Error(2000).

It is bundled, because drill results are audit evidence.

### 5.5 New FK columns on existing entities (all Cascade, like their CNPG/Mongo siblings)
- `VaultSecret.MariaDbClusterId`, `VaultSecret.MariaDbDatabaseId`, configured in `SecretsModel` (PR 2).
- `DatabaseBinding.MariaDbDatabaseId`, configured in `DeliveryModel` (PR 5). Doc: "exactly one of four".
- `AppAllowedDatabase.MariaDbDatabaseId`, configured in `DeliveryModel` (PR 5) with `.WithMany()` and no inverse, like its siblings.

### 5.6 Registration and migrations
- `ModuleMap.Entities` maps the new entities to DataServices.
- Re-bank `ModuleBoundaryBaseline` with reasons (DataServices→Identity/Fleet, Secrets→DataServices, Delivery→DataServices). Always re-measure it on the merge result, never merge the constant by hand.
- One migration per provider per PR that changes schema, under `Data/Migrations/{Postgres,SqlServer,Sqlite}`, each **with its Designer file** and an updated snapshot. Hand-correct every scaffolded `defaultValue` whose C# initializer is non-zero (`RetentionDays`, `MaxScaleReplicas`, `MaxUserConnections`).

---

## 6. Services, module placement and UI

### 6.1 Classes
| Class | Role |
|---|---|
| `MariaDbService` | Lifecycle, databases, credentials, backups, restore, day-2, bindings. Public names mirror **CNPG or Mongo**, whichever has the operation: from CNPG `CreateClusterAsync`, `ImportClusterAsync`, `DeleteClusterAsync`, `UnregisterClusterAsync`, `GetClustersAsync`, `GetClusterDetailAsync`, `ExpandStorageAsync`, `UpgradeClusterAsync`, `RestartClusterAsync`, `UpdateBackupSettingsAsync`, `BackupAsync`, `RestoreAsync`, `CreateDatabaseAsync`, `ImportDatabaseAsync`, `RotateDatabasePasswordAsync`; from Mongo `ResizeClusterAsync`, `GetScheduledBackupStatusAsync`, `UnregisterDatabaseAsync`, `GetDatabaseCredentialsAsync`, `SyncDatabaseCredentialsToK8sAsync`. New names, with the nearest existing equivalent: `ReapplyAsync` (CNPG `ReapplyClusterSpecAsync`/`ReapplyClusterManifestsAsync`), `DropDatabaseAsync` (CNPG `DeleteDatabaseAsync`; named "Drop" because it may drop data, distinct from unregister), `FetchRootPasswordAsync` (CNPG `FetchSuperuserCredentialsAsync`, Mongo `FetchAdminPasswordAsync`), `ScaleReplicasAsync`, `CancelScaleOutAsync`, `SwitchoverAsync` (MaxScale `primaryServer` or MariaDB `podIndex`, §3.3.4), `CutOverToRestoredClusterAsync`, `DiscoverDatabasesAsync`, `AddMariaDbDatabaseBindingAsync` (CNPG `AddCnpgDatabaseBindingAsync`), `ReconcileAllAsync`. `DetectManifestDriftAsync` is private and called from `GetClusterDetailAsync`, as in CNPG. If the file passes ~1500 lines, split it into `MariaDbBackupService` and `MariaDbDatabaseService` along the same seams. |
| `MariaDbManifests` (static, pure, public) | `Cluster(spec, bootstrapFrom?)`, `MaxScale(spec)`, `ReplicaTemplateBackup`, `PhysicalBackup`, `OneShotBackup`, `PointInTimeRecovery`, `Database`/`User`/`Grant`. Serialized from typed records with YamlDotNet (already referenced), never by string concatenation. A builder fact asserts `cleanupPolicy: Skip`, explicit charset/collation, explicit `maxUserConnections`, explicit Grant `database`/`table`/`host`, no `podIndex`, no `primaryServer`, no `volumeClaimTemplate`, and a 23.08 MaxScale image. |
| `MariaDbStorage` (static, pure) | Claim parsing and the resize patch over `CnpgStorage`; compares spec size with PVC `status.capacity`. |
| `MariaDbOperator` (static) | `IsInstalled`, CRD resource names, `MariaDbVersions`, `MaxScaleImage` (pinned, §3.3.8). |
| `MariaDbStatusMap` (static, pure) | Conditions and reasons → `MariaDbClusterStatus`, backup status and database status (§6.3). |
| `MariaDbAuthString` (static, pure) | `NativeHash(pw)` = `*` + UPPER(HEX(SHA1(SHA1(pw)))), checked against `mysql.user.authentication_string` **[verified on kind, 26.10.2]**; `CachingSha2Hash(pw, salt)` (SHA-256 crypt, 5000 rounds, `$A$005$` + 20 alphanumeric salt, matched server `PASSWORD()` output byte for byte in the spike's Python reference) for the PR 15 opt-in. |
| `DbIdentifier` (static, pure) | `IsValidMariaDbCluster` (DNS-1123, ≤28 user-entered); `IsValidMariaDbDatabase` (`^[a-z][a-z0-9_]{0,47}$`, not `mysql`/`information_schema`/`performance_schema`/`sys`). 48 + `_owner` = 54 stays under the `User.spec.name` CRD limit of 80 and MariaDB's 128 **[verified on kind, 26.10.2]**. The operator validates only length (80 for Database names, although MariaDB allows 64) and does **no escaping**, so this regex is the safety control (§9.6). Also `CrName(cluster, db)` (`_`→`-`, injective); `IsValidCron5` (Cronos); `IsAllowedPrivilege`. Called by DatabaseTab, the portal **and** the service, which throws. |
| `BackupHistoryPolicy` (static, pure) | Observed facts → Running/Completed/Failed/Unknown/Expired, mapped by condition **reason**. Never infers success. Available to Mongo later. |
| `MariaDbStatusPollingService` | §6.3 |

**Constructor.** `CnpgService` and `MongoService` take six dependencies; MariaDB takes the same six:
- `IDbContextFactory<DataServicesDbContext>`
- `VaultService`
- `Clusters.IClusterClientFactory`
- `Contracts.Catalog.ICatalogApi`
- `Contracts.Delivery.IDeliveryApi`
- `ClusterChanges.IClusterChangeGate`

StorageLinks are read from its own `DataServicesDbContext`, as CNPG does. It does **not** take `IDataServicesApi`: that is DataServices' outward contract (only `GetStorageLinkAsync`, consumed by other modules) and a DataServices-owned service has no reason to call its own module through it.

S3 reads are now needed in three places: the restore-time "object exists" check (§10 PR 7), the PITR window (oldest retained object plus `index.yaml`, §4.1 row 16), and the leftover report. `StorageBrowserService` exists (its constructor takes `StorageLinkClientFactory`), but neither CNPG nor Mongo injects it. Whether to inject `StorageBrowserService` or `StorageLinkClientFactory` directly, and whether its lifetime suits a scoped service, is open question Q4; it is the one addition beyond CNPG's shape.

**Never inject** `ApplicationDbContext` or `IKubernetesClientFactory`, and never touch `.Kubeconfig`.

Wiring: `DataServicesServices.AddDataServicesModule` registers `AddScoped<MariaDbService>()` and `AddSingletonWork<MariaDbStatusPollingService>()`, both mapped in `ModuleMap.Services`. `DatabaseService` gains `MariaDbAvailable` and `GetMariaDbEnabledClustersAsync`.

Binding list and remove reuse the engine-agnostic `CnpgService.GetDatabaseBindingsAsync` / `RemoveDatabaseBindingAsync`, extended with `.Include(b => b.MariaDbDatabase).ThenInclude(d => d!.MariaDbCluster)`.

### 6.2 Tenancy rules (every method)
- `Guid tenantId` is the first parameter.
- Rows are loaded as `c.Id == id && c.TenantId == tenantId`. Children are loaded through the parent.
- A private `ClusterFor(tenantId, clusterId, ct) => await clusters.ForAsync(...) ?? throw` is called with the **caller's** tenant.
- Create, import and restore verify the target Kubernetes cluster through `GetComponentsForClusterAsync(tenantId, clusterId)` and load the StorageLink tenant-scoped.
- Bindings and grants check both ends: the database through its cluster's tenant, and the deployment through `IDeliveryApi.GetDeploymentAsync(tenantId, …)`.
- All CRs live in the MariaDB's namespace. Cross-namespace `mariaDbRef.namespace` is honoured by the operator (a wrong namespace gives `"MariaDB not found"`), but a positive cross-namespace case was not run **[verify; not needed]**. Credential Secrets are always read from the User CR's own namespace **[verified on kind, 26.10.2]**, so EntKube never splits them.

### 6.3 Background work
`MariaDbStatusPollingService` follows the shape of `MessagingStatusPollingService`:
- Each cycle is `CreateScope()` → `DeclareUnattended("mariadb-status-polling")` → `MariaDbService.ReconcileAllAsync`.
- `ReconcileAllAsync` loops `db.Tenants` outermost and acts as each tenant in turn, as `RabbitMQService.ReconcileAllAsync` does.
- Interval: 120 s.
- **What it reads [verified on kind, 26.10.2 for every field named]:**
  - MariaDB: `metadata.uid`, `status.conditions`, `currentPrimary`, `currentPrimaryPodIndex`, `currentPrimaryFailingSince`, `replicas`, `scaleOutInitialIndex`, `replication.roles`, `replication.replicas[*].{gtidCurrentPos,lastIOErrno,lastIOError,lastSQLErrno,secondsBehindMaster,slaveIORunning,slaveSQLRunning}`, `pointInTimeRecovery.{lastArchivedTime,lastArchivedGtid}`, `tls.*.notAfter`.
  - MaxScale: `Ready` and `PrimarySwitched` conditions, and the primary server (exact status path **[verify]**).
  - PhysicalBackup status and the Jobs it owns.
  - PointInTimeRecovery `lastRecoverableTime` (display only).
  - PVC capacities.
  - While a restore or scale-out runs, the transient Jobs' status and a log tail.
- **Status mapping (`MariaDbStatusMap`).** No condition carries `observedGeneration`, so mapping relies on reasons **[verified on kind, 26.10.2]**:

| Observed | `MariaDbClusterStatus` |
|---|---|
| `Ready=True/StatefulSetReady` (and MaxScale Ready when enabled) | Running |
| `Ready=False/StatefulSetNotReady`, never Ready yet | Creating |
| `Ready=False/StatefulSetNotReady` after Running, or `ReplicaRecovering`, or `GaleraNotReady` | Degraded |
| `Ready=False` with `PendingUpdate`, `Updating`, `ScalingOut`, `ResizingStorage`, `SwitchPrimary` | Updating |
| `Ready=False/ScaleOutError` | Degraded + LastError (cancel offered) |
| `Ready=False/ArchiveBinlogsError` | Degraded + LastError **"binlog archival failing; all backups of this cluster are paused"** (not "down") |
| `Ready=False/Failed` (reconcile error, e.g. a missing Secret) | Degraded + LastError; Failed only if the cluster was never Ready |
| `Initializing`, `ReplayBinlogs` (restore) | Restoring |
| `InitError` (e.g. strict PITR target) | Failed |
| MaxScale `Ready=False` for more than 2 min | Degraded + MaxScaleLastError ("no automatic failover while MaxScale is not Ready") |

- **Backup mapping (`BackupHistoryPolicy`, by `Complete` reason):** `JobComplete` → Completed; `JobFailed` → Failed (`status` is **True**); `JobNotComplete` → Running; `JobSuspended` → no row, schedule suspended; `Failed` with message "MariaDB not ready" → **paused**, which is not a backup failure; the schedule panel and advisor show it. A Running row whose Job vanished maps to Unknown, or to Expired past `timeout`.
- **Database/User/Grant mapping:** `Ready=True/Created` → Ready; `Ready=False/Failed` → Failed with the message; absent → Pending.
- **Job history is bounded by count, not time [verified on kind, 26.10.2].** `successfulJobsHistoryLimit` and `failedJobsHistoryLimit` default to 5 and are enforced per PhysicalBackup. Jobs past `timeout` are deleted with no trace. History therefore comes from the poller's records, never from Jobs alone.
- **Read-only.** It writes Status, LastError, CurrentPrimary, ReadyReplicas, the MaxScale fields, the PITR fields, CrUid and LastObservedAt, and upserts `MariaDbBackups`. It never applies, annotates, prunes or deletes. A test proves this with a gate that throws on any acknowledgment.
- Upserts key on `(MariaDbClusterId, Name)`, so N instances racing is harmless.

A scheduled `MariaDbRestoreDrillService` is a later opt-in (open decision 9). It mutates, so it needs its own `DeclareUnattended` entry and a CAS claim per row.

### 6.4 UI
- **`Components/Pages/Tenants/MariaDbSection.razor`** (new) is rendered from `DatabaseTab.razor` and gated on `MariaDbAvailable`. It keeps the 6.6k-line tab from growing and leaves the CNPG/Mongo markup untouched. It contains:
  - Operator badge.
  - Create form:
    - name (≤28), namespace, operator-enabled target cluster, version, and topology with trade-off text (including that the operator labels replication alpha);
    - **MaxScale (on by default for Replication; "fixed at creation"; shows the G1 Preview notice and the endpoint apps will use)**;
    - replicas, storage + class (only classes with `allowVolumeExpansion` are marked "expandable"), resources;
    - StorageLink (required unless "no backups" is ticked), validated 5-field cron, retention, PITR (hidden for Standalone), TLS;
    - a node-count notice (MariaDB replicas, and 2 for MaxScale) and the degraded-HA tick.
  - Import form.
  - Cluster cards:
    - status, topology, primary, ready x/y, PDB, endpoints and an HA-degraded badge;
    - **MaxScale: ready x/2, MaxScale's primary vs the operator's primary (mismatch highlighted), image 23.08.x**;
    - "backups paused" when archival fails;
    - delete vs "remove from EntKube only", plus "also delete volumes". The delete dialog lists what will be removed, including the cluster-scoped auth-delegator binding.
  - Detail view:
    - pods (role, node, zone, restarts, logs via `GetPodLogsAsync`), MariaDB and MaxScale;
    - replica health (IO/SQL threads, errnos, lag);
    - storage + expand (requested vs actual capacity), drift + re-apply;
    - resize, scale (with "cancel scale-out" and leftover cleanup), switchover (states the mechanism and that MaxScale keeps sessions open), upgrade, and restart (states the write stall).
  - Databases: add, discover/import, credentials (with CA_CERT and READ_PORT), sync, rotate, drop with typed name and "drop data".
  - Backups: schedule panel ("configured vs running vs paused"), Backup now, history, PITR window bar with measured archive lag and the RPO note, restore panel (backup or time; same or other Kubernetes cluster; new or cut-over), drills panel.
  - It injects `MariaDbService` and at most `IDbContextFactory<DataServicesDbContext>`.
- **`MariaDbMonitoringPanel.razor`** (new) takes `[Parameter] Guid TenantId` and queries by `(TenantId, Id)`. It shows MariaDB and MaxScale series. The CNPG panel is left alone; it still reads `c.TenantId` off the row, as noted in §11.3.
- **`AppEnvironmentDetail.razor`**: a MariaDB optgroup and badge. The attach branch becomes an explicit switch per kind, and an unknown kind is an error rather than falling through to CNPG.
- **`GovernanceTab.razor` / `Portal/AppGovernancePanel.razor`**: MariaDB type label and `AvailableDatabaseOption.MariaDbId`.
- **`VaultTab.razor`**: a "MariaDB" scope via `GetAllMariaDbSecretsForClusterAsync`.
- **`Portal/PortalDatabasesPanel.razor`**: `DbKind.MariaDb` list, create (non-external only), connect, sync, rotate (then sync), reveal and disconnect. Scope is re-checked before each write.
- **`BlueprintsTab.razor`**: the "MariaDB (MySQL-compatible)" service kind and `ServiceParamsFor("mariadb")`.

---

## 7. API and CLI

No database engine has a public API, CLI or MCP surface today:
- `PublicApiEndpoints.cs` has no database routes.
- `IDataServicesApi` exposes only `GetStorageLinkAsync`.

Parity therefore needs nothing here. PR 14 (optional) adds the following.

Routes:
- `GET /databases`: engine-neutral read model for CNPG, Mongo and MariaDB (engine, name, status, version, replicas, last successful backup, PITR window).
- `GET /databases/mariadb/{id}`
- `GET /databases/mariadb/{id}/backups`
- `POST /databases/mariadb/{id}/backups` (on-demand)
- `POST /databases/mariadb/{id}/switchover` (uses MaxScale or the operator, as configured)
- `POST /databases/mariadb/{id}/restore-drills`

Rules:
- Mutations run under `gate.DeclareUnattended("api-token:…")`, as `/apps/{appId}/secrets/sync` does.
- Scopes are `databases:read` and `databases:write` in `ApiScopes`.
- The tenant comes only from the token.
- Results are typed; the OpenAPI document and `EntKube.ApiClient` are regenerated.
- Create, delete and restore stay UI-only at first, because they need the acknowledgment dialog.

CLI: `entkube databases list|show`, `entkube mariadb backups list|create`, `entkube mariadb switchover`, `entkube mariadb drill`. MCP gets the same.

---

## 8. Integration points (each with its enforcing test)

| # | Integration point | What MariaDB does | Enforced by |
|---|---|---|---|
| 1 | Entities and module ownership | §5; explicit ToTable; both contexts | `ModuleBoundaryTests`, `ModuleContextTests` |
| 2 | Migrations ×3 | Designer + snapshot; hand-corrected defaults (`RetentionDays`, `MaxScaleReplicas`, `MaxUserConnections`); SQL Server cascade checked | `MigrationSnapshotTests`, `SqliteMigrationPathTests`, `MigrationReconcilerTests` |
| 3 | Service registration / god context | DataServices context only; `AddScoped` + `AddSingletonWork`; no `IDataServicesApi` self-dependency | `ModuleCompositionTests`, `ModuleContractTests` (`[DataServices] = 2` unchanged), `CatalogContractAdoptionTests` |
| 4 | Kubeconfig custody | `ClusterFor` → `ForAsync`; no `.Kubeconfig`; the exec verb uses `Path.GetTempFileName()` + `finally` delete | `ClusterCredentialCustodyTests` (15/8 unchanged), `SecretTempFileTests` |
| 5 | Tenant is an input | §6.2; stranger-then-owner tests for every method | `VacuousTenantCheckTests` (0), `ComponentTenantScopingTests`, new `MariaDbTenancyTests` |
| 6 | Change gate: one ack per action | `BeginOperation` on every multi-change public method; `DeclareConsequence` for typed-name drop, named-pod switchover, delete-with-PVCs, scale-in PVC deletion, leftover PVC/Job cleanup before scale-out | `OneAckPerActionRatchetTests` (0), `ClusterChangeGateCoverageTests`, `TypedSdkMutationTests` (0) |
| 7 | Exec ChangeIntent / argv | §9.3: one read verb pair; no Change sites; root password on stdin | `ExecChangeIntentTests` (verbs listed, no default, `BaselineReadClaims` 13 → 14 (PR 4, rotation verification) → 15 (PR 10, discovery) → 16 (PR 12, drills), each with a reason, re-measured on the merge result), `KubectlExecArgvTests` (probed, not banked) |
| 8 | Vault | Tenant-scoped Set/Get/Delete/GetAll per cluster and database; Set archives versions | `VaultSecretDeleteTenancyTests`, `VaultServiceTests` |
| 9 | App credential delivery | `SharedSecretKeys.WriteAsync(…, Owners.Database, …)`; `DatabaseBindingTarget.MariaDb`; a binding naming no database throws; keys include READ_PORT and, on MaxScale clusters, CA_CERT from `<name>-maxscale-ca-bundle` | `SharedSecretWriterRatchetTests` (0), `SharedSecretKeysTests`, `AppSecretSyncTests` |
| 10 | Governance + portal scope | Tenant **and** environment filter; both-ends checks | `GovernanceClusterCallTests`, `PortalServiceScopeServiceTests` |
| 11 | Connectivity graph | Explicit arms for every engine, including `("MariaDB", 3306)`. On MaxScale clusters the edge targets the MaxScale pods on 3306 and 3308 (never the admin port 8989). If the arm's shape allows one port only, it becomes a port list. The current default `_ => ("PostgreSQL", 5432)` (`ConnectivityGraphService.cs:211`, directly below the `{ MongoDatabaseId: not null } => ("MongoDB", 27017)` arm) becomes an "unknown engine" warning that emits no policy. | New `ConnectivityGraphDatabasePortTests` (pins each port, and that 8989 is never emitted) and new **`DatabaseEngineParityTests`**: reflects over every `Guid? *DatabaseId` on `DatabaseBinding`/`AppAllowedDatabase` and every engine FK on `VaultSecret`, and asserts a `ResolveTarget` arm, a connectivity arm and a portal-scope arm exist |
| 12 | App-state bundle | Lists, insert order (after StorageLinks/KubernetesClusters, before DatabaseBindings/AppAllowedDatabases), VaultSecretRecord tail fields, `MariaDbBackup` excluded, `CurrentVersion` 22 → 23 | `BackupCoverageTests`, `BackupRoundTripTests` |
| 13 | Tenant purge | Cascades only; no FK-less TenantId | `TenantPurgeTests` (fixture gains MariaDB rows) |
| 14 | Catalog + detection | Two entries; pinned; `ConfiguredIn`; operator `metrics.enabled` + `release` label; **CRD uninstall guarded by EntKube** (the chart cannot keep its CRDs; Q1) | `ComponentCatalogTests` (incl. `NoTwoEntriesDetectByTheSameCrd`, YAML parse, operator ServiceMonitor `release` label present), `CatalogDefaultFillInTests`, `OperatorStatusViaContractTests`, plus a new test that uninstalling `mariadb-operator-crds` with a `MariaDB` CR present is refused (or that the keep annotation is applied, per Q1) |
| 15 | Import / adoption | Operator CR import, including an existing MaxScale; `IsAdopted` databases; operator-owned CRs excluded; no plaintext URI | `MariaDbTenancyTests` (import arms) |
| 16 | Deployment import | `DetectMariaDb`; the generic DB_* heuristic in `DetectPostgres` no longer claims MySQL apps (PR 15) | `DeploymentImportTenancyTests` |
| 17 | Blueprints | `"mariadb"` in 5 places; tenant predicate in the existence check | `ClusterBlueprintTenancyTests`, `BootstrapRunClaimTests`, new key-parity test (BlueprintsTab keys = runner keys) |
| 18 | Advisor | `EvaluateScheduledBackup` loop plus new findings with stable ids, listed below the table | `AdvisorWireShapeTests`, `AcknowledgeAFindingThatExistsTests`, new `MariaDbAdvisorTests` (never / stale / failed-with-error / **healthy-unvisited** / debounce / MaxScale not Ready) |
| 19 | Backup scheduling correctness | Schedule refused without a link in every path; S3 Secret before the CR; live reality check (no CronJob; PhysicalBackup status + owned Jobs); history independent of Job history limits; same `maxRetention` for every writer to a prefix; seed prefix separate | `MariaDbServiceTests` (behavioural) |
| 20 | Background work | `AddSingletonWork`; per-tenant loop; read-only | `HostedWorkPlacementTests`, `UnattendedDeclarationTests` (new named entry), `SweepTenancyRatchetTests`, `SingletonWorkWitnessTests` |
| 21 | Prometheus | Metrics in the single builder (MariaDB and MaxScale); ServiceMonitor diagnosis through the existing `DiagnoseServiceMonitorScrapeAsync`; namespace in the selector; MariaDB pod via `target` | `PrometheusServiceTests` (new `MariaDbSelector_*` facts measured from a live scrape, still to be done: the spike had no Prometheus), `ComponentCatalogTests.KubePrometheusStack_ScrapesMonitorsCreatedByOtherOperators` |
| 22 | UI ratchets | Section components inject services only | `RazorGodContextRatchetTests`, `OneAckPerActionRatchetTests` |
| 23 | Component consumers | Deferred | – |
| 24 | Public API / CLI / MCP | PR 14 | `ApiDocumentedResponsesTests`, `GeneratedOpenApiDocumentTests`, `ApiClientGeneratedTests`, `CliEndpointParityTests`, `CliColumnParityTests`, `CliRenderedColumnTests`, `McpEndpointParityTests`, `ApiTenantConfinementTests`, `CrossTenantProbeTests`, `ApiScopeAdvertisementTests` |
| 25 | Audit | Cluster changes are audited by `AuditClusterChangeRecorder` because every write is a gated `IClusterClient` call, including database and user writes. Row-only destructive actions (unregister, cut-over, drill outcome) also call `AuditService.RecordAsync`. | `ClusterChangeRecordTests` |
| 26 | DR / cost | DR page gets a "Databases" readiness line (last backup age, PITR lag, last drill) in PR 15; cost unchanged (namespace unattributed) | – |
| 27 | Test fixture shape | `InterceptingTestDb`, real `VaultService`, `Mock<IKubernetesClientFactory>` behind the **real** `ClusterClientFactory`, real `CatalogApi`/`DeliveryApi`/`ClusterChangeGate`. Assert what is **sent** (YAML parsed with YamlDotNet, exec argv and stdin), not only rows. Seed FKs in stages; never seed an `ApplicationUser` into the shared host. | – |
| 28 | MaxScale licence pin | The builder's MaxScale image is explicit and starts with `23.08.`; no code path reads the operator's default image | New `MaxScaleImagePinTests` |
| 29 | Operator SQL safety | Every emitted `Database`/`User`/`Grant` has `cleanupPolicy: Skip` and explicit defaults (§9.6); every identifier passes `DbIdentifier`; privileges come from an allow-list; passwords and salts are alphanumeric | New `MariaDbManifestSafetyTests` |

Advisor finding ids (row 18):

| Finding | Fires when | PR |
|---|---|---|
| `backup-*:mariadb:{id}` | scheduled-backup evaluation | 6 |
| `mariadb-ha-degraded:{id}` | – | 3 |
| `mariadb-no-backups:{id}` | – | 6 |
| `mariadb-replica-broken:{id}` | a replica has `lastIOErrno` 1236 or any role is `Unknown` for more than 5 min | 3 |
| `mariadb-maxscale-not-ready:{id}` | `maxScaleRef` is set but MaxScale has not been Ready for more than 2 min; message: "no automatic failover" | 3 |
| `mariadb-maxscale-primary-mismatch:{id}` | as defined in §3.3.9 | 3 |
| `mariadb-maxscale-single-replica:{id}` | – | 3 |
| `mariadb-maxscale-image:{id}` | – | 3 |
| `mariadb-storage-not-expanded:{id}` | – | 8 |
| `mariadb-scaleout-stuck:{id}` | – | 8 |
| `mariadb-pitr-archive-error:{id}` | "backups paused" | 7 |
| `mariadb-pitr-lag:{id}` | §9.5 | 7 / 9 |
| `mariadb-semisync-off:{id}` | debounced to more than 5 min | 9 |
| `mariadb-tls-optional:{id}` | – | 9 |
| `mariadb-drill-stale:{id}` | – | 12 |
| `mariadb-galera-bootstrap-needed:{id}` | – | 13 |

---

## 9. Security

### 9.1 Credentials and vault
**Root password.**
- 32 alphanumeric characters, so it is DSN-safe without escaping and safe in the operator's unescaped SQL (§9.6).
- Stored in the vault as `ROOT_PASSWORD` (`MariaDbClusterId`).
- Written to `<name>-root` (label `k8s.mariadb.com/watch`) with `SharedSecretKeys.WriteAsync` before the `MariaDB` CR.
- The CR carries only `rootPasswordSecretKeyRef`, which is immutable **[verified on kind, 26.10.2]**.
- Each `MariaDbBackup` records `RootPasswordSecretVersionId`. **A physical restore brings back `mysql.global_priv` as it was when the backup was taken [verified on kind, 26.10.2].** In the spike, with the new Secret holding the *current* value:
  - login with it was denied, and the startup probe failed with 1045, so the pod restart-looped;
  - fixing the Secret afterwards was not enough, because the operator snapshots the root password into Secret `internal-<name>` at creation, so `Ready` kept flapping with "Error reconciling Root Password … Access denied".

  Restore therefore writes **the archived root value** into the new cluster's vault and root Secret **before** the `MariaDB` CR exists. This also makes root rotation safe to add later.
- Rotating root through the watched Secret works (the operator ALTERs `root@localhost` and `root@%` and pushes `/api/environment` to the agent), but **the pod env `MARIADB_ROOT_PASSWORD` keeps the old value until the pod restarts** **[verified on kind, 26.10.2]**. The exec path therefore never relies on the pod env (§9.3). Root rotation stays out of the first release.

**Database users.**
- The password goes into `{cluster}-{db}-credentials` (key `PASSWORD`, `Owners.Database`, label `k8s.mariadb.com/watch`), which a `User` CR references through `passwordSecretKeyRef`.
- The vault keeps HOST, READ_HOST, PORT, READ_PORT, DATABASE, USERNAME, PASSWORD, DATABASE_URL and JDBC_URL, plus CA_CERT (always on MaxScale clusters; when TLS is required otherwise).
- Engine secrets are scoped by owning entity (FK), not by `EnvironmentId`.
- The operator's `Connection` CR is **not** used for credential delivery. Its default Secret has one `dsn` key naming an operator-internal Go TLS config, which apps cannot use; with `secretTemplate` it is usable but renders only one templated key **[verified on kind, 26.10.2]**. Its health check could become a cheap "credentials still work" probe (PR 15).

**Operator- and MaxScale-generated credentials.**
- `<name>-repl-password`, `<name>-metrics-password`, `internal-<name>`, and the six `<name>-maxscale-*` Secrets are generated by the operator **[verified on kind, 26.10.2]**.
- EntKube does not copy them into the vault and never syncs or shows them. Delete removes the unowned ones (§4.1 row 6).
- `<name>-maxscale-client` (DML on `*.*`) must never reach an app.

**S3 credentials.**
- `<name>-s3-credentials` is written directly from the StorageLink's vault values.
- **Do not** call `VaultService.ConfigureKubernetesSyncAsync` on the link's own `ACCESS_KEY`/`SECRET_KEY`, as `CnpgService.EnsureStorageSecretsInK8sAsync` (line 2398) and Mongo (line 2643) do. That call re-points a shared link's sync target to whichever service ran last.
- The PITR agent receives them as env `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` from the secretKeyRefs **[verified on kind, 26.10.2]**.

**No password in any CR spec field.** `SecretRedaction` only redacts `data:`/`stringData:` blocks. Any Secret body that passes through an acknowledgment dialog must be YAML with a `stringData:` block, or go through `SharedSecretKeys`.

**Scoped-prune hazard.** Every `Database`/`User`/`Grant` CR is created with `cleanupPolicy: Skip`. The default is `Delete`, which drops data, users and grants when the CR is deleted **[verified on kind, 26.10.2]**. Deleting a CR for any reason other than an explicit Drop with "drop data" ticked therefore never drops data or users. No sync or prune path ever deletes a MariaDB CR, Secret or key.

**CRD uninstall hazard.** A `helm uninstall` of `mariadb-operator-crds` cascade-deletes every `MariaDB` CR **[verified on kind, 26.10.2]**: `helm uninstall mariadb-operator-crds` with 10 `MariaDB` CRs present removed all 12 CRDs within 60 s, and with them every `MariaDB` CR, all 9 StatefulSets, their pods and 39 of 77 Secrets in those namespaces; 32 of 34 PVCs survived, so the data is orphaned rather than wiped. `cleanupPolicy: Skip` does not protect against that, because the CRD itself goes. The catalog guard in §4.1 row 2 (Q1) is a data-safety control, not a convenience.

### 9.2 Rotation (Q5 settled)
**There is no Kubernetes-side signal for a completed rotation [refuted on kind].** After a rotation:
- the `User` CR was byte-identical: same resourceVersion, generation and `lastTransitionTime`;
- the operator emits no Events for User/Database/Grant;
- it logs nothing at INFO for a successful reconcile;
- `password_last_changed` changes on **every** reconcile, because the operator ALTERs the user each time, so it cannot be used either.

A "wait for a Ready transition" design would have timed out and rolled back every rotation.

The reliable signal is the stored hash **[verified on kind, 26.10.2]**. The flow, in one operation:
1. Generate the new password (32 alphanumerics).
2. Write it to the `{cluster}-{db}-credentials` `PASSWORD` key. The Secret carries `k8s.mariadb.com/watch`. **Without the label the change is not picked up for hours:** 121 s later the old password still worked. The periodic requeue is `--requeue-sql=10h` plus up to 1 h of random offset, and `spec.requeueInterval` also gets the offset **[verified on kind, 26.10.2]**.
3. Annotate the `User` with `entkube.io/rotated-at=<ts>`. Any metadata change triggers an immediate reconcile; it applied an unlabelled Secret within about 8 s **[verified on kind, 26.10.2]**. This is the belt-and-braces trigger in case the label was ever lost.
4. Compute the expected `authentication_string` with `MariaDbAuthString.NativeHash` (`*` + UPPER(HEX(SHA1(SHA1(pw))))), and poll `SELECT authentication_string FROM mysql.user WHERE user='<u>' AND host='%'` through the read-only exec verb (§9.3) on `status.currentPrimary`, every 2 s, bounded at 60 s. With the watch label the new hash was in place in under 1 s **[verified on kind, 26.10.2]**. The user's password never enters the pod; only the root password does, on stdin.
5. On a match: write the vault (which archives the previous version) and sync every SyncEnabled binding.
6. On timeout: write the **previous** value back to the Secret, annotate again, leave the vault unchanged, and report "rotation failed, rolled back".

Both outcomes converge to one value. CNPG's ALTER-then-vault has no rollback, and Mongo's DatabaseTab rotate never re-syncs.

The old password stopped working at once (`ERROR 1045`). Two Users in two MariaDBs sharing one watched Secret both rotated **[verified on kind, 26.10.2]**. Whether MaxScale's user cache accepts the new password at once for connections through the proxy is **[verify, PR 0b]**; MaxScale is expected to reload users on an authentication failure.

Rotation ships in PR 4.

### 9.3 Argv and the single exec path
Writes never use exec. Exec is used only for reads, all `ChangeIntent.Read`:
- rotation verification (PR 4);
- `SHOW DATABASES` on imported clusters (PR 10);
- drill verification (PR 12).

```csharp
Task<string> ExecuteMariaDbWithOutputAsync(string podName, string ns, string rootPassword, string sql,
    ChangeIntent intent, CancellationToken ct = default);          // intent: NO default
public static IReadOnlyList<string> MariaDbExecArgs(string podName, string ns, string kubeconfigPath);
// ["--kubeconfig", path, "exec", "-i", podName, "-n", ns, "-c", "mariadb", "--",
//  "sh", "-c", "IFS= read -r MYSQL_PWD && export MYSQL_PWD && exec mariadb -uroot --batch --skip-column-names --connect-timeout=10"]
// stdin: rootPassword + "\n" + sql
```

The verb lives on `IKubernetesClientFactory`, `KubernetesClientFactory`, `IClusterClient` and `ClusterClient`.

How the password stays off argv:
- **The argv builder has no password parameter.** The `sh -c` string is a constant. The password and the SQL travel on **stdin**: the password is the first line, read by the in-pod shell into `MYSQL_PWD`.
- kubectl's argv carries no secret. `ArgumentList` means there is no host shell.
- The client reads `MYSQL_PWD` from its **environment**. The 12.3 client honours `MYSQL_PWD` **[verified on kind, 26.10.2]**, and the spike's helper passed a password over stdin into `MYSQL_PWD=$(cat)` **[verified on kind, 26.10.2]**. The exact first-line `read` form is **[verify, PR 0b]**; it is a trivial check.
- **Why not the pod's `MARIADB_ROOT_PASSWORD`.** The operator injects it (from `rootPasswordSecretKeyRef`, into the mariadb, init and agent containers; `MYSQL_PWD` is not set), and the earlier form `MYSQL_PWD="$MARIADB_ROOT_PASSWORD"` works on 12.3.3 **[verified on kind, 26.10.2]**. But that env keeps the **old** value after a root rotation until the pod restarts, and an exec using it failed with 1045 **[verified on kind, 26.10.2]**. EntKube takes the root password from the vault instead. No `ENTKUBE_ROOT_PASSWORD` env and no `spec.env` are needed.
- SQL and any `USE \`db\`;` follow on stdin, after `DbIdentifier` validation.
- The connection is the in-pod unix socket, which is permitted under `require_secure_transport=ON`. TCP to 127.0.0.1 without TLS is refused (`ERROR 3159`) **[verified on kind, 26.10.2]**.

Target pod: `status.currentPrimary` (it follows MaxScale when MaxScale is on) **[verified on kind, 26.10.2]**, else any Ready pod. Never a hard-coded `-0` (Mongo's defect). Rotation verification uses the primary; other reads may use a replica.

Ratchets:
- `KubectlExecArgvTests` adds a `mariadb` probe and asserts the sentinel is absent and the builder is **not** banked in `StillCarryASecret`. A further fact asserts that no `-p`/`--password` element exists and that the password appears only in the stdin payload.
- `ExecChangeIntentTests.Verbs` gains the verb names.

Operator-side argv exposure, which EntKube cannot change, is recorded in §9.7.

### 9.4 TLS
Facts **[verified on kind, 26.10.2]**:
- `tls.enabled` defaults to true even with no `tls` block. `tls.required` defaults to false (`require_secure_transport = false`, `tls_version = TLSv1.3` in `<name>-config-tls`).
- The operator CA `<name>-ca` (keys `ca.crt` **and `ca.key`**, CN `<name>-ca`) is valid for 3 years. With no `clientCASecretRef`, it issues both the server certificate (`<name>-server-cert`, CN `<name>.<ns>.svc.cluster.local`, about 90 days) and the client certificate (`<name>-client-cert`, CN `<name>-client`, EKU clientAuth).
- Server SANs cover `<name>`, `<name>-primary` and `<name>-secondary` in every short and FQDN form, `*.<name>-internal.*` and `localhost`, so `sslMode=verify-full` works against those hostnames.
- `status.tls` reports issuer, subject, notBefore and notAfter for the CA bundle and both leaves.
- Clients get the trust bundle from **`<name>-ca-bundle` (`ca.crt`)**. EntKube never copies `<name>-ca`, which holds the private key.
- `tls.required: true` sets `require_secure_transport=ON` and refuses plaintext TCP with `ERROR 3159`. The exporter keeps working (it uses the client certificate). **Flipping it rolls every pod with a switchover**, so it is a gated change.
- A MariaDB 11.4+ client verifying the server by IP with no CA still succeeded, through the password-based zero-configuration check. JDBC and other drivers do not do this, so the mariadb CLI cannot prove CA distribution works.

Design:
- `tls.enabled: true` on every cluster with the operator CA, unless a cert-manager issuer is chosen.
- `TlsRequired` controls MariaDB `tls.required`. The default is decision 7.
- **MaxScale clusters always require TLS at the listener** (§3.3.6), whatever `TlsRequired` says: credentials always carry CA_CERT from `<name>-maxscale-ca-bundle`, `DATABASE_URL` gets `?tls=true`, and `JDBC_URL` gets `sslMode=verify-full`. MariaDB `tls.required` then governs only direct connections (operator, exporter, backups, MaxScale's server leg).
- On non-MaxScale clusters, credentials gain CA_CERT and the URL parameters when TLS is required.
- The advisor raises `mariadb-tls-optional:{id}` for non-MaxScale clusters with TLS not required.
- The 90-day leaves are renewed by the operator **[cited; renewal not observed]**. The poller records `notAfter`.

### 9.5 Network and metrics endpoints
**Metrics facts [verified on kind, 26.10.2]:**
- **The MariaDB exporter is a separate Deployment, not a sidecar.** It is named `<name>-metrics`, runs `prom/mysqld-exporter:v0.15.1` (container `exporter`, port `metrics` 9104) behind Service `<name>-metrics`, and has selector `app.kubernetes.io/instance=<name>-metrics, app.kubernetes.io/name=exporter`.
  - It runs in multi-target mode: the ServiceMonitor `<name>-metrics` has one endpoint per MariaDB pod, `/probe?target=<pod>.<name>-internal.<ns>.svc.cluster.local:3306`, with metricRelabelings setting `target=<pod>` and `instance=<pod FQDN>`, and a static `role=primary|replica` that the operator rewrites on switchover.
  - **A plain `/metrics` scrape returns `mysql_up 0`**, so a PodMonitor or annotation scrape would report the database as down.
  - The exporter authenticates as the operator-created User `<name>-metrics` (`SELECT, PROCESS, BINLOG MONITOR, SLAVE MONITOR ON *.*`, 3 connections) over TLS with the client certificate.
- **The ServiceMonitor's labels and `jobLabel` are set only at creation.** Later changes to `prometheusRelease` are ignored, while interval changes apply. Without `prometheusRelease` it has **no labels** and kube-prometheus-stack ignores it. The operator does not watch ServiceMonitor deletion: a deleted one is recreated, with current labels, only on the next MariaDB reconcile.
- **Turning metrics off (`metrics.enabled` or `serviceMonitor.enabled` false) removes nothing.** The exporter Deployment, Service, ServiceMonitor, User, Grant and Secrets stay until the MariaDB is deleted.
- **MaxScale metrics:** Deployment `<name>-maxscale-metrics` (`mariadb/maxscale-prometheus-exporter-ubi:v0.0.1`), Service port `metrics` 9105, and ServiceMonitor `<name>-maxscale-metrics` with one `/probe?target=<mxs-pod>.<name>-maxscale-internal.<ns>.svc.cluster.local:8989` endpoint per MaxScale pod. The exporter reaches the REST API over TLS when MaxScale TLS is on. In the spike the MaxScale ServiceMonitor had no `release` label; whether the MaxScale CR offers `prometheusRelease` is **[verify, PR 0b]**.
- Raw series carry no `namespace` or `pod` labels. Prometheus adds `namespace`, `service`, `job` and `pod`, and **that `pod` is the exporter pod**. This follows standard ServiceMonitor semantics but was not observed, because the spike had no Prometheus **[verify, PR 9 live scrape]**.

**Metric names [verified on kind, 26.10.2]:**
- MariaDB:
  - `mysql_up`;
  - `mysql_slave_status_seconds_behind_master{master_host=…}` (replicas only), `mysql_slave_status_slave_io_running`, `_slave_sql_running`, `_last_io_errno`, `_last_sql_errno`;
  - `mysql_global_status_rpl_semi_sync_master_status` (1 on the primary), `_master_clients`, `_slave_status`, `_master_no_tx`, `_master_yes_tx`, and `mysql_global_variables_rpl_semi_sync_master_timeout`;
  - `mysql_global_variables_read_only` (0 on the primary);
  - connections (`mysql_global_status_threads_connected`, `_threads_running`, `_max_used_connections`, `mysql_global_variables_max_connections`, `_aborted_connects`);
  - queries (`mysql_global_status_queries`, `_slow_queries`, `_commands_total{command}`);
  - buffer pool (`mysql_global_status_buffer_pool_pages{state}`, `_innodb_buffer_pool_bytes_data`, `_read_requests`, `_reads`, `_wait_free`);
  - `mysql_binlog_size_bytes`, `mysql_binlog_files` (with `--collect.binlog_size`);
  - `mysql_exporter_collector_success`, `mysql_version_info`;
  - Galera: `mysql_global_status_wsrep_cluster_size`, `_wsrep_cluster_status`, `_wsrep_connected`, `_wsrep_flow_control_paused`.
- MaxScale: `maxscale_up`, `maxscale_server_state{id,role}`, `maxscale_server_connections`, `maxscale_service_connections`, `maxscale_service_active_connections`, `maxscale_service_statistics_failed_auths`, `maxscale_monitor{id,module_name}`, `maxscale_version`.

**PromQL rules (PR 9):**
- Select the cluster with `namespace="<ns>", service="<name>-metrics"` (or `job`), and a member with `target="<pod>"`. Never use `pod=`.
- Lag: `max by (target) (mysql_slave_status_seconds_behind_master)`.
- Primary: `mysql_global_variables_read_only == 0`. This is taken from the server itself, rather than the relabelled `role`, so it stays correct behind MaxScale (whether the `role` relabel follows a MaxScale-driven switchover was not observed **[verify]**).
- Semi-sync off: on the primary, `rpl_semi_sync_master_status == 0` or `rpl_semi_sync_master_clients < 1`, **for more than 5 min** (a routine primary restart costs about 47–60 s of async, §3.3.3).
- Down: `mysql_up == 0` on a `/probe` series; **an absent series means unknown, never healthy**.
- MaxScale health: `maxscale_up == 0`, or a `maxscale_server_state` with no Master.

**PITR lag (advisor `mariadb-pitr-lag`).** `now − lastArchivedTime` alone would fire on idle clusters, where nothing is unarchived. The finding fires when a replica's `gtidCurrentPos` (from `status.replication.replicas`) differs from `status.pointInTimeRecovery.lastArchivedGtid` **and** `now − lastArchivedTime` exceeds the threshold (default 30 min = the 10-minute cycle plus margin).

**Repairs EntKube owns:**
- On re-apply, if the stored `prometheusRelease` differs from the live ServiceMonitor's `release` label, delete the ServiceMonitor and annotate the MariaDB so the operator recreates it.
- Scrape diagnosis compares the **live** label.
- Drill clusters are created with metrics off from the start.
- If EntKube ever turns metrics off on a live cluster, it deletes the exporter objects itself.

**Network policy.** Bindings create egress on 3306 (and 3308 behind MaxScale; integration point 11). If default-deny policies cover the database namespace, the following must also be allowed:
- apps → **MaxScale pods on 3306/3308 only, never 8989**. On non-MaxScale clusters, apps → MariaDB pods on 3306;
- MaxScale pods → every MariaDB pod on 3306;
- operator → MaxScale pods on 8989 (it configures each pod through the REST API), MariaDB pods on 3306 (the operator connects as `root` over TCP from its pod IP, as seen in an `Access denied … 'root'@'<operator-ip>'` log), and the agent on 5555 (5566 is the probe port). The agent runs in **replication and Galera** **[verified on kind, 26.10.2]**;
- MariaDB pod ↔ pod on 3306 (replicas reach the primary through `<pod>.<name>-internal`), plus 4444/4567/4568 for Galera (`<name>-internal` exposes them **[verified on kind, 26.10.2]**);
- Prometheus → MariaDB exporter pods on 9104, and → MaxScale exporter pods on 9105;
- MariaDB exporter → MariaDB pods on 3306 (TLS with the client certificate); MaxScale exporter → MaxScale pods on 8989;
- backup, restore and PITR Jobs, and the primary's agent (binlog archival) → S3.

The agent authenticates the operator with `kubernetesAuth` via TokenReview, which needs the leaking `system:auth-delegator` ClusterRoleBinding (§4.1 row 6, decision 13).

### 9.6 SQL the operator builds without escaping
**[verified on kind, 26.10.2]** The operator interpolates names, passwords, hashes, plugin arguments and privileges into SQL **unescaped**:
- a user `o'brien` failed with Error 1064;
- a database name containing a backtick failed with Error 1064, which is an injection vector;
- the existence check `WHERE SCHEMA_NAME = '%s'` is also unescaped.

Admission checks only lengths, and the Database name limit is 80, although MariaDB allows 64: a 65-character name passes and then fails with Error 1102. EntKube is therefore the only validator:
- names: `DbIdentifier` (§6.1), in the service as well as the UI;
- passwords and `caching_sha2` salts: alphanumeric only;
- privileges: an allow-list (`ALL PRIVILEGES` on `<db>.*` for owners; named privileges for later roles);
- every Grant emits `database`, `table`, `host` and `grantOption` explicitly. They are immutable through the webhook, so a change means delete and recreate (`Skip` keeps the privileges);
- every User emits `maxUserConnections` explicitly. The operator default is 10, which breaks any pool larger than 10. EntKube's default is 100, editable per database. Behind MaxScale the limit applies **per server**, because readwritesplit opens one backend connection per server for each client session. -1 is accepted unvalidated, so EntKube refuses negative values;
- `User` identity fields (name, host, `mariaDbRef`), `Database` name/charset/collation and the Grant fields above are immutable **[verified on kind, 26.10.2]**. The UI does not offer to edit them.

### 9.7 Secrets visible outside the vault (operator-side; recorded, not fixable by EntKube)
- **`MariaDB.status.rootPasswordHash` is an unsalted SHA-256 of the root password** **[verified on kind, 26.10.2]**. Anyone with `get` on MariaDB CRs can attack it offline. For EntKube-generated passwords (32 alphanumerics, about 190 bits) brute force is infeasible. For **imported** clusters with weak root passwords it is a real risk. Tenants and portal users never get read access to MariaDB CRs, and EntKube never displays the field.
- **MaxScale's PVC holds the server and monitor passwords in plaintext** (0644 files) **[verified on kind, 26.10.2]**. Anyone who can mount or snapshot that PVC can read them.
- Standalone probes run `mariadb -u root -p"${MARIADB_ROOT_PASSWORD}"`, and mariadb-backup receives `--password=${…}`, both expanded onto argv **inside the pod** **[verified on kind, 26.10.2]**. Processes in the same pod can see them; nothing outside the pod can.
- `root@%` exists by default, so remote root over TCP is open, and the operator itself uses it (§9.5). EntKube does not restrict it, because that would break the operator; NetworkPolicy and the 32-character password are the controls (Q9).

---

## 10. Phased delivery

Rules for every PR:
- Branch from an up-to-date `main` and open with `--base main`.
- Check `git branch --show-current` in the same call as the commit.
- Gate the commit on the test-suite exit code.
- Re-measure every ratchet constant (`BaselineReadClaims`, module boundary, `BackupBundle.CurrentVersion`) on the merge result.
- **Start PR 1 only after the open catalog branch (`feat/catalog-node-maintenance-security`, a7e6fffb) and the pending Kyverno PR have merged.** Both touch `ComponentCatalog.cs` and `ComponentCatalogTests.cs`.

The feature carries a **"Preview"** badge until PR 7 (restore) ships. Until then the UI says plainly: "Backups cannot yet be restored through EntKube." MaxScale-fronted clusters additionally keep a Preview notice until release gate **G1** (§3.3.9) passes.

| PR | Scope | Tests | Explicitly excluded |
|---|---|---|---|
| **0: Spike + facts (done 2026-11-20)** | kind, operator 26.10.2, MariaDB 12.3.3, MaxScale 23.08.13: replication lifecycle, MaxScale, databases/users/credentials, backups/PITR/restore, metrics/TLS/Galera. Results in §A. | none (docs only) | all code |
| **0b: MaxScale and remaining facts** (before PR 3) | The list is below the table. Append the results to §A. | none (docs only; the G1 harness is scripted and kept with the spike files) | all code |
| **1: Catalog + detection** | `mariadb-operator-crds` + `mariadb-operator` (pinned, hardened securityContext, requests and memory limit, webhook and cert-controller, `ConfiguredIn`, `metrics.enabled` + `serviceMonitor.additionalLabels.release`); CRD uninstall guard per Q1; `MariaDbOperator.IsInstalled`; `DatabaseService.MariaDbAvailable`, `GetMariaDbEnabledClustersAsync`; operator badge | `ComponentCatalogTests` (required fields, unique keys, YAML parses, no shared detection CRD, operator ServiceMonitor `release` label); CRD uninstall refused (or keep annotation present) while a `MariaDB` CR exists; `CatalogDefaultFillInTests`; `OperatorStatusViaContractTests` (match and near-miss per alias); `CatalogContractAdoptionTests` | any service, any entity |
| **2: Data model** | `MariaDbCluster` (incl. MaxScale, PITR and `CrUid` columns), `MariaDbDatabase`, `MariaDbBackup`; `VaultSecret` FKs; ModuleMap; both contexts; migrations ×3 with hand-corrected defaults; BackupBundle v23 + VaultSecretRecord tail; purge fixture; boundary re-bank | `ModuleBoundaryTests`, `ModuleContextTests`, `MigrationSnapshotTests`, `SqliteMigrationPathTests`, `BackupCoverageTests` (`MariaDbBackup` excluded with reason), `BackupRoundTripTests`, `TenantPurgeTests` | behaviour; binding/allowlist FKs (PR 5) |
| **3: Cluster lifecycle + MaxScale + status poller** | `MariaDbManifests.Cluster` (incl. replica-template PhysicalBackup with its own prefix) and **`MariaDbManifests.MaxScale`** (pinned 23.08 image, 2 replicas, rw 3306 and read 3308 listeners, create-time TLS with the MariaDB CA bundle and client certificate, explicit MaxScale-only anti-affinity, metrics, no `primaryServer`); strict field validation on apply; `DbIdentifier`; `MariaDbStatusMap`; `MariaDbService` create (Standalone/Replication, MaxScale default ON for Replication, HA defaults, root custody, TLS, node-count check and degraded flag, StorageLink stored, no scheduled backups applied yet), list, detail (applies nothing), delete (MaxScale first, then the full leftover cleanup incl. the auth-delegator ClusterRoleBinding and the six MaxScale Secrets; PVC toggle; leftover report), unregister; `MariaDbStatusPollingService` (MariaDB + MaxScale status, CrUid); advisor `ha-degraded`, `replica-broken`, `maxscale-not-ready`, `maxscale-primary-mismatch`, `maxscale-single-replica`, `maxscale-image`; `MariaDbSection.razor` (create with MaxScale toggle and G1 notice, cards with MaxScale state, pods) | Listed below the table | databases, backups, Galera; adding/removing MaxScale on a live cluster |
| **4: Databases + credentials + rotation** | Database/User/Grant CRs (`Skip`, utf8mb4 + collation, explicit `maxUserConnections`, explicit Grant fields); credentials Secret labelled `k8s.mariadb.com/watch`; create; drop (typed name, `DeclareConsequence`, flip to `Delete` only with "drop data"); unregister; vault methods (incl. READ_PORT, CA_CERT from the MaxScale bundle); credentials view; sync to cluster namespace via `SharedSecretKeys`; **`ExecuteMariaDbWithOutputAsync` + `MariaDbExecArgs` (root password on stdin)**; **rotation per §9.2 (hash check)**; `FetchRootPasswordAsync`; VaultTab scope | CR shapes (`MariaDbManifestSafetyTests`); watch label present; the only exec is the Read verb, used for rotation verification (mock verifies; no Change exec); invalid identifier refused **in the service**; create/drop each ask once, drop with N bindings asks once (`RegisteredPostgresDropOneAckTests` shape); rotation does not touch the vault before the hash matches, annotates the User, and rolls the Secret back on timeout; `ExecChangeIntentTests` (verbs; no default; Read baseline 13 → 14 with reason); `KubectlExecArgvTests` (probed, sentinel absent, password only on stdin, not banked); `VaultSecretDeleteTenancyTests` arms; `SharedSecretWriterRatchetTests` stays 0 | apps, portal, discovery |
| **5: Apps, portal, governance** | `DatabaseBinding` / `AppAllowedDatabase` FKs (migrations ×3); `AddMariaDbDatabaseBindingAsync`; Include in `GetDatabaseBindingsAsync`; `AppSecretSyncService` arm; **explicit connectivity arms (MaxScale pods on 3306/3308, never 8989)**; AppEnvironmentDetail exhaustive switch; GovernanceTab / AppGovernancePanel; PortalServiceScopeService; PortalDatabasesPanel | `AppSecretSyncTests` theory gains MariaDb; `ConnectivityGraphDatabasePortTests`; **`DatabaseEngineParityTests`**; `GovernanceClusterCallTests` (stranger's MariaDB DB refused); `PortalServiceScopeServiceTests` `DatabaseScope_*` arms; binding both-ends tenancy | backups |
| **6: Backups + history + advisor** | StorageLink attach/change; `<name>-s3-credentials` without vault re-pointing; scheduled `<name>-backup` (`immediate` on create, explicit Job history limits, same `maxRetention` for every writer to the prefix); "Backup now" via `schedule.onDemand` (one-shot with the same prefix and retention when suspended; serialized); `UpdateBackupSettingsAsync` (one ack); `GetScheduledBackupStatusAsync` (PhysicalBackup status + Jobs by ownerReference, no CronJob, "paused" state); `BackupHistoryPolicy` by reason; poller records history; advisor backup and no-backups findings | schedule without a link refused before any write (create, settings, imported settings); 5-field cron validated; no `onPrimaryChange` or other non-existent field emitted (manifest parsed against the 26.10.2 CRD schema); `JobFailed` with `status: True` maps to Failed; reason `Failed`/"MariaDB not ready" maps to paused, not failed; history recorded **with no page view**; a vanished Running row → Unknown, never Completed; seed prefix differs from the physical prefix; advisor never / stale / failed-with-error / **healthy-unvisited**; `BackupHistoryPolicy` unit tests | PITR, restore |
| **7: Restore + PITR** | `PointInTimeRecovery` (Replication only, refused for Standalone; Id prefix; `strictMode: true`); PITR window from S3 + MariaDB status (dependency per Q4); `RestoreAsync` to a new cluster on the same or another Kubernetes cluster (time, or backup with an explicit `targetRecoveryTime`; `bootstrapFrom.s3` with `backupContentType: Physical` cross-cluster); S3 object-exists check before apply; root password restored **before** the CR; restored cluster includes MaxScale; DB rows and vault recreated; restore Job status/log captured while running; `CutOverToRestoredClusterAsync` (HOST, READ_HOST, READ_PORT and CA_CERT); advisor `pitr-archive-error` and `pitr-lag`. **Remove the Preview badge** (MaxScale clusters keep theirs until G1). | restored manifest == create manifest + `bootstrapFrom` (structural diff); target time outside the window refused; Galera/Standalone offer no time target; cross-tenant target cluster refused; restore asks once; cut-over asks once and rewrites HOST and CA_CERT for every DB and binding; restore uses the backup's root version, not the current one, and writes it before the CR | logical / single-DB restore |
| **8: Day-2** | Expand storage (class must allow expansion; completion from PVC capacity), resize (MariaDB and MaxScale resources), scale replicas (preconditions and cancel cleanup per §4.2; scale-in deletes the PVC), switchover (MaxScale `primaryServer` or MariaDB `podIndex`; `DeclareConsequence`; completion per §3.3.4), minor/major upgrade (fresh-backup precondition, no downgrade), restart (`entkube.io/restarted-at`), drift + re-apply (ignore list; User/Grant poke; ServiceMonitor `release` repair; MaxScale image moves to the current pin), imported clusters patch the image only; advisor `storage-not-expanded`, `scaleout-stuck` | ports of CNPG `ExpandStorageAsync_*` and Mongo `ResizeClusterAsync_*`; expansion refused on a non-expandable class; `StorageResized=True` with unchanged capacity reports not-expanded; storage aligned before every build; scale-out refused when there is no template, no Ready replica, a leftover PVC/Job, or too few nodes; switchover writes `primaryServer` when MaxScale is on and `podIndex` otherwise, and never re-emits either on re-apply; refusals before any write; drift `Unknown` ≠ `Matches`; one ack each | Galera; MaxScale add/remove |
| **9: Metrics** | `GetMariaDbClusterMetricsAsync` (MariaDB + MaxScale); scrape diagnosis via the existing `DiagnoseServiceMonitorScrapeAsync` for both ServiceMonitors (live `release` label); `MariaDbMonitoringPanel` (TenantId); re-apply adds metrics to older clusters; advisor semi-sync-off (debounced), replication-lag, tls-optional | selector facts measured on a live scrape (one brace block, namespace included, `target` not `pod`); "no series" ≠ healthy; panel queries by tenant | |
| **10: Adoption** | Import an existing `MariaDB` CR (and its MaxScale, read-only); discover + import databases (`IsAdopted`, operator-owned CRs excluded); backup settings for imported clusters | `ExecChangeIntentTests` (Read baseline 14 → 15 with reason); import tenancy; operator-owned CRs never listed | external non-operator servers |
| **11: Blueprints** | `"mariadb"` in `ExecuteServiceStepAsync`, `ServiceInstanceExistsAsync` (with tenant predicate), run-scope resolution, `BlueprintFromClusterService`, `BlueprintsTab` | idempotent re-run creates nothing; existence check tenant-scoped; key-set parity test | databases and bindings in blueprints (CNPG lacks them too) |
| **12: Restore drills (manual)** | `MariaDbRestoreDrill` (migrations ×3, bundled); "Run drill now": scratch cluster `<base>-d<6>` (no MaxScale, no metrics) from PITR (now − 5 min) or the latest backup, verify via exec Read (schemata vs rows), record RecoverySeconds and RecoveredToTime, delete the scratch cluster and its leftovers (incl. the auth-delegator binding) even on failure; advisor drill-stale/failed | Read baseline 15 → 16 with reason; scratch deleted on failure; one ack; drill evidence in `BackupRoundTripTests` | scheduled drills (open decision 9) |
| **13: Galera** | Starts with a spike: Galera + MaxScale (`galeramon`) failover, Galera Jobs' Istio labels, second 100Mi PVC per pod. Then: topology option, odd-count validation (CRD CEL already enforces it; EntKube validates first for a clear message), no-PITR warning, "bootstrap needed" surfacing (`GaleraReady=False` plus `status.galeraRecovery` with no `bootstrap` entry, or every recovered seqno −1, beyond `clusterBootstrapTimeout` 10 m; mere presence of `galeraRecovery` is not an alarm), guarded "bootstrap from pod N" (`spec.galera.recovery.forceClusterBootstrapInPod`, `DeclareConsequence`, never automatic, cleared afterwards; the dialog shows per-pod recovered seqno) | manifest facts; PITR toggle disabled; action names the pod and clears the field | |
| **14: Public API / CLI / MCP** | §7 | full API ratchet set (§8 row 24) | create/delete/restore endpoints |
| **15: Follow-ups** | Logical `Backup`/`Restore` (single-DB restore, per-DB migration, import from external MySQL/MariaDB, restore to external URI, all with owned Secrets); deployment-import `DetectMariaDb` (and narrowing the generic DB_* heuristic); DR readiness line; scheduled drills; `myCnf` editing; **adding/removing MaxScale on a live cluster** (with the `Master_User` pre-flight and repair, §3.3.10); `caching_sha2_password` opt-in; `Connection` CR as a credentials health probe; a time-bounded binlog flush (Q8); `FormFieldType.MariaDbDatabase`; `ExternalMariaDB` registered servers (research spike); Oracle-MySQL engine on PXC | per item | |

**PR 0b scope.** It must land before PR 3.
1. **G1 hard-failure test through MaxScale.** File the upstream issue with the logs, and re-test with `log_slave_updates=ON` (Q7). The test uses a real node stop where possible, because kindnet policies do not cut established connections.
2. MaxScale behaviour in combinations the spike did not run:
   - with MariaDB `tls.required: true`;
   - with scale-out (is the new server added?) and replica recovery;
   - PITR archival after a MaxScale failover;
   - MaxScale on a `bootstrapFrom` (restored) cluster;
   - rotation through MaxScale (user cache);
   - `caching_sha2_password` through MaxScale.
3. MaxScale CR fields and lifecycle:
   - the `podMetadata` and `prometheusRelease` field names and the primary-server status path;
   - the MaxScale-first create order;
   - the MaxScale finalizer when the MariaDB is already gone.
4. Operator facts still open:
   - replica template on `storage.persistentVolumeClaim`;
   - scale-in with `whenScaled: Delete`;
   - whether `-secondary` drops a lagging replica;
   - whether a standalone gets a PDB;
   - `enableServiceLinks`;
   - `autoUpdateDataPlane`.
5. The remaining open questions: CRD uninstall (Q1); bucket-missing on a non-auto-creating S3; cross-cluster PITR; whether EntKube database namespaces are Istio-injected; which provisioners enforce RWO as RWOP; the exact first-line `read` form of the exec; the 23.08 EOL date.

**PR 3 tests** (`MariaDbServiceTests` unless named):
- The parsed manifest has semi-sync `AfterSync`, `autoFailover`, replica recovery **with** `bootstrapFrom.physicalBackupTemplateRef`, `mariadbAutoUpgradeEnabled`, required anti-affinity when nodes ≥ replicas, degraded only when ticked, root Secret applied before the CR, **no secret text in any CR**, no `podIndex`/`volumeClaimTemplate`/`spec.maxScale`, `maxScaleRef` set and the MaxScale CR applied in the same operation, MaxScale TLS refs present at create, and a MaxScale image starting with `23.08.` (`MaxScaleImagePinTests`).
- Create and delete each ask **once**.
- Delete removes the ClusterRoleBinding named from `CrUid`, and the unowned Secrets.
- A foreign Kubernetes cluster, a foreign StorageLink, or an installing operator is refused.
- PITR on Standalone is refused.
- Stranger-then-owner on every method.
- Detail makes zero gated calls.
- Status mapping unit tests per reason.
- The poller loops tenants and acks nothing (`SweepTenancyRatchetTests`, `UnattendedDeclarationTests`, `HostedWorkPlacementTests`).
- Advisor MaxScale findings.

The minimum to claim "managed like CNPG" is PRs 1–7. The smallest user-valuable cut (clusters plus databases bound to apps) is PRs 1–5. PR 6 must not ship the advisor backup loop without the poller history in the same PR. PR 3 must not ship MaxScale without the `maxscale-not-ready` and `primary-mismatch` findings, because a broken MaxScale means no failover at all.

---

## 11. Risks

### 11.1 Operator and engine
- **v1alpha1 CRDs with frequent behaviour changes.** 26.10.0 alone dropped `RESET MASTER`, changed per-role semi-sync handling and Galera bootstrap.
  - Pin the chart and read every `UPGRADE_x.md`.
  - Treat an operator bump as a catalog change, because it can roll every database pod.
  - Re-run the G1 hard-failure test and the MaxScale test set on every bump.
  - Keep `autoUpdateDataPlane` false **[verify; not covered by the spike]**.
  - Manifest builders are pure and snapshot-tested, so a bump shows its diff.
- **The operator undoes MaxScale's failover on 26.10.2 [refuted on kind].** A hard primary failure behind MaxScale gave a write outage for the whole outage and lost acknowledged writes (§3.3.9). Mitigations: release gate G1, the upstream issue, the primary-mismatch advisor finding, and Q6 asking the user to re-confirm. Without MaxScale the operator's failover worked (2–11 s, no loss).
- **Failover time is not constant [verified on kind, 26.10.2].** Without MaxScale: 2 s in one run, 11 s in another (Error 1948 on a replica that had once been primary, then a 10 s `syncTimeout` and a retry), and a full re-seed of the old primary when it rejoined with a diverged GTID. With MaxScale on a node-loss-style failure, MaxScale's own promotion took about 68 s before the operator reverted it (`STOP SLAVE` hung against the black-holed primary).
- **`log_slave_updates` is off on replicas** **[verified on kind, 26.10.2]**. A promoted replica's binlog lacks earlier history: that caused the 1236 on rejoin, and MaxScale warns on every failover that promoting a lagging replica breaks the others. Q7.
- **Every primary restart costs about 10 s of stalled writes and about 60 s of async commits** (`rpl_semi_sync_master_timeout=10000`, replicas' `Connect_Retry=60`) **[verified on kind, 26.10.2]**. Whether `Connect_Retry` is configurable through the operator is **[verify]**.
- **Replication is alpha in the operator's own API docs [cited].** EntKube's default rests on a topology the operator does not yet call production-ready. Mitigations: the poller and advisor surface failover and semi-sync state; switchover is gated; restore drills prove recoverability; decisions 2 and 3 are revisited with each operator bump.
- **The operator mutates spec [verified on kind, 26.10.2]** (`podIndex` on failover; defaulted agent, initContainer and repl-password refs; expanded affinity; a rewritten `volumeClaimTemplate`). The builder never emits these fields, and drift ignores them (§4.1 row 8).
- **CRD uninstall deletes every database [verified on kind, 26.10.2].** The CRDs chart has no keep option. EntKube's uninstall guard (Q1) is the only protection.
- **Leaked cluster-scoped objects [verified on kind, 26.10.2].** One auth-delegator ClusterRoleBinding per replication cluster per uid is never garbage-collected. EntKube deletes it, and the leftover report names it when it cannot.
- **The storage resize state can lie [verified on kind, 26.10.2].** A failed expansion cannot be reverted (no shrink), and the only escape makes the operator report `StorageResized=True` while the PVCs are unchanged. Hence the class check before expanding and the capacity check after.
- **Scale-out leftovers [verified on kind, 26.10.2].** A cancelled scale-out leaves a Job and a PVC that later act on their own; scale-in leaves a PVC that blocks the next scale-out. §4.2 handles both.
- **ReadWriteOncePod is fatal for this operator [refuted on kind]** (the earlier "partially supported" framing). Physical-backup Jobs mount the running pod's PVC, so with RWOP the Job pod stays Pending. Scheduled backups, scale-out and replica recovery all hang, and the PhysicalBackup only ever says `Complete=False/JobRunning` until its timeout. RWOP is not offered, and EntKube never emits `volumeClaimTemplate` (the webhook rewrites it to RWO anyway when `storage.size` is set). The remaining hazard is provisioners that **enforce** RWO as RWOP, such as openebs lvm-localpv without `shared` **[cited]**. EntKube warns based on the StorageClass's provisioner and parameters; which provisioners to list is **[verify]** (the spike's `local-path` is not one).
- **Staging can fill a node.** mariadb-backup staging defaults to an emptyDir on the Job's node **[cited]**. Staging size is a per-cluster setting mapped to the operator's `stagingStorage.persistentVolumeClaim` **[cited]**.
- **Failing schedules give no pruning signal, and timed-out Jobs leave no trace [verified on kind, 26.10.2].** `maxRetention` prunes only inside a successful Job. A Job past `timeout` is deleted silently. The poller and advisor are the signal.
- **Retention is per prefix, not per CR [verified on kind, 26.10.2].** Any writer with a shorter `maxRetention` prunes everyone else's objects in the same prefix. Hence one retention per prefix and a separate seed prefix.
- **Two backups in the same second overwrite each other** (second-resolution keys) **[verified on kind, 26.10.2]**. "Backup now" is serialized.
- **A binlog-archive failure pauses all backups [verified on kind, 26.10.2].** An unreachable PITR endpoint left `BinlogsArchived` at True for about 5 min. Then the MariaDB went `Ready=False/ArchiveBinlogsError`, which paused every PhysicalBackup of the cluster: cron, pruning and timeouts. It cleared only at the next 10-minute tick after S3 recovered. An S3 black hole is therefore invisible for up to about 15 min, and then stops physical backups too.
- **RPO depends on write volume [verified on kind, 26.10.2].** Archival runs every 10 min and never includes the active binlog (§3.5 item 7, Q8).
- **PITR archive.** It has no documented binlog retention, so a bucket lifecycle rule is needed. A manual `PURGE BINARY LOGS` breaks the archive, so raw SQL is never exposed. SSE-C is not used by default. The prefix must be empty at first archival **[verified on kind, 26.10.2]**, which the Id-keyed prefix guarantees.
- **A missing bucket was not testable**: SeaweedFS auto-created it on PUT. Real S3 or MinIO would presumably fail the Job like the bad-credentials case **[verify]**.
- **Galera full outage** can need a human, and forcing the wrong pod loses data. Never automate it.
- **Istio sidecars** keep Jobs from completing, and the operator never adds the opt-out **[verified on kind, 26.10.2]**. The builder sets it on every Job-producing path; whether database namespaces are injected is still **[verify]**.
- **Kyverno policies** EntKube enforces may reject operator, agent, exporter or MaxScale pods. Check against the built-in toggles in PR 3.
- **MaxScale version drift into a commercial licence.** An operator bump could move `RELATED_IMAGE_MAXSCALE` to 24.x or later, whose BSL grant does not cover 3 servers. The explicit pin and `MaxScaleImagePinTests` (§3.3.8) close that path. 23.08's end-of-support date is unknown **[verify]**.

### 11.2 EntKube-side
- **Shared files touched:** `CnpgService.GetDatabaseBindingsAsync` (an Include), `DatabaseService`, `AppSecretSyncService`, `ConnectivityGraphService`, `OperationsAdvisor`, `PortalServiceScopeService`, `AppGovernanceService`, `VaultService`, `BackupService`/`BackupBundle`, `BootstrapRunnerService`, `BlueprintFromClusterService`, the exec verb files, `PrometheusService` (metrics only; diagnosis reuses an existing method), `ComponentCatalog` / `ComponentLifecycleService` (the CRD uninstall guard), and possibly `IClusterClient`'s apply (strict field validation).
  - Each touch is additive and covered by an existing test, except the uninstall guard and strict validation, which are new behaviour and get their own tests.
  - Merge conflicts with other work in these files must be re-tested, not just resolved: a clean merge is not a working merge.
- **`DatabaseEngineParityTests` checks wiring, not correctness.** It proves an arm exists, not that the port is right. `ConnectivityGraphDatabasePortTests` pins the actual values.
- **Cost:** per cluster, 3 MariaDB pods, 2 MaxScale pods (plus 100Mi PVCs), and two exporter Deployments, all in unattributed cost.
- **The cluster name cap is now 28 characters** (§5.1). Users may find it short; it follows from operator Job naming and the 63-character label limit.

### 11.3 CNPG/Mongo defects found while surveying (not fixed here; MariaDB avoids each by construction)
- **CNPG:**
  - `MajorUpgradeAsync` is unsafe.
  - `BuildRestoreManifest` drops HA settings.
  - Status and backup history are recorded only on page view, so the advisor is wrong for unvisited clusters (Mongo too).
  - `DatabaseMonitoringPanel` reads the tenant off the row and has no namespace in its selector.
  - Operator alias mismatch between `DatabaseService` and `CreateClusterAsync`.
  - Rotation ALTERs before the vault write, with no rollback.
  - `DeleteClusterAsync` leaves Secrets and binding keys behind.
  - The primary falls back to `{cluster}-1`.
- **Mongo:**
  - Scheduled archives cannot be restored or pruned (name/key mismatch).
  - The S3 prune Job YAML is malformed and its failure is swallowed.
  - Stale Running backups are marked Completed.
  - mongosh targets `{name}-0`.
  - The DatabaseTab rotate does not re-sync.
  - `ExternalUri` is stored in plaintext.
  - The Atlas URI Secret is never deleted.
- **Shared:**
  - `EnsureStorageSecretsInK8sAsync` re-points a shared StorageLink's sync target.
  - The connectivity default arm (`ConnectivityGraphService.cs:211`) labels unknown engines as PostgreSQL:5432. PR 5 fixes this for all engines.
  - `ServiceInstanceExistsAsync` has no tenant predicate. PR 11 adds it for the new arm.
  - `DeploymentImportService.DetectPostgres` classifies any app with generic `DB_*`/`PORT` keys as Postgres regardless of port.

These defects are listed as found by the survey; they were not individually re-verified in the verification pass except where a line number or name is cited above. Each should become its own PR against CNPG or Mongo.

---

## 12. Open decisions (each with a recommended answer)

1. **Engine scope:** MariaDB only now, with Oracle MySQL as a later separate engine?
   **Decided [user decision, 2026-11-20]: MariaDB only now, as "MariaDB (MySQL-compatible)"; Oracle MySQL later as a separate engine** (most likely on PXC, on the same seams).
2. **Default topology:** semi-sync replication with PITR, or Galera?
   **Decided [user decision, 2026-11-20]: semi-sync replication + PITR by default, documented as EntKube's trade-off** against the operator's maturity labels (replication alpha; Galera its "more production-ready HA" [cited]). Galera is a later opt-in (PR 13) with the no-PITR warning.
3. **MaxScale:** off, on (GPL 23.08), or the MariaDB Enterprise operator?
   **Decided [user decision, 2026-11-20]: ON by default for HA clusters** (the earlier recommendation was off). The decision is implemented throughout §3.3 and in PR 3, with these recorded conditions:
   - MaxScale is pinned to 23.08.x, which is GPLv2+ since its BSL Change Date (2026-09-21). 24.08+ is BSL with a grant that does not cover 3 servers until 2027-09-09 (§3.3.8).
   - The 23.08 end-of-support date is unknown **[verify]**.
   - On 26.10.2 the operator reverts MaxScale's promotion on a hard failure **[refuted on kind]** (§3.3.9). Release gate G1 holds MaxScale clusters in Preview until the hard-failure test passes, and Q6 asks the user to re-confirm the default with this evidence.
4. **Community vs Enterprise operator:**
   **Recommend: community 26.10.2.** The Enterprise operator has the same kinds but a **different API group** (`enterprise.mariadb.com/v1alpha1`, not `k8s.mariadb.com`) **[cited]**. Moving to it would mean rewriting every CR's apiVersion and re-adopting resources; it is a migration, not a drop-in paid-support switch.
5. **Anti-affinity:** required with an explicit "degraded HA" opt-in, or CNPG's silent preferred?
   **Recommend: required, with explicit opt-in and an advisor finding.** Use an explicit rule on both MariaDB and MaxScale; never `antiAffinityEnabled` (§3.2, §3.3.7).
6. **Retention model:** age-only (`maxRetention`) with MaxBackups dropped?
   **Recommend: age-only, one retention per prefix.** `maxRetention` acts per prefix, not per CR **[verified on kind, 26.10.2]**, so every writer to the physical prefix shares the cluster's retention, and seeds have their own prefix. Pair it with bucket lifecycle rules for the binlog and seed prefixes. Use a per-cluster Id prefix under a shared StorageLink rather than a dedicated bucket.
7. **TLS default:** required or optional?
   **Recommend: enabled everywhere with the operator CA; `required` on by default for new clusters, with a per-cluster opt-out.** On MaxScale clusters, client TLS is required at the listener regardless (§3.3.6), so `TlsRequired` governs only direct connections there. Requiring TLS was verified compatible with the exporter **[verified on kind, 26.10.2]**; with MaxScale's server leg it is **[verify, PR 0b]**. Flipping it later rolls every pod, so it is a gated change. Clients without TLS on non-MaxScale clusters show up in the advisor rather than failing silently. cert-manager is optional, not a prerequisite.
8. **Backups required for HA clusters:**
   **Recommend: a StorageLink is required for Replication/Galera unless "no backups (not production)" is ticked.** Warn (do not refuse) when the link's endpoint resolves inside the same Kubernetes cluster. The replica template is required for every Replication cluster: without it, a diverged member crash-loops forever after failover **[verified on kind, 26.10.2]**. With "no backups", the template uses `storage.persistentVolumeClaim` if PR 0b proves it works; otherwise "no backups" is refused for Replication.
9. **Restore drills:** manual only, or scheduled on customer infrastructure?
   **Recommend: manual in PR 12; scheduled drills opt-in per cluster in PR 15,** running in the same namespace with resources capped at the source's requests.
10. **Restore-in-place:**
    **Recommend: accept "restore to new + cut over".** The physical in-place route does not exist, and delete-then-recreate risks reusing old PVCs.
11. **`cleanupPolicy`:**
    **Recommend: `Skip` everywhere; flip to `Delete` only on an explicit "drop data" tick.** The operator default is `Delete` and drops data **[verified on kind, 26.10.2]**, so this is mandatory, and a builder test asserts it.
12. **Versions offered:**
    **Recommend: 12.3 LTS (default) and 11.8 LTS only, pinned to patch tags.**
13. **Operator install scope:** cluster-wide or per-tenant namespace?
    **Recommend: cluster-wide** (chart default). The auth-delegator ClusterRoleBinding is created per **replication** cluster (none for a standalone), is **not** garbage-collected, and accumulates per uid **[verified on kind, 26.10.2]**. EntKube deletes it on cluster delete, and the catalog uninstall notes record it.
14. **Public API in the first release:**
    **Recommend: no.** PR 14 adds read endpoints, backup trigger and switchover after the UI path is proven. Use new scopes `databases:read` and `databases:write`.

### 12.1 Open questions
- **Q1 — CRD retention (open: which guard; the hazard itself is verified on kind, §A.7).** The `mariadb-operator-crds` chart cannot keep its CRDs, and uninstalling it deletes every `MariaDB` CR [cited]. Does EntKube (a) refuse uninstall while any `MariaDB` CR exists, (b) add `helm.sh/resource-policy: keep` through a post-render step, or both? Neither mechanism exists in the catalog today.
- **Q2 — On-demand backups (answered).** Use `schedule.onDemand` on the scheduled object, which is tracked in `status.lastScheduleOnDemand` and shares its history limits. It is ignored while the schedule is suspended, so then fall back to a one-shot `PhysicalBackup` with the same prefix and `maxRetention`. Serialize per cluster (§4.1 row 14).
- **Q3 — Replica-seed backups in history (answered).** Exclude them. They go to their own prefix (`mariadb/<clusterId:N>/replica-seed/`), are not recorded, and are never restore choices. Their transient objects vanish about 20 s after success, so they could not be tracked reliably anyway (§3.2, §5.3).
- **Q4 — S3 read dependency (open).** Inject `StorageBrowserService` or `StorageLinkClientFactory`, and is either lifetime compatible with a scoped `MariaDbService`? It is now needed for the object-exists check, the PITR window and the leftover report.
- **Q5 — Rotation completion signal (answered).** No Kubernetes-side signal exists [refuted on kind]. Compare the computed hash with `mysql.user.authentication_string` through the read-only exec, after annotating the User (§9.2). Rotation ships in PR 4.
- **Q6 — MaxScale default vs the 26.10.2 failover defect (new; needs the user).** Decision 3 stands. The user is asked to re-confirm it with the §3.3.9 evidence.
  **Recommend:** build MaxScale in PR 3 exactly as decided; file the upstream issue; hold MaxScale clusters in Preview behind G1. If G1 has not passed when PR 7 ships, the user chooses between shipping the default with the documented failure mode, or defaulting MaxScale off until a fixed operator is pinned.
- **Q7 — `log_slave_updates` (new).** It is off on replicas. MaxScale warns that promoting a lagging replica breaks the others, and a promoted primary's binlog lacks the history a rejoining member needs (the 1236 case).
  **Recommend:** test `log_slave_updates=ON` in `myCnf` in PR 0b, together with the G1 test. Adopt it if it does not break PITR archival or the operator's replication configuration.
- **Q8 — Bounding RPO on quiet clusters (new).** The active binlog is never archived, so RPO is 10 min plus time-to-rotate.
  **Recommend:** `max_binlog_size=64M` now; show the measured lag; evaluate the operator's `SqlJob` CR running a scheduled `FLUSH BINARY LOGS` (declarative, no exec, unverified) as a PR 15 follow-up.
- **Q9 — `root@%` (new).** Remote root exists by default, and the operator uses it over TCP.
  **Recommend:** leave it; rely on the generated password and on NetworkPolicy limiting 3306 on MariaDB pods to the operator, MaxScale, exporter, peers and Jobs.
- **Q10 — MaxScale 23.08 end of support (new, [verify]).** No official date was found; 23.08 still receives maintenance releases (23.08.15 on 2026-09-17). If support ends before 2027-09-09, decision 3 must be revisited (licence or a different proxy).

---

## Changes after verification

- **Replica recovery and scale-out.** The spec now always emits `replication.replica.bootstrapFrom.physicalBackupTemplateRef` and a suspended `<name>-replica-template` PhysicalBackup. Without it, recovery and scale-out fail. The scale-out precondition changed from "a completed PhysicalBackup exists" to "the template is configured and a replica is Ready". (The name cap changed again after the spike; see below.)
- **`schedule.onPrimaryChange` removed.** It does not exist. `schedule.immediate` is used for a first backup; on-demand mechanism was Q2.
- **No CronJob.** `GetScheduledBackupStatusAsync` reads PhysicalBackup status and owned Jobs.
- **Poller rationale.** "120 s is inside the Job history window" was wrong (history is count-limited; timed-out Jobs are deleted). The interval stays; the builder now sets Job history limits explicitly.
- **Rotation.** The watch label is `k8s.mariadb.com/watch` and is now set on creation. The "wait for Ready transition" step may never fire on success; the completion signal was Q5.
- **CRD retention (design change).** "CRDs kept on uninstall like `keepCRDs`" was wrong; the chart cannot keep them, and uninstall would delete every database. PR 1 gains a required uninstall guard (Q1).
- **MaxScale (rationale change).** Licence no longer justified leaving MaxScale off (23.08 is GPLv2+). Decision 3 was re-opened; the user has since decided ON (below).
- **Agent sidecar.** It is used by replication too, so network policy and the `system:auth-delegator` note apply to all topologies.
- **RWOP.** Replaced "create refuses RWOP classes" with PVC access-mode control plus provisioner-based detection (later narrowed further by the spike).
- **Enterprise operator** is a different API group, so it is not a drop-in escape hatch.
- **`caching_sha2_password`** downgraded from "not promised" to "not by default", with an opt-in in PR 15.
- **Alternatives table.** PXC monitoring and backup-history statements corrected; Oracle operator and MOCO claims corrected or qualified; "no operator manages both engines" narrowed to mariadb-operator.
- **Repo corrections.** `DiffOutcome.Matches` (not `Match`); CNPG's `DetectManifestDriftAsync` is private (MariaDB's now is too); method names attributed to CNPG or Mongo correctly, with new names listed; connectivity default arm is at line 211; no `AllowedBy` navigation; enums stored as int like CNPG; constructor drops `IDataServicesApi` (the S3 dependency is Q4); scrape diagnosis reuses `DiagnoseServiceMonitorScrapeAsync`; `DetectPostgres` misclassifies through its generic DB_* heuristic, not port 3306.

## Changes after the kind spike (2026-11-20) and the user's decisions

- **Decisions 1–3 recorded.** MariaDB only; replication + PITR by default as EntKube's trade-off; **MaxScale ON by default for HA clusters**.
- **MaxScale built in, not deferred.** §3.3 is new, and it lands in the cluster-lifecycle PR (PR 3):
  - a separate `MaxScale` CR plus `maxScaleRef` (the embedded field is gone), with strict apply validation;
  - endpoints moved to MaxScale (3306 rw, 3308 read), with the new `READ_PORT` key;
  - failover owned by `mariadbmon`; switchover via `primaryServer`;
  - create-time TLS with the MariaDB CA bundle and client certificate, and CA_CERT from the MaxScale bundle;
  - explicit MaxScale-only anti-affinity; 2 replicas because of its 50% PDB;
  - operator-generated MaxScale credentials that are never handed out;
  - MaxScale metrics; MaxScale columns in the data model; MaxScale advisor findings;
  - delete order and leftover cleanup;
  - adding or removing MaxScale on a live cluster refused for now.
- **MaxScale licence and version recorded, with a bump rule.** Pinned explicitly to 23.08.x, GPLv2+ by Change Date; 24.08's grant excludes 3 servers; 23.08 EOL unknown (§3.3.8).
- **Blocker recorded [refuted on kind].** The operator reverts MaxScale's hard-failure promotion on 26.10.2. Release gate G1 and Q6 are new.
- **PR 0 marked done; PR 0b added** for MaxScale combinations and the remaining **[verify]** items.
- **Rotation (Q5) settled by hash comparison.** This moves the exec verb from PR 10 to PR 4 (Read baselines 13 → 14 → 15 → 16). The exec takes the root password from the vault on stdin, because the pod env goes stale after a root rotation.
- **Q2 answered** (`schedule.onDemand`, ignored when suspended). **Q3 answered** (separate seed prefix, excluded from history; `ReplicaSeed` trigger removed).
- **Retention is per prefix.** The seed prefix is separate, and every writer to the physical prefix shares one retention.
- **Backup status maps by reason.** `Complete=True` also means failure (`JobFailed`); "MariaDB not ready" means paused, not failed; timed-out Jobs vanish without trace.
- **PITR.** Standalone refused (the operator silently no-ops). Window computed from S3 and MariaDB status (the PITR CR's only field goes stale). RPO stated as 10 min plus time-to-rotate. `max_binlog_size=64M`. An archival failure pauses all backups.
- **Restore.** Explicit `targetRecoveryTime` always; `backupContentType: Physical` for `bootstrapFrom.s3`; root value written before the CR (the operator snapshots it into `internal-<name>`).
- **Builder hardening, all from observed defaults:**
  - `cleanupPolicy: Skip` (the default `Delete` drops data);
  - utf8mb4 plus collation (the default is utf8mb3; immutable);
  - explicit `maxUserConnections` (the default is 10);
  - explicit Grant database/table/host (the default is global `*.*`);
  - no `podIndex` (a re-apply would cause a switchover);
  - no `volumeClaimTemplate` (rewritten to RWO).
- **RWOP dropped.** It hangs physical backups, scale-out and recovery.
- **Storage.** Expansion requires `allowVolumeExpansion`; completion is checked from PVC capacity (the operator can report `StorageResized=True` falsely).
- **Scale.** Leftover-PVC and orphan-Job handling; node-count precondition; scale-in deletes the PVC.
- **Restart** via `spec.podMetadata.annotations["entkube.io/restarted-at"]`.
- **Metrics.** The exporter is a separate Deployment scraped through `/probe`; select by `target`, never `pod`; `--collect.binlog_size`; ServiceMonitor `release` label set only at creation (repair on re-apply); metrics-off leaves objects; operator chart ServiceMonitors need the `release` label.
- **PDB** no longer emitted (the operator creates it). **Anti-affinity** semantics settled. **Istio**: the operator never adds the opt-out.
- **Delete leftovers.** EntKube now deletes the leaked auth-delegator ClusterRoleBinding (new `CrUid` column) and the unowned operator/MaxScale Secrets.
- **Name cap lowered from 40 to 28** (derived names 36), because the operator's `<name>-pb-scaleout-<ts>` Job names add 27 characters.
- **Security additions:** unsalted `rootPasswordHash`; plaintext passwords on the MaxScale PVC; in-pod argv exposure by probes and mariadb-backup; `root@%` (Q9); the operator builds SQL without escaping (§9.6).
- **New open questions Q6–Q10.** Q1 (CRD uninstall) and Q4 remain open.

---

## §A Verified operator facts (kind spike, 2026-11-20, mariadb-operator 26.10.2, MariaDB 12.3.3)

Environment:
- kind with 3 workers and a control plane;
- `local-path` StorageClass, which cannot expand volumes;
- SeaweedFS-backed S3 at `minio.s3:9000`, bucket `backups`; it auto-creates missing buckets;
- kindnet CNI, whose NetworkPolicy does not cut established connections;
- no Prometheus, no metrics-server.

Engine: MariaDB `12.3.3-MariaDB-ubu2404`, plus `11.8.9-MariaDB-ubu2404` for the `caching_sha2_password` check. MaxScale 23.08.13 (`mariadb/maxscale:23.08.13-2`).

Spike files are under the session scratchpad: `spike-repl/`, `spike/maxscale/` (the `status-*.log` timelines), `spike-db/` (00–15 YAML, `login.sh`), `spike-db-scripts/sha2hash.py`, `spike-backup/m/`, `spike/spike-metrics/` (probe outputs, observed ServiceMonitor), and the operator source at tag 26.10.2 (`mdbsrc/src`, `spike-backup/src/mariadb-operator-26.10.2`).

Status key:
- **verified**: observed.
- **refuted**: the expected behaviour did not happen.
- **inconclusive**: tried but not settled.

### A.1 Replication lifecycle without MaxScale (namespace `spike-repl`)
- **Q1 — CR status and conditions — verified.**
  - Status fields: `status.currentPrimary`, `currentPrimaryPodIndex`, `currentPrimaryFailingSince` (set while the primary is not ready), `replicas` (lags spec during a scale), `scaleOutInitialIndex`, `replication.roles{pod: Primary|Replica|Unknown}`, `replication.replicas{pod:{gtidCurrentPos, gtidIOPos, lastIOErrno, lastIOError, lastSQLErrno, lastSQLError, lastErrorTransitionTime, secondsBehindMaster, slaveIORunning, slaveSQLRunning, usingGtid}}`, `replication.replicaToRecover`, `tls.{caBundle[],serverCert,clientCert}`, `defaultVersion`, `rootPasswordHash`. No `observedGeneration` anywhere.
  - Condition types (each appears only once its event first happens): `Ready`, `Updated`, `ReplicationConfigured`, `PrimarySwitched`, `ReplicaRecovered`, `ScaledOut`, `StorageResized`.
  - `Ready` reasons: `StatefulSetNotReady`, `StatefulSetReady`, `SwitchPrimary`, `ReplicaRecovering`, `ScalingOut`, `ScaleOutError`, `PendingUpdate`, `Updating`, `ResizingStorage`, `Failed`.
  - Ready came about 37 s after apply. A switchover and a failover show the same `PrimarySwitched` condition; only the Events differ.
  - Events: `PrimarySwitching`, `PrimaryNewSync`, `PrimaryNew`, `ReplicaConn`, `PrimaryLock`, `PrimaryReadonly`, `ReplicaSync`, `PrimaryToReplica`, `SemiSyncEnabled`/`Disabled`, `PrimarySwitched`, `Maintenance`, `MariaDBScaleOutError`.

  Evidence: `sw-watch.json` rv 5127 `Ready False/SwitchPrimary "Switching primary to 'repl-1'"` → rv 5141 `Ready True/StatefulSetReady`; `api/v1alpha1/condition_types.go`.
- **Q2 — Services — verified.**
  - `<n>` (all pods, 3306).
  - `<n>-primary` (adds `statefulset.kubernetes.io/pod-name=<currentPrimary>`, rewritten on failover and switchover).
  - `<n>-secondary` (no selector; operator-managed EndpointSlice with replicas only; a broken replica listed `ready=false`).
  - `<n>-internal` (headless, `publishNotReadyAddresses`, ports 3306/5555/5566; the StatefulSet serviceName).
  - `<n>-metrics` (9104, the exporter Deployment).
  - The `-primary` EndpointSlice switched within 2 s in run 1.

  Evidence: `watch.log "11:09:53 primEP=repl-0:true" → "11:09:55 primEP=repl-1:true"`.
- **Q3 — Pod/PVC labels — verified.**
  - Pods `<n>-<i>` (StatefulSet `Parallel`, `OnDelete`), with labels `app.kubernetes.io/instance=<n>`, `app.kubernetes.io/name=mariadb`, `k8s.mariadb.com/role=primary|replica` (live), `statefulset.kubernetes.io/pod-name`, `apps.kubernetes.io/pod-index`.
  - PVCs `storage-<n>-<i>`, labelled `pvc.k8s.mariadb.com/role=storage`.
  - `persistentVolumeClaimRetentionPolicy` is Retain/Retain. The StatefulSet itself has no labels.

  Evidence: `kubectl get pods --show-labels`; the StatefulSet spec.
- **Q4 — PDB — verified.** Created by default for replication with `spec.podDisruptionBudget` unset: `<n>`, `minAvailable: 50%`, selector instance+name, owned by the MariaDB. With 3 pods, 1 disruption is allowed. A standalone was not checked. Evidence: `kubectl get pdb repl -o yaml`.
- **Q5 — Anti-affinity — verified.**
  - `antiAffinityEnabled: true` gives a **required** rule on `kubernetes.io/hostname` (matchExpressions on `instance`), and the webhook writes it back into spec.
  - An explicit `podAntiAffinity` passes through verbatim.
  - Scaling beyond the schedulable nodes leaves `pb-init` Pending and the CR in `ScalingOut`.

  Evidence: `nt` StatefulSet affinity; `"3 node(s) didn't match pod anti-affinity rules"`.
- **Q6 — Root password env and Secret — verified.**
  - `MARIADB_ROOT_PASSWORD` comes from the secretKeyRef in the init, mariadb and agent containers; `MYSQL_PWD` is not set.
  - The exec form `MYSQL_PWD="$MARIADB_ROOT_PASSWORD" exec mariadb -uroot --batch --skip-column-names` works (exit 0).
  - A missing Secret leaves the CR at `Ready=False/Failed "Error reconciling Secret…"`, and it self-heals about 37 s after the Secret appears.
  - `generate: true` creates the Secret with no labels or owner.
  - `rootPasswordSecretKeyRef` is immutable.

  Evidence: StatefulSet env dump; `"spec.rootPasswordSecretKeyRef: Invalid value: … field is immutable"`.
- **Q7 — `spec.env` passthrough — verified.** Entries reach the mariadb, init and agent containers. Evidence: `ENTKUBE_SPIKE_MARKER` in all three.
- **Q8 — Rolling restart — verified.**
  - There is no restart annotation. Patching `spec.podMetadata.annotations["entkube.io/restarted-at"]` (or any pod-template field) gives `Updated=False/PendingUpdate` → `Updating`.
  - The rollout went replicas, then a switchover, then the primary; about 1 min 41 s for 3 pods.
  - Writes were locked for about 10 s (PrimaryLock 11:25:29.79 → PrimaryToReplica 11:25:39.84).
  - `kubectl rollout restart` does not apply.

  Evidence: pod restarts repl-2 11:24:24, repl-1 11:25:00, repl-0 11:25:41.
- **Q9 — Failover — verified.**
  - Deleting the primary pod: run 1 switched in about 2 s; run 2 took about 11 s (Error 1948, then a 10 s `syncTimeout`). The candidate was the Ready replica with the highest `gtid_current_pos`, the lowest index on a tie.
  - The operator rewrites `spec.replication.primary.podIndex`.
  - The old primary rejoined with IO error 1236 in run 1 and was re-seeded through the template (healthy about 33 s after failover); in run 2 it rejoined cleanly.
  - **Without a template (`nt`), the old primary crash-looped permanently** (startup probe HTTP 500, `lastIOErrno 1236`, role `Unknown`).
  - No data loss in any run.

  Evidence: operator log `"replica-recovery Creating PhysicalBackup repl-pb-recovery"`; `nt-0 RESTARTS 3`.
- **Q10 — Switchover via `podIndex` — verified.**
  - The patch works. Sequence: PrimaryLock → PrimaryReadonly → ReplicaSync → PrimaryNew → ReplicaConn → PrimaryToReplica → SemiSync events → PrimarySwitched. About 80 ms, no restarts.
  - Completion is `PrimarySwitched=True "Switchover complete"`.
  - The operator also defaults `spec.replication` fields: agent, initContainer, `replPasswordSecretKeyRef {generate:true}`, `syncTimeout 10s`, `gtid CurrentPos`, `standaloneProbes false`, `autoFailoverDelay 0s`.

  Evidence: events 11:11:16.318 → 11:11:16.397.
- **Q11 — Scale-out/in — verified.**
  - With the template, 3 → 4 took about 70 s through `<n>-pb-scaleout` and `<n>-3-pb-init`. Transient objects are deleted; the S3 objects remain.
  - A cancel left an orphan Job and PVC behind while reporting `ScaledOut=True`; the Job later ran on its own.
  - The next scale-out failed with `"storage PVCs already exist"`. Scale-in retains the PVC.
  - Without a template: `ScaleOutError "replica datasource not found"`. The webhook rejects `recovery.enabled` without `bootstrapFrom`.
  - `pvcRetentionPolicy.whenScaled: Delete` is accepted but was not exercised.

  Evidence: `so3-watch.json`; the Warning `MariaDBScaleOutError`.
- **Q12 — Agent and auth — verified.**
  - The agent runs `agent replication --addr=:5555 --probe-addr=:5566 --kubernetes-auth …`, and the mariadb container's probes are HTTP to 5566.
  - The operator creates a SA, Role and RoleBinding `<n>`, plus a ClusterRoleBinding `<n>-<ns>-<uid8>:auth-delegator` whose ownerReference is invalid, so it is **never removed** (`OwnerRefInvalidNamespace`). A new one appears per re-create. A standalone has none.

  Evidence: `nt-spike-repl-56680d88:auth-delegator` still present after the delete.
- **Q13 — Resize on a non-expandable class — verified.**
  - The PVC patch is forbidden, and the CR goes `Ready=False/ResizingStorage`, then `Failed`, with no Event. Shrinking is rejected.
  - Setting `resizeInUseVolumes: false` recreates the StatefulSet and reports `StorageResized=True` **while every PVC stays 1Gi**.

  Evidence: operator log `"only dynamically provisioned pvc can be resized…"`; PVC REQ/CAP 1Gi.
- **Q14 — RWOP — verified.**
  - The default is RWO. With `storage.size` set, the webhook rewrites the template to RWO, dropping RWOP. RWOP sticks only without a size.
  - **A physical backup against RWOP stays Pending** (the Job mounts the live pod's PVC), and the PhysicalBackup only says `JobRunning`.

  Evidence: `"PersistentVolumeClaim with ReadWriteOncePod access mode already in-use"`.
- **Q15 — Delete leftovers — verified.**
  - No finalizer. The StatefulSet, pods, Services, EndpointSlices, PDB, ConfigMaps, SA/Role/RoleBinding and TLS Secrets are garbage-collected.
  - Left behind: PVCs; `<n>-repl-password`; `<n>-metrics-password`; the user's root Secret; the ClusterRoleBinding; S3 seeds; orphan pb-init Jobs/PVCs.

  Evidence: the listing 30 s after `kubectl delete mariadb nt`.
- **Other surprises — verified.**
  - The exporter is a separate Deployment (`prom/mysqld-exporter:v0.15.1`).
  - The default image is `mariadb:12.3.3`, and `mariadb:12.3` ran fine.
  - Self-signed TLS: a 3-year CA, about 90-day leaves.
  - Service-link env vars are injected (`enableServiceLinks` is on).

### A.2 MaxScale in front of semi-sync replication (namespace `spike-maxscale`, MaxScale 23.08.13)
- **Q0 — Attachment — verified.** Only a separate `MaxScale` CR plus `MariaDB.spec.maxScaleRef` works. `spec.maxScale` does not exist; a strict apply rejects it, and a non-strict apply drops it silently. Evidence: `kubectl explain mariadb.spec.maxScale` → `field "maxScale" does not exist`.
- **Q1a — Default image — verified.** `RELATED_IMAGE_MAXSCALE=mariadb/maxscale:23.08.13-2`; exporter `mariadb/maxscale-prometheus-exporter-ubi:v0.0.1`; binary 23.08.13. 23.08.15 (2026-09-17) exists but is not pinned by the operator. Evidence: `mariadb-operator-env` ConfigMap; `maxscale --version`.
- **Q1b — Licence — verified.** `LICENSE.TXT`: BSL 1.1, grant "less than three server instances in production", Change Date 2026-09-21, Change License GPLv2+, so 23.08 is GPL today. The RPM still says BSL, and the image has no licence labels. 24.08's `LICENSE2408.TXT` has the same grant with Change Date 2027-09-09, so EntKube's 3-server default is not covered. Evidence: `rpm -qi maxscale`; the LICENSE file lines.
- **Q1c — 23.08 EOL — inconclusive.** No official date was found. The policy PDFs list 22.08 (EOL 2026-01-01) and 23.02 (EOL 2027-01-01). 23.08 still gets releases (23.08.14 on 2026-06-15, 23.08.15 on 2026-09-17).
- **Q2 — Services — verified.** `<mxs>` (`admin` 8989 plus one port per listener, e.g. `rw-router-listener` 3306, `rconn-master-router-listener` 3307, `rconn-slave-router-listener` 3308); `<mxs>-gui` (only with `guiEnabled`; selects pod `-0`); `<mxs>-internal` (headless); `<mxs>-metrics` (9105). With `services` omitted, only readwritesplit on 3306. **8989 is on the app-facing Service.** No `Connection` CR is created. Evidence: `kubectl get svc -o wide`; 3307 → primary, 3308 → replica.
- **Q3a — Failover owner — verified.** `mariadbmon` owns failover; the operator's pod-replication failover is disabled while `maxScaleRef` is set (`pod_replication_controller.go:142`). The replication reconciler still runs and takes the primary from MaxScale's API, falling back to `status.currentPrimaryPodIndex`. `podIndex` is not updated; `status.currentPrimary` follows MaxScale.
- **Q3b — Primary pod delete (returns at once) — verified.**
  - No failover (within the 4-tick failcount).
  - Open sessions are closed (`ERROR 2013`/`2006`).
  - New connections could not write for about 13 s (semi-sync timeout 10 s; `Connect_Retry` 60 s).
  - Semi-sync was off for about 47 s.
  - MariaDB `Ready=False` for about 66 s.

  Evidence: `status-failover.log`; MaxScale log 11:13:19–11:14:18.
- **Q3c — Hard primary failure — refuted.**
  - MaxScale started failover at +14 s; `STOP SLAVE` hung for about 50 s; failover was "performed" at about +68 s.
  - **The operator then reconfigured the promoted node as a replica of the dead primary within 3 s and re-enabled `read_only`.**
  - The cluster stayed write-unavailable for the whole isolation (3 min in run 2).
  - Acknowledged writes were lost (1 in run 1; 19 in run 2, ids 5490–5508).
  - Errant GTIDs led to 1236. Another replica was left `read_only=OFF`, replicating as the MaxScale monitor user.
  - Reproduced with 2 and with 1 MaxScale replicas.

  Evidence: `status-failover2/3/4.log`; operator `"MaxScale primary server changed"` → `"Configuring replica mdb-1"`.
- **Q3d — Switchover transparency — verified.** `spec.primaryServer` patch: a persistent session saw 0 errors and a 2.2 s stall; a per-connection loop saw a 2.2 s stall and no failures. Evidence: gap 11:26:53.551 → 11:26:55.765.
- **Q4 — Switchover trigger — verified.** `MaxScale.spec.primaryServer=<pod>` gives `PrimarySwitched=False/SwitchPrimary` → `True "Switchover complete"` and event `MaxScalePrimaryServerChanged`. MariaDB status and the `-primary` selector follow in about 2 s; MariaDB `podIndex` is ignored (`switchover.go:34`). A rolling update also switches over through MaxScale.
- **Q5 — Replicas, sync, PDB — verified.**
  - `replicas: 2` works.
  - Config sync is set, but `mysql.maxscale_config` stays empty (config goes over REST); `majority_of_all`.
  - PDB `<mxs>` with `minAvailable 50%`: at 1 replica, 0 disruptions are allowed.
  - Per-user max connections is 30 × replicas.
  - Default affinity is empty. `antiAffinityEnabled` is required against MaxScale **and** MariaDB instances (needs 5 nodes) and is immutable.
  - The GUI targets pod `-0`. The ServiceMonitor has one endpoint per pod.
- **Q6 — Secrets and users — verified.**
  - Secrets `<mxs>-{admin,client,server,monitor,sync,metrics}`, with no owner and no labels; grants as listed in §3.3.5; the monitor user is also the `replication_user`.
  - User/Grant CRs `<mxs>-{client,server,monitor,sync}`.
  - No passwords in any CR or ConfigMap, but **plaintext server and monitor passwords on the MaxScale PVC** (0644; SHA matches the Secrets).
  - The Secrets survive MaxScale deletion.
- **Q7 — Metrics — verified.** Exporter Deployment `<mxs>-metrics` (port 9105), and a ServiceMonitor with a `/probe` endpoint per pod (`target=<mxs>-N.<mxs>-internal…:8989`). Metric names: `maxscale_up`, `maxscale_server_state{id,role}`, `maxscale_service_connections`, and others. No `release` label was set in the spike. Evidence: 345-line probe output incl. `maxscale_server_state{id="mdb-0",role="Master"} 1`.
- **Q8 — TLS defaults — refuted (TLS is not on by default).**
  - With MariaDB `tls.required=false`, MaxScale TLS is off on both legs, and a default client connected in plaintext.
  - `tls.enabled` alone fails reconcile; with `serverCASecretRef=<mdb>-ca-bundle` and `serverCertSecretRef=<mdb>-client-cert` the operator creates `<mxs>-ca`, `-ca-bundle`, `-listener-cert` and `-admin-cert`. The listener and servers then run TLSv1.3, verify-full works against `<mxs>-ca-bundle`, and plaintext is refused (`ERROR 1045`).
  - Enabling TLS on an existing MaxScale does not convert existing listeners.
  - MaxScale → MariaDB does not verify the peer.
- **Q9 — Footprint — verified.** `memory.current` 24.7 MiB, peak 57.7 MiB (67 MiB on a fresh replica); exporter 22.8 MiB; `query_classifier_cache_size` = 15% of the limit; a 100Mi PVC per pod; CPU not measured.
- **Q10 — Removing MaxScale — verified.**
  - The finalizer removes the StatefulSet, PVCs, PDB, Services, exporter, ServiceMonitor, TLS Secrets, configs and User/Grant CRs, and drops the SQL users.
  - The six password Secrets remain.
  - The MariaDB keeps serving on `-primary`/`-secondary`, but while `maxScaleRef` dangles it shows `Ready=False/Failed`, and the operator's failover stays off.
  - **A replica replicating as `<mxs>-monitor` broke on reconnect (`Access denied`) and was not repaired** by `maxScaleRef: null` or a pod restart; it needed a manual `CHANGE MASTER TO MASTER_USER='repl'`.
- **Q-extra — verified.** `log_slave_updates` is OFF (MaxScale warns on every failover); replicas' `Connect_Retry` is 60 s; `rpl_semi_sync_master_timeout` is 10000; `MYSQL_PWD` works.

### A.3 Databases, users, grants and credentials (namespace `spike-db`; MariaDB 12.3.3 and 11.8.9)
- **Q1a — Success condition — verified.** One condition, `Ready/True/Created/"Created"`, with no `observedGeneration`. Grant status adds `currentPrivileges`. Connection: `Ready/True/Healthy`. Every MariaDB gets an operator-owned User `<n>-mariadb-sys` and Grant `<n>-mariadb-sys-global-priv`.
- **Q1b — Failure condition — verified.** `Ready/False/Failed`, and the message carries the cause:
  - a missing Secret or key;
  - `Error 1064`, `Error 1133`;
  - "MariaDB not found";
  - `Error 1372`.

  Retries use exponential backoff; a missing-Secret failure recovered in about 2 s. A CR that never succeeded has no finalizer.
- **Q2 — cleanupPolicy — verified.** The **default is `Delete`** (`finalizer.go:88`). Delete runs `DROP DATABASE` (even with data), `DROP USER` and `REVOKE`; `Skip` keeps everything. Adoption with `Skip` keeps the data and ALTERs the user to the Secret's password.
- **Q3a — passwordSecretKeyRef — verified.** The Secret is always read from the CR's namespace. `CREATE USER IF NOT EXISTS … IDENTIFIED BY '<pw>'`, or ALTER, runs on **every** reconcile; with no password ref the account is `ACCOUNT LOCK PASSWORD EXPIRE`. The password is interpolated **unescaped**, so a quote breaks the statement. The account uses `mysql_native_password` (hash `*`+UPPER(HEX(SHA1(SHA1(pw))))).
- **Q3b — passwordHashSecretKeyRef — verified.** It works with the native hash; only one of password, hash or plugin is allowed (webhook).
- **Q3c — caching_sha2_password — verified.**
  - The plugin is NOT INSTALLED by default on 12.3.3 or 11.8.9; `plugin_load_add = auth_mysql_sha2` activates it after a restart.
  - The plugin argument must be the 70-character `$A$005$…` hash; plaintext gives `Error 1372`. A locally computed hash matched the server byte for byte.
  - The 12.3 client needs a CA or `--skip-ssl`. The MySQL 8.4 client works with `REQUIRED`, or with `DISABLED --get-server-public-key`.
  - Rotation through a watched Secret applied at once on 12.3 and 11.8.
- **Q4a — Rotation applies — verified.** With the watch label, the new password worked within 1 s and the old one gave `ERROR 1045`. A `Connection` Secret followed within the same second.
- **Q4b — Rotation signal on the User — refuted.** The CR was byte-identical, there are no Events for User/Database/Grant, and nothing is logged at INFO. `password_last_changed` bumps on every reconcile. The reliable signal is `mysql.user.authentication_string` equal to the locally computed hash (`*1D935E4E…` matched).
- **Q4c — Without the watch label — verified.** The change was not applied 121 s later. An annotation on the User applied it within about 8 s. The requeue is `--requeue-sql=10h` plus up to 1 h of offset, and `requeueInterval` also gets the offset; a manual REVOKE was not re-granted within 90 s until poked.
- **Q5 — Connection CR — verified.** Its default Secret has the single key `dsn`, with a `tls=` that names an operator-internal config, so it is unusable by apps. A `secretTemplate` renders `DATABASE_URL`, `HOST`, `PORT`, `USERNAME`, `PASSWORD` and `DATABASE`, follows rotations within about 1 s, and the health check gives `Ready/True/Healthy`.
- **Q6 — Grants — verified.**
  - Database-level is `ON \`db\`.*`; table-level is `ON \`db\`.\`t\``.
  - **The CRD defaults database and table to `*`, so a Grant with no database is global.** `host` defaults to `%`; `grantOption` to false.
  - Narrowing `privileges` revokes the removed ones. Database, table, username, host and grantOption are immutable. Privileges are joined unescaped.
- **Q7 — Identifiers — verified.** Only lengths are checked: Database name 80 (MariaDB allows 64), User name 80, host 255.
  - Created: `my-db`, a 64-character name, `has space`, `MixedCase`.
  - Failed: a 65-character name (`Error 1102`), a backtick or quote in the name (`Error 1064`), user `o'brien`.
- **Q8 — User/Database fields — verified.**
  - `maxUserConnections` **defaults to 10**; it is mutable; 0 means unlimited; -1 is accepted.
  - The Database defaults are `utf8`/`utf8_general_ci`, stored as utf8mb3, while the server default is utf8mb4/uca1400. Both fields are immutable. An existing database's charset is never ALTERed.
- **Q9 — Cross-namespace — inconclusive.** The source honours `mariaDbRef.namespace`, and a wrong namespace gave "MariaDB not found". A positive cross-namespace case was not run. Secrets are always read from the CR's namespace.
- **Side facts — verified.** `MARIADB_ROOT_PASSWORD` is injected and `MYSQL_PWD` works over the socket. TLS is on by default (CA 3 years, leaves about 90 days), the CA is mounted at `/etc/pki/ca.crt`, and `require_secure_transport` is 0.

### A.4 Physical backups, PITR and restore (namespace `spike-backup`)
- **Q1a — Objects — verified.**
  - Jobs only, no CronJob. Jobs are named `<pb>-<yyyyMMddHHmmss>` (UTC), linked only by ownerReferences, with no operator labels.
  - Pod annotations come from `podMetadata`. `restartPolicy: OnFailure`; `backoffLimit` 5.
  - The Job mounts the target replica's PVC and is scheduled on that node (`podAffinity` true). `PreferReplica` logged "Using replica as PhysicalBackup target".
  - A ServiceAccount is created (and a Role with PITR).
- **Q1b — Status — verified.**
  - Fields: `conditions[type=Complete]`, `lastScheduleCheckTime`, `lastScheduleTime`, `nextScheduleTime`, `lastScheduleOnDemand`, and the annotation `k8s.mariadb.com/last-gtid` (PITR only).
  - `Complete` values:
    - `True/JobComplete/"Success"`;
    - **`True/JobFailed/"Failed"`**;
    - `False/JobNotComplete/"Scheduled"`;
    - `False/JobSuspended`;
    - `False/Failed/"MariaDB not ready"` (with Warning `MariaDBNotReady`; no Job is created).
- **Q1c — History limits — verified.** Both default to 5 per PhysicalBackup. With a limit of 2, the oldest Complete Job was pruned; failed Jobs were kept up to 5.
- **Q1d — Timeout — verified.** A Job past `timeout` is deleted with its pod, with only an operator log line and no event or condition. Status falls back to `JobNotComplete`. The timeout is enforced only while the MariaDB is Ready.
- **Q2 — `schedule.immediate` — verified.** It runs once the MariaDB is Ready (40 s after creation). `nextScheduleTime` appears only after the first cron run. Missed ticks are caught up when Ready returns.
- **Q3 — onDemand vs one-shot — verified.** `onDemand` triggers within about 1 s, is tracked in `lastScheduleOnDemand`, shares naming and limits, and is **ignored while suspended**. A one-shot CR runs exactly once and keeps its own status. Both write the same key pattern into the prefix.
- **Q4 — Retention — verified.** The key is `<prefix>/physicalbackup-<ts>.xb[.zst]`, with no CR name. Each successful Job deletes **every** `physicalbackup-*` older than its own `maxRetention`, whoever wrote it. No pruning happens while paused. Same-second backups collide.
- **Q5a — PITR archival — verified.** The `PointInTimeRecovery` CR has `physicalBackupRef`, `storage`, `compression` (immutable), `archiveTimeout` (1 h) and `strictMode`; one CR per MariaDB. The **current primary's agent** archives every **10 minutes (hard-coded)** from agent start. **The active binlog is never archived.** The prefix must be empty at first use. Objects: `server-<id>/<binlog>.gz` plus `index.yaml`. Event `BinlogArchived`.
- **Q5b — PITR status — verified.** The PITR CR has only `lastRecoverableTime`, which was stale (it stayed at 11:12:14 after newer archives) because the latest backup's GTID sat in the active binlog. `MariaDB.status.pointInTimeRecovery` has `lastArchivedBinaryLog`, `lastArchivedGtid`, `lastArchivedPosition`, `lastArchivedTime`, `serverId` and `storageReadyForArchival`. Condition `BinlogsArchived`.
- **Q6a — Restore from `backupRef` — verified.** Picks the newest object ≤ the target, from any writer. `<n>-<i>-pb-init` is deleted after success. Conditions: `Initializing` → `Initialized` → `BackupRestored` → Ready. About 28 s.
- **Q6b — Restore from `s3` — verified.** Requires `backupContentType: Physical`. Picks the newest object without a target. About 31 s.
- **Q6c — PITR restore — verified.** A dropped table came back and the DROP was excluded, into a new 3-replica cluster. `ReplayBinlogs` via Job `<n>-pitr`. About 62 s in total. Strict mode with a target beyond the window fails fast with `InitError` and creates no pods.
- **Q7 — Standalone PITR — refuted (expected an error).** Silently admitted: no agent, `log_bin=0`, `lastRecoverableTime` empty forever, no event.
- **Q8 — Replica-seed backups — verified.** The transient `<n>-pb-scaleout`/`-pb-recovery` are deleted about 20 s after success. The S3 object stays in the same prefix with the same naming, so it is indistinguishable, a valid restore candidate, and pruned by the scheduled retention. The PVC template alternative was read from source and not run. Scale-in left `storage-src-3`.
- **Q9a — Bad S3 credentials — verified.** mariadb-backup runs, then the push fails. The Job fails with `BackoffLimitExceeded`, **and the pod (and with it the error text) is deleted**. The PhysicalBackup shows only `JobFailed`.
- **Q9b — Missing bucket — inconclusive.** SeaweedFS auto-created the bucket and the backup succeeded. A stray bucket `nonexistent-bucket-spike` was left on the shared S3.
- **Q9c — Unreachable PITR endpoint — verified.** The archive cycle hung about 5 min with `BinlogsArchived` still True, then the MariaDB went **`Ready=False/ArchiveBinlogsError`**. That **paused every PhysicalBackup** (cron, pruning, timeout). It cleared only at the next 10-minute tick after the fix.
- **Q10 — Root password after a physical restore — verified.** The backup-time root password comes back. With the current value in the Secret, login was denied (1045) and the probes restart-looped. Fixing the Secret afterwards left `internal-<n>` with the creation-time value, and Ready flapped. **The archived value must be written before the CR.**
- **Other surprises — verified.**
  - `status.rootPasswordHash` is an unsalted SHA-256.
  - Init Job pods match the MariaDB PDB selector (`CalculateExpectedPodCountFailed`).
  - Restore and scale-out Jobs are garbage-collected on success.
  - After a root rotation via the watched Secret, the pod env `MARIADB_ROOT_PASSWORD` keeps the old value until restart, so an exec using it failed with 1045.
  - Standalone probes and mariadb-backup put the root password on argv inside the pod.

### A.5 Metrics, TLS and Galera basics (namespace `spike-metrics`)
- **1a — Exporter topology — refuted (not a sidecar).** It is a Deployment `<n>-metrics` (`prom/mysqld-exporter:v0.15.1`, port `metrics` 9104) behind Service `<n>-metrics` (selector `instance=<n>-metrics`, `name=exporter`), running in multi-target `/probe` mode. MariaDB pods have only `mariadb` and `agent`. `metrics.exporter.args` are appended.
- **1b — Exporter auth — verified.** User `<n>-metrics` (`%`, 3 connections) with `SELECT, PROCESS, BINLOG MONITOR, SLAVE MONITOR ON *.*`. It reads `exporter.cnf` from `<n>-metrics-config` and uses TLS with the client certificate, which works under `tls.required=true`.
- **1c — Metric names — verified.** As listed in §9.5. Binlog size needs `--collect.binlog_size`. `mysql_up` is 0 on an unreachable target, with HTTP 200.
- **1d — Series labels — inconclusive.** Raw series have no namespace or pod labels. The ServiceMonitor relabels `target`=pod, `instance`=pod FQDN and the static `role`. The Prometheus-added labels (`pod` = the exporter pod) were not observed, because there was no Prometheus.
- **1e — Role follows switchover — verified.** The operator rewrote the ServiceMonitor endpoints' `role` across two switchovers (without MaxScale).
- **2 — ServiceMonitor — verified.**
  - It is created whenever `metrics.enabled`, labelled only `release: <prometheusRelease>` (none if unset).
  - The selector is the exporter's; there is one endpoint per pod.
  - **Labels and `jobLabel` are set only at creation**, while interval changes apply.
  - Turning metrics off removes nothing.
  - A deleted ServiceMonitor is recreated only on the next MariaDB reconcile.
- **3 — Operator metrics — verified.** Port `metrics` 8080. Services `mariadb-operator-metrics`, `-webhook-metrics` and `-cert-controller-metrics`. With chart `metrics.enabled`, three ServiceMonitors appear with **no `release` label** (set `metrics.serviceMonitor.additionalLabels.release`). Only controller-runtime metrics.
- **4a — TLS defaults — verified.** `tls.enabled` defaults to true and `required` to false (`require_secure_transport = false`, `tls_version = TLSv1.3`). `<n>-ca` (3 years, with the private key) issues both the server certificate (about 90 days, SANs incl. `-primary`/`-secondary`/`*.<n>-internal`/`localhost`) and the client certificate. `status.tls` reports validity. Galera SST TLS is off by default.
- **4b — CA for clients — verified.** `<n>-ca-bundle` (`ca.crt`), owned by the MariaDB. Never `<n>-ca`.
- **4c — TLS and plaintext clients — verified.** With `required=false`, `--skip-ssl` connects in plaintext. With `required=true`, plaintext TCP gets `ERROR 3159`; **the unix socket is allowed without TLS**. Flipping `required` rolls every pod with a switchover. The MariaDB 11.4+ client's no-CA verification succeeds via the password.
- **5a — Galera odd count — verified.** A CRD CEL rule; replicas 2 and 4 are rejected unless `replicasAllowEvenNumber`.
- **5b — Galera full outage — verified.** After a forced delete of all pods, recovery was automatic in about 4 min. Signals: `Ready=False/GaleraNotReady`, `GaleraReady=False`, event `GaleraClusterNotHealthy`, `status.galeraRecovery.{state,recovered,bootstrap,podsRestarted}`, per-pod `<pod>-recovery` Jobs. `galeraRecovery` stays in status after recovery. The all-seqno −1 case was not reproduced.
- **5c — Force bootstrap — verified (field).** `spec.galera.recovery.forceClusterBootstrapInPod`; not exercised. Galera defaults: agent 5555/5566, SST mariabackup, a 100Mi config PVC per pod, `-internal` ports 3306/5555/5566/4567/4568/4444, PDB 50%.
- **6 — Istio — refuted (expected injection handling).** The operator adds no `sidecar.istio.io/inject` anywhere; its docs tell users to add it (Galera Jobs via `galera.recovery.job.metadata`/`galera.initJob.metadata`, other Jobs via `podMetadata`).
- **Side facts — verified.** Five Services and the agent in replication. Generated Secrets: `<n>-root` (when generated), `<n>-repl-password`, `internal-<n>`, `<n>-metrics-password`, `<n>-metrics-config`, plus four TLS Secrets. PDB 50%. **`root@%` and `root@localhost` both exist.**

### A.6 Reference manifests
These are the working YAML from the spike, with every credential value replaced by `<redacted>`. Spike-only values are marked; where EntKube's builder differs, a comment says so. Test-only objects (the writer pod, the isolating NetworkPolicy, the SQL client pod) are omitted; they are in the spike directories.

**A.6.1 Replication cluster with replica template, no MaxScale (`spike-repl`)**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: repl-root
  namespace: spike-repl
  labels:
    k8s.mariadb.com/watch: ""
type: Opaque
stringData:
  password: "<redacted>"
---
apiVersion: v1
kind: Secret
metadata:
  name: repl-s3-credentials
  namespace: spike-repl
type: Opaque
stringData:
  access-key-id: "<redacted>"
  secret-access-key: "<redacted>"
---
# Replica template: used ONLY as a template for scale-out and recovery seeds.
# EntKube: own prefix mariadb/<clusterId:N>/replica-seed/, maxRetention 168h, compression zstd.
apiVersion: k8s.mariadb.com/v1alpha1
kind: PhysicalBackup
metadata:
  name: repl-replica-template
  namespace: spike-repl
spec:
  mariaDbRef:
    name: repl
    waitForIt: false
  schedule:
    suspend: true
  target: PreferReplica
  compression: gzip
  timeout: 1h
  podAffinity: true
  successfulJobsHistoryLimit: 3
  failedJobsHistoryLimit: 3
  podMetadata:
    annotations:
      sidecar.istio.io/inject: "false"
  storage:
    s3:
      bucket: backups
      prefix: spike-repl/replica-seed
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef:
        name: repl-s3-credentials
        key: access-key-id
      secretAccessKeySecretKeyRef:
        name: repl-s3-credentials
        key: secret-access-key
      tls:
        enabled: false
---
# Ready in ~37 s. Later spike patches: podIndex (switchover), replicas 4/3, podMetadata restarted-at,
# tolerations (spike-only), storage.size 2Gi + resizeInUseVolumes false (resize test).
# EntKube differences: patch-pinned image; no spec.env; maxScaleRef + MaxScale CR by default;
# metrics.exporter.args/serviceMonitor as in §3.2; max_binlog_size in myCnf.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: repl
  namespace: spike-repl
spec:
  image: mariadb:12.3
  replicas: 3
  rootPasswordSecretKeyRef:
    name: repl-root
    key: password
  storage:
    size: 1Gi
    storageClassName: standard
    resizeInUseVolumes: true
  replication:
    enabled: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    gtidStrictMode: true
    primary:
      autoFailover: true
    replica:
      bootstrapFrom:
        physicalBackupTemplateRef:
          name: repl-replica-template
      recovery:
        enabled: true
  updateStrategy:
    type: ReplicasFirstPrimaryLast
    mariadbAutoUpgradeEnabled: true
  affinity:
    podAntiAffinity:
      requiredDuringSchedulingIgnoredDuringExecution:
        - topologyKey: kubernetes.io/hostname
          labelSelector:
            matchLabels:
              app.kubernetes.io/instance: repl
              app.kubernetes.io/name: mariadb
  tls:
    enabled: true
  metrics:
    enabled: true
  env:                                   # spike-only passthrough probe; EntKube emits no spec.env
    - name: ENTKUBE_SPIKE_MARKER
      value: "spec-env-passthrough"
  podMetadata:
    annotations:
      sidecar.istio.io/inject: "false"
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
    limits:
      memory: 512Mi
  myCnf: |
    [mariadb]
    innodb_buffer_pool_size=128M
    binlog_format=ROW
    sync_binlog=1
    innodb_flush_log_at_trx_commit=1
    max_connections=500
```

**A.6.2 MaxScale in front of a 3-replica semi-sync cluster (`spike-maxscale`), final working form (Ready, TLS on both legs)**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mdb-root
  namespace: spike-maxscale
  labels:
    k8s.mariadb.com/watch: ""
stringData:
  password: "<redacted>"
---
apiVersion: v1
kind: Secret
metadata:
  name: mdb-app-credentials
  namespace: spike-maxscale
  labels:
    k8s.mariadb.com/watch: ""
stringData:
  PASSWORD: "<redacted>"
---
# EntKube: explicit podAntiAffinity instead of antiAffinityEnabled; replica template + backups as in A.6.1/A.6.4.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: mdb
  namespace: spike-maxscale
spec:
  image: mariadb:12.3
  rootPasswordSecretKeyRef:
    name: mdb-root
    key: password
  replicas: 3
  maxScaleRef:
    name: mdb-maxscale
  replication:
    enabled: true
    gtidStrictMode: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    primary:
      autoFailover: true   # ignored by the operator while maxScaleRef is set
  storage:
    size: 1Gi
  resources:
    requests: {cpu: 100m, memory: 256Mi}
    limits: {memory: 512Mi}
  affinity:
    antiAffinityEnabled: true
  metrics:
    enabled: true
  tls:
    enabled: true
---
# EntKube: image pinned explicitly (mariadb/maxscale:23.08.13-2); the 3307 listener is not emitted.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MaxScale
metadata:
  name: mdb-maxscale
  namespace: spike-maxscale
spec:
  replicas: 2
  mariaDbRef:
    name: mdb
  services:
    - name: rw-router
      router: readwritesplit
      params:
        transaction_replay: "true"
        transaction_replay_attempts: "10"
        transaction_replay_timeout: "5s"
        max_slave_connections: "255"
        max_replication_lag: "3s"
        master_accept_reads: "true"
      listener:
        port: 3306
        protocol: MariaDBProtocol
        params:
          connection_metadata: "tx_isolation=auto"
    - name: rconn-master-router
      router: readconnroute
      params:
        router_options: "master"
        max_replication_lag: "3s"
        master_accept_reads: "true"
      listener:
        port: 3307
    - name: rconn-slave-router
      router: readconnroute
      params:
        router_options: "slave"
        max_replication_lag: "3s"
      listener:
        port: 3308
  monitor:
    interval: 2s
    cooperativeMonitoring: majority_of_all
    params:
      auto_failover: "true"
      auto_rejoin: "true"
      switchover_on_low_disk_space: "true"
  admin:
    port: 8989
    guiEnabled: false
  tls:
    enabled: true                       # must be set at CREATE time for listener TLS
    serverCASecretRef:
      name: mdb-ca-bundle               # <mariadb>-ca-bundle
    serverCertSecretRef:
      name: mdb-client-cert             # <mariadb>-client-cert
  # NOT affinity.antiAffinityEnabled (immutable; also repels MariaDB pods -> needs 5 nodes)
  affinity:
    podAntiAffinity:
      requiredDuringSchedulingIgnoredDuringExecution:
        - labelSelector:
            matchLabels:
              app.kubernetes.io/instance: mdb-maxscale
              app.kubernetes.io/name: maxscale
          topologyKey: kubernetes.io/hostname
  metrics:
    enabled: true
  resources:
    requests: {cpu: 100m, memory: 128Mi}
    limits: {memory: 512Mi}
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Database
metadata: {name: app, namespace: spike-maxscale}
spec:
  mariaDbRef: {name: mdb}
  characterSet: utf8mb4
  collate: utf8mb4_general_ci
  cleanupPolicy: Skip
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: User
metadata: {name: app, namespace: spike-maxscale}
spec:
  mariaDbRef: {name: mdb}
  passwordSecretKeyRef: {name: mdb-app-credentials, key: PASSWORD}
  host: "%"
  maxUserConnections: 50
  cleanupPolicy: Skip
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Grant
metadata: {name: app, namespace: spike-maxscale}
spec:
  mariaDbRef: {name: mdb}
  privileges: ["ALL PRIVILEGES"]
  database: app
  table: "*"
  username: app
  host: "%"
  cleanupPolicy: Skip
```

**A.6.3 Databases, users, grants, hash and caching_sha2 users, Connection (`spike-db`)**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: db1-root
  namespace: spike-db
stringData:
  password: "<redacted>"
---
apiVersion: v1
kind: Secret
metadata:
  name: db1-app1-credentials
  namespace: spike-db
  labels:
    k8s.mariadb.com/watch: ""
stringData:
  PASSWORD: "<redacted>"
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: db1
  namespace: spike-db
spec:
  image: docker-registry1.mariadb.com/library/mariadb:12.3
  rootPasswordSecretKeyRef:
    name: db1-root
    key: password
  replicas: 1
  myCnf: |
    [mariadb]
    plugin_load_add = auth_mysql_sha2
  storage:
    size: 1Gi
    storageClassName: standard
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
    limits:
      memory: 512Mi
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Database
metadata:
  name: db1-app1
  namespace: spike-db
spec:
  mariaDbRef:
    name: db1
  name: app1
  characterSet: utf8mb4
  collate: utf8mb4_unicode_ci
  cleanupPolicy: Skip
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: User
metadata:
  name: db1-app1-owner
  namespace: spike-db
spec:
  mariaDbRef:
    name: db1
  name: app1_owner
  host: "%"
  passwordSecretKeyRef:
    name: db1-app1-credentials
    key: PASSWORD
  maxUserConnections: 20
  cleanupPolicy: Skip
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Grant
metadata:
  name: db1-app1-owner-all
  namespace: spike-db
spec:
  mariaDbRef:
    name: db1
  privileges:
    - "ALL PRIVILEGES"
  database: app1
  table: "*"
  username: app1_owner
  host: "%"
  grantOption: false
  cleanupPolicy: Skip
---
# Table-level grant (reader); Secret db1-app1-reader (key PASSWORD) labelled k8s.mariadb.com/watch.
# cleanupPolicy Delete was a spike test; EntKube always emits Skip.
apiVersion: k8s.mariadb.com/v1alpha1
kind: User
metadata: {name: db1-app1-reader, namespace: spike-db}
spec:
  mariaDbRef: {name: db1}
  name: app1_reader
  host: "%"
  passwordSecretKeyRef: {name: db1-app1-reader, key: PASSWORD}
  maxUserConnections: 5
  cleanupPolicy: Delete
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Grant
metadata: {name: db1-app1-reader-orders, namespace: spike-db}
spec:
  mariaDbRef: {name: db1}
  privileges: ["SELECT"]
  database: app1
  table: orders
  username: app1_reader
  host: "%"
  cleanupPolicy: Delete
---
# Native hash user; Secret db1-hash-credentials key HASH = '*'+UPPER(HEX(SHA1(SHA1(pw)))), labelled watch.
apiVersion: k8s.mariadb.com/v1alpha1
kind: User
metadata: {name: db1-hash-user, namespace: spike-db}
spec:
  mariaDbRef: {name: db1}
  name: hash_user
  host: "%"
  passwordHashSecretKeyRef: {name: db1-hash-credentials, key: HASH}
  cleanupPolicy: Delete
---
# caching_sha2_password user; Secret db1-sha2-credentials (watch label):
# PLUGIN=caching_sha2_password, AUTH_STRING=$A$005$<20 alnum salt><43 sha256-crypt chars> (70 chars).
apiVersion: k8s.mariadb.com/v1alpha1
kind: User
metadata: {name: db1-sha2-hash, namespace: spike-db}
spec:
  mariaDbRef: {name: db1}
  name: sha2_hash
  host: "%"
  passwordPlugin:
    pluginNameSecretKeyRef: {name: db1-sha2-credentials, key: PLUGIN}
    pluginArgSecretKeyRef: {name: db1-sha2-credentials, key: AUTH_STRING}
  cleanupPolicy: Delete
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: Connection
metadata: {name: db1-app1-conn-tpl, namespace: spike-db}
spec:
  mariaDbRef: {name: db1}
  username: app1_owner
  passwordSecretKeyRef: {name: db1-app1-credentials, key: PASSWORD}
  database: app1
  secretName: db1-app1-conn-tpl
  healthCheck:
    interval: 10s
  params:
    parseTime: "true"
  secretTemplate:
    key: DATABASE_URL
    format: "mysql://{{ .Username }}:{{ .Password }}@{{ .Host }}:{{ .Port }}/{{ .Database }}{{ .Params }}"
    usernameKey: USERNAME
    passwordKey: PASSWORD
    hostKey: HOST
    portKey: PORT
    databaseKey: DATABASE
    metadata:
      labels: {entkube.io/managed: "true"}
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: db3
  namespace: spike-db
spec:
  image: docker-registry1.mariadb.com/library/mariadb:11.8
  rootPasswordSecretKeyRef:
    name: db1-root
    key: password
  replicas: 1
  myCnf: |
    [mariadb]
    plugin_load_add = auth_mysql_sha2
  storage:
    size: 1Gi
    storageClassName: standard
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
    limits:
      memory: 512Mi
```

**A.6.4 Physical backups, PITR and the three restore paths (`spike-backup`)**
```yaml
apiVersion: v1
kind: Secret
metadata:
  name: src-s3-credentials
  namespace: spike-backup
type: Opaque
stringData:
  access-key-id: "<redacted>"
  secret-access-key: "<redacted>"
---
apiVersion: v1
kind: Secret
metadata:
  name: src-root
  namespace: spike-backup
  labels:
    k8s.mariadb.com/watch: ""
type: Opaque
stringData:
  password: "<redacted>"
---
# Replica-seed template (never runs on its own; the operator clones it as <name>-pb-scaleout / -pb-recovery).
# The spike shared the scheduled prefix; EntKube uses a SEPARATE prefix (mariadb/<clusterId:N>/replica-seed/).
apiVersion: k8s.mariadb.com/v1alpha1
kind: PhysicalBackup
metadata:
  name: src-replica-template
  namespace: spike-backup
spec:
  mariaDbRef:
    name: src
    waitForIt: false
  target: PreferReplica
  schedule:
    suspend: true
  compression: zstd
  maxRetention: 720h
  storage:
    s3:
      bucket: backups
      prefix: spike-backup/src/physical
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef: {name: src-s3-credentials, key: access-key-id}
      secretAccessKeySecretKeyRef: {name: src-s3-credentials, key: secret-access-key}
  resources:
    requests: {cpu: 50m, memory: 128Mi}
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: PhysicalBackup
metadata:
  name: src-scheduled
  namespace: spike-backup
spec:
  mariaDbRef:
    name: src
  target: PreferReplica
  schedule:
    cron: "*/5 * * * *"
    immediate: true
    # onDemand: "<new id>"   # "Backup now"; ignored while suspend: true
  compression: zstd
  maxRetention: 20m            # spike value; prunes EVERY physicalbackup-* in the prefix
  timeout: 1h
  successfulJobsHistoryLimit: 2
  failedJobsHistoryLimit: 2
  podMetadata:
    annotations:
      sidecar.istio.io/inject: "false"
  storage:
    s3:
      bucket: backups
      prefix: spike-backup/src/physical
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef: {name: src-s3-credentials, key: access-key-id}
      secretAccessKeySecretKeyRef: {name: src-s3-credentials, key: secret-access-key}
  resources:
    requests: {cpu: 50m, memory: 128Mi}
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: PointInTimeRecovery
metadata:
  name: src-pitr
  namespace: spike-backup
spec:
  physicalBackupRef:
    name: src-scheduled
  compression: gzip
  archiveTimeout: 1h
  strictMode: true
  storage:
    s3:
      bucket: backups
      prefix: spike-backup/src/binlog        # must be empty at first archival
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef: {name: src-s3-credentials, key: access-key-id}
      secretAccessKeySecretKeyRef: {name: src-s3-credentials, key: secret-access-key}
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: src
  namespace: spike-backup
spec:
  image: mariadb:12.3
  rootPasswordSecretKeyRef: {name: src-root, key: password}
  replicas: 3
  replication:
    enabled: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    gtidStrictMode: true
    primary:
      autoFailover: true
    replica:
      bootstrapFrom:
        physicalBackupTemplateRef:
          name: src-replica-template
      recovery:
        enabled: true
  pointInTimeRecoveryRef:
    name: src-pitr
  storage:
    size: 1Gi
  myCnf: |
    [mariadb]
    max_binlog_size=4M         # spike value; the active binlog is never archived, so size bounds RPO (EntKube: 64M)
  resources:
    requests: {cpu: 100m, memory: 256Mi}
    limits: {memory: 1Gi}
---
# One-shot on-demand backup (EntKube uses it only when the schedule is suspended, with the SAME maxRetention).
apiVersion: k8s.mariadb.com/v1alpha1
kind: PhysicalBackup
metadata:
  name: src-ondemand-20261120111200
  namespace: spike-backup
spec:
  mariaDbRef:
    name: src
  target: PreferReplica
  compression: zstd
  maxRetention: 720h
  storage:
    s3:
      bucket: backups
      prefix: spike-backup/src/physical
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef: {name: src-s3-credentials, key: access-key-id}
      secretAccessKeySecretKeyRef: {name: src-s3-credentials, key: secret-access-key}
---
# Restore (a): from a PhysicalBackup CR
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: rst-ref
  namespace: spike-backup
spec:
  image: mariadb:12.3
  rootPasswordSecretKeyRef: {name: rst-root, key: password}   # must hold the root password AS OF THE BACKUP
  replicas: 1
  bootstrapFrom:
    backupRef:
      kind: PhysicalBackup
      name: src-scheduled
    targetRecoveryTime: "2026-11-20T11:11:30Z"
    restoreJob:
      metadata:
        annotations:
          sidecar.istio.io/inject: "false"
      resources:
        requests: {cpu: 50m, memory: 128Mi}
  storage:
    size: 1Gi
  resources:
    requests: {cpu: 100m, memory: 256Mi}
---
# Restore (b): straight from S3 (cross-cluster capable)
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: rst-s3
  namespace: spike-backup
spec:
  image: mariadb:12.3
  rootPasswordSecretKeyRef: {name: rst-root, key: password}
  replicas: 1
  bootstrapFrom:
    backupContentType: Physical
    # targetRecoveryTime: optional; default = newest object (EntKube always sends it)
    s3:
      bucket: backups
      prefix: spike-backup/src/physical
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef: {name: src-s3-credentials, key: access-key-id}
      secretAccessKeySecretKeyRef: {name: src-s3-credentials, key: secret-access-key}
    restoreJob:
      resources:
        requests: {cpu: 50m, memory: 128Mi}
  storage:
    size: 1Gi
  resources:
    requests: {cpu: 100m, memory: 256Mi}
---
# Restore (c): point-in-time into a new replication cluster
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: rst-pitr
  namespace: spike-backup
spec:
  image: mariadb:12.3
  rootPasswordSecretKeyRef: {name: rst-root, key: password}
  replicas: 3
  replication:
    enabled: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    gtidStrictMode: true
  bootstrapFrom:
    pointInTimeRecoveryRef:
      name: src-pitr
    targetRecoveryTime: "2026-11-20T11:12:10Z"
    restoreJob:
      metadata:
        annotations:
          sidecar.istio.io/inject: "false"
      resources:
        requests: {cpu: 50m, memory: 128Mi}
  storage:
    size: 1Gi
  resources:
    requests: {cpu: 100m, memory: 256Mi}
```

**A.6.5 Metrics and Galera (`spike-metrics`)**
```yaml
# Replication cluster, final applied state (Ready, primary repl-0 at end of spike).
# EntKube never uses rootPasswordSecretKeyRef.generate; it writes the Secret itself.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: repl
  namespace: spike-metrics
spec:
  image: mariadb:12.3
  replicas: 3
  rootPasswordSecretKeyRef:
    name: repl-root
    key: password
    generate: true
  storage:
    size: 1Gi
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
  replication:
    enabled: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    primary:
      autoFailover: true
  tls:
    enabled: true
    required: true
  metrics:
    enabled: true
    exporter:
      args:
        - --collect.binlog_size
      resources:
        requests:
          cpu: 10m
          memory: 32Mi
    serviceMonitor:
      enabled: true
      prometheusRelease: kube-prometheus-stack
      interval: 15s
      scrapeTimeout: 10s
---
# Galera quick check. Metrics were later toggled on, then serviceMonitor.enabled=false, then metrics.enabled=false;
# the exporter Deployment, Service, ServiceMonitor, User, Grant and Secrets were left behind by the operator.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: galera
  namespace: spike-metrics
spec:
  image: mariadb:12.3
  replicas: 3
  rootPasswordSecretKeyRef:
    name: galera-root
    key: password
    generate: true
  storage:
    size: 1Gi
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
  galera:
    enabled: true
```

**A.6.6 Negative cases kept for reference (they show behaviour, not a target)**
```yaml
# nt: no replica template; antiAffinityEnabled; RWOP silently replaced by RWO because size is set;
# scale-out without template -> ScaleOutError; old primary crash-looped after failover (1236). Later deleted (Q15).
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: nt
  namespace: spike-repl
spec:
  image: mariadb:12.3
  replicas: 2
  rootPasswordSecretKeyRef:
    name: nt-root
    key: password
  storage:
    size: 1Gi
    storageClassName: standard
    volumeClaimTemplate:
      accessModes: ["ReadWriteOncePod"]   # silently replaced by ReadWriteOnce because size is set
    pvcRetentionPolicy:
      whenScaled: Delete
      whenDeleted: Retain
  replication:
    enabled: true
    semiSyncEnabled: true
    semiSyncWaitPoint: AfterSync
    gtidStrictMode: true
    primary:
      autoFailover: true
  affinity:
    antiAffinityEnabled: true
  tls:
    enabled: true
  podMetadata:
    annotations:
      sidecar.istio.io/inject: "false"
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
    limits:
      memory: 512Mi
  myCnf: |
    [mariadb]
    innodb_buffer_pool_size=128M
---
# rwop: RWOP sticks only when storage.size is omitted; a physical backup against it stays Pending.
apiVersion: k8s.mariadb.com/v1alpha1
kind: MariaDB
metadata:
  name: rwop
  namespace: spike-repl
spec:
  image: mariadb:12.3
  replicas: 1
  rootPasswordSecretKeyRef:
    name: rwop-root
    key: password
    generate: true
  storage:
    volumeClaimTemplate:
      accessModes: ["ReadWriteOncePod"]
      storageClassName: standard
      resources:
        requests:
          storage: 1Gi
  podMetadata:
    annotations:
      sidecar.istio.io/inject: "false"
  resources:
    requests:
      cpu: 100m
      memory: 256Mi
    limits:
      memory: 512Mi
---
apiVersion: k8s.mariadb.com/v1alpha1
kind: PhysicalBackup
metadata:
  name: rwop-oneshot
  namespace: spike-repl
spec:
  mariaDbRef:
    name: rwop
    waitForIt: true
  target: PreferReplica
  podAffinity: true
  timeout: 10m
  storage:
    s3:
      bucket: backups
      prefix: spike-repl/rwop
      endpoint: minio.s3:9000
      region: us-east-1
      accessKeyIdSecretKeyRef:
        name: repl-s3-credentials
        key: access-key-id
      secretAccessKeySecretKeyRef:
        name: repl-s3-credentials
        key: secret-access-key
      tls:
        enabled: false
---
# solo: standalone with pointInTimeRecoveryRef -- admitted, Ready, and archives nothing (log_bin=0, no agent).
# (Manifest in spike-backup/m/; shape: replicas 1, no replication, pointInTimeRecoveryRef: {name: solo-pitr}.)
```

State left on the spike cluster for inspection:
- `spike-repl`:
  - `repl` (spec 2Gi, PVCs 1Gi, `resizeInUseVolumes=false`, orphan `storage-repl-3`);
  - `rwop`;
  - `nt` leftovers (PVCs, `nt-root`, `nt-repl-password`, leaked ClusterRoleBinding `nt-spike-repl-56680d88:auth-delegator`).
- `spike-maxscale`: `mdb` + `mdb-maxscale` (healthy, TLS, 2 replicas) and the probe pod.
- `spike-db`: `db1`, `db3` and every test CR, plus a mysql:8.4 client pod.
- `spike-backup`:
  - `src` (root rotated), `solo`, `rst-ref`, `rst-s3`, `rst-pitr`, `rst-pw`;
  - `bad-creds`, `timeout-test` (suspended), `bad-bucket`;
  - orphan PVC `storage-src-3`;
  - the s3client pod.
- `spike-metrics`: `repl`, `galera` (metrics objects orphaned), `sqlclient`.
- Shared S3: the stray bucket `nonexistent-bucket-spike`.

### A.7 Uninstalling the CRDs chart (Q1, run last on the spike cluster)

Before: 10 `MariaDB` CRs across `spike-backup`, `spike-db`, `spike-maxscale` and `spike-metrics`, 9 StatefulSets (plus MaxScale's), 34 PVCs, 12 CRDs in `k8s.mariadb.com`.

`helm uninstall mariadb-operator-crds -n mariadb-operator`, then:

| | +10 s | +60 s |
|---|---|---|
| CRDs (`k8s.mariadb.com`) | 5 | 0 |
| `MariaDB` CRs | still listed | resource type gone |
| StatefulSets (spike namespaces) | 9 | 0 |
| Running pods (spike namespaces) | 29 | 3 (terminating) |
| PVCs | 34 | 32 |
| Secrets (spike namespaces) | 77 | 38 |

So the chart's uninstall is a delete of every MariaDB cluster on the Kubernetes cluster. The volumes outlive it, which makes recovery possible but manual. EntKube's guard (Q1) has to stop this before Helm runs.
