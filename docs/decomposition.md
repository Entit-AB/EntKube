# Decomposing EntKube into a product

**Status:** Phase 0 under way — the module map and its enforcing test are in.
Written 2026-10-05 against `02f970d`; measurements in §4.0 taken against `7357fb2`.

This document answers three asks that arrived together — "make it microservices",
"give it a proper frontend with BFFs", "make it easy to run EntKube agents inside
the cluster" — and argues that they are one piece of work in the middle and two
pieces of work on the outside, done in a specific order.

---

## 1. What is actually there

Measured, not estimated:

| | |
|---|---|
| `EntKube.Web` | **235,289 lines** of C#/Razor, excluding migrations |
| Razor components | 215 files / 101k lines — `Pages/Tenants` alone is 119 files / 79.7k lines |
| Services | 251 files / 117k lines, of which **141 open the DbContext directly** |
| Entities | 152 files, **164 `DbSet`s on one `ApplicationDbContext`** (3,592 lines) |
| Tenancy | 82 of 152 entities carry a `TenantId` |
| Composition root | `Program.cs` is 1,582 lines: 229 `AddX` calls, 168 service registrations, **24 `AddHostedService`** |
| Machine-callable surface | **22 endpoints** in `PublicApi/PublicApiEndpoints.cs` |
| UI model | Blazor **Server**, interactive circuits. `EntKube.Web.Client` is an empty WASM shell — it exists only to serialize auth state |
| Tests | 186 files |

Everything except telemetry (`EntKube.Telemetry`, `EntKube.TelemetryNode`) and the
installer runs in one process, against one database, behind one Caddy.

### This is not a ball of mud, and that matters

The service layer is genuinely layered — `ComponentCatalog`, `KubernetesOperationsService`,
`VaultService`, `ConnectivityGraphService` are real domain services with real
boundaries, and the telemetry extraction already proved the repo can push a
subsystem out into its own assembly and its own in-cluster deployable.

What's missing is not structure. It's **contracts**.

---

## 2. The three asks, honestly separated

> **Microservices** is a deployment topology.
> **A frontend with BFFs** is a presentation topology.
> **In-cluster agents that can do things** is a capability.
>
> Only the third one is a product feature. The other two are means.

And here is the part worth stopping on: **the capability does not require either
topology.** An EntKube agent running in a customer cluster does not care whether
the control plane is one process or twelve, or whether the operator UI is Blazor
or React. It cares about exactly one thing:

**Is every operation a human can perform in the UI also reachable as an authenticated,
authorized, versioned API call?**

Today the answer is: 22 endpoints out of a surface area of roughly 250 services.
Around 8%.

That is the whole blocker. It is also — and this is the useful part — *the same
blocker for all three asks*:

- You cannot split a service out of the monolith until its callers go through a
  contract instead of a `.Include()`.
- You cannot put a BFF in front of a page until that page's data access goes
  through a contract instead of an injected `ApplicationDbContext`. (32 Razor
  components inject it directly.)
- You cannot let an agent do anything until that thing has a contract.

**So: the API is the product. Build it first, and the other two become ordinary
refactors instead of rewrites.**

---

## 3. The trap to avoid

The instinct with 164 `DbSet`s is to split the database along service lines. Do not
start there, and for most of these modules do not finish there either.

The entity graph is heavily interlinked — 82 entities hang off `TenantId`, there are
~287 cross-entity navigation properties, and the FKs are `Restrict`, which is why
`DeleteTenantAsync` had to be written as a hand-ordered transactional delete
(see the tenant-purge work). Cutting that graph into per-service databases means
replacing joins with network calls and foreign keys with eventual consistency,
across a domain where "which cluster is this app's environment on" is a question
asked on nearly every page.

The cost is enormous and the benefit is near zero, because **EntKube's scaling
problem is not request throughput**. It is:

1. a sweep (cost scan, drift scan, supply-chain scan) burning the process the UI lives in;
2. a bad deploy of an unrelated subsystem taking down the control plane;
3. an in-cluster agent needing a stable surface to call.

All three are solved by **process separation with a shared database**, which is a
completely respectable architecture and is what most of this plan proposes. Only
two modules earn their own store, and both already essentially have one.

---

## 4. Target architecture

### 4.0 What the boundaries actually cost (measured, not estimated)

The module map below is now enforced by `ModuleBoundaryTests`, which walks the EF model and
counts every foreign key crossing a module boundary. Measured 2026-10-05 against `7357fb2`:

**164 cross-module foreign keys, across 39 module pairs.**

That number splits in two, and the split is the whole story:

| | count | what it is |
|---|---|---|
| **Universal** | **70** | `Tenant` (55) and `Customer` (15). Tenancy. Permanent — and barely a join, since a module needs the *id*, not the row. |
| **Structural** | **94** | Everything else. This is the actual bill. |

The structural weight concentrates on five tables:

| target | edges | owner |
|---|---|---|
| `App` | 21 | Delivery |
| `KubernetesCluster` | 19 | Fleet |
| `ClusterComponent` | 9 | Catalog |
| `Environment` | 7 | Delivery |
| `StorageLink` | 5 | DataServices |

**`App` and `KubernetesCluster` are the two hubs of this schema** — though see §4.0.1,
which found that `KubernetesCluster`'s share is mostly not a hub at all but credential custody — — 40 of the 94 structural
edges point at one or the other. Any decomposition has to treat them as shared reference
data, which is another way of saying Delivery and Fleet are the two modules least able to
leave.

**This confirms §3 with numbers instead of instinct.** Splitting databases means converting
94 real joins into network calls, against a schema where two tables account for nearly half
of them.

It also sharpens the order of extraction, and corrects an impression:

- **Support looks like the most coupled module and is in fact the least.** It has 39 outbound
  edges, more than any other — but 28 are `Tenant`/`Customer`, and *all eleven* of the rest
  point at exactly one table, `App`. One foreign key to sever, repeated eleven times.
- **Delivery→DataServices (15) is the genuinely hard one.** It is spread across
  `CnpgDatabase`, `RegisteredPostgresDatabase`, `RedisCluster`, `MongoDatabase`,
  `KafkaCluster`, `RabbitMQCluster` and `StorageLink` — the service bindings. Seven tables,
  not one.

So the sequence in §6 stands, and Support-before-DataServices is now justified rather than
asserted.

### 4.0.1 The blocker this plan did not account for: who holds the kubeconfig

Every one of the eighteen methods on `IKubernetesClientFactory` takes a `string kubeconfig`.
So anything that applies a manifest, reads a pod or creates a namespace must first load the
cluster row and pull the credential out of it.

**508 places do, across 57 files and twelve of the thirteen modules.**

| file | sites | | file | sites |
|---|---|---|---|---|
| `KubernetesOperationsService` | 72 | | `RabbitMQService` | 37 |
| `CnpgService` | 48 | | `PrometheusService` | 20 |
| `MongoService` | 47 | | `HeadscaleService` | 17 |
| `ElasticsearchService` | 42 | | `ComponentLifecycleService` | 16 |

Five files hold nearly half, which is also where converting pays best. By module it is
DataServices ~236, Fleet ~99, Catalog ~52, then single and double figures elsewhere; only
Fleet's share is unremarkable, because it owns clusters.

*(First reported as 534/61. That count matched `.Kubeconfig` as text, which also caught
`.KubeconfigSecretId` — a foreign key — and the `VaultSecretType.Kubeconfig` enum member.
Thirty were those. Overstating a problem is no better than understating it.)*

**This explains a measurement that previously looked like ordinary coupling.** §4.0 found
`KubernetesCluster` read by 31 services across module boundaries and filed it under "hub".
It is not a hub. Those services do not want a cluster's *name*; they want its *credentials*,
because the factory signature makes every caller a custodian.

It matters twice:

- **As security.** A credential passing through twelve modules has twelve places it can be
  logged, cached or mishandled.
- **As architecture.** No module can stop depending on Fleet's cluster table while it needs
  the kubeconfig out of it. `IFleetApi` deliberately carries no credential, which is correct —
  and it means the contract cannot serve these 534 callers at all. **This is a hard blocker on
  the remaining conversions, not a tidying job.**

**The fix is now built, and unused.** `IClusterClient` is bound to one cluster and holds the
credential itself; `IClusterClientFactory.ForAsync(tenantId, clusterId)` resolves it through
Fleet's own context — tenant-scoped, and returning null rather than a `.Kubeconfig!` that
throws further in for the two ordinary cases, a cluster that is not this tenant's and a cluster
with no credential stored. Callers name a cluster by id and never see what is used to reach it.

Migrating the 508 call sites onto it is the remaining work, best taken file by file starting
with the five that hold half of them. `ClusterCredentialCustodyTests` ratchets the number so it
cannot grow while that proceeds — in both directions, so slack has to be banked rather than left
lying around. **508 → 259 so far**, with the seven DataServices services done outright (none of
them can even reach the raw-credential API any more) and `KubernetesOperationsService` down from
72 to 56.

**Convert the private helpers, not the public methods.** The largest file turned out to be much
less work than its 72 sites suggested, because its routing, gateway, L4 and mesh paths shared
eight private helpers that each took a `string kubeconfig` only because their callers had one —
`ApplyRawYamlAsync` alone funnelled ten applies. Changing the helpers' parameter moved every
caller at once and converted each caller's `IsNullOrWhiteSpace(cluster.Kubeconfig)` guard into
the `ForAsync` that already answers it. Two categories of the classification below, 96 + 70, are
largely this shape.

**The seam is also the first thing that makes these paths testable.** `KubernetesOperationsService`
had seven tests for 32 public methods, and every one asserted the same thing: a cluster with no
kubeconfig is refused. Nothing observed a manifest, a delete or a patch, because the kubectl
invocation sat behind a `private static` method. So for this file, unlike the DataServices ones,
coverage could not come first — it had to come *with* the conversion. `RoutingClusterCallTests`
is that coverage, asserting on what reaches the cluster and verified by mutation.

**What actually gates the rest, measured rather than assumed.** Site count has been the wrong
way to choose the next conversion, repeatedly. Of the 246 credential sites left, **40 are in a
method that already has a tenant in scope and 206 are not** (`CustodyTriageTests`). *(Those
figures are from 2026-10-11; §4.0.1.4 has the current ledger.)* The seam is
not the constraint any more — it can run kubectl, build a typed client, pooled or not, run helm and
apply or delete a whole manifest set. The constraint is that `ForAsync(tenantId, clusterId)` needs
a tenant, and most of the code reaches clusters by id alone.

> ⚠️ **"The seam is not the constraint any more" was wrong**, and reading it as settled fact cost
> several conversions their simplest route. Six times afterwards, a caller that could not stop
> holding a credential turned out to be a caller the seam could not express — a missing verb, a
> missing parameter, or a convention applied in one method and not its sibling. §4.0.1.4 lists
> them. The sentence is left standing because the correction is the useful part.

`PrometheusService` is the clearest case and the reason this matters. Twenty sites, which reads as
the obvious next job — and **all twenty are unreachable**, because not one of its fifteen public
methods receives a tenant. *(It is now at zero; §4.0.1.3 records how, and what the shape of that
work turned out to be.)* Converting it is not a refactor; it is deciding who may read which
cluster's metrics and where that tenant comes from, then changing fifteen signatures and their
callers. The same is true of `KubernetesOperationsService` (49 sites, none with a tenant),
`HeadscaleService` (17), `NodeManagementService` (13) and `ComponentScanService` (10).

Two capabilities are also genuinely still missing, and they are small:

- a **server-side dry-run apply** and a **server version read**, which is what stops
  `DriftDetectionService` moving even though all five of its sites have a tenant;
- a **default namespace on apply**, for documents that do not name one — `ApplyManifestAsync` has
  no namespace parameter and adding one touches 65 Moq setups and 153 callers, so it wants its own
  change.

So the remaining work splits three ways: **40 sites that could move today**, **two small seam
additions**, and **206 sites behind an authorization question that is not ours to answer by
refactoring**. The third is the large one, and it is the same question §5.4 asks about the gate —
who is acting, and on whose behalf.

#### 4.0.1.1 Two things the custody count does not say (measured 2026-10-11)

The 508→223 figure counts kubeconfigs that stopped travelling. That is real, and it is not
the same as calls that became authorized. Two measurements separate them.

**19 of the 48 seam calls get their tenant from the cluster row they are naming.**
`IClusterClientFactory.ForAsync` enforces `c.TenantId == tenantId`, so

```csharp
await clusterClients.ForAsync(cluster.TenantId, cluster.Id, ct)
```

asks for the cluster whose tenant equals its own tenant. The wrong-tenant branch cannot fire.
At the call site it is indistinguishable from a real check, and it satisfies the custody
ratchet. The split is 22 **independent** (a `tenantId` from the caller — the only kind that
authorizes anything), 7 **cross-row** (a tenant column on a different row that holds a foreign
key to the cluster, which compares two tables and so catches a mismatched row: an integrity
check, not an authorization one), and 19 **tautological**.

This is not carelessness. Every one of the 19 is in a method with no tenant in its signature,
so the cluster row was the only tenant in scope. One of them says so in a comment already.
They are concentrated — eleven are `KubernetesOperationsService`'s, four
`ComponentLifecycleService`'s — which is the useful part of the figure: the 206 gated sites are
not 206 separate decisions.

`VacuousTenantCheckTests` bans growth and resolves a tenant laundered through a local, because
otherwise the cheapest way to satisfy it would be to assign the expression to a variable first.

**`PlannedClusterChange.Kubeconfig` is `required`, and it costs less than it looks.** The gate
is constructed in **13 places across 5 files**, and in only **2** of them is the gate the last
reader of a stored credential.

*(First written here as "35 gate call sites across 18 files". That counted `Kubeconfig = ` as
text, which caught `VaultSecretType.Kubeconfig = 3` — an **enum member** — along with the
materialization interceptor and the client factory's own parameter plumbing. It is the identical
mistake this section already records two paragraphs above, made against the same field. The
count is `new PlannedClusterChange`, and it is 13.)*

