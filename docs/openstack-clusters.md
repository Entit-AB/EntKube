# Kubernetes on OpenStack, from credentials to a running cluster

## What this is for

Give EntKube an OpenStack project and nothing else, and it should produce complete, production
Kubernetes clusters: highly-available control planes, worker pools of whatever shape the workload
needs, block storage, object storage, load balancers, and the day-2 operations that keep them
alive — scaling, upgrades, node repair, and clean deletion.

The clusters this replaces are Gardener's. What it does **not** copy is Gardener's architecture.
There is no garden cluster, no seed, no permanent management plane that every cluster depends on.
Each cluster EntKube creates owns its own Cluster API installation and is complete in itself: it
keeps running, and keeps being operable, whether or not EntKube is up.

That is a deliberate trade and it is worth naming. A seed makes fleet operations cheap and a
broken control plane repairable from outside. Without one, some operations need a management plane
that does not otherwise exist — so we summon one for the minutes it takes and then destroy it
again (see [The summoned plane](#the-summoned-plane)). The cost is complexity in a handful of
operations. What we buy is that no cluster's fate is tied to a piece of shared infrastructure, and
there is nothing extra to pay for, patch, back up, or explain.

**Registering an existing cluster by kubeconfig stays exactly as it is.** It is how EntKube adopts
clusters it did not build, and nothing here changes it.

## The hard constraint: upstream Kubernetes, kubeadm, everywhere

Every cluster this design creates is bootstrapped by kubeadm and is plain upstream Kubernetes.
That includes the throwaway management cluster — **k3s is not used anywhere**, and the current
bootstrap VM, which cloud-inits `get.k3s.io`, has to be replaced before anything else here is
built.

The target clusters were never the problem: CAPI's `KubeadmControlPlane` is kubeadm by definition.
It is the ephemeral plane that has to change, and it costs more than swapping an installer, because
k3s bundles what kubeadm does not:

| | k3s gave us | kubeadm needs |
|---|---|---|
| Bootstrap | one `curl \| sh` | `kubeadm init --apiserver-cert-extra-sans <floating ip>`, from an image that already has kubeadm, kubelet and containerd |
| Scheduling | workloads run on the single node | the control-plane `NoSchedule` taint removed, or the CAPI controllers never start |
| Networking | flannel, built in | a CNI applied before anything else — `clusterctl init` installs cert-manager, whose webhooks must actually answer |
| Kubeconfig | `/etc/rancher/k3s/k3s.yaml`, world-readable by flag | `/etc/kubernetes/admin.conf`, root-only, fetched over `sudo cat` |
| Readiness | seconds | longer, and the poll must wait for the node to go Ready rather than for a file to appear |

The consequence worth noticing: **the bootstrap VM should boot the same machine image as the
cluster nodes.** They need exactly the same things — kubeadm, kubelet, containerd, the control-plane
images pre-pulled — and one image serving both means the ephemeral plane needs no internet access
at boot either. That turns [the image question](#from-scratch-means-owning-the-image) from an
upgrade concern into the foundation of the whole path.

## Where we actually are

`ClusterProvisioningService` (555 lines, one public method) already stands a cluster up:
authenticate → application credential + `clouds.yaml` → SSH keypair → ephemeral bootstrap VM (k3s
today, kubeadm per the constraint above) →
`clusterctl init` CAPO → `clusterctl generate cluster` → apply → wait for the control plane →
Calico → write the `cloud-config` secret → `clusterctl init` on the target → `clusterctl move` into
it → register the kubeconfig → record node inventory → destroy the bootstrap VM.

That is a real from-zero path and the shape below keeps it. What it is not yet:

| | today |
|---|---|
| **Worker pools** | `WorkerPools` is a list in the config, but `BuildEnv` sends `WORKER_MACHINE_COUNT = TotalWorkerCount` and `OPENSTACK_NODE_MACHINE_FLAVOR = WorkerPools[0].Flavor`. Every worker lands in one `md-0` MachineDeployment with the first pool's flavor. Multi-pool is declared, not delivered. |
| **Bootstrap cluster** | k3s, installed from `get.k3s.io` at boot. Has to become a single-node kubeadm cluster, which also means shipping it a CNI and untainting its control-plane node. |
| **Machine image** | `OPENSTACK_IMAGE_NAME` must already name a kubeadm-ready image in Glance. Nothing builds or uploads one, so "from credentials alone" is not yet true. |
| **Day-2** | Nothing. No scale, no pool changes, no version upgrade, no repair, no delete. A self-managed cluster cannot delete itself, so today its floating IPs, volumes, load balancers and security groups leak when it goes. |
| **Sizing inputs** | Flavors, images, networks and AZs are free text typed by hand. Nothing lists what the cloud actually offers. |
| **API endpoint** | Whatever the stock CAPO template does, which assumes Octavia. Clouds without it are not handled. |
| **Reconciliation** | One shot. Provisioning state is JSON on the cluster row; after the run nothing watches. |
| **Storage** | Cinder CSI and CubeFS exist as catalog components and are ordered correctly, but nothing verifies a PVC ever binds. |

So: the spine exists and is sound. What is missing is everything that makes it a cluster *service*
rather than a cluster *installer* — and the pieces that make "from scratch" literally true.

**None of it has run against a real OpenStack.** Every line below is designed, not proven.

## The model

Three things, and the discipline is in keeping them separate.

**1. A cloud connection** — `OpenStackConnection`, which exists: Keystone auth, region, project,
and the egress routing (direct, proxy, in-cluster relay, or agent) that gets EntKube to an API
behind an allowlist. One connection can carry any number of clusters.

**2. A cluster spec** — what the operator asked for. Control plane shape, worker pools, network
topology, versions, and which foundation pieces to install. Declarative and editable: changing the
spec is how day-2 happens.

**3. A cluster's own CAPI** — the authority on what exists. After the pivot, the cluster holds its
own `Cluster`, `KubeadmControlPlane`, `MachineDeployment`s and `Machine`s. EntKube reads and writes
those through the cluster's kubeconfig. It does not keep a second copy of the truth; the spec is
intent, CAPI is fact, and the difference between them is what the reconciler acts on.

### The summoned plane

Everything routine — scale a pool, add a pool, roll the workers, change a label — is a patch
against the cluster's own CAPI resources, applied with its kubeconfig. No extra infrastructure.

Two things a cluster cannot do to itself:

- **Delete itself.** The controllers that would tear down the OpenStack resources are running on
  the machines being torn down.
- **Repair a control plane that is already broken.** If the API server is down, so is the CAPI
  that would replace the machine.

For those, EntKube boots the same ephemeral VM the initial bootstrap uses — one machine, the shared
node image, `kubeadm init`, untainted, a CNI applied — `clusterctl init`s CAPO on it, `clusterctl
move`s the cluster's CAPI state *out* to it, performs the operation, and then either moves the state
back (repair) or destroys everything including itself (delete). The plane exists for the length of
one operation and leaves nothing behind.

This is the piece that pays for having no seed, and it is honest about the cost: a delete takes
minutes rather than seconds, and it needs the cloud reachable. The alternative — sweeping Nova,
Neutron, Cinder and Octavia directly for CAPO's `cluster.x-k8s.io` tags — is faster and needs no
VM, but it reimplements a teardown CAPO already knows how to do and will silently miss whatever
CAPO learns to create next. **Recommendation: the summoned plane, with a direct-sweep verification
pass afterwards that reports anything left behind rather than deleting it.**

## From scratch means owning the image

A kubeadm-ready Glance image is the one prerequisite that cannot be wished away: CAPI's whole model
is "boot this image, run this cloud-init". With k3s ruled out, the same image also boots the
ephemeral management plane, so **one image is now on the critical path twice** — nothing gets built
without it. Three ways to have one:

**(a) EntKube builds it.** A one-off job — a temporary VM, `image-builder` or a scripted
`cloud-init` + `qemu-img`, upload to Glance, tag it with the Kubernetes version. Costs one build
per Kubernetes version (~15 minutes, cached forever after). Gives byte-identical nodes, fast boots,
and an upgrade story: a version bump is "build the new image, roll the machines". This is what
Gardener does and it is the only one of the three that makes upgrades sane.

**(b) Plain Ubuntu plus cloud-init that installs kubeadm at boot.** Nothing to build, works
immediately. Every node boot then depends on package repositories and registries being reachable
and unchanged — slower, and a node that comes up in six months gets different bits than its
siblings. This is what the k3s bootstrap was doing, and it is precisely the property being ruled
out. Not built.

**(c) The operator supplies one.** Today's behaviour. Keeps working as an override and should
stay, because some clouds ship a blessed image.

**Recommendation: (a), with (c) as an override.** The image builder is a self-contained piece of
work that can land before anything else, and it is no longer only about upgrades: it is what makes
a kubeadm bootstrap possible in a closed network, and what guarantees the ephemeral plane and the
cluster it creates are running identical bits.

## The cluster spec

First-class rows, not JSON on a blueprint — day-2 needs something to edit and diff.

```
ProvisionedCluster
  KubernetesClusterId        the registered cluster this became
  OpenStackConnectionId      which cloud
  Name, KubernetesVersion, ImageRef
  ControlPlaneCount          1 or 3 (5 for the paranoid); even numbers refused
  ControlPlaneFlavor, ControlPlaneDiskGb
  ApiEndpoint                Octavia | FloatingIp
  NetworkMode                Managed (CAPO creates router/network/subnet) | Existing
  NodeNetworkId, ExternalNetworkId, PodCidr, ServiceCidr, DnsNameservers
  FailureDomains             AZs to spread across
  Foundation                 which of the stack below to install
  DesiredState               Running | Paused | Deleting
  ObservedGeneration, Conditions, LastReconciledAt

ProvisionedWorkerPool  (many per cluster)
  Name, Flavor, DiskGb, FailureDomain
  Count | (MinCount, MaxCount) when autoscaled
  Labels, Taints
  KubernetesVersion          may lag the control plane during an upgrade
```

Multi-pool is where the stock `clusterctl generate cluster` template stops being enough: it emits
exactly one MachineDeployment. **We generate the manifests ourselves** — `clusterctl generate` for
the cluster and control plane, then one `MachineDeployment` + `OpenStackMachineTemplate` per pool,
authored from the spec. This is also what makes labels, taints, per-pool AZs and per-pool disk
sizes possible at all, and it removes the dependency on whatever the upstream flavor template
happens to contain this month.

## What "complete" means

A cluster is not done when nodes are Ready. The foundation, installed in order, each already a
catalog component or a blueprint step:

1. **CNI** — Cilium. Calico's single-file manifest stays as the bootstrap CNI, because something
   must be schedulable before the pivot; Cilium replaces it as a foundation step.
2. **Cloud controller manager** — node lifecycle and `Service type=LoadBalancer` onto Octavia.
3. **Cinder CSI** — a default StorageClass, `allowVolumeExpansion`, and a VolumeSnapshotClass.
   Verified by actually binding a PVC, not by the Helm release reporting success.
4. **Object storage** — CubeFS for a portable S3 that exists on any OpenStack, *or* the cloud's own
   Swift/RGW where it has one (`OpenStackS3Service` already speaks to it). Per-cloud choice on the
   connection; CubeFS is the default because it is the one that always exists.
5. **metrics-server** — nothing scales without it, and its absence is invisible until an HPA
   silently does nothing.
6. **cluster-autoscaler** — in `clusterapi` `incluster-incluster` mode, reading the per-pool
   min/max annotations. Already in the catalog with the `cluster.x-k8s.io` RBAC the chart omits.
7. **Ingress** — gateway + cert-manager per the production baseline.
8. **Backup** — Velero to the object storage above, plus **etcd snapshots**, which are separate and
   more important: Velero restores workloads, etcd restores the cluster.
9. **Observability** — the EntKube telemetry components, already per-cluster.

`ProductionBaseline` already expresses most of this as capability → ordered steps. It grows etcd
backup and metrics-server, and each step gains a verification that asserts the thing works rather
than that Helm exited zero.

## Day-2

Each of these is "edit the spec, let the reconciler make it so".

| Operation | How |
|---|---|
| **Scale a pool** | Patch `MachineDeployment.spec.replicas`. Seconds. |
| **Add / remove a pool** | Apply or delete a `MachineDeployment` + `OpenStackMachineTemplate`. Removal cordons and drains first, and refuses while it is the last pool. |
| **Reshape a pool** (flavor, disk, image) | New `OpenStackMachineTemplate`, point the MachineDeployment at it, CAPI rolls it. `maxSurge`/`maxUnavailable` from the spec. |
| **Kubernetes upgrade** | Build/select the image for the new version, bump `KubeadmControlPlane.spec.version` (CAPI rolls control-plane machines one at a time), then each pool in turn. Skew is enforced: workers never lead the control plane, never lag by more than one minor. |
| **Node repair** | A `MachineHealthCheck` per pool, declared at creation. CAPI deletes and replaces unhealthy machines on its own — this is the one piece of day-2 that needs no EntKube involvement at all, and it should be on by default with conservative thresholds. |
| **Certificate renewal** | kubeadm certificates expire after a year. CAPI renews them on control-plane rollout, so the reconciler surfaces "certificates expire in N days" as an advisor finding and offers a rollout. Silent expiry is how these clusters would die at the twelve-month mark. |
| **Kubeconfig rotation** | CAPI issues a short-lived admin kubeconfig into a secret. The vaulted copy must be refreshed from it, or EntKube locks itself out of its own cluster. |
| **Credential rotation** | Re-mint the application credential, rewrite `clouds.yaml`, update the in-cluster `cloud-config` secret and the CAPO identity secret together. |
| **Delete** | The summoned plane, then a direct sweep that reports leftovers. |

## Reconciliation

A `BackgroundService`, one pass per cluster on an interval and on demand after an edit:

1. Read the cluster's CAPI resources through its kubeconfig.
2. Project them into observed state and conditions — control plane ready and at what version, per
   pool desired/current/ready/updated replicas, machines in trouble, rollouts in progress.
3. Diff against the spec and apply what differs, one operation at a time, never more than one
   destructive operation in flight per cluster.
4. Record conditions and events so the UI can show *why* a cluster is not what was asked for.

A cluster whose API server is unreachable is marked `Unreachable` and left alone. Reconciliation
never assumes absence means deletion — that is the failure mode that eats production clusters.

## Clouds behind an allowlist

The Cleura case from `docs/egress-agent.md` constrains this more than it first appears. The
bootstrap VM needs to reach package repositories and container registries; `clusterctl` fetches
provider manifests from GitHub; every node pulls images. EntKube's own egress routing (proxy,
relay, agent) covers EntKube→OpenStack, and **does not** cover VM→internet, which runs inside the
customer's network.

So the design has to allow: a pinned local `clusterctl` provider repository (no GitHub at run
time), an image registry mirror configured into the machine image, and package repositories baked
in rather than fetched. This is another argument for building the image ourselves — a prebaked
image is the only version of this that works in a closed network.

## What of Gardener we keep, and what we do not

| Gardener | Here |
|---|---|
| Shoot clusters on OpenStack | Yes — self-contained rather than seed-hosted |
| Managed control plane (as pods in the seed) | **No.** Control planes run on their own machines. Costs three VMs per cluster; buys independence. |
| Machine images per version | Yes, built by EntKube |
| Autoscaling, health checks, auto-repair | Yes, via CAPI's own `MachineHealthCheck` and cluster-autoscaler |
| Automated Kubernetes upgrades | Yes, operator-triggered rather than by maintenance window (windows can come later) |
| etcd backup and restore | Yes, to the cluster's object storage |
| Multi-cloud (AWS, Azure, GCP…) | Not now. The design is CAPI-shaped, so another infrastructure provider is a new provider plus a new template writer, not a new architecture. |
| Projects, quotas, multi-tenancy of the garden | No — EntKube's tenants already cover this |

## Build order

Each phase leaves the product better than it found it, and nothing later invalidates something
earlier.

1. **Cloud discovery.** List flavors, images, networks, AZs, and detect Octavia and Cinder volume
   types from the connection. Turns every free-text field in the wizard into a picker, and is
   worth having on its own.
2. **Machine images, and the kubeadm bootstrap.** Build, upload and tag an image by Kubernetes
   version, then rebuild the ephemeral plane on it: `kubeadm init`, untaint, CNI, wait for Ready.
   These are one phase because they are one problem — the image is what the bootstrap VM boots, and
   replacing k3s without it just moves the internet dependency around.
3. **Own the manifests.** Replace `clusterctl generate cluster` with EntKube-authored manifests, so
   worker pools stop collapsing into one. First point at which the config stops lying.
4. **The spec as data.** `ProvisionedCluster` + `ProvisionedWorkerPool`, migrated from the existing
   blueprint JSON, and the wizard writes it.
5. **The reconciler.** Read-only first — show observed state and conditions — then let it act.
6. **Day-2, in order of blast radius.** Scale, add/remove pool, reshape, upgrade.
7. **Delete**, with the summoned plane and the leftover sweep.
8. **Foundation verification.** Every foundation step asserts its own success.
9. **etcd backup**, certificate-expiry findings, credential and kubeconfig rotation.

Phases 2 and 3 are the ones that cannot be skipped — 2 because k3s has to go and nothing else can
be tested honestly until it has, 3 because until we author the manifests the worker pools in the
spec are fiction. Together with 1 they are what make "connect a cloud and get whatever machines are
required" true.

## Where this stands

All nine phases are built and unit-tested. None of it has run against an OpenStack.

| Phase | Built | Notes |
|---|---|---|
| 1 Cloud discovery | ✅ | `OpenStackDiscoveryService`. Not yet wired into the wizard's form fields — the pickers still render as free text. |
| 2 Images + kubeadm bootstrap | ✅ | `MachineImageBuilder`, `NodeImageRecipe`. k3s is gone. |
| 3 Authored manifests | ✅ | `CapiManifestBuilder`. Multi-pool is real. |
| 4 Spec as data | ✅ | `ProvisionedCluster` + pools, migrations on all three providers. |
| 5 Reconciler | ✅ | Observes, and corrects replica drift and missing pools. Will not reshape, upgrade or delete — see below. |
| 6 Day-2 | ✅ | `ClusterOperationsService`, with a Lifecycle tab on provisioned clusters. |
| 7 Delete | ✅ | Summoned plane + leftover sweep, behind a type-the-name confirmation. |
| 8 Foundation verification | ✅ | Runs after every bootstrap, recorded as a step of its own. Also on demand. |
| 9 etcd backup, cert expiry, rotation | ✅ mostly | Kubeconfig and cloud-credential rotation shipped. The etcd CronJob is built but not yet scheduled by the production baseline, and certificate findings are surfaced on the Lifecycle tab rather than in the Operations Advisor. |

**Known gaps, in the order they matter:**

1. **Nothing has touched a real cloud.** The tests cover judgement and document structure. They
   cannot tell you whether CAPO accepts these manifests, whether the bake script produces a
   bootable image, or whether `clusterctl move` behaves as assumed during teardown. This is the
   only item on this list that cannot be closed by writing more code.
2. **Cloud discovery is not wired into the wizard's form.** `OpenStackDiscoveryService` reads
   flavors, images, networks and zones, and the New Cluster wizard still asks an operator to type
   them. Purely a UI job.
3. **The etcd CronJob is not scheduled by the production baseline.** The manifest exists and is
   tested; nothing installs it yet, so a cluster built today has Velero and no etcd snapshot.
4. **Certificate expiry is shown on the Lifecycle tab, not in the Operations Advisor.** Someone
   has to open the cluster to see it, which is the wrong way round for something whose whole
   problem is that nobody is looking.
5. **The reconciler's apply half is narrow on purpose.** Replica drift and missing pools only.
   Reshape, upgrade and removal stay operator-driven, and drift of that kind is reported rather
   than corrected. Worth revisiting only after the read half has been watched against real
   clusters for a while.

## Decisions still open

1. **Machine images: build them?** Recommended yes (a) — and with k3s ruled out this is close to
   forced, since the ephemeral plane needs the same image. The remaining question is whether to run
   `image-builder` or a scripted `cloud-init` + `qemu-img`, and where the build itself runs.
2. **Control-plane default.** 3 nodes across 3 AZs, or 1 for cheapness with an obvious warning?
   Recommended: 3 is the default, 1 is offered and labelled non-production.
3. **API endpoint on clouds without Octavia.** Fall back to kube-vip on a floating IP, or refuse
   the cloud? Recommended: detect at discovery, fall back to kube-vip, say so in the UI.
4. **Object storage default.** CubeFS everywhere, or the cloud's Swift/RGW when present?
   Recommended: CubeFS, because it is the only answer that is the same on every cloud — with the
   cloud's own store as an opt-in.
5. **etcd snapshot destination.** The cluster's own CubeFS is circular — losing the cluster loses
   the backup. Recommended: EntKube's own MinIO, or a bucket on the cloud outside the cluster.
6. **Delete when the cloud is unreachable.** Refuse, or let the operator forget the cluster and
   report the resources they will have to remove by hand? Recommended: refuse by default, offer
   "forget" explicitly, and always list what was left.
