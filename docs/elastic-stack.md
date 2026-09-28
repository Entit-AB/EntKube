# Elastic Stack (Elasticsearch + Kibana)

EntKube manages the Elastic Stack through Elastic's own operator, ECK. The operator is a catalog
component; the clusters, their node tiers, their Kibana and their index lifecycle policies are
managed under **Services › Search**.

## What gets installed

| Piece | Where it comes from |
| --- | --- |
| ECK operator | Catalog component `eck-operator` — Helm chart `eck-operator` 3.5.0 from `https://helm.elastic.co`, namespace `elastic-system` |
| Elasticsearch | `Elasticsearch` CR applied by `ElasticsearchService` |
| Kibana | `Kibana` CR, one per Elasticsearch cluster, sharing its name |
| Lifecycle policies | ILM policies + composable index templates, applied by a Job inside the cluster |
| Ingest pipelines | Processors applied by a Job; attached to a template as `index.default_pipeline` |
| Snapshots | S3 repository + SLM policy, registered by a Job; restores run the same way |
| Application users | Native-realm users + per-user roles, created by a Job; bound into an app's namespace as a Secret |
| Metrics | Community Elasticsearch exporter + Service + ServiceMonitor, labelled to match the live Prometheus |
| Kibana spaces | Created through Kibana's API by a Job; accounts are scoped to one by privilege |
| Cross-cluster search | `remoteClusters` + `remoteClusterServer` on the CRs, with an ECK-managed API key |

The operator on its own starts nothing. Installing it and stopping there leaves a working controller
with no clusters, which is why its catalog entry points at Services › Search.

## The topology

Each role becomes its own nodeSet rather than one set of nodes doing everything:

| Tier | Roles | Why it is separate |
| --- | --- | --- |
| master | `master` | Holds cluster state only. A master that also serves searches turns a heavy query into a master election. |
| hot | `data_hot`, `data_content`, and `ingest` when there is no dedicated ingest tier | Takes every write. `data_content` is where Kibana's own indices live — without it Kibana never goes green. |
| warm | `data_warm` | Recent data still searched but no longer written. Optional. |
| cold | `data_cold` | Retention rather than speed. Optional. |
| ingest | `ingest` | Dedicated ingest **and** coordinating nodes: they run the pipelines and fan out searches without holding a shard. Optional. |

A cluster with no data tier at all is rendered as a single nodeSet with no `node.roles` — every role
on one node, which is what makes one node a working cluster. That shape is refused above one node:
two all-roles nodes is neither a quorum nor a tier.

Other refusals, all at create/update time: an even master count above one, a warm or cold tier with
no hot tier, a tier under 1Gi, a lifecycle policy that ages data into a tier the cluster does not
have, and ILM phases out of order.

## The resource bounds

- **Memory request equals its limit** on every tier. The heap is sized from that number, and a node
  that could burst past it gets OOM-killed rather than throttled.
- **Heap is half the memory**, never above 31Gi. The other half is the file cache Lucene actually
  searches through; past ~31Gi the JVM drops compressed object pointers and a larger heap starts
  addressing less.
- **No CPU limit.** Throttling a JVM mid-GC turns a slow query into a timing-out one; the request
  already guarantees the floor the tier was sized for.
- **Kibana's `--max-old-space-size`** is pinned to 75% of its container memory, because Kibana left
  to itself grows until the kernel takes the pod away mid-request.
- **The whole footprint is measured before anything is applied**: total requests against the
  cluster's unallocated capacity (cordoned nodes excluded, finished pods not counted), and the
  largest single pod against the roomiest single node. An update is charged only for what it adds.
  A topology that cannot schedule is refused with the numbers, and can be applied anyway only by
  ticking the acknowledgement — which is the right answer on a cluster whose autoscaler has not
  added the nodes yet, and wrong everywhere else.

## Index lifecycle policies

Node tiers only give data somewhere to move to. The lifecycle policy is what rolls an index over
before its shards grow past a searchable size, ages it hot → warm → cold, and eventually deletes it.
Without one, a tiered cluster fills its hot tier and stops, which looks exactly like a disk sizing
mistake.