Where the 13 are:

- **7 in `KubernetesClientFactory`** and **1 in `KubernetesOperationsService.RequireAckAsync`**,
  where the kubeconfig is already the method's own `string kubeconfig` parameter. The gate adds
  no custody whatever: it is handed what the caller handed in.
- **1 in `KubernetesJitProvisioner`** for a kubeconfig the provisioner **generates** for a JIT
  grant. Not a stored credential at all.
- **2** (`TrustBundleService`, `CertificateDistributionService`) that sit directly above a client
  call passing the same credential, so the gate is not why it is in scope.
- **2** — `TrustBundleService.ApplyBundleAsync` and
  `CertificateDistributionService.DistributeAsync` — where the apply itself already goes through
  `IClusterClient` and the gate's field plus its null guard are the **only** remaining reason the
  method touches `.Kubeconfig`.

So this is not an independent ceiling, and changing the model first would be the wrong order. The
gate becomes the last reader only *after* a file's client calls move to the seam, which is what
happened in those 2. The sequencing is: convert the client calls, and the gate's requirement
turns into the final site in that file — then it is worth changing, and by then the shape is
obvious, because something has to tell the gate which cluster it is diffing.

Note also that the gate cannot simply take `IClusterClientFactory`: `KubernetesClientFactory`
depends on `IClusterChangeGate`, and `ClusterClientFactory` depends on that, so the gate asking
the seam for a client is a dependency cycle. Whatever resolves the credential for the gate has to
sit below both — sharing the `c.Id == clusterId && c.TenantId == tenantId` predicate, not the
seam that applies it.

#### 4.0.1.2 DECIDED (2026-10-12): a sweep acts as each tenant in turn

The question this section has been blocked on — *who is acting, and on whose behalf* — is
answered. **A background sweep acts as each tenant in turn.** Its outer loop is over tenants and
it calls the existing tenant-scoped APIs once per tenant.

Two alternatives were offered and rejected:

- **a system principal** (`ActorContext.System(reason)`) accepted by the contracts and the seam,
  recorded in the audit log — one query per sweep instead of N;
- **installation-scoped read-only contract methods** usable only by services declared as sweeps,
  with a ratchet forbidding their use elsewhere.

Both cost **a second authorization path that has to stay correct forever**, with a ratchet as the
only thing stopping a page reaching for the wide surface. The per-tenant loop adds no concept at
all.

**The deciding argument is not the query count.** Iterating tenants makes the tenant an *input*
to every lookup rather than something read off the row being authorized. That is exactly what the
19 tautological seam calls (§4.0.1.1) are: `ForAsync(cluster.TenantId, cluster.Id)` exists because
the method had no tenant to pass. A sweep that iterates tenants has one, so the predicate can
fail — which is the difference between a check and a decoration.

`CostScanService` already worked this way, so the shape was in the codebase before the decision
named it: collect tenant ids, loop, per-tenant try/catch, rethrow cancellation.

**First application — the headscale cert sweep, and it is the pattern for the rest.**
`HeadscaleCertSyncService` selected cluster ids across the whole installation and acted on them;
it now loops tenants and asks Catalog for each tenant's own headscale installs.
`HeadscaleService`'s 29 public methods take the tenant their two Razor-page callers always had,
its `GetComponentAsync` funnel (thirteen internal callers) asks Catalog tenant-first, and the
credential comes from `IClusterClient`. **Custody 200 → 183, contract 27 → 25** — the single
largest fall either number has had, from one service.

Order the rest by the same rule the triage taught: `AlertSyncService`,
`ResourceUsageCollectorService` and `HeadscaleCertSyncService` were in *both* counts, so a sweep
conversion pays twice.

**A derived tenant is not a label.** Three more sweeps now ask per tenant, and all three had
written the derivation off as bookkeeping:

- `AlertEscalationService` selected every unacknowledged incident in the installation and read
  `i.Cluster.TenantId` off each row — and that is the tenant the escalation is **re-notified as**.
  An incident whose `ClusterId` pointed at another tenant's cluster would have reached that
  tenant's channels about an alert from somewhere else.
- `KeycloakBackupSchedulerService` carried `r.TenantId` out of each scheduled realm into
  `CreateBackupAsync` — the call that resolves the realm's cluster credential and its storage link.
- `BootstrapRunnerService` read the queue across the installation and took each run's tenant off
  its cluster row, then handed it to the component registrar and the install orchestrator. That is
  the tenant an entire cluster bootstrap acts as. The `(tenant, run)` pair now travels together
  from the queue read, rather than being flattened to run ids and re-derived — flattening is the
  documented way to throw away exactly what the loop establishes.

**What made them assertable.** Each selection is now a named `public static` query
(`DueFor`, `ScheduledFor`, `QueuedFor`), the same device as the seam's public argument builders.
The logic lived in a `private` method of a `BackgroundService` behind a startup delay, so there was
nothing a test could call; and the shape being replaced returned *every* row in the installation,
which satisfies any test that only counts what came back for one tenant. `SweepTenancyTests` gives
two tenants a qualifying row each and requires each to see exactly its own — verified by deleting
the tenant predicate, which fails exactly the three tests that are about it.

**The set is now classified rather than counted.** `SweepTenancyRatchetTests` names every
management-plane hosted service as one of four things: it loops the tenant table, it delegates to
something that does (and that owner is named), it has no tenant-scoped work (with a reason), or it
is in the backlog (with a reason). **16 loop, 3 delegate, 2 are exempt, 6 remain** — and a new
sweep cannot be added without picking one, which is the part that pays: every conversion so far
began by re-deriving which sweeps were left.

The honest half of it is the test that requires the loop signal to be **absent** from the backlog
and the exemptions. Without that, a sweep someone converts and forgets to move out of the backlog
sits there forever, making finished work look like work remaining — and the count would drift the
way it already had.

⚠️ Three things the classification corrected, all of them measurement rather than code:

- **Enumerating `AddHostedService<T>` alone finds 25; the answer is 27.** `GitSyncService` and five
  `SegmentSealService`s are registered through lambdas (`sp => new …`,
  `sp => sp.GetRequiredService<…>()`).
- **`SupportMailPoller` reads as converted and is not.** It has
  `foreach (Guid tenantId in due)` — and `due` comes from `SupportMailboxes`, so the tenants it
  takes mail in as are taken from the work rows. *The loop variable's name is not evidence of where
  its value came from*, which is the third time in this programme that name-shaped evidence was
  wrong.
- **The in-cluster node has three hosted services of its own** — `ObjectStorageBudgetService`,
  `SegmentCacheTrimService`, `VolumeGuardService` — and this decision does not reach them: a node
  serves one cluster and authenticates with a token, so there is no tenant to act as. They are
  named in the test so the next reader can see they were considered rather than missed.

⚠️ **A count of "sweeps that loop tenants" is easy to get wrong, twice over.** A regex for
`.Tenants ... Select(` misses `AdvisorScanService`, which loops `db.Tenants.ToListAsync()`, and
misses `GitSyncService`, whose loop is upstream of a queue. Separately, four hosted services look
exempt because they only call a `ReconcileAllAsync` elsewhere — and none of those four owners
(`RabbitMQService`, `KafkaService`, `ElasticsearchService`, `CertificateDistributionService`)
loops tenants either: they select installation-wide and read the tenant off the row, which is
*cross-row* rather than tautological and so invisible to `VacuousTenantCheckTests`. Classify by
reading, not by matching; a ratchet over this set is worth having, but it needs the whole set
classified first and must not be introduced while a PR in flight changes the count.

**It also strengthens the case for §5's agent model.** An agent running *inside* the cluster
needs no kubeconfig at all — there is nothing to hand out, because the work happens where the
credentials already are. Credential custody was presented there as a side benefit of
inverting control. On this measurement it is one of the main ones.

#### 4.0.1.3 Done (2026-10-12): the largest block, and what it cost

`PrometheusService` is at **zero**, from twenty. It is worth recording because §4.0.1 named it the
clearest case of the hard kind — twenty sites, none reachable — and the way it actually went is
now the method for the rest.

**Custody 183 → 160, in six landed steps** (`#179`–`#184`), each one compiling and tested. The
first attempt was one change threading all seventeen signatures at once; it never compiled, and the
error count went up as often as down, because every signature threaded surfaced callers that had
not been counted. What worked:

- **Two resolutions side by side.** The old `ResolvedPrometheusInfo` (a kubeconfig string, found by
  `c.Id == clusterId`) stayed while a new `ReachablePrometheus` (an `IClusterClient`, found
  tenant-first) grew beside it, and methods crossed one at a time.
- **Pick the method whose caller already holds a tenant.** The first two shared one caller whose
  rows already carried `TenantId`, so there was no `[Parameter]` plumbing at all.
- **When plumbing is needed, do the subtree in one step.** Seven methods moved together in `#182`
  because they share exactly one act: giving `ClusterMonitoring` a `TenantId`, which then reaches
  three panels. Splitting them would have added a parameter in one change and used it in another.
- **The last step deletes more than it converts.** With the final caller gone,
  `ResolvedPrometheusInfo`, `ResolvePrometheusInfoAsync`, the `string kubeconfig` overload of
  `WithServiceAsync`, `CreateK8sClient` and the pool field all went at once.

Three things this surfaced that apply to every remaining conversion:

- **Threading a tenant into a method creates work inside it.** Giving `RolloutWatcherService` a
  tenant made its two remaining credential reads *reachable* in the triage's sense — a kubeconfig
  read in a method that now knows its tenant — and that ratchet failed, correctly. The conversion
  had created the easy case rather than taking it. Budget for converting those in the same step.
- **A conversion can need a new seam verb, not just a new caller.** `kubectl rollout undo` is none
  of apply/delete/patch/scale/restart/helm: nothing in it names the state being moved to, because
  the cluster remembers the revision. Hence `UndoDeploymentsAsync` and `ChangeVerb.Rollback` — and
  one fewer own-process kubectl outside the gate (§5.4), 13 → 12.
- **Moving a kubectl call can lose a protection its old caller had.** `RolloutService`'s own runner
  closed stdin, because a kubeconfig without usable credentials makes kubectl *prompt* and an
  inherited stdin never delivers the EOF that ends the prompt — in a sweep, a hang nobody sees. The
  seam's runner did not. Read the runner being deleted.

**A sweep must not flatten its pairs.** `ResourceUsageCollectorService` looped tenants to find
clusters and then reduced them to a list of cluster ids, which threw away precisely what §4.0.1.2
decided to keep: it knew the tenant, discarded it, and the call it then made would have answered
about any cluster. The pair is the unit.

**Convert the private funnel, not the public method** — the fourth file where that was the whole
job. `CostReportService` had the tenant at the top of `GetTenantReportAsync` and dropped it through
five private helpers, the last of which read a kubeconfig by cluster id to count load balancers.

What is left is **160 sites in 32 files**, and the balance has shifted: the biggest single holding
is `KubernetesOperationsService` with 46, which also holds eleven of the nineteen tautological seam
calls (§4.0.1.1). It is the same question, now concentrated.

### 4.0.2 The shape no service-level count could see: the pages themselves (measured 2026-10-22)

The god-context metric in §4.0 counts **services** — by constructor, by scope, and since #327 by
static helpers taking the context as a parameter. A **Razor component** is none of those: it is not
in `ModuleServiceMap`, has no constructor reflection can read, and is compiled from a `.razor` file.

**31 components injected `IDbContextFactory<ApplicationDbContext>` and queried the database from the
page**, while every existing count read unchanged. That is more sites than the 35 services the
programme has been tracking, and none of it was visible.

Classified by what the tables themselves can decide, rather than by preference:

| bucket | n | basis |
|---|---|---|
| dead injection | 2 | injected and never used — deleting the line is the whole fix |
| exactly one viable module context | 10 | derived: only one context exposes every table the page touches |
| hub-only, subject derives from its services | 7 | touches only Tenant/Customer/App/KubernetesCluster, but all its injected services belong to one module per `ModuleServiceMap` |
| hub-only, subject does not derive | 10 | its services split across modules, or it injects none |
| no viable context | 2 | genuine contract work: `CustomerStatusDashboard`, `SlaTargetManager` |

**19 converted, 12 left**, banked by `RazorGodContextRatchetTests`.

**Why a hub-only page is still worth converting.** Fourteen of the 31 only looked up a cluster or a
customer — tables every module context already exposes read-only as hubs, so the conversion changes
no query. The cost was never what the page reads today; it is what the type system permits tomorrow.
On `ApplicationDbContext` the next edit can join anything and nothing fails. On the module's own
context it does not compile. `ModuleDbContext` builds the *same full model*, so the swap is
compile-time checked and runtime-identical — the compiler found every mistyped local, and there was
nothing left for a test to catch.

⚠️ **Two measurement traps, both hit here.**
- The first scan matched the type name anywhere in the file and reported **33**, two of which were a
  *comment* about `IDbContextFactory` in a page that injects nothing. A grep for a subject matches
  prose about that subject.
- The second resolved "which context" by asking which one *covers* the page's tables, and answered
  `AdvisorDbContext` for every cluster-lookup page — because the hubs are declared `IQueryable`, not
  `DbSet`, every context exposes them, and `Advisor` sorts first. Coverage is necessary and not
  sufficient; ownership is what picks, and where ownership is a hub the tables cannot pick at all.

### 4.1 Modules (logical — inside the monolith first)

Twelve bounded contexts, drawn where the code already clusters:

