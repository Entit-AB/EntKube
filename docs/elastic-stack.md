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