ECK has a declarative route for this — `StackConfigPolicy` — but **it requires an ECK Enterprise
licence**. ILM itself is free, so EntKube applies the policies and their index templates with a
short-lived Job that runs inside the cluster:

- it runs the stack's own image, already pulled on those nodes and carrying curl;
- it reads the operator-generated `elastic` password straight from the `*-es-elastic-user` Secret,
  so the credential never reaches the management plane;
- it verifies the HTTP layer against the operator's own CA rather than skipping verification;
- it waits for the cluster to answer before pushing anything, fails on any HTTP error response, and
  is safe to re-run — applying the same policies twice is a no-op.

Deleting a policy removes its index template first, then the policy: Elasticsearch refuses to delete
a policy an index template still names. Indices already created keep their settings.

## Ingest pipelines

Node roles decide where a document lands and the lifecycle policy decides how long it stays; the
pipeline decides whether it is worth anything when it gets there. A log line indexed as one opaque
`message` field cannot be filtered by level, grouped by service, or aged by its own timestamp rather
than its arrival time.

The common processors are form fields — grok a field, parse a timestamp into `@timestamp`, rename,
drop and stamp constants — and anything beyond them goes in as a raw JSON array of processors. They
are ordered so that grok runs first (it creates the fields), removals last (removing a field before
something reads it is the classic way to lose it), and renames and removals tolerate a document that
does not have the field.

**Every pipeline gets an `on_failure` handler** that records the error on the document as
`ingest.failure` instead of letting the processor throw. Without one, a grok pattern that does not
match rejects the whole document and the line is simply gone; with one it arrives, searchable, and
can be fixed later. That is not optional here.

**Test it** runs one sample document through the pipeline and shows what comes out. The pipeline is
sent inline rather than by name, so an edit can be tried before it is applied to anything. This is
the difference between a pipeline somebody believes works and one they have seen work — a grok
pattern that does not match produces no error at index time, just documents missing the fields
everything downstream was written against.

A lifecycle policy can name a pipeline, which puts it in the index template as
`index.default_pipeline`, so everything written through that template goes through it. A pipeline a
template still names cannot be deleted — the template would point at something that is gone, and
every write through it would fail somewhere far from this screen.

## Snapshots

Node tiers and lifecycle policies delete data on purpose, so a cluster wants snapshots before its
first ILM delete phase fires. Configure them per cluster from the same tab: pick a tenant storage
link (any S3-shaped one with a bucket), a path inside it, a schedule and a retention window.

What happens when you save:

1. The link's `ACCESS_KEY`/`SECRET_KEY` are read from the vault and written to a Secret whose keys
   are the keystore entry names verbatim (`s3.client.default.access_key`, `…secret_key`). ECK loads
   it into every node's keystore and reloads it without a restart. **The keys never appear in a
   manifest** — only the non-secret half (endpoint, protocol, path-style, region) goes into
   `elasticsearch.yml`, on every nodeSet, because the repository is used by every node.
2. The cluster is re-applied with `secureSettings` pointing at that Secret.
3. A Job registers the repository (`PUT _snapshot/entkube-s3?verify=true`) and the SLM policy. The
   repository PUT is **retried for up to two and a half minutes**: the keystore reaches the nodes
   shortly after the CR does, and a repository registered one second early fails with an
   authentication error indistinguishable from wrong credentials.

Path-style addressing is chosen from the provider — MinIO, CubeFS and Cleura get it, AWS does not.
Snapshots include the global cluster state, so a restore brings the index templates, the ILM
policies and the roles with the data rather than leaving indices nobody has told Elasticsearch what
to do with.

**Reading the result back.** A scheduled snapshot happens entirely inside the cluster, so nothing
outside it would notice a failure. "Check now" runs a Job that reads the SLM policy's own record and
stores the last success and, if it is newer than that success, the last failure. "Snapshot now" asks
SLM to run off-schedule — it returns as soon as the snapshot has *started*, which is why the result
is read separately.