| Module | Principal code | Owns |
|---|---|---|
| **Identity** | `KeycloakService`, `Sso/`, `Scim/`, `Jit/`, `Authorization/`, `ApiToken` | users, tenants, roles, tokens, JIT grants |
| **Fleet** | `KubernetesOperationsService`, `KubernetesClientFactory`, `NodeManagementService`, `ClusterProvisioningService`, `WorkloadService`, `ClusterChanges/` | clusters, nodes, kubeconfigs, the apply path |
| **Catalog** | `ComponentCatalog` (5,555 lines), `ComponentLifecycleService`, `ComponentScanService`, `CatalogComponentRegistrar`, `Upgrades/`, `Adoption/` | component definitions, installs, upgrades, drift |
| **DataServices** | `CnpgService`, `MongoService`, `RedisService`, `KafkaService`, `RabbitMQService`, `ElasticsearchService`, `OpenLdapService`, `HarborService`, `StorageService`, `RegisteredPostgresService` | managed service lifecycle |
| **Mail** | `StalwartService`, `StalwartPlanBuilder`, `MailWorkloadManifests` | the mail stack |
| **Delivery** | `DeploymentSyncService`, `DeploymentImportService`, `Rollouts/`, `AppGovernanceService`, `KyvernoPolicyService`, `KedaScalerService` | apps, environments, deploys, policy, autoscaling |
| **Connectivity** | `ConnectivityGraphService`, `ExternalRouteService`, `AppRouteService`, `IngressDashboardService`, `HeadscaleService`, `VpnService` | routes, network policy, mesh, VPN |
| **Secrets** | `VaultService` | the vault, cert/OAuth/kubeconfig secret types |
| **Telemetry** | `EntKube.Telemetry`, `PrometheusService`, `LokiService`, `TelemetryAlertEvaluator`, `IncidentService` | logs, traces, metrics, alerts, incidents |
| **Cost** | `Cost/` | rates, ledger, chargeback |
| **Support** | `Tickets/`, `Contracts/`, `Time/`, `Knowledge/`, `Mail/Support*`, `OnCallService` | the consultancy business layer |
| **Advisor** | `OperationsAdvisor`, `AdvisorScanService` | the cross-cutting findings feed (reads everything, owns nothing) |

Advisor is deliberately last and deliberately read-only. It is the module that
proves the contracts work: if Advisor can be written entirely against other
modules' public APIs, the boundaries are real.

### 4.2 Deployables (physical)

Six server processes and two in-cluster ones. Not twelve, not twenty.

```
                        ┌───────────────────────┐
  operator browser ───► │  entkube-ops-bff      │ ─┐
                        └───────────────────────┘  │
                        ┌───────────────────────┐  │
  customer browser ───► │  entkube-portal-bff   │ ─┤
                        └───────────────────────┘  │
                        ┌───────────────────────┐  │   internal API
  CLI / MCP / agents ─► │  entkube-gateway      │ ─┤   (shared DB)
                        └───────────────────────┘  │
                             ▲ agent WebSocket      │
                             │                      ▼
                             │            ┌──────────────────┐
                             │            │  entkube-api     │ core control plane
                             │            └──────────────────┘
                             │            ┌──────────────────┐
                             │            │  entkube-worker  │ the 24 sweeps
                             │            └──────────────────┘
                             │            ┌──────────────────┐
                             │            │  entkube-support │ own schema
                             │            └──────────────────┘
                             │            ┌──────────────────┐
                             │            │ entkube-telemetry│ own store (S3+Lucene)
                             │            └──────────────────┘
      ┌──────────────────────┴───────────────────────┐
      │ customer cluster                             │
      │   entkube-agent        entkube-telemetry-node│
      └──────────────────────────────────────────────┘
```

**Why each one exists** — a deployable with no answer here does not get created:

| Deployable | Why it is its own process |
|---|---|
| `entkube-api` | the core. Scales with operator activity. |
| `entkube-worker` | the 24 hosted services. A cost scan should never be able to stall a circuit, and sweeps scale on cluster count, not user count. **Highest value, lowest risk — extract this first.** |
| `entkube-gateway` | different *trust boundary*. It terminates connections initiated from customer clusters and from third-party tokens. That belongs behind its own blast wall. |
| `entkube-telemetry` | already an assembly with its own storage engine; just needs to stop being hosted in the web process. |
| `entkube-support` | genuinely different domain, different users, its own mail poller, and the least entangled with the K8s graph. Its schema can actually separate. Also: this work is already on its own branch. |
| `entkube-ops-bff` / `entkube-portal-bff` | **this is where BFF earns the name.** Two audiences with two authorization models — the Portal is customer-facing and confines everything to an app's bindings (`PortalServiceScopeService`); Ops is tenant-wide. One API serving both is how authorization bugs happen. |

Everything still runs under one `docker compose up`. The compose file grows from
one app service to six; that is the whole operational delta for a small install.

---

#### 4.0.1.4 Ledger and what the seam learned (2026-10-13)

The conversion is far enough along that the useful thing to record is not the next step but the
shape the work turned out to have. Where it stands, against the figures this section opened with:

| measure | at the start | now |
|---|---|---|
| credential sites | 508 in 57 files | **15 in 8** |
| tautological seam calls (§4.0.1.1) | 19 | **0** |
| foreign reads of `ClusterComponents` | 87 | **4 direct + 2 navigation** |
| foreign *writes* to it | 22 | **0** |
| own-process kubectl/helm outside the gate (§5.4) | 49 | **1** |
| component lookups escaping their tenant | 14 | **0** |
| direct module reads in the **public API** | 8 | **0** |

Those are seven ratchets, each with a banked baseline and a second test that fails when the
baseline carries slack, so none of them can drift back quietly.

**The public-API row is the first to reach zero with nothing deliberately exempted**, and it is the
row §1 argues matters first: every route the CLI, the MCP server and any future BFF go through now
asks a module contract rather than the shared database. ⚠️ Zero there does *not* mean the API is
finished — the routes are still 22 against ~250 services, which is the gap the agents need closed
and a different measurement entirely.

The last one was `GET /api/v1/rollouts`, which walked rollout → deployment → app → customer: four
navigations of Delivery's own structure, written out by hand in the public API because
`IDeliveryApi` had no method for rollouts. It has one now, with that route as its only consumer —
the shape `IDataServicesApi` was added in. ⚠️ And it carried the hazard §4.0.1 already records: the
route publishes `Status.ToString()`, so the string clients parse now comes from the *contract's*
copy of the enum. `ApiRolloutStatusTests` pins the two together and pins the near-miss —
`RolloutStatus` is a **different** enum belonging to a blueprint rollout, one word away from
`DeploymentRolloutStatus`, and mapping one onto the other would publish a wrong verdict for every
release.

⚠️ **The foreign-reads row was wrong for several batches, and the measurement was why.**
`CatalogContractAdoptionTests` resolved a file's owning module by looking up its *file name* in a
map keyed by *type* names, and skipped whatever it could not find — so a file whose name differs
from the class inside it was silently exempt. `OperationsAdvisor.cs` (declaring
`OperationsAdvisorService`), `Mail/SmtpSettings.cs` (declaring `SmtpSettingsResolver`) and
`PublicApi/PublicApiEndpoints.cs` held four real queries between them, one of which read KEDA
installs across the whole installation with no tenant predicate, and one of which was in the
**public REST API**. The true figure when the row said 6 was 10.

Ownership now comes from the types a file declares, `ModuleServiceMap.PlatformServices` supplies
the one exemption that is deliberate (`BackupService`, a whole-installation export), and a file
with no mapped type is counted rather than skipped. All four sites then converted, so the row is
6 again — and means it. **The lesson is the one §4.0.1.1 already records in a different form: a
number that becomes documentation has to be checked against an independent measurement, because
by the time it is wrong it is also an argument.**

**The seam was the constraint far more often than the callers were.** §4.0.1 said the opposite —
"the seam is not the constraint any more" — and that was wrong in a specific and repeatable way.
Six times, a caller that *had* to hold a kubeconfig turned out to be a caller the seam could not
express:

- **`PlannedChange`** — the largest one. `PlannedClusterChange.Kubeconfig` is `required`, which
  guarantees the gate can always compute a preview. For callers that mutate through the k8s SDK
  rather than through a seam operation, that requirement *was* the reason they read a credential:
  they needed one to ask the gate. `IClusterClient.AcknowledgeAsync` takes the operation and the
  seam fills in the credential, so the invariant holds and the caller is free of it. This alone
  unblocked about twenty sites in the file that held the most.
- **`UndoDeploymentsAsync` + `ChangeVerb.Rollback`** — `kubectl rollout undo` is none of
  apply/delete/patch/scale/restart/helm, because nothing in a rollback names the state being
  moved to: the cluster remembers the revision, not EntKube.
- **`DrainNodeAsync` + `ChangeVerb.Drain`** — same reasoning, and see the warning below.
- **`ApplyManifestFromUrlAsync`** — an operator install published as a URL. Downloading it to
  pipe the text would make EntKube the thing that must reach the internet rather than the cluster.
- **`GetResourceYamlAsync`**, and a **`container` parameter on `GetPodLogsAsync`**. The second is
  a single optional argument, and its absence is why a kubeconfig sat on disk for the duration of
  every helm run.
- **An empty namespace meaning "cluster-scoped"**, in `DeleteManifestAsync` and `GetJsonAsync`.
  `ResourceExistsAsync` had read it that way from the beginning and documented the convention; the
  other two sent `-n ` and were rejected by kubectl, which is why two services kept their own
  kubectl for a trust Bundle and a Namespace list.

The useful generalisation: **when a caller cannot stop holding a credential, check the seam's
vocabulary before accepting it.** Three of the six were recorded in this repository as
impossibilities — see below — and all three were a missing verb, parameter or convention.

**⚠️ Three "cannot"s that were wrong, and what they had in common.**

1. `ComponentTenantScopingTests` exempted one component lookup because its caller "addresses
   components by id by design and holds no tenant of its own". The caller took a `tenantId` and
   used it on the next line.
2. `ClusterChangeGateCoverageTests` listed four invocations as ones "the seam can express none
   of". Two were the missing verb and parameter above; one had already been solved.
3. A service's own comment explained that its tenant check "is vacuous here" because the method
   "is reached by component id by design". All five callers had a tenant.

Each claim was *about a caller*, written without opening it; each then propagated into a code
comment where the next reader found it as settled fact; and each survived several readings
because it was phrased as an architectural limit rather than as a missing argument. The
discipline that follows: an exemption is a claim that something is impossible, so open the caller
before writing one, and when one is retired keep the entry with the correction rather than
deleting it.

**⚠️ A verb whose gate behaviour was wrong in the safe-looking direction.** The node drain was
first written as `ChangeVerb.Delete` with `Kind = Node`. The gate computes a dry-run preview per
verb and skips the dialog when the preview reports no change — so for a node whose object could
not be read, it concluded "no change" and **applied the drain without asking anyone**; and when
the object *could* be read, it described deleting a node that a drain leaves in place. Both were
found by a test that drives the real factory with a cancelling sink, which is the only kind that
can see this: a mocked factory asserts that a method was called, not that the gate agreed.

**Sweeps.** §4.0.1.2's decision has been applied nine times (headscale certs, alert sync,
resource usage, deployment sync, git sync, L4 route health, certificate distribution, the rollout
watcher, and the telemetry alert evaluator). The last of those is the clearest statement of what
the decision is *for*: its rules carry a `TenantId` and it read that off the row it had just
loaded, which is a tenant to label an incident with and not a tenant to ask as — every query
beneath it was answered for whatever rows happened to be there. Two hazards recurred and are worth
naming, because both produce a *silent* wrong answer:

- **Flattening the pair.** A sweep that loops tenants to find work and then reduces it to a list
  of cluster or deployment ids has thrown away the thing it just established. The tenant has to
  travel with each unit of work — including across a queue boundary, which is where
  `GitSyncService`'s on-demand path was dropping it after a webhook had already resolved one.
- **A memo keyed on the old question.** When a cached lookup gains a tenant, the cache key must
  gain it too. A tenant who may not see a cluster gets an empty answer cheaply, and under a
  cluster-only key that answer is then served to the owner — logs routed to a Loki that is not
  installed, a gateway address read as absent. Test it by asking stranger-then-owner; the other
  order passes against the bug.

**⚠️ An interface that is part of a shipped artifact cannot take an authorization parameter.**
The telemetry routing layer was the last contract query's blocker, and it was not shaped the way it
looked. It hands the viewers an `ILogBackend`/`ITraceQueryService`, so the move was apparently a
`tenantId` on those methods — but both interfaces live in `EntKube.Telemetry` and are implemented
**inside the in-cluster node**, where a tenant means nothing: a node serves one cluster and
authenticates with a token. Every implementation but the management plane's would have ignored the
parameter, and a management-plane authorization change would have become a change to the node's
wire surface — which reaches no cluster until the chart's `appVersion` and both catalog pins are
bumped and a new image ships. The tenant goes on a factory in front of the interface instead
(`ITelemetryBackends.LogsFor(tenantId)`, the same shape as `IClusterClientFactory.ForAsync`). Worth
checking, before the next cascade that ends at an interface, whether anything outside this process
implements it.

**No ordinary conversion work is left** (2026-10-15). `DeploymentImportService` and
`KubernetesOperationsService` were the last two files holding sites that were simply work, and both
are at zero:

- `ImportAsync` already took a tenant and refused a cluster that was not that tenant's; the **scan**
  feeding it still read the credential off whatever row the page passed in, so a preview —
  namespaces, workloads, secrets, PVCs — could be built for a cluster the caller does not own.
  *Half a feature authorized* is the shape worth watching for: the write was fixed and the read that
  precedes it was not.