**Stopping** removes the SLM policy only. The repository and everything already in the bucket are
left alone: deleting the repository is how you lose the backups you turned this off while still
having. Deleting the *cluster* removes the keystore Secret and the ConfigMaps EntKube created, but
never the bucket.

## Restoring

**List snapshots** runs a Job that reads the repository and shows what is in it, with each
snapshot's state — a `PARTIAL` one restores, but not all of it, and is not a clean backup.

Restoring has two modes, because they are different decisions:

- **Side by side** (the default) restores the matching indices under a prefix. Nothing live is
  touched, so somebody can look at what came back before trusting it. Aliases are deliberately left
  behind — restoring them would collide with the live aliases still pointing at the live indices,
  and Elasticsearch fails the whole restore over it. The global cluster state cannot be restored
  this way at all: there is only one set of settings, templates and ILM policies, so restoring them
  always overwrites the live ones.
- **In place** closes the matching indices, restores over them, and Elasticsearch reopens them when
  the data is back. The close is not a nicety — Elasticsearch refuses to restore into an open index,
  and without it the restore fails having done nothing, which reads as a broken backup rather than a
  busy index. This mode asks for confirmation, and is written to the audit log with the operator's
  name, the snapshot and the pattern.

Both report Elasticsearch's own response rather than a claim that it worked. A large restore takes
longer than the page waits; the Job keeps going in the cluster and says so.

## Application users and bindings

Until a user exists, the only account on the cluster is the operator-generated `elastic`
superuser — the credential that can also delete every index. Application users are native-realm
users with a role scoped to **one index pattern** and one of three levels:

| Access | Index privileges |
| --- | --- |
| Viewer | `read`, `view_index_metadata` |
| Writer | `read`, `write`, `view_index_metadata`, `create_index`, `auto_configure` |
| Manager | `all` on the pattern, plus cluster `monitor` |

`auto_configure` is what lets a writer add a field to a data stream's mapping; without it the first
document carrying a new field is rejected, which reads as a broken client rather than a missing
privilege.

The password is generated once, written to a Secret beside the cluster, and handed to the Job
through an environment variable from that Secret — it is never in a command line, never in a
manifest EntKube keeps, and **never in the management plane's database**. That is also why the user
document is the only thing in the apply script built with an expanding heredoc.

**Binding an application** writes `ELASTICSEARCH_URL`, `ELASTICSEARCH_USERNAME`,
`ELASTICSEARCH_PASSWORD` and `ELASTICSEARCH_CA_CRT` into the deployment's own namespace, reading the
password back out of the Elasticsearch namespace as it goes. The CA matters: the HTTP layer is
served with the operator's own certificate, so a client that does not trust it either fails or gets
talked into skipping verification — and the second one is how a search cluster ends up reachable by
anything on the network. Re-syncing is how a rotated password reaches the application.

A user an application is still bound to cannot be deleted; the binding goes first. Deleting the
cluster removes the bindings' Secrets from their namespaces too, rather than leaving applications
holding working-looking credentials for something that is gone.

### People signing in to Kibana

The same account can be a person's Kibana login, at **Read** (open Kibana and search its own indices,
save nothing) or **Build** (also create data views, dashboards and saved searches). That is granted
as application privileges on the user's own role rather than through Elasticsearch's built-in
`viewer` and `editor` roles — those carry read, or write, on *every* index, and would undo the index
scoping in the same breath as granting the login. A person with Read on `logs-orders-*` sees exactly
that data in Kibana and nothing else.

This needs no licence beyond Basic. Signing in to Kibana with an external identity provider (OIDC,
SAML, LDAP) is a **Platinum** feature of Elasticsearch, not something ECK or EntKube can grant.

### Kibana spaces

A space is a partition of Kibana with its own dashboards, data views and saved searches. Create one
per team, scope their accounts to it, and they stop seeing each other's work — and, because the
scoping is a privilege rather than a filter, stop being able to.