- ⚠️ `KubernetesOperationsService`'s last site was **also an ungated delete**.
  `DeletePrunedResourceAsync` wrote the kubeconfig to a temp file and ran its own `kubectl delete`,
  so every resource dropped from a manifest set — which is what pruning *is* — was deleted with
  nobody asked and nothing recorded. §5.4's metric cannot see it, and says so about this very file:
  one that acknowledges *anywhere* scores zero. That is the **second** delete path this blind spot
  has hidden (the first was Kyverno's `delete clusterpolicy`), so the way to find the next one is to
  grep for own-process `kubectl` inside the files that metric *excludes* rather than trusting its
  total. **That sweep has now been done and comes back empty:** the only own-process CLI left in an
  excluded file is `helm repo` / `helm repo update`, which touches no cluster and is excluded by
  name on purpose. So the blind spot is not one case smaller — it is closed, until the next file
  starts acknowledging somewhere and running its own binary elsewhere.

Three things were checked before routing that delete through the seam, each of which would have
changed behaviour silently: `--ignore-not-found` survives (`BuildDeleteArguments` passes it, so a
resource already gone still counts as pruned); the seam **throws** where the helper returned false,
and its caller counts failures and carries on pruning, so the conversion catches; and the gate's
delete preview returns `NoChange()` for an object it cannot read *by design* — "mirrors
--ignore-not-found" — which is right for a prune and the opposite of the node-drain bug, where
`NoChange` meant "apply the destructive thing without asking".

⚠️ **Two open changes both move this constant against different trees** — one banks 18 with these
three sites still present, the other 19 with `RemediationService`/`TenantService` still present.
Whichever lands second must **re-measure** (expected 15 in 8) rather than keep either number; a
ratchet constant has no correct merge.

⚠️ **And the merge of those two changes is itself the lesson.** Each PR said that whichever landed
second had to re-measure, because one banked 18 against a tree where the other's three sites were
still present and vice versa. The merge instead kept one side's constant and took both sides'
conversions, so `main` carried **three sites of slack with its slack test failing**, *and silently
dropped the paragraph above* — the ungated-delete finding and the sweep result — in favour of the
other side's version of the same section. Both were restored by re-measuring and re-reading, not by
re-resolving the diff. **A ratchet constant has no correct merge, and neither does a prose section
two branches both rewrote**: git cannot know which number describes the merged tree, and a textual
resolution of two single-line changes looks completely clean.

**What the remaining 15 are made of** (re-measured 2026-10-15 after that merge, not carried forward
from either branch — an earlier version of this list still named `ComponentScanService` with ten
after it had reached zero), and the useful thing now is that **none of it is work**:

- **Supposed to hold a credential — the floor, 6:** `KubeconfigMaterializationInterceptor` (2),
  `ClusterClient` (2), `ClusterChangeGate` (2).
- **A kubeconfig EntKube *mints*, not a cluster's — 2:** `JitAccessService`,
  `PortalJitAccessPanel.razor`. Counted anyway, and correctly: a generated kubeconfig carrying a
  live token is also a place it can be logged or cached.
- **Waiting on the credentialed-channel decision — 7:** `StorageLinkClientFactory` (3) and
  `StorageService` (2) hand a kubeconfig to the S3 proxy; and ⚠️ **`OpenStackKeystoneClient`'s 2
  were reclassified on inspection** — they looked like ordinary callers, and the kubeconfig they
  load goes to `ClusterEgressTunnel.GetLocalPortAsync`, the `kubectl port-forward` that is the
  same blocker. *Check where a credential goes, not just how it is read.*

⚠️ **`RemediationService` was the worst-placed missing check this programme has found.** The method
deletes pods and restarts Deployments, StatefulSets and DaemonSets, and it loaded its cluster with
`FirstOrDefaultAsync(c => c.Id == clusterId)` — no tenant predicate anywhere in the file — so a
cluster id was the whole authorization for the most destructive verb in the product. Its one caller
is the alerts page, which always had a tenant. Its **gate** exemption is deliberate and unchanged
(§5.4.1: remediation is the one flow that must act without asking), which is worth stating in a
conversion like this: moving the credential into the seam does not gate the calls.

**`TenantService` reached zero by deleting a line.** Registration stored the kubeconfig in the
vault and then assigned `cluster.Kubeconfig = kubeconfig` "to reflect the just-persisted values on
the returned entity" — handing the plaintext back to callers that never asked, since all three read
only `Name` and `Id`. `CustodyTriageTests` had classified it as *reachable*, i.e. convertible; the
conversion was to remove it. **A reachable site is not always a conversion** — sometimes it is a
credential being handed to people who did not want one.

⚠️ **And one of the 22 was never a credential site at all.** `Program.cs`'s single occurrence was
the log line *"Dropped legacy plaintext KubernetesClusters.Kubeconfig column after vault
migration."* — prose about the column this programme removed, counted as a place the credential
lives, and enough to keep a whole file on the list. The metric now skips matches inside string
literals, as it already skipped comments for the same reason. It is a counting rule rather than a
named exemption on purpose: an exemption would claim `Program.cs` may hold a credential, when what
is true is that a sentence mentioning one is not one.

Six of those sites are **blocked on one undecided seam shape**, and it is worth naming rather than
rediscovering: `StorageService` + `StorageLinkClientFactory` reach an in-cluster S3 gateway with
the AWS SDK through `KubernetesS3Proxy` — many calls, arbitrary object paths, binary bodies — and
`ClusterEgressTunnel` needs `kubectl port-forward`, a process that must outlive the call. Neither
is a command (`ApplyManifestAsync`-shaped) nor a bounded request (`ProxyServiceAsync`-shaped) nor a
description (`GetConnectionAsync`-shaped). A verb that hands back a configured client would serve
all three while granting the same authority — the failure mode §4.0.1.4 records twice — so this is
a decision, not an oversight.

The rest are the seam and its infrastructure, and are *supposed* to hold a credential:
`KubeconfigMaterializationInterceptor` (2), `ClusterClient` (2), `ClusterChangeGate` (2) and one
line of `Program.cs`.

#### 4.0.1.5 Three blockers that were written down in advance (2026-10-15)

`ConnectivityGraphService`, `VpnService` and `LokiService` are at zero, and all three had been
*recorded* as blocked — twice in a comment at the top of the file, once in §5.4 of this document.
Each note was correct about the dependency and each had the same shape: **this cannot move until
that moves, and both have to move in the same change.**

- `ConnectivityGraphService.ApplyNetworkPoliciesAsync` derived its tenant from the app and
  explained that a tenant parameter "makes those reads reachable in `CustodyTriageTests`'s sense —
  they have to move onto the seam in the same change".
- `LokiService`'s header said `ResolveLokiInfoAsync` "loads the cluster WITH its components and
  reads cluster.Kubeconfig off the row", so it had to move as custody work, because "converting
  the storage read on its own would otherwise look like this service had been dealt with".
- §5.4 said `VpnService`'s CLI invocation was "expressible, two credential reads", so it belonged
  with the custody work rather than the gate work.

**A recorded half-measure is a to-do with its precondition attached, and it is cheaper to read than
to rediscover.** Two of the three cost nothing to thread once the precondition was met —
`ConnectivityTab.razor` and `LogQueryService` already had a tenant on every relevant call — which
is the §4.0.1.3 rule ("pick the method whose caller already holds a tenant") arriving for free.
The generalisation worth keeping is the inverse: when a conversion looks blocked, write the note in
the file and say *what would unblock it*, because the next person to read it is the one doing that
other half.

⚠️ **`LokiService` moved nothing on the foreign-read row, and that is a gap in the metric.** It
read Catalog's table through `Include(c => c.Components)` on the cluster row, and
`CatalogContractAdoptionTests` counts `.ClusterComponents`. A navigation read is the same coupling
by a different spelling — it is a query against another module's table, it breaks the same way when
that table moves, and the contract has to serve it either way.

**49 navigation reads in 11 files**, in three spellings: `KubernetesOperationsService` 13,
`ExternalRouteService` 11, `IngressDashboardService` 5, `KeycloakService` 5, `StorageService` 5,
`AppL4RouteService` 3, `AppRouteService` 2, `DatabaseService` 2, and one each in
`ExternalRouteHealthService`, `HeadscaleCertSyncService` and `VpnService`. The metric now counts
them with the same per-mention unit as the DbSet form, and — this is the part that matters — it
**derives the navigation names from the entity model** rather than listing them, so a twelfth
entity gaining a link to Catalog's table is counted the day it is added. The ledger row reads
**4 direct + 49 navigation**, and a new one in any spelling fails the build.

⚠️ **Two measurements of this gap were wrong before this one, both in the same direction, and the
first had already been quoted.** It counted only the `Include`/`ThenInclude` form of `.Components`
and reported *8 in 5 files*, missing every in-memory traversal of the loaded collection; the figure
8 reached a commit message, a pull request and this document. The correction to 22 was then itself
one spelling of three — `Component` and `ClusterComponent` on ten other entities hold the other 27.
That is the third time in this programme that a count became an argument before it was checked (see
§4.0.1.4's file-name/type-name bug), and the rule it keeps earning is: **enumerate a metric's
spellings before trusting its total**, and treat a conversion that moves a ratchet by zero as
evidence about the ratchet.

**47 of the 49 are converted (2026-10-15), and no new contract method was needed for any of
them** — which was the prediction, so it is worth saying that it held. Eight files are at zero:
`ExternalRouteService` (11), `IngressDashboardService` (5), `KeycloakService` (5),
`AppL4RouteService` (3), `AppRouteService` (2), `DatabaseService` (2), `ExternalRouteHealthService`
and `HeadscaleCertSyncService`.

`ExternalRouteService` is the one worth reading. Its eleven were six public static YAML generators
and two split-backend helpers reading `route.Component` for a namespace, a name, a chart name and a
release name — *exactly* `IComponentNaming`'s four members. They take that interface now, so the
contract's DTO and the entity both satisfy them with no conversion, and the two async loaders
resolve the component through `GetComponentAsync(tenantId, …)`. **An interface the contract already
published turned out to be the whole conversion**, which is the inverse of the #138 lesson: there,
four contracts had no consumers; here, a consumer-shaped interface was sitting unused by the people
who needed it most.

⚠️ **Two of the conversions closed real holes that nobody was looking for.**
`DeploymentImportService.ImportAsync` opened with `Guid tenantId = cluster.TenantId;` — a label, not
a check, so whatever cluster row arrived was imported into its own tenant and every tenant-scoped
call beneath it compared that row to itself. And `KeycloakService` took the component off the row
the theme pointed at, so a theme naming another tenant's component deployed a theme onto it; the
contract's lookup is tenant-first, so that now resolves to nothing. Both were found by *threading
the tenant*, not by auditing for missing checks — which is the argument for doing these conversions
rather than only measuring them.

Three of the fourteen in the first pass were reads that had already been **half** moved, and that
pattern is worth watching for: `HeadscaleCertSyncService` asked Catalog for its component ids and
then joined back into Catalog's table for `r.Component.ClusterId`, a field the contract answer in
its hand already carried; `IngressDashboardService` walked `ExternalRoute → ClusterComponent →
KubernetesCluster` for a cluster *name* that `InstalledComponent` also carries; and
`ExternalRouteHealthService`'s `RoutesFor` reached its tenant through two navigations. A service
that asks the contract and *then* follows a navigation has not finished converting — it has added a
round trip.

`KubernetesOperationsService` (13) was the last large holding and the clearest case of the
**half-moved** shape: it already asked `GetComponentsForClusterAsync` in three methods while four
others walked `cluster.Components`, and two of those carried comments explaining that the cluster
row was read "for its COMPONENTS" — the exact read the contract exists to serve. Converting it
**removed more than it moved**: `GetL4EndpointAddressAsync` loaded the row tenant-scoped,
null-checked it and *then* called `ForAsync(tenantId, clusterId)`, the same question twice; and
`ApplyGatewayForClusterAsync`'s `r.Component = c` mutated the navigation purely to feed one line in
`LabelRouteNamespacesAsync`. It also gained a tenant check it never had — its "Cluster not found."
came from `FirstOrDefaultAsync(c => c.Id == clusterId)` with no tenant predicate, so a cluster id
from elsewhere had its Gateway reconciled.

**The two that remain are one blocked method**, and that is the floor until a decision is made
rather than work anyone can pick up: `StorageService.ResolveMinioKubeconfigAsync` walks
`StorageLink → ClusterComponent → KubernetesCluster` to hand a *raw kubeconfig* to
`KubernetesS3Proxy` for the AWS SDK. It sits on the custody ratchet and this one for the same
reason — the **credentialed channel** seam shape below.

The last two conversions were both a *page* holding the entity graph, and neither half could move
alone, because removing the `Include` first leaves the navigation null and the UI silently wrong.
`GetStorageLinksAsync`'s `Include(s => s.Component)` had exactly one reader in the product —
`StorageTab`'s "is this MinIO instance already registered?" check, which wanted the component's
*cluster id* — so the page builds that map from `ICatalogApi` now.

⚠️ **`VpnService`'s looked like the same blocker and was dead code.** Nothing that loads a tunnel
through `GetTunnelWithDetailsAsync` reads `Component` (both callers are the swanctl generators), and
the page's component badge read it off `GetTunnelsForTenantAsync`'s tunnels, which never included
it — so the badge had never rendered, on a field `ReconcileGatewayLinksAsync` does populate. **Check
who actually consumes a navigation before concluding that a page blocks you**: this one cost a line
to delete and fixed a feature on the way past.

⚠️ **The ratchet reads the source tree at test time, so it cannot be trusted while that tree is
being edited.** A full run during this work reported one failure — the navigation count had dropped
below the banked figure because conversions landed while the suite was running — and an earlier
disrupted run reported green on 3,983 of 4,005 tests in 30 minutes. Neither is a result. Any
source-reading ratchet makes the suite non-hermetic with respect to the working tree: finish the
edits, then run it.

**This is the gap the module contexts already documented, and it had no number.**
`ModuleDbContext`'s own summary says it plainly: "the model still contains every entity, so a
navigation property that already crosses a module boundary — there are 94 of those — can still be
walked with `Include` from an entity the context does own. Starting a query from another module's
table is prevented; following an existing foreign key out of your own is not." So the ratchet was
counting **only the spelling the type system already prevents**, and none of the spelling the
design explicitly still allows — which is why "foreign reads: 4" read as *Catalog is nearly done*
while 49 crossings into its table remained. A ratchet aimed at what the compiler already catches
measures the compiler, not the work.

⚠️ Note also what this does *not* mean: a navigation read **compiles and runs fine** on a module
context, because the model is shared. It blocks the next phase, not this one. (I assumed the
opposite for a few minutes — that `ConnectivityDbContext` not exposing `ClusterComponents` would
make `cluster.Components` fail — and the base class's own doc says otherwise.)

**No new contract method serves any of the 49.** Fifteen pass the loaded collection to one of
`ExternalRouteService`'s gateway resolvers, and those already take `IComponentNaming` — the
contract's own interface — so they are contract-shaped today and only their callers feed them an EF
navigation. Four ask "which of this tenant's clusters have X installed?", which is
`GetComponentsForTenantAsync` plus a name predicate in memory. The rest reach *through* Catalog's
row into a foreign module's own table: `ExternalRoute`, `StorageLink`, `KeycloakComponentConfig` and
`VpnLocalEndpoint` are each keyed by component id, so those are real conversions — ask Catalog which
components are on the cluster, then query your own table by those ids.

Tenant availability, which is what decides whether each can move: three of the four
`KubernetesOperationsService` entry points already take one; its private
`ApplyGatewayForClusterAsync` does not and will have to.

#### 4.0.1.6 The id-only sweep, classified (2026-10-20)

The tenant-scoping programme ran out of obvious targets, so the question was measured instead of
guessed: **which public service methods take an entity id, touch the database, and receive no
tenant?** That is the sibling gap §4.0.1.1 left open, and it is a different shape from credential
custody — nothing here reaches a cluster, so the seam cannot catch it.

**43 methods in 21 files.** Measured per site, because the counts that matter are the ones a
signature grep gets wrong in both directions: five of the 43 were already fixed in open PRs, and
one file's ten "hits" turned out to be pure manifest renderers over an entity the caller had
already loaded.

The classification, and it is the point of this entry — **more of them are legitimate than not**:

| Class | Count | What decides it |
|---|---|---|
| Genuine — a tenant surface reads or writes somebody else's row | 17 | the caller is a `Pages/Tenants/*` or portal page |
| Installation-admin surface | 9 | the only caller is an `/admin/*` page with `[Authorize(Roles = "Admin")]` |
| Principal-keyed | 4 | the id *is* the credential, and the caller proved possession before the call |
| Renderers and resolvers | 10 | no row is fetched by that id; it is handed in |
| Dead | 3 | no caller at all |

> ⚠️ **These figures are the corrected ones.** The first version of this table said 9 genuine
> and 17 installation-admin, because it classified `TenantService` (8 methods) and
> `CustomerAccessService` (5) by **file** from a recorded exemption instead of opening each
> method's callers. Eight of those thirteen are reached from tenant pages. The classification
> is only as good as the caller survey, which is the whole point of the paragraph below — and
> it caught me on the first pass.

**The deciding question is almost never in the method.** `TenantRoleService.SavePermissionsAsync`
reads like the worst hole in the codebase — it writes access levels onto a role found by bare id,
which is authorization itself. Its only caller is `/admin/roles`, an installation-admin page with a
tenant *picker*, where acting across tenants is the feature. `TicketBridgeService.DeliverAsync`
reads nearly as badly and is reached from an HTTP endpoint, but that endpoint matches the presented
secret against the connection's hash before it calls: the id is the address and the secret is the
authorization. Both are correct as written. **Opening the caller is not optional diligence here, it
is the measurement** — the same conclusion §4.0.1.1 reached from the other direction, and the same
one two wrong exemptions cost earlier in this programme.

The two fixed in this change share a shape worth naming, because it is why they outlasted the
obvious ones: **neither row carries a tenant, and the owning table belongs to another module.**
`AuditEvent` hangs off a deployment (Delivery's) and `ExternalRouteHealthHistory` hangs off a route
whose tenant lives behind a Catalog component. Neither could be fixed by adding a column to a
predicate — the tenant had to be *asked for*, through `IDeliveryApi.GetDeploymentAsync` and through
the file's own `LoadRouteAsync` funnel. A read that cannot express its own authorization is a read
that gets skipped, and five sibling route methods used that funnel while the sixth did not.

⚠️ **Two recorded exemptions were wrong, and both were inherited rather than checked.**
`TenantService` and `CustomerAccessService` were written down as installation-admin and
principal-keyed surfaces respectively, and I carried both forward across several conversions
before opening their callers. `TenantService` is admin surface in 3 of 8 methods — the three
whose id *is* the tenant, which is also why a predicate on them would be vacuous — and the other
five are reached from `ClusterDetail`, `CustomerTab`, `GroupTab` and three more tenant pages, all
of which had their own `TenantId` one line above the unscoped call. `CustomerAccessService` is
principal-keyed in exactly one method, `GetAccessAsync`, which asks whether *this signed-in user*
may see a customer; the grant, the revoke and the user list beside it are reached from a tenant
page with a bare customer id.

**An exemption is per method, never per file.** One admin page among a service's callers does not
make the service admin surface, and an exemption written at file granularity will be inherited
wholesale — which is what happened here, twice.

⚠️ **One recorded gap is now closed and the note outlived it.** §4.0.1's sibling-gap paragraph named
`AppRouteService` as keeping twelve id-only methods "through the whole programme". It has none: the
only methods without a tenant are the ten manifest renderers, which have no row to authorize. The
note was true when written and is not now, which is the ordinary failure mode of a recorded gap —
it is cheaper to re-measure than to trust.

## 5. The agent model

### 5.1 What exists, and what it is not

`EntKube.Agent` + `EntKube.Agents.Protocol` already implement a WebSocket link where
**the agent dials out** and EntKube never connects inbound. That is exactly the right
transport and exactly the right control direction for reaching a customer network
that permits no inbound traffic.

But it is a **TCP tunnel**, not an agent. The frame types are `Open / OpenAck / Data /
Close / Ping / Pong` — it multiplexes opaque byte streams. It carries no concept of a
task, a result, or a capability.

### 5.2 The generalization

Keep the link, widen the vocabulary. Add task frames alongside the stream frames:

```
Lease       server → agent   here is a unit of work, with a deadline
LeaseAck    agent → server   accepted / refused, with a reason
Progress    agent → server   partial output, heartbeat against the deadline
Result      agent → server   terminal: succeeded / failed + structured payload
Capability  agent → server   on connect: what this agent can do, and its version
```

`Capability` is the important one. It makes the fleet self-describing, so the control
plane never dispatches a task to an agent that cannot run it — and so a rolling agent
upgrade is a non-event rather than a flag day. (This is the same lesson as the
telemetry node ship path: a fix that cannot be version-negotiated reaches no cluster.)

### 5.3 This inverts how EntKube talks to clusters

Today the management plane holds a kubeconfig and shells out to `kubectl` *into* each
cluster. The telemetry work already moved the other way — indexer and querier run
*in* the cluster, and the mgmt plane federates. The agent model generalizes that
inversion to everything else: push the work to where the cluster is.

The payoff is real: no kubeconfig custody for agent-run operations, no mgmt-plane
egress to customer API servers, and operations that survive a management-plane
restart because the lease is durable.

### 5.4 ⚠ The safety prerequisite — do this before any agent mutates anything

`ClusterChangeGate` computes a server-side dry-run diff and blocks for operator
acknowledgment. But read `ClusterChangeGate.AcknowledgeAsync`:

```csharp
// Bypass when the feature is off, or when there is no interactive sink on this scope
// (background/automated flows). This is the "interactive UI only" boundary.
if (!Enabled || _sink is null)
    return;
```

The sink is registered per Blazor circuit. **No circuit, no gate.** Which means that
the moment an agent can call a mutation path, every ack-before-apply protection in
the product silently evaporates for it — it takes the `_sink is null` branch and
applies straight through.

That was a defensible design when the only sinkless callers were trusted in-process
background services. It stops being defensible when a remote agent is a caller.

**The gate has to become durable before agents can write.** Concretely:

- a `PlannedClusterChange` persists as a row with its computed diff, not a blocking call;
- approval is out of band — the UI, a mail reply, an API call with an approver token —
  and the change carries who approved it and when;
- automation declares its intent up front: `RequiresApproval` / `PreApproved(policy)` /
  `Ungated(reason)`, and the ungated path is auditable rather than accidental;
- ~~`RemediationService`'s existing deliberate exemption becomes an explicit
  `Ungated("remediation")` instead of an emergent property of having no circuit.~~
  **Done 2026-10-22 — and the primitive named here was the wrong one. See §5.4.3.**

This is a prerequisite, not a phase-4 nicety. Everything else in this document can
slip; this cannot.

⚠️ **The gate's reach used to be far narrower than "every human-triggered mutation" suggests: 46
invocations in nine files never reached it at all.** The gate covers every mutation that goes
through `IKubernetesClientFactory`, because the factory raises the acknowledgment itself. A
service that spawns `kubectl` or `helm` as its own process bypasses the factory, so the gate is
never invoked — and the recorder below cannot see those either, because there is no outcome to
record when nothing asked. `ComponentLifecycleService` held 21 of them, and is what installs,
upgrades and removes every catalog component on every cluster; `VaultService` 11, syncing secrets
into namespaces; `ClusterProvisioningService` 7. All three are now at zero or exempt for a stated
reason.

This is the same work as §4.0.1 arriving from the other side: routing a call through
`IClusterClient` puts it behind the factory and therefore behind the gate, so custody and gate
coverage come down together. `ClusterChangeGateCoverageTests` ratchets the number and names the
files, and is deliberately a **lower bound** — a file that acknowledges some paths and not others
scores zero, so it can understate the gap but never overstate it.

**Four are left, and the earlier claim that "the helm invocations need a seam operation that does
not exist" is retired: it was wrong twice over.** The `helm repo`/`helm registry` calls never
touch a cluster at all and are excluded by name; the rest needed verbs and parameters that were
small to add, not operations that could not exist (§4.0.1.4). `DockerRegistryService` is converted — it
applied a Namespace+Secret manifest, and the seam has had `EnsureNamespaceAsync` and
`ReplaceSecretAsync` all along, so a registry password was being written into a namespace with
nobody asked and nothing recorded. `ReleaseVolumeGuard` and `VpnService` are converted too, and both confirm that these are
custody work rather than gate work: each also held credential reads, and both ratchets moved in
one change. `VpnService` is worth a note because the metric counted it **once** and it was six
invocations — one private runner called from six places, all `kubectl get … -o jsonpath=…`. The
seam has no jsonpath and should not: the objects are fetched whole and parsed in C#, which also
turned two fetches of the node list into one. Nothing there needed gating, because all six are
reads; what they escaped was the authorization. **One left**: `ClusterEgressTunnel`'s
`kubectl port-forward`, which is the one genuine gap in the seam's vocabulary —
every verb on it is one-shot, and a port-forward is a long-lived process that must stay alive for
as long as the tunnel is used. It is also the only entry that changes nothing — a tunnel is
neither a read nor a change but a channel.

### 5.4.1 Decided (2026-10-09): declare or be refused

The product decision the four bullets above were waiting on has been made, and the first half
of it is built.

**A scope with nobody to ask must say what it is.** `IClusterChangeGate.DeclareUnattended(reason)`
sits beside `RegisterSink` — same shape, same lifetime, and it answers the same question: who is
answerable for what happens in this scope. A declared scope proceeds and every change it makes is
recorded against its reason. **An undeclared one is refused**, with a `RefusedUndeclared` row and
an exception that says how to declare.

That inverts the old failure mode, which is the whole point. Before, "nobody is watching" and
"this is allowed to run unwatched" were the same state, so a background reconcile and a remote
agent were indistinguishable to the gate — and the agent would have inherited every bypass in the
product. Now the two are different states, and the consequence of missing one is a **loud refusal
in a job** rather than a silent unaudited mutation. An incomplete enumeration is therefore safe to
ship: it breaks visibly, in the audit trail, and it breaks forward.

**And agents may not write.** An in-cluster agent may read and may propose — compute a diff, open
an approval row — but every mutation needs a human approver. It is the conservative starting
point, and it can be loosened per-policy later from evidence of what agents are actually asked to
do, rather than from guesses made before the feature exists.

What that leaves to build is the approval queue itself: bullets one and two, a persisted
`PlannedClusterChange` with its diff and an out-of-band approval carrying who approved it and
when. Until that exists, a context that needs approval rather than autonomy has nowhere to wait,
which is why every existing unattended context was declared as what it already does rather than
converted to an approval. `PreApproved(policy)` was deliberately **not** added yet: a label that
implies a policy engine which does not exist would be worse than no label.

Declared today: 26 background scopes, named in `UnattendedDeclarationTests`, plus the two
authenticated API endpoints that mutate — those declare `api-token:<name>`, because an API request
has no circuit but does have someone answerable, and it is already the thing the change is
attributed to. The ratchet is an **upper bound** on purpose: it lists every context that *could*
act unattended, not every one that does, which is the set worth being able to read in one place.

**The other half was already started: the ungated path is now recorded, which it was not.** The
four bullets above were — whether unattended work stalls waiting for
an out-of-band approval, proceeds under a policy, or is refused outright, is a question about
how EntKube should behave, not a detail to settle in passing. The half that needs no such
decision came first: `IClusterChangeRecorder` writes every gate outcome to the audit trail,
so an unattended change leaves a trace.

It matters because the previous behaviour was not "ungated", it was *invisible*:
`AcknowledgeAsync` returned on the first line, before a diff was even computed, and nothing
anywhere said a cluster had been altered with nobody watching. The two bypass reasons are now
recorded separately — `GateDisabled` (an operator opted out of the dialog) and
`AppliedUnattended` (there was no operator to ask) — because only the second one grows as more
work moves off the Blazor circuit, and that trend is exactly what decides how urgent the
remaining four bullets are. Before choosing what *should* happen to unattended changes, it is
worth being able to count them.

Two things deliberately not done, both recorded in code rather than left to be discovered:

- **Diff bodies and manifests are never written to the record.** A manifest on its way to a
  cluster routinely contains a Secret, and `kubectl diff` of one shows its old and new values.
  Persisting either would turn a safety feature into a second copy of every credential EntKube
  applies. The row carries what happened, where, and the diff's size.
- **The rows cannot be grouped by cluster**, because `PlannedClusterChange` has no cluster id —
  only a `ClusterLabel`, a non-nullable string whose default is the literal `"cluster"`, so a
  caller that never set it is indistinguishable from a cluster named that. This is the *same*
  missing identity that stops the gate moving into `IClusterClient` (§4.0.1): none of the
  eighteen factory methods receives a cluster to put in the model. One `ClusterId` on
  `PlannedClusterChange` fixes both, and belongs with that work rather than ahead of it.

### 5.4.2 The write family the gate cannot see: execution inside a pod (measured 2026-10-20)

Every Kubernetes mutation that goes through `IKubernetesClientFactory` raises an acknowledgment,
because the factory raises it itself. **Nine verbs on that same factory do not**, and they are the
most consequential writes in the product:

| verb | sites |
|---|---|
| `ExecuteSqlAsync`, `ExecuteSqlOnPodAsync`, `ExecuteSqlOnPodWithOutputAsync` | 9 |
| `ExecuteSqlInCnpgDatabaseAsync`, `ExecuteSqlInCnpgDatabaseWithOutputAsync` | 8 |
| `ExecuteMongoAsync`, `ExecuteMongoWithOutputAsync` | 4 |
| `RunCommandOnPodAsync`, `RunCommandOnPodWithStdinAsync` | 9 |

**30 call sites across 8 services**, carrying `CREATE ROLE`, `CREATE DATABASE`, `DROP DATABASE`,
`ALTER ROLE … PASSWORD`, `REASSIGN OWNED`, Mongo `createUser`/`dropUser`, and
`rabbitmqctl import_definitions`. None of it is gated, none of it is recorded by
`IClusterChangeRecorder` — there is no outcome to record when nothing asked.

**The obvious fix is wrong.** Gating the nine verbs would make reads prompt, because *reads share
the verbs*. `ExecuteSqlOnPodAsync` carries `CREATE ROLE "x" … CREATE DATABASE "y"` at
`RegisteredPostgresService.cs:231` and `SELECT 1;` at `:822`. That is the defect #307 had just
removed from the Postgres detail panel, reintroduced across eight services.

**A classifier is worse than wrong, it is dangerous.** A regex over the SQL left **9 of the 30
undecidable** and mis-read at least one: `ExecuteMongoWithOutputAsync` — the *output-returning*
variant, which looks like a read by signature — performs the password rotation at
`MongoService.cs:1386`. A classifier that answers "read" for a write produces an unasked,
unrecorded change while appearing to have checked, which is strictly worse than today's honest
silence.

#### What to do instead: the caller declares, and a ratchet checks that it did

The caller knows what its SQL does; nothing else can. So each of the nine verbs takes an explicit
intent — no default value, so every one of the 30 sites has to state it — and the factory gates
only the ones that declare a change:

```csharp
Task ExecuteSqlOnPodAsync(
    string podName, string ns, string sql, ChangeIntent intent, …);
```

- **No default.** A defaulted parameter would make the 30 existing sites compile unchanged and
  declare nothing, which is how this gap got here.
- **`ChangeIntent.Read` is a claim, not a hint.** It is auditable in review and in the diff, and
  it is the thing to be suspicious of — a ratchet can list every site that claims `Read`, which is
  a short list a human can actually check (currently 4 or 5 of the 30).
- **The dialog can show the statement.** `SecretRedaction` already strips values out of a
  `ClusterChangeDiff`; SQL carrying a password (`CREATE ROLE … PASSWORD '…'`) needs the same
  treatment before it reaches an operator's screen, and that is the one piece of new work this
  needs beyond signatures.
- Cost: 9 signatures, 30 call sites, and the redaction. Mechanical, but it changes a
  security-relevant contract, which is why it is written down here rather than done.

#### Done, except the gating (2026-10-22)

**The declaration landed; the gate does not act on it yet**, and that split is deliberate. The
parameter forces every site to state its intent and makes the statement auditable; wiring the gate
and redacting SQL that carries a password are the next change. Keeping them apart means this
change's entire review surface is one question — *are these declarations correct?* — and that a
wrong declaration cannot yet cause an unasked change, because nothing reads them.

**39 declarations, not 30: 13 `Read` and 26 `Change`.** The count is higher than the measurement
above because the intent *propagates to whoever knows*. `RabbitMQService.RunCtlAsync` is a private
helper that runs arbitrary `rabbitmqctl` args — one factory call site serving ten callers, nine of
which are writes (`add_vhost`, `delete_queue`, `add_user`, `set_permissions`…). It cannot declare
for them, so it takes a `ChangeIntent` and threads it, and its ten callers declare instead. That is
the same reasoning as the public verbs, one level in: the caller knows what the command does.

`RunCtlListAsync` is the exception that proves it — it appends `--formatter json` and parses an
array out of the result, so it is a listing *by construction* and declares `Read` on its callers'
behalf.

**Both open questions below are now answered in code, one of them only provisionally:**

1. **The probe declares `Read`** (`TestConnectionCoreAsync`, `SELECT 1;`). That does not settle
   whether `Read` is *trusted* — it makes the claim enumerable. `ExecChangeIntentTests` lists all
   thirteen with what each one actually runs, which is short enough to audit by hand. If the answer
   turns out to be "a probe must still be recorded", the change is to the gate, not to the
   declarations.
2. **`rabbitmqctl import_definitions` belongs to this family**, and declares `Change` at both call
   sites. It replaces a broker's entire topology from stdin; `export_definitions` is its `Read`
   counterpart. Neither was visible to any gate before.

#### Two questions this cannot settle on its own

1. **Should a probe be exempt?** `TestConnectionCoreAsync` runs `SELECT 1;` to decide whether
   credentials work. It is a read by any reading, and it is also the cheapest possible place to
   hide something. The answer decides whether `Read` is a trusted declaration or merely a quieter
   one.
2. **Does `rabbitmqctl import_definitions` belong to this family or to the one above it?** It is
   not SQL and not a manifest; it replaces a broker's entire topology from stdin. Today it is
   invisible on both counts.

### 5.4.3 The typed SDK, and remediation's exemption made real (2026-10-22)

§5.4's fourth bullet — make `RemediationService`'s exemption explicit — had been open since the
gate was written. Closing it turned up two things worth recording: the bullet named the wrong
primitive, and the metric that was supposed to notice this class of gap could not see it.

#### The exemption was invisible, not ungated

`RemediationService` deleted pods and restarted Deployments, StatefulSets and DaemonSets through
the typed Kubernetes SDK and **never called the gate at all**. Its comment said so and argued the
case, which is right as far as it goes: the operator picks the action from the alerts page and
confirms it in a panel naming the resource, so the gate's own dialog is a second prompt for one
decision.

What was wrong is the mechanism. A flow that does not call the gate produces no `ClusterChange`
record of any outcome — not `AppliedUnattended`, not `AppliedAsConsequence`, nothing. That is the
exact distinction §5.4.1 was written to kill: *invisible* rather than *ungated*. The `AuditService`
row it already wrote is a different trail, and says a human asked for a remediation rather than
that a cluster was changed.

The five mutations now go through `IClusterClient` inside a `DeclareConsequence` scope. Each is
recorded as `AppliedAsConsequence`; the operator is still asked once.

#### `DeclareUnattended` would have been the wrong primitive

`Ungated(reason)` as sketched above became `DeclareUnattended(reason)`, and reaching for it here
would have made things worse rather than better. `ClusterChangeGate.AcknowledgeAsync` tests
`_sink is null` **first**: an unattended declaration only takes effect when there is nobody to ask.
Remediation's one caller is `TenantAlertsTab`, a Blazor circuit, and `ClusterChangeAckDialog` is
mounted in `MainLayout` — so there is always a sink, the declaration would have been inert, and the
operator would have acknowledged every remediation twice.

`DeclareConsequence` is the primitive that suppresses the *question* while still recording the
*outcome*, and it is what `ReleaseVolumeGuard` already uses for the same shape of problem: work the
operator approved once, carried out as several changes. The general lesson is that "exempt" is not
one thing — **whether a declaration takes effect depends on whether anyone is there**, and a
sketch written before the gate existed could not have known which half it needed.

#### The CLI metric is structurally blind to the SDK

`ClusterChangeGateCoverageTests` counts `"kubectl"` and `"helm"` as the *program argument of a
process call*. It is down to one invocation (`ClusterEgressTunnel`'s port-forward, a channel rather
than a change) and that number is quoted as a fact in §5.4. But a mutation issued as
`client.AppsV1.PatchNamespacedDeploymentAsync(…)` spawns no process and names no program, so it was
never in scope for that metric — by construction, not by oversight. The CLI number reaching one
said nothing about the SDK.

`TypedSdkMutationTests` measures the second dimension: **19 typed-SDK mutations in three files.**
Fourteen acknowledge (`KubernetesOperationsService` ×11, `NodeManagementService` ×3); the other five
were these. Four things about how it is built are deliberate:

- **Containment is per method, not per file.** The CLI metric asks only whether a file
  acknowledges *anywhere*, and says itself that this undercounts. Here the enclosing method is
  resolved for each site, so removing one of `KubernetesOperationsService`'s five acknowledgments
  fails the test while the other four keep theirs — verified by doing it, and the CLI metric stayed
  green throughout.
- **The receiver identifies an SDK call, never the method name.** Matched by shape
  (`<Name>V<n>`) rather than a list of group names, so a seventh API group is caught without
  editing the test. A first pass keyed on `(Create|Delete|…)(Namespaced|Node|Cluster)\w*Async`
  returned 39 sites, twenty of them EntKube's own `CreateClusterAsync` and `DeleteNodeAsync`:
  **"Cluster" and "Node" are this product's words too.**
- **The health check cannot be "we found something",** because the finding is expected to be
  empty. It asserts the receiver pattern still matches SDK calls at all (64 today, nearly all
  reads), and a site whose enclosing method cannot be resolved fails loudly instead of being
  skipped. This earned itself immediately: a stale test binary from an earlier verification step
  made the corpus read as empty, and the health check refused to let four absence assertions pass
  against source it had not read.
- **The blind spots are named and asserted empty.** `GenericClient`, the `WithHttpMessagesAsync`
  overload, `DeleteCollection*`, and `CustomObjects` mutations — the last of these has no `V<n>` in
  the receiver, so a CRD write there would be genuinely invisible. All four are unoccupied; CNPG
  clusters, Istio policies and cert-manager Certificates are written with `ApplyManifestAsync`,
  which acknowledges.

#### What is deliberately not measured, and what is still open

`IClusterClient.CreateSdkClient()`/`GetPooledSdkClient()` hand out a live `k8s.Kubernetes` at **34
call sites in 9 files**, and the accessor's own doc-comment admits the credential stays reachable.
Those sites are overwhelmingly legitimate reads — EntKube has to read clusters — so counting them
would be a census rather than a ratchet: a number that cannot reach zero and whose members are all
defensible teaches nothing. What is worth guarding is that none of them *mutates* without an
acknowledgment, which is the number above. Any of the 34 is one line from becoming a site it
catches.

**Still outside the gate, measured 2026-10-22 and not yet addressed.** Every component's own admin
API is a third dimension, which neither ratchet counts because none of it is `kubectl`, `helm` or
the typed SDK:

| Family | Mutating sites | Worst case |
|---|---|---|
| Keycloak admin REST | 33 in 1 file | `DELETE /admin/realms/{realm}`; `reset-password`; client-secret regeneration |
| Object storage via the API-server proxy | 9 in 2 files | CubeFS user mint (credentials); bucket policy/CORS; paginated folder delete over Velero backups and telemetry segments |
| Harbor API | 11 in 1 file | project and registry deletion — breaks the pulls live deployments depend on |
| Headscale API | 8 in 1 file | `PUT /api/v1/policy` replaces the ACL for the mesh EntKube reaches clusters over |
| Alertmanager | 1 | silence creation — low blast radius, time-bounded, carries `createdBy` |

Three judgements about that table rather than a plan to gate all of it:

1. **Not all of it should be gated.** Adding a user to a Keycloak group, or deleting one object
   through a file browser, is routine administration; a dialog there produces the click-through
   habit the gate's own doc-comments warn about. The destructive and credential-minting subsets are
   the ones that need a record.
2. **`PlannedClusterChange` cannot preview any of it.** It requires a kubeconfig and computes a
   `kubectl diff`. A Keycloak or Harbor change would get the honest-summary fall-through that
   `ChangeVerb.Drain` already takes — which is fine, but it means "gate it" is a new shape of
   record, not a new call site.
3. **Keycloak can be registered as *external*.** `KeycloakComponentConfig` rows with a null
   `ClusterComponentId` are not in any cluster, so for those the gate would be acknowledging a
   change to something the invariant does not cover.

**Second-order writes are a fourth dimension and no approach here reaches them.** EntKube applies a
Job or CronJob — acknowledged at apply — whose container then PUTs Elasticsearch ILM policies, dumps
a database, or exports a broker topology. The acknowledgment previews the manifest, never what the
manifest goes on to do. That is where the Elasticsearch and RabbitMQ backup mutations actually
live.

---

## 6. Sequence

Each phase ships on its own and leaves the product working. No big-bang cutover.

### Phase 0 — Make the boundaries real (no new processes)

Nothing moves. The monolith becomes modular.

1. Split `ApplicationDbContext` into **12 module contexts over one connection and one
   migration history.** ☑ **Done.** A compile-time boundary, not a database change — the EF
   model was dumped before and after and is byte-for-byte identical. The day a module needs
   its own store, it already has a context.

   One thing this turned up: EF derives a table name from the `DbSet` property on whichever
   context declares it, so `AlertIncident` lived in `AlertIncidents` only because
   `ApplicationDbContext` spelled that property in the plural, and eight entities were never
   configured at all. All 165 table names are now stated in the model, so renaming a C#
   property can no longer rename a production table.
2. One `IXxxApi` interface per module — the module's whole contract, in `EntKube.Contracts`.
   ◐ **Five of twelve written** (Advisor, Catalog, Fleet, Delivery, DataServices), and the
   lesson from using the first one is that **writing more of them is not the next job**.

   All four were registered in DI and unit-tested, and **nothing in production called any of
   them.** An interface with no consumers decomposes nothing; it only looks like progress. So
   the work turned to making one real, and that immediately found two things the contract had
   wrong — neither of which could have surfaced from writing eight more:

   - **`ICatalogApi` said the table was never written from outside. It is, in 22 places.** The
     measurement behind that claim looked for `.ClusterComponents.Add/Remove/Update` and found
     none. EF writes through change tracking, so `component.HelmValues = x` followed by
     `SaveChangesAsync` is a write with no `Update()` in sight. Nine foreign services do exactly
     that. The claim was in the interface's own documentation as the reason it was read-only.
   - **`InstalledComponent` omitted `Configuration`, which nine call sites deserialize.** A
     record missing the one field its callers read cannot be adopted by them — which is a
     tidy explanation for how an interface stays unused.

   Both are fixed, with `MergeHelmValuesAsync`/`SetHelmValuesAsync` covering all 22 writes
   (14 merge form values, 4 replace outright, 4 paired configuration writes — two operations,
   not 22). Tempo, Mimir and Loki are the first real consumers.

   **The fifth one was written the other way round, from its callers backwards.**
   `IDataServicesApi` has exactly one method, because exactly one question was being asked
   from outside the module: four Telemetry services — Loki, Mimir, Tempo and EntKube's own
   node — each held `ApplicationDbContext` for the *same* hand-written query, "this tenant's
   storage link, by id", and each read three fields off it to build object-store chart values.
   No list method, no find-by-name, nothing for symmetry. Three of the four came off the
   all-seeing context the same day it was written, which is the measurement the first four
   contracts could not produce for months.

   It also records a schema fact worth keeping visible: `StorageLinkSummary` carries the
   *whole* row, where `ClusterSummary` deliberately omits the kubeconfig — because a storage
   link's access keys live in the vault and not on the entity. That is true of the schema, not
   of contracts, so `ModuleApiTests` asserts it rather than trusting it.

   **How to pick the next contract: by adoptable call sites, not by reader count.** Identity
   looks like the biggest prize — 43 foreign services touch its tables, `Customer` 57 times.
   But **28 of those 57 are `a.Customer.TenantId`**: services hopping App → Customer purely to
   resolve a tenant, because `App` carries no `TenantId`. Only **two** are direct queries a
   contract would serve, and replacing an EF join with an API call would turn one query into N
   round trips. The fix for the other 28 is a `TenantId` on `App`, or nothing at all. Same
   shape for DataServices (16 services, 9 direct queries), Secrets (12/4) and Connectivity
   (9/4) — so Catalog's 87 direct queries in 34 services is not merely the largest, it is
   larger than the rest put together.

   This repeats §4.0's lesson exactly: a big coupling number usually has one structural cause,
   and it is rarely the one the number's name suggests.

   **⚠️ Correction: the headline count for this ratchet was measured with half the metric.**
   "Services still on the all-seeing context" was read off constructor parameters by
   reflection — and a singleton cannot take a scoped `DbContext` in its constructor, because
   that is a captive dependency the container refuses. Every `BackgroundService` in the product
   therefore takes `IServiceScopeFactory` and resolves `IDbContextFactory<ApplicationDbContext>`
   inside its loop, and **not one of them was ever counted.** Twelve files do it; ten are mapped
   services. The real total was **44, not 34**, and the corrected per-module figures are
   Telemetry 6, Delivery 6, Connectivity 6, Fleet 3 and Cost **1** — Cost having been recorded
   as a finished module at 0 while its usage collector swept every tenant's clusters on the
   all-seeing context the whole time. A zero that could never have been anything else is worse
   than a large number, because nobody revisits a module that is done.

   **And correcting it found the cheapest work left in the programme.** Eight of the ten
   newly-visible sweeps came off the all-seeing context immediately, as pure context swaps with
   no contract, no tidying and no behaviour change: `AlertSyncService` and
   `TelemetryAlertEvaluator` (Telemetry 6 → 3), `DeploymentSyncService`, `GitSyncService` and
   `RolloutWatcherService` (Delivery 6 → 3), `AppL4RouteHealthService` and
   `HeadscaleCertSyncService` (Connectivity 6 → 4), and `ResourceUsageCollectorService`
   (Cost 1 → 0). Running total **44 → 35**. Every one of them had been swappable since the day
   the module contexts were written; the only thing standing in the way was a metric that said
   they were not there.

   Only `BootstrapRunnerService` stayed behind, and the compiler is what established that rather
   than a grep: it writes `CnpgClusters`, `RabbitMQClusters` and `RedisClusters`, so the narrower
   context refuses it. That is the right adjudicator for a swap — a scan for `db.Table` cannot
   see a read that arrives through a navigation, which is the error §4.1 has now recorded three
   times.

   ⚠️ One hazard the swaps introduce is **not type-visible**, so compiling says nothing about it.
   A module context exposes the four hubs `AsNoTracking`, so a row read through one comes back
   *detached* where `ApplicationDbContext`'s `DbSet` returned it tracked — and attaching a
   detached principal to a *tracked* dependent is how EF is persuaded to insert a row that
   already exists. `AlertSyncService` does exactly that with a cluster, to render
   `incident.Cluster?.Name`, and is correct only because every write in the method happens above
   that line. Every hub read in the other seven is a scalar projection, so the trap cannot arise
   there. All of that had to be read rather than inferred.

   The exemption was not random: it removed precisely the code with the widest reach — the
   unattended sweeps that cross every tenant and that §4.0 exists to govern. `ModuleContractTests`
   now derives both spellings from the source instead of asking reflection one question, and
   this is the sixth time a ratchet in this programme turned out to be counting **one spelling**
   of the thing it names (`DbSet` vs navigation vs `Set<T>()`, a wrapper call, a filename-keyed
   map, a log string, and now constructor injection vs scope resolution). The fix has been the
   same every time: make the measurement derive what it looks for.

   **`ISupportApi` is the sixth contract, and it is one method.** `ErrorBudgetService` owns no
   table — deployments and health samples are Delivery's, availability objectives are Support's —
   so the all-seeing context was the only way it could read anything at all. It now takes two
   contracts and no context. The write side was left out on purpose, because looking for it
   turned up something worth fixing separately: **the product's SLA-target CRUD lives in
   `IncidentService`, a Telemetry service**, operating on Support's table and handing the EF
   entity to three Razor pages, one of which also adds and removes rows straight off the
   `DbSet`. Resolution was left out for a sharper reason — there are two implementations of
   most-specific-wins and they are not the same predicate, differing in a branch no caller
   currently reaches, so publishing either as *the* resolution would be a behaviour change
   wearing the costume of a move.
3. **An architecture test that fails the build when the boundaries erode.** ☑ **Done.**
   `ModuleMap` assigns all 165 entities to a module and `ModuleBoundaryTests` holds it to the
   EF model in both directions — an unassigned table fails the build, and so does a mapping
   for a table that no longer exists. It then counts every cross-module foreign key and
   ratchets it: a pair may shrink or vanish, it may not grow, and a new pair may not appear
   without someone raising the baseline deliberately. A second test fails when the baseline
   carries *slack*, so a reduction has to be banked rather than left as headroom for the
   coupling to creep back. Both directions were verified to actually fail by injecting a
   violation. Same technique as `BackupCoverageTests`.

   **What this does not yet catch** is a *service* in module A querying module B's table.
   That deliberately waits for item 1: once each module has its own context, "module A cannot
   query module B's table" becomes a compile error, which is strictly better than a test.
   Enforcing it by reflection in the meantime would mean baselining 141 services and then
   throwing the baseline away — so the data boundary is enforced now and the code boundary
   follows the contexts.
4. **Split `Program.cs` into 12 `AddXxxModule()` extensions.** ◐ **Mostly done.**
   `ModuleMap.Services` now assigns all 188 registered services — 176 to a module, 12 named
   as platform wiring — and `ModuleCompositionTests` runs each module's `Add…Module()` and
   fails if it registers something the map says another module owns, or if a mapped service
   has quietly stopped being registered. The 176 moved into twelve composition files.

   **The "under 200 lines" criterion was wrong, and worth correcting rather than chasing.**
   It was written before measuring. `Program.cs` went 1,582 → 1,443, and the remaining bulk
   is not service registration at all: it is the HTTP pipeline, authentication and OIDC
   setup, the database provider switch, the segment-engine configuration and a handful of
   endpoints. Roughly 28 registrations also stayed because they close over values computed
   during startup (`rootKey`, `segmentOptions`, configuration sections) — moving those is a
   rewrite rather than a move, and it belongs with the module contracts in Phase 1 where the
   configuration can be passed in deliberately.

   What the criterion was reaching for — "no one can append a service to a nameless pile" —
   is met by the map and its test. Extracting the startup pipeline is worth doing, but it is
   platform housekeeping rather than decomposition, and should be judged on its own.

*Exit criterion: the module boundary and composition tests are green, and every entity and
every service has a named owner.* ☑

**Phase 1 and §4.0.1 are the same dependency chain.** Of Catalog's 87 direct queries, the ones
whose surrounding code also reads `cluster.Kubeconfig` cannot move to the contract at all — it
deliberately carries no credential, so those callers have to go through `IClusterClient` first.
That is **34 queries in 7 services** (Stalwart 16, Storage 6, OpenLdap 5, Headscale 3,
StorageLinkClientFactory 2, TelemetryNodeClient 1, StalwartDns 1); the other **53 in 27 services**
can move today. Credential custody is not a parallel tidying job — it gates contract adoption.
`CatalogContractAdoptionTests` ratchets both halves of the number so neither can creep back.

### Phase 1 — The API (the actual product work)

5. Promote every module contract to HTTP under `/api/v1/{module}/…`, generated from the
   `IXxxApi` interfaces so they cannot drift.
6. Extend the existing scoped-token auth (`ApiToken`, already hashed + scoped + tenant-bound)
   to cover every new scope. Reuse, do not reinvent.
7. **Durable change gate** (§5.4).
8. OpenAPI out of the box → regenerate `EntKube.ApiClient`, the CLI and the MCP server from
   it rather than hand-maintaining three clients.

*Exit criterion: `EntKube.Cli` can do everything the Tenants UI can do. That is the test
of whether the API is finished, and it is a harsh one — Tenants is 79.7k lines of Razor.*

### Phase 2 — Agents

9. Widen `AgentProtocol` with the task frames and `Capability` negotiation (§5.2).
10. Extract `entkube-gateway`; the agent link and the public API move behind it.
11. Durable lease store: dispatch, deadline, retry, result. An agent restart resumes; a
    control-plane restart does not lose the work.
12. First agent capability — pick a **read-only** one. Component scan is the obvious
    candidate: valuable, idempotent, and incapable of damaging anything if the protocol
    has a bug.

*Exit criterion: an agent in a real cluster reports component inventory, and the mgmt
plane never opened a connection to that cluster's API server.*

### Phase 3 — Processes

13. `entkube-worker` — move all 24 hosted services out. Lowest risk in the whole plan:
    they already have no UI coupling and no shared in-process state.
14. `entkube-telemetry` — stop hosting it in the web process.
15. `entkube-support` — own process, own schema. Least entangled.
16. `docker-compose.yml` grows to six services; the Helm chart gains five deployments.

*Exit criterion: a cost scan cannot make the UI slow.*

### Phase 4 — Frontend

17. `entkube-ops-bff` and `entkube-portal-bff`, both thin: authn, authz, aggregation,
    and nothing else. Zero domain logic, enforced by review.
18. Migrate the UI **page by page**, Portal first — it is 11.4k lines against Tenants'
    79.7k, it has the cleaner authorization story (`PortalServiceScopeService` already
    derives scope from bindings), and it is the surface customers see.
19. Tenants migrates per tab over however long it takes. Blazor Server and the new
    frontend coexist behind the same Caddy indefinitely. There is no deadline on this
    phase and there should not be one.

*Note on the design system:* the shadcn-shaped token layer just landed on
`feat/dark-mode`, mapped onto `--bs-*` rather than installed as React+Tailwind. When a
real frontend arrives, that token layer is the thing that carries over — which is
precisely why it was worth doing as tokens rather than as component CSS.

---

## 7. What not to do

- **Don't split the database** beyond Support and Telemetry. 287 cross-entity navigations
  and `Restrict` FKs; the joins are the domain. §3.
- **Don't add a message bus yet.** Phases 0–3 need none. Add one when a specific flow
  needs it, and name the flow in the PR.
- **Don't let an agent write before the gate is durable.** §5.4.
- **Don't rewrite the frontend before the API is finished.** A frontend against a
  half-API becomes its own backend, and then there are two.
- **Don't skip Phase 0's architecture test.** It is four hours of work and it is the only
  thing standing between this plan and a monolith with twelve folders in it.

---

## 8. Honest costs

| Phase | Shape of the work | Risk |
|---|---|---|
| 0 | mechanical; ~150 files touched, almost all of it moves rather than changes | low |
| 1 | the big one — genuine new surface across ~250 services | medium; the volume is the risk, not the difficulty |
| 2 | small and well-understood; the transport already exists | medium — it is new runtime behaviour in someone else's cluster |
| 3 | mostly configuration and composition roots | low |
| 4 | large and open-ended; 101k lines of Razor, migrated incrementally | low per page, unbounded in total |

Phase 1 is the one that buys the product. Phases 0, 2 and 3 are weeks each. Phase 4 is
a year and should be treated as a continuous activity rather than a project.

**If only one phase ever happens, make it Phase 1.** A complete API turns the current
monolith into a product that agents, the CLI, the MCP server, Terraform and any future
frontend can all build on — while it is still one process, and without touching a single
line of Razor.

---

## 9. Running more than one instance (measured 2026-10-23)

The ask: several EntKube instances behind a load balancer, sharing one vault, so that load
spreads and no single instance is the installation. Every platform feature likewise.

This section is the audit, not a design. It was measured by reading the code, and the
headline is that the work divides very unevenly: **the vault is essentially ready, the UI is
not, and four background services would do real damage quietly.**

### 9.1 The vault is configuration, not code

Taking the central ask first, because the answer is better than expected.

| Piece | Where it lives | Shared by |
|---|---|---|
| Root key | `Vault:RootKey` config (`Program.cs:332`) | giving every instance the same value |
| Per-tenant DEK | `SecretVaults.EncryptedDataKey` + `Nonce` | the database |
| Secret values | `VaultSecrets.EncryptedValue` + `Nonce` | the database |

Nothing secret is generated per process and nothing is written to local disk. The root key
unseals a DEK from the database, which decrypts a value from the database — so **N instances
pointed at one Postgres with one root key already share one vault**, and each is a live
replica of the decrypt path. That is the redundancy the ask describes, and it needs no new
component.

⚠️ **One code defect spoils it, and only at N > 1.** `KubeconfigResolver.planCache`
(`KubeconfigResolver.cs:24`) holds *decrypted* kubeconfig YAML in a `ConcurrentDictionary`
with **no TTL**. The only eviction is `Invalidate(secretId)` at `VaultService.cs:760` — an
in-process call on whichever instance performed the write. Rotation upserts in place
(`secretId = existing.Id`, `VaultService.cs:728`), so the cache key does not change. An
operator rotates a cluster credential on instance A; instances B and C keep using the revoked
one **until their process restarts**, across the ~350 call sites that reach
`cluster.Kubeconfig` through the materialization interceptor. In one instance this is
invisible. It must be fixed before a second instance exists, not after.

`dekCache` has the same shape and lower risk, since a DEK changes only on restore.

**On backups.** `BackupService` decrypts every secret and writes **plaintext** into the
bundle, re-sealing with the target's root key on restore (`BackupService.cs:12-17,634`). That
is deliberate and right for server migration — the bundle needs no key — but it means a
vault backup is a plaintext secret store, which is a different risk profile from a ciphertext
copy. Worth deciding on explicitly rather than inheriting.

### 9.2 What breaks first, and loudest

All three are topology or configuration, and they will mask everything else until done.

| # | What | Consequence of a second instance |
|---|---|---|
| 1 | **Blazor Server circuits**, `AddInteractiveServerComponents()` (`Program.cs:39`), no backplane | `POST /_blazor/negotiate` and `GET /_blazor?id=…` land on different instances about half the time. The connection token is known only to the process that minted it, so the circuit never starts: the page renders normally and responds to **nothing**. No login or feature needed to trigger it. |
| 2 | **DataProtection key ring** on local disk (`Program.cs:189`) | Each container mints its own ring, so antiforgery tokens, the auth cookie and OIDC correlation/nonce stop surviving a hop. Login breaks. |
| 3 | **Telemetry active tier** is a local Lucene index (`SegmentManagerBase.cs:245`) | Ingest lands on whichever instance the LB picked; a query searches only *that* process's writer. Up to `RollMaxDocs` (1,000,000) or `RollMaxAge` (1h) **per signal per tenant** is invisible to the other instance. Refresh the log view and lines appear and vanish. |

1 and 2 are fixed by sticky sessions plus a shared ring — and `Presence:Provider=Redis`
(`Program.cs:296-317`) already establishes Redis as an optional dependency, so it can back
both without adding a component. 3 is not a configuration fix: see 9.5.

### 9.3 What breaks silently, and is worse

These do not announce themselves, which makes them the real hazard.

| What | What two instances do |
|---|---|
| **`RolloutWatcherService`** (`Rollouts/RolloutWatcherService.cs:143`) | Both judge the same rollout failed and both call `RollbackAsync` → `kubectl rollout undo` with **no `--to-revision`**. The second undo rolls *forward* to the bad release. The automatic rollback cancels itself out, the bad release is live, and both instances record `RolledBack`. |
| **`TelemetryAlertEvaluator`** (`TelemetryAlertEvaluator.cs:107,144`) | Incidents dedupe by lookup with no claim against a unique `(ClusterId, Fingerprint)` index (`TelemetryModel.cs:100`). A collision throws on the **single** `SaveChangesAsync` for the whole installation, rolling back that cycle for *every* tenant — and dispatch at line 146 never runs, so nobody is notified of anything. The file already warns about this failure mode by another route. |
| **`BootstrapRunnerService`** (`BootstrapRunnerService.cs:121`) | The claim is a read-then-write with no conditional update, token or lease. Both instances read `Queued`, both write `Running`, and the same Helm releases and CNPG/Redis/RabbitMQ clusters are installed **twice on a real customer cluster**. The comment says "picked up by another pass", which is true across sequential passes in one process and false across processes. |
| **Startup migrations + `MigrationReconciler`** (`Program.cs:1467`) | Both instances run EF migrations at boot, and the reconciler can drop and re-stamp. It refuses when data is at stake, but the race itself is unguarded. |

### 9.4 The systemic gap

The reason so many of the above are read-then-write:

- **No optimistic concurrency anywhere.** `IsRowVersion`, `IsConcurrencyToken`, `[Timestamp]`
  and `ConcurrencyCheck` appear nowhere in `src/EntKube.Web/Data` outside migrations.
- **No lease primitive.** No leader election, no advisory lock, no distributed lock.
- **Exactly one claim exists**: `CostLedgerWriter`'s hand-written `ExecuteUpdateAsync` CAS
  (`Cost/CostLedgerWriter.cs:112-124`), written to stop double-billing. It is the pattern the
  others need and the proof it is affordable.
- **Every hosted service registers unconditionally.** All twelve module compositions are added
  with no condition (`Program.cs:129`), so ~25 background services plus five
  `SegmentSealService`s start on every instance. **There is not even a switch to run an
  instance web-only.**

### 9.5 The sequence this forces — and it is the plan's own first step

The audit converges with §4.2 rather than competing with it. `entkube-worker` — "the 24
hosted services", already named there as *highest value, lowest risk, extract this first* —
is exactly what must stop running everywhere before a second instance is safe.

So the order is:

1. **A switch that decides whether this instance runs background work.** Cheapest possible
   version of the worker extraction: configuration, not a new process. It immediately permits
   *N web instances + 1 worker*, which is the whole ask minus telemetry. A ratchet should
   assert every `AddHostedService` goes through it, or the next one added will not.
2. **Shared DataProtection ring + sticky sessions.** Makes the UI survive a second instance.
   Redis is already an optional dependency. The worked deployment that steps 1 and 2 add up to —
   compose, Caddy, the environment matrix, how to verify it, and what it still does not make safe
   — is [running-two-instances.md](running-two-instances.md).
3. **Fix `planCache`.** A TTL at minimum; cross-instance invalidation properly. This is the
   one that is silent *and* security-relevant.
4. **Claims on the four dangerous services**, following `CostLedgerWriter`'s CAS. Rollout
   watching and bootstrap runs are the two that touch customer clusters.
5. **Telemetry last, and as its own process.** The active tier cannot be shared by
   configuration: one RWX volume fails on Lucene's `write.lock`, separate volumes split the
   data. `SegmentSealService`'s own docstring says "single-replica by deployment, so no leader
   election is needed" — it *asserts* the constraint rather than enforcing it. This is
   `entkube-telemetry` from §4.2, and the audit is the argument for doing it.

   ⭐ **It is smaller than it looks, because the seam already exists and is in production.**
   This was measured rather than assumed (2026-10-24). `TelemetryBackendRouter` already holds
   *both* paths and chooses per cluster:

   ```csharp
   public sealed class TelemetryBackendRouter(
       SegmentLogService localLogs,        // in-process segment store
       SegmentTraceService localTraces,
       TelemetryNodeClient nodes,          // the same engine, over HTTP
       …)
   ```

   A cluster with an in-cluster node is **already** answered over HTTP by
   `EntKube.TelemetryNode` — a complete separate process with `NodeHttpApi`, `NodeIngest`,
   `HttpLogBackend`, `HttpTraceBackend`, `RemoteSegmentCatalog` and its own budget and cache
   services. Those reads are multi-instance safe today, because any web instance can call the
   node. Only the fallback — a cluster with no node, served from the management plane's own
   local index — is in-process, and that is the single thing a second instance gets wrong.

   So `entkube-telemetry` is not a service to design. It is the node binary (or the same
   engine behind the same `NodeHttpApi`) hosted beside the management plane, with the router's
   `localLogs`/`localTraces` replaced by an HTTP backend pointing at it. The wire surface, the
   federation, the catalog and the authorization model are all written and exercised.

   ⚠️ One thing to carry across rather than rediscover: the router chooses by **data**, not by
   configuration. An earlier version chose "in-cluster if a node is present", and installing
   the indexer made that wrong — a node exists from that moment while ingest only moves when
   the collector is re-applied, so every view read an empty node and showed nothing, with no
   error. Whatever replaces `localLogs` must keep that property, or the split reintroduces a
   silent empty answer.

⚠️ **Nothing above should be read as "add leader election everywhere".** Twenty of the
services audited are already safe — idempotent reconcilers, or claim-protected. The work is
per-service and the list is in the audit, not a blanket mechanism.

### 9.7 The last telemetry gap, and the pattern that already solves it (2026-10-26)

With every instance sealing its own index (`AddPerInstanceWork`, the sealer reclassification),
telemetry **writes** are distributed: a sealed segment lands in object storage with its row in
the shared catalog, and any instance can read it. One gap remains.

**A query on instance B cannot see what instance A ingested in the last `RollMaxAge`.** The
active index exists only in the process writing it. That is not an oversight — `SegmentScope`
says so outright:

> the hot active index exists only in the process writing it, while sealed segments live in
> object storage and can be read by any pod that can reach the bucket

#### ⚠️ Do not reach for a shorter RollMaxAge

It is the obvious fix and it is the wrong one. **There is no compaction anywhere in
`EntKube.Telemetry`** — nothing merges sealed segments after they are written. At 90-day
retention and hourly rolling, a tenant already accumulates roughly 2,160 segments per signal;
at five-minute rolling that becomes ~26,000, and a query opens a reader over every segment
overlapping its window. Shortening the window trades a one-hour visibility gap for a permanent
query cost, and the hydration work that made log fetches slow is the evidence of what that
costs.

Compaction would make the trade viable and does not exist. That is its own change.

#### The pattern is already built — for the in-cluster split

`SegmentScope` has three values and the split already uses all of them:

| Scope | Reads | Answerable by |
|---|---|---|
| `Hot` | only the unsealed active index | the process that wrote it |
| `Sealed` | only sealed segments | any pod with bucket access |
| `All` | both | a single process that owns the whole index |

And `SegmentLogService`'s own doc comment describes the arrangement:

> The querier runs one instance at `Sealed` for what it can read from object storage, and the
> indexer exposes a second instance at `Hot` on an internal route for the querier to merge in.

That is exactly the shape a multi-instance management plane needs, running today in customer
clusters. `FederatedLogBackend`, `HttpLogBackend`, `HttpTraceBackend` and the node's HTTP API
are the machinery, and they are exercised in production.

#### What is actually missing: peer discovery

The management plane is a single process today, so it runs at `All` and has never needed to ask
another instance anything. To distribute it:

1. Each instance exposes its **own** `Hot` tier on an internal route — the indexer already does
   this; the route and the backend exist.
2. A query becomes `Sealed` **once** (shared, any instance) plus `Hot` from **every** instance,
   merged. `FederatedLogBackend` already merges across sources.
3. Instances have to know their peers. This is the only genuinely new part.

⚠️ **Step 3 is where the design decision lives**, and it is an operational one rather than a
code one: a static list of instance URLs in configuration (works everywhere, must be updated
when scaling), DNS against a headless Service (Kubernetes-native, nothing to maintain), or
registration rows in the shared database (self-maintaining, needs liveness and cleanup). The
first is the smallest honest step and the one a `docker compose` deployment can use unchanged.

⚠️ And a budget caveat that is already recorded elsewhere in this programme: sealed and hot need
DIFFERENT query budgets. A single budget once returned a *successful empty answer* when the
sealed half timed out. A federated read that silently drops a peer's hot tier is the same defect
— a partial answer must fail rather than look complete.

#### What this means for sequencing

`entkube-telemetry` as a separate deployable (§4.2) and this are **alternatives, not steps**.
A single telemetry process removes the gap by having one writer again; peer federation removes
it by making every writer readable. The first is less code and more operations; the second is
more code and no new deployable. Both are legitimate, and neither should be started without
deciding which.

### 9.6 The three the audit could not settle, now settled (2026-10-24)

`ObservedSecretRefreshService`, `MessagingStatusPollingService` and
`SearchStatusPollingService` came back **unclear** rather than being guessed at, because
whether a duplicate run is harmless depends on what the write actually does. Read:

**`MessagingStatusPollingService` and `SearchStatusPollingService` — safe as they are.**
Both call a `ReconcileAllAsync` that observes a live cluster and assigns a status
(`cluster.Status = …`) derived from that observation. No inserts. Two instances observe the
same cluster, derive the same value and write it; last write wins and both writes agree, so
the state converges to the same place a single instance polling twice would reach. The cost
of duplication is a second round of cluster API calls, not a wrong answer. They belong with
the twenty already-safe reconcilers.

**`ObservedSecretRefreshService` — a small, real defect.** The value is fine; the audit trail
is not. `VaultService.RefreshAppSecretFromClusterAsync` compares the live Secret against the
vault copy and, when they differ, calls `ArchiveVersionAsync` **before** writing the new
value. Two instances that both observe the same change both append a version row, so one
change is recorded as two. The plaintext converges correctly — the two ciphertexts differ
only by nonce — so nothing is lost or corrupted; what is wrong is the record of *what changed
and when*, which in a vault is not a cosmetic concern.

⚠️ The method's own comment says the no-change skip exists to avoid "version-history churn",
so the author was already guarding this property — against a slow poll rather than against a
second instance. The fix is the same claim shape as the others: archive-and-write once per
observed change. It is listed below the four in §9.3 because it corrupts a record rather than
a cluster, not because it is imaginary.

With these three read, every hosted service in the installation is classified.