Spaces are created through Kibana's own API rather than Elasticsearch's, which means a different
service, a different certificate, and the `kbn-xsrf` header on every write — without it Kibana
answers 400 with a message about cross-site request forgery that reads as a problem with the body.
The job waits for Kibana too: it becomes available well after the cluster it connects to does.

Applying a space twice is harmless (it creates, or updates the one already there). A space an
account is confined to cannot be deleted — that account would sign in to a space that no longer
exists and simply see nothing, with nothing saying why — and deleting one is audited, because every
dashboard in it goes at the same time.

### Rotating a password

**Reset password** generates a new one, sets it through Elasticsearch's dedicated password endpoint —
so the account's roles are left exactly as they are — and then re-syncs every bound application, in
that order. A bound application holds the old password from the moment it changes, so the re-sync is
part of the reset rather than something to remember afterwards. The new password is shown once, for
handing to a person; it lives in the cluster's Secret, never here. If the change fails, the old
password is still in force and the message says so.

## Watching it

A search cluster's usual way of stopping is not a crash: the disk fills, Elasticsearch stops
allocating shards, and eventually turns indices read-only. So three figures are kept current and
turned into Operations Advisor findings.

**How it is read.** The API server's proxy strips the caller's `Authorization` header before
forwarding — it exists precisely so a user's token never reaches a pod — which is the header
Elasticsearch needs. The Jobs elsewhere here get around that by running inside the cluster, but a
Job costs the better part of a minute. So live reads `kubectl exec` into a Ready node and ask over
loopback, with the password on stdin so it never lands in an argument list. That is also the one
place in this feature where certificate verification may be skipped, and only as a fallback: the
request never leaves the container that serves it, so there is no position to intercept it from.

**Three cadences, because they cost three different amounts:**

| Reading | How | How often |
| --- | --- | --- |
| Orchestration phase and health | `kubectl get` on the CR | every poll (60 s) |
| Disk per node, unassigned shards | exec into a node | when the stored reading is older than 15 min |
| Snapshot success/failure | a Job against SLM | every 6 h |

**Findings** (all computed from what the poller stored, so opening the Priorities page never fans
out into cluster calls):

- Snapshots never taken, stale, failing, or not configured at all — through the same evaluator every
  other managed datastore uses.
- Cluster **red** (critical) — shards cannot be placed, so some reads and writes fail.
- Cluster **yellow** with unassigned shards (warning) — the data is readable but has no copy.
- A node **at or past 95%** (critical) — Elasticsearch has made indices read-only, and does not undo
  that by itself once space is freed.
- A node **at or past 85%** (warning) — no new shards are going there, so the next rollover has
  fewer places to land.
- Figures **nobody has been able to refresh for a day** (info) — stale numbers are not reassurance.

**Indices & disk** in the Search tab shows the same thing live on demand: per-node disk against the
two watermarks, and the indices biggest-first with their lifecycle phase. An index whose ILM policy
has stalled is shown as *stuck*, with the reason — that failure is otherwise invisible, because the
index simply stays where it is.

## Upgrading

An upgrade is checked before it is applied, because ECK's webhook will otherwise refuse it in one
line — or accept it and start restarting nodes.

**Refused outright:**

- **A downgrade.** Not a policy: Elasticsearch migrates its data directory in place on first start,
  and the older version will not open it. The way back from a bad upgrade is a restore into a new
  cluster.
- **Skipping a major.** One major at a time, as Elasticsearch supports it.
- **A red cluster.** A rolling upgrade restarts every node in turn, and doing that while shards are
  already unavailable is how a recoverable problem becomes a lost index.
- **No snapshots, or none that ever succeeded.** An upgrade cannot be undone, so there has to be
  something to go back to.

**Warned about:** a yellow cluster (some shards have no replica, and each restart takes their only
copy offline), a snapshot more than two days old, a node past the 85% watermark (a rolling upgrade
moves shards, and there is nowhere for them to go), a major upgrade's breaking changes, and a
cluster that is not in the Running state yet.

Blockers can be accepted deliberately — sometimes there is a reason — and the acceptance is written
to the audit log with the operator's name and which blockers they overrode.

Elasticsearch and Kibana are applied together at the new version. ECK rolls the Elasticsearch nodes
one at a time with the masters last, and holds Kibana at its current version until the cluster can
serve it. A single-node cluster is told plainly that it will be down for the restart rather than
rolling through it.

## Metrics in Prometheus

The cluster's own metrics are visible in Kibana; **Export to Prometheus** also puts them in the
Prometheus that scrapes everything else. It deploys the community Elasticsearch exporter beside the
cluster with:

- **its own Elasticsearch account**, holding cluster `monitor` and `monitor` on all indices and
  nothing else — every metric it reads is a read-only operation, and this credential sits in a pod
  for years;
- **the operator's CA mounted**, because unlike the in-pod reads this one crosses the network;
- **explicit small resources**, since a metrics sidecar that competes with the data nodes it
  measures is worse than no metrics at all.

**Per-index metrics are opt-in.** Turning them on makes every index its own set of series and makes
each scrape ask the master nodes for all of their stats — the exporter's own README warns about it.
On a cluster that rolls an index over daily, that is unbounded growth in Prometheus.

### The label that decides whether any of this works

kube-prometheus-stack ships `serviceMonitorSelectorNilUsesHelmValues: true`, which becomes a
selector of `release: <its release name>`. A ServiceMonitor created by anything else carries no such
label, is silently ignored, and the metrics simply never appear — with no error anywhere.

So the ServiceMonitor is not written blind: the live Prometheus resource is read first and the
monitor is stamped with exactly the labels it selects on. The result is reported back in the UI —
including the case where Prometheus has no `serviceMonitorNamespaceSelector` at all and therefore
only looks in its own namespace, which no Elasticsearch namespace will ever satisfy.

## Searching another cluster

A cluster can be given a one-way link to another one, so a single Kibana answers a question that
spans both — `prod:logs-*` beside `staging:logs-*` — without either side's data being copied. The
alternative is one cluster holding everyone's data, which is the arrangement every isolation
decision in this feature exists to avoid.

ECK wires this declaratively: the cluster being searched opens its **remote cluster server**, and
the searching one gains the connection plus a **cross-cluster API key scoped to the index patterns
you name**. ECK creates and rotates that key, so nothing here holds a credential for it.

- **Replication is not offered.** Cross-cluster *search* is free; cross-cluster *replication* is a
  Platinum feature of Elasticsearch.
- **Both clusters must be on 8.14 or later** — that is when cross-cluster API keys arrived. Clusters
  that are not are left out of the picker rather than offered and then refused.
- **Both must be on the same Kubernetes cluster.** ECK can only wire clusters it manages together;
  across Kubernetes clusters the addresses and the trust have to be arranged by hand, and EntKube
  does not pretend otherwise.
- **Opening the remote cluster server restarts the searched cluster's nodes**, one at a time, because
  it is a transport change. You are told that before the link is created, not after the restarts
  start.

Removing a link re-applies the searching cluster without it and deliberately leaves the remote
cluster server open: closing it is a second rolling restart, and an open server nobody holds a key
for reaches nothing.

## Publishing Kibana

Kibana's Service is `{cluster}-kb-http` on port 5601. Publish it like any other service from
**Routes & Ingress** — nothing in this feature owns an external hostname.

## Things worth knowing

- **`vm.max_map_count`.** Elasticsearch wants 262144 on the host. Where the nodes cannot be tuned,
  turn off "Allow memory-mapped storage" and the tiers are rendered with
  `node.store.allow_mmap: false`, which costs some search performance and starts at all.
- **PVCs never shrink.** A smaller storage size is rejected by the API server, not by EntKube.
- **Volumes survive a scale-down** (`volumeClaimDeletePolicy: DeleteOnScaledownOnly`), so scaling a
  tier back up does not silently start with empty disks. Deleting the cluster does delete them.
- **Zone-aware placement** uses ECK's `zoneAwareness` and only means anything on a cluster whose
  nodes actually carry `topology.kubernetes.io/zone`.
