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

## 6.1 The shape of the API work, measured (2026-10-26)

Phase 1's exit criterion — *"`EntKube.Cli` can do everything the Tenants UI can do"* — is the
right test, and the 79.7k and 11.4k figures beside it are still accurate (81,575 and 11,457
today). What they do not say is **how much of that is behaviour and how much is markup**, and the
two are different kinds of work in different phases.

Measured by splitting each `.razor` file at its `@code` directive:

| Area | total | `@code` | markup | code share |
|---|---:|---:|---:|---:|
| Tenants | 81,575 | **36,026** | 45,549 | 44% |
| Portal | 11,457 | **5,224** | 6,233 | 45% |
| Account | 3,018 | 1,741 | 1,277 | 57% |
| Shared | 3,190 | 1,619 | 1,571 | 50% |
| Admin | 1,893 | 712 | 1,181 | 37% |
| Fleet | 502 | 155 | 347 | 30% |

Across every `.razor` file in `src/`: **45,603 lines of C# sit inside components** — 21% of the
170,635 lines of hand-written C# in the product. (The repo also holds 3.15M lines of *generated*
EF migration code; any ratio computed over all `.cs` files is meaningless without excluding it.)

Reproduce:

```sh
find src/ -name '*.razor' | while read p; do
  t=$(wc -l < "$p"); c=$(grep -n '@code' "$p" | head -1 | cut -d: -f1)
  [ -n "$c" ] && echo $((t - c))
done | awk '{s+=$1} END {print s}'
```

### Why the split is the useful number

The markup half is **Phase 4** work: a new frontend rewrites it, and counting it as API work
overstates Phase 1 by more than double. The `@code` half is **Phase 1** work, and it is the thing
that actually blocks everything else.

⚠️ **The blocker is not that the API is thin. It is that the operations are not callable at all.**
The product API is 22 endpoints — **17 reads and 5 writes**, the writes being deployment
sync/restart, advisor acknowledge, and cost-rate put/delete. Every other operation EntKube
performs exists as an event handler inside a component. `DatabaseTab.razor` is 6,375 lines of
which 2,863 are one `@code` block holding **129 methods** and injecting 7 services;
`ClusterDetail.razor` injects **25**. You cannot put an HTTP surface in front of an event
handler, so "write more endpoints" is not the work — there is nothing yet to expose.

This also explains why the eight `I{Module}Api` contracts total only ~40 methods, three of them
with exactly one (`ISupportApi`, `ITelemetryApi`, `IDataServicesApi`). That is not neglect: by the
contracts README's own rule 3 they cover *what one module offers another module*, which is a far
smaller set than what a client needs. ⚠️ **Contracts scoped that way can never grow into the
product API by themselves** — the rule that keeps them honest as a module boundary is the same
rule that stops them becoming a client surface. The README's "the HTTP surface can be generated
from them" holds only once the behaviour exists as services to put behind them.

### What this changes about sequencing

Extracting behaviour out of a component into a service **leaves Blazor Server working** — the
component keeps rendering and calls the service instead. So it needs no frontend decision, no new
deployable, and no API design committed up front. It is Phase 0/1 work available now, and it is
the only phase that unblocks microservices, BFFs, agents and the CLI at once, because all four
want the same thing: operations that are callable without a browser.

It also quantifies why **Portal first** (Phase 4, item 18) is right: ~5.2k lines of behaviour to
extract against Tenants' ~36k, a factor of seven.

⚠️ **`@code` lines are an upper bound on what moves, not a target.** A component legitimately
keeps render state, form binding, validation display and event wiring. The honest claim is that
the extractable operations are *within* those 36k lines, not that 36k lines relocate. Any PR doing
this should report what it moved and what it deliberately left.

### 6.2 Which Portal component to start with — and why it is not the biggest (measured 2026-10-26)

§6.1 measured `@code` lines per area and warned they are an upper bound rather than a target. Measuring
*per component* turns that caveat into a decision, and reverses the obvious one: **the largest `@code`
block in Portal is close to the worst place to start.**

Portal is 21 components and 5,245 `@code` lines. The four that matter:

| component | `@code` | injects | extractable operations | what the rest is |
|---|---|---|---|---|
| `PortalSecretsDetail` | 794 | 12 | **high** | vault writes, cross-service orchestration |
| `CustomerLogBrowser` | 580 | 3 | **near zero** | UI toggles, JS interop, computed render props |
| `PortalDatabasesPanel` | 427 | 6 | high | create/connect/detach/sync/rotate |
| `PortalAppRoutesPanel` | 323 | 4 | low–medium | mostly form wiring |

`CustomerLogBrowser` is the counter-example worth keeping. 580 lines looks like the second-biggest prize;
almost none of it moves. It is `selectedPod`, `textFilter`, `tsMode`, `wrapLines`, `follow`, `copied`,
`pendingScroll`, an `ElementReference`, an `IJSObjectReference`, and computed properties that exist to
render — all of which legitimately belong to a component. Its actual querying already lives in an injected
`LogQueryService`. **Line count measures how much code is in the page, not how much of it is an
operation**, and those are different questions.

#### The first extraction, named

`PortalSecretsDetail.SyncAllToKubernetes` → `SyncDeploymentBindingsAsync` is **~125 lines of pure
orchestration with no render state at all** (126 lines, measured), and it uses 9 of that component's
12 injects. It:

1. syncs the app's vault secrets (environment-scoped when in environment context);
2. loads every deployment of the app;
3. for each, fans out over five binding families — DB (dispatching three sub-types off
   `CnpgDatabaseId` / `MongoDatabaseId` / `RegisteredPostgresDatabaseId`), cache, messaging, storage,
   identity — filtering each on `SyncEnabled`;
4. isolates failures twice over: per family, so one family failing to load does not stop the others, and
   per binding, so one broken binding does not stop its siblings.

"Re-sync every bound credential for this app" is an operation the CLI and the API both need, and today it
is reachable only by clicking a button. That makes it the highest-value single extraction in Portal.

⚠️ **It also mixes presentation into the operation**, which is the part that makes it uncallable rather
than merely misplaced: the per-binding results are accumulated as pre-formatted strings
(`$"✓ db/{binding.KubernetesSecretName}"`, `$"✗ identity/{name}: {ex.Message}"`). A service has to return
the outcomes structured and let the page format them, or the API inherits a display format as its
contract.

#### ⚠️ The cost nobody would see coming: the collaborators cannot be mocked

All nine services this orchestration depends on — `VaultService`, `DeploymentService`, `CnpgService`,
`MongoService`, `RegisteredPostgresService`, `RedisService`, `RabbitMQService`, `StorageService`,
`KeycloakService` — are **concrete classes whose methods are not virtual**, and **nothing in the suite
mocks any of them** (`Mock<…Service>` appears zero times for all nine). Moq cannot intercept a
non-virtual method, so the extracted service cannot be unit-tested against test doubles as written.

The options, with their real costs:

- **Real-service fixture.** Construct all nine against `TestDbContextFactory` and a mocked
  `IKubernetesClientFactory`, the shape `CnpgServiceTests` and `MongoServiceTests` already use for one
  service each. Nine at once is the honest price, and it is the only option that tests the dispatch and
  the failure isolation — which is the behaviour actually worth pinning.
- **Narrow interfaces per collaborator.** Cleanest long-term and what Phase 1 wants anyway, but it is a
  far larger change than the extraction it is meant to enable, so it should not be smuggled in with it.
- **Extract without tests.** Not acceptable for a credential path.

~~This is worth stating before anyone starts: the extraction itself is an afternoon, and the fixture is
the project.~~

#### ⚠️ That was wrong, and the third option is the cheap one (done 2026-10-27)

The two options above are a false choice, and the paragraph they led to — "the fixture is the project" —
would have sent the next person down the expensive path. **The concrete-class problem only blocks testing
if the behaviour reaches those services directly.** It does not have to.

Expressed as two small records — `BindingFamily` (how to list a family's bindings) and `BindingToSync`
(how to sync one) — the orchestration takes delegates instead of services:

```csharp
public static async Task<(List<BindingSyncOutcome>, List<FamilyLoadFailure>)>
    RunFamiliesAsync(IReadOnlyList<BindingFamily> families, CancellationToken ct = default)
```

Everything that carries behaviour is then testable with lambdas — the `SyncEnabled` filter, both layers of
failure isolation, the shape of the report — and the nine services become one line of wiring each, in a
private `FamiliesFor` that contains no decisions. No fixture, no interfaces, and no coverage traded away.
Thirteen tests, four mutations verified.

**The general lesson, which is not about this page.** "These collaborators cannot be mocked" is a fact
about *reaching* them, not about the behaviour that uses them. Before paying for a fixture or inventing
interfaces, check whether the behaviour can be separated from the collaboration — because if it can, the
part worth testing usually needs none of them. The question to ask is not "how do I fake these nine
services" but "what here is a decision, and does it actually need a service to make it".

What remains untested is `FamiliesFor` and the one-line-per-target dispatch, which is the right thing to
leave: both are deliberately free of decisions, and the dispatch's one real choice (`ResolveTarget`) was
pulled out as a pure function and tested exhaustively.

⚠️ **It also found a silent success.** The page dispatched with `if / else if / else if` and no final
branch, then reported `✓ db/{name}` regardless — so a `DatabaseBinding` naming no database at all was
reported as synced while nothing was sent. The three ids are each `Guid?` and documented "mutually
exclusive", which guarantees at *most* one and not at least one, and no schema constraint requires one.
Not a row anyone has been shown to have; the point is that the code could not have told them.

⚠️ The extracted service sits in Secrets while depending on DataServices, Identity and Delivery services
directly — acceptable for Phase 0 (it is strictly better than a Razor page doing it), and exactly the edge
Phase 1's contracts exist to replace. Recorded in the class doc and in `ModuleServiceMap` rather than left
to be discovered.

#### What it actually cost, against the estimate

| | predicted | measured |
|---|---|---|
| orchestration moved | ~126 lines | 126 lines |
| injects on the page | 12 | **6** (seven died, one added) |
| test fixture | "the project" | **none** — 13 tests, all lambdas |

The `@code` block did not shrink by 126 lines: about 60 lines of formatting and wiring stayed, which is
what §6.1 means by "report what it moved and what it deliberately left". Turning the outcomes into the
text in the box is a page's job, and the service returning data rather than `"✓ db/{name}"` strings is the
specific thing that makes it callable by anything else.

### 6.3 Ranking by orchestration instead of by line count (measured 2026-10-27)

§6.2 rated components "high" or "low" yield by reading their operation *names*. That was better than
counting lines and still not good enough: it rated `PortalDatabasesPanel` **high** on the strength of
`CreateDatabase`, `Connect`, `Detach`, `RotatePassword` — and reading them shows each is a 15-line
wrapper (guard → **one** service call → toast → reload) around a service that already exists. Nothing
to extract. Same error as `CustomerLogBrowser`, one level subtler: I measured *"are there operations?"*
instead of *"is there behaviour in them?"*.

#### The signal that does discriminate

Learned from the one extraction actually done: **an operation worth extracting either calls several
distinct domain services, or loops over things with its own failure handling.** A thin wrapper calls one
service and shows a toast — that is page code and should stay. So, per method: count distinct injected
services used, loops, and `catch` blocks; keep methods of ≥15 lines that use ≥2 services or loop with
their own error handling.

⚠️ **Exclude UI plumbing from the service count.** Counting `ToastService`, `IJSRuntime`,
`NavigationManager` and `AuthenticationStateProvider` as collaborators turned every thin wrapper into
"orchestration" on the first run, and inflated Portal's total by 49 %. A toast is not a dependency the
API needs.

The script is nine lines of Python over the `.razor` files; it is a heuristic, not a proof, and the point
is that it is cheap to re-run and does not flatter a page the way line count does.

#### What it says

| | raw `@code` | candidate orchestration | components with any |
|---|---|---|---|
| Portal | 5,245 | **602** | 10 of 21 |
| Tenants | 36,026 | **2,728** | 40 |

⚠️ **Raw `@code` overstates the opportunity by roughly nine to one.** Portal is not 5.2k lines of
extractable behaviour; it is about 600, and 126 of those have now been taken. §6.1's "~5.2k against
~36k, a factor of seven" compared two numbers that both mostly measure render state — the real ratio is
**4.5x**, and the argument for doing Portal first is correspondingly weaker than it looked.

The densest single targets anywhere, by this measure:

| lines | services | component · operation |
|---|---|---|
| **191** | **13** | `Tenants/ClusterDetail` · `LoadComponentDetailAsync` |
| 132 | 3 | `Portal/CustomerStatusDashboard` · `LoadData` |
| 98 | 2 | `Tenants/IdentityTab` · `SwitchRealmTab` (9 `catch` blocks) |
| 80 | 2 | `Tenants/DatabaseTab` · `ExecuteBackupRestore` |
| 57 | 6 | `Tenants/ClusterDetail` · `LoadCatalogFormAsync` |
| 50 | 6 | `Portal/CustomerPortal` · `LoadCustomerAsync` |

`ClusterDetail.LoadComponentDetailAsync` — 191 lines reaching 13 services — is half again the size of the
block this measurement came out of. ⚠️ It is also in the file whose Razor the 10.0.403 compiler choked on
(see the SDK pin), so it is already a file worth having less in it.

#### ⚠️ But spot-check the top of the list before believing it — this one is not 191 extractable lines

Reading it: most of the method assigns form fields (`editRepoUrl`, `editChartName`, `editNamespace`,
`editReleaseName`, `editValues`, `editFormFieldValues`). Populating an edit form is a component's job and
is not an operation any API would call. **That is the third time a metric has overstated a target** —
line count, then operation names, now service count — and the pattern is the same each time: the measure
sees the shape of the code, never what the code is *for*.

What is genuinely in there is a *sub-operation*: **resolve a component's effective configuration**, layered
catalog defaults → stored YAML → vault secrets → pseudo-path `IComponentFormValueProvider`s. "What is this
component actually configured with?" is a real question an API has to answer, and today it can only be
answered by opening a page.

⚠️ **Treat it as delicate rather than large.** That layering is precisely where a wrong answer is silent:
a value that fails to re-populate is later written back as a catalog default over live configuration,
which has happened before. Any extraction here wants the layering pinned by tests first, and is better
done as "resolve the effective values" alone than as "move the 191 lines".

So the honest ranking of what to do next is by *sub-operation*, and this list stops being a work queue at
that point — each candidate needs the same ten minutes of reading that this one did before it earns a PR.

### 6.4 The page-extraction thread is nearly done, and §6.1 overstated it by about thirty to one (measured 2026-10-27)

§6.3 ranked components by "uses ≥2 services, or loops with its own error handling". **Checked against
the tree, its top four candidates were all false positives**, and reading them is the only reason that
is known:

| candidate | what it actually is |
|---|---|
| `PortalDatabasesPanel.CreateDatabase` | guard → **one** service → toast → reload |
| `CustomerStatusDashboard.LoadData` | already contract-adopted; reads only its own module's tables |
| `IdentityTab.SwitchRealmTab` | lazy-load per tab: 9 catches around 9 **single** service calls |
| `DatabaseTab.ExecuteBackupRestore` | a form submit handler reading `selected*` fields |

⚠️ **The nine catches were read as evidence OF orchestration when they are the opposite.** Repetitive
per-field error handling is the signature of a page. That is the third metric in this exercise to
flatter a target — line count, then operation names, then service count — and each failed the same
way: it saw the *shape* of the code and never what the code was *for*.

#### The signal that does discriminate

From the one true positive, `PortalSecretsDetail.SyncDeploymentBindingsAsync`:

> an **operation** accumulates into a result and touches no UI state;
> **page code** assigns to loading flags, `*Error` strings and `selected*` fields.

So: ≥2 domain services **and no writes to UI state**. `scripts/razor-orchestration-yield.py`
implements it, and — unlike its predecessor — **it was validated against the known answer**: run over
the pre-extraction tree it puts `SyncDeploymentBindingsAsync` at the top, 95 lines across 7 services.
A metric that cannot find the one case you already know is real should not be used to find new ones.

#### What is actually left

| | raw `@code` | §6.3's metric | **validated metric** |
|---|---|---|---|
| Portal | 5,245 | 602 | **163** across 6 components |
| Tenants | 36,026 | 2,728 | **855** across 12 |

The largest single candidate anywhere is now **45 lines**. There is no second
`SyncDeploymentBindingsAsync`.

⚠️ **So "operations trapped in pages" was essentially ONE 126-line block, and it is extracted.**
§6.1 framed Portal as ~5.2k lines of behaviour to move against Tenants' ~36k; the real figures are
~163 and ~855, in pieces of 17–45 lines. That is roughly a **thirty-fold** overstatement, and it came
from counting lines that mostly measure render state.

#### What this changes about Phase 1

The bottleneck is **not** extraction. Six module contracts now exist, and the one genuinely large
operation that was unreachable is reachable. What stands between here and "the CLI can do everything
the Tenants UI can" is plan item 5 — **promoting the existing contracts to HTTP** — not another
season of page surgery. The remaining ~1,000 lines are worth taking opportunistically, when someone
is already in the file, and are not worth a programme.

⚠️ **One limitation, stated so it is not discovered later:** the metric requires *no* UI-state writes,
so an operation written inline inside a method that also sets a loading flag is invisible to it. The
extracted one was only found because its inner method was already separate. A page with 200 lines of
orchestration in one `OnClick` would score zero.

### 6.5 The self-description shipped broken, and a source scan could never have said so (measured 2026-10-28)

Plan item 8 wants `EntKube.ApiClient`, the CLI and the MCP server generated from an OpenAPI
document instead of hand-maintained three times. The document was wired, built, and asserted by
`OpenApiDocumentTests` — which scanned the *source* and stated honestly that it did not generate
the document. Generating it found two defects.

#### 1. It threw on every request

The document transformer indexed into `Components.SecuritySchemes`. In OpenAPI.NET v2 that
property is **null, not an empty dictionary**, so every generation threw a
`NullReferenceException` and `GET /api/v1/openapi.json` answered a **bare 500 with an empty
body**.

⚠️ **That empty 500 is the whole lesson.** An earlier attempt at this test was abandoned on the
theory that serving the document needed a host, that resolving the provider hit keyed-service
details, and that the machine was busy — all plausible, all wrong. The feature's failure mode is
*indistinguishable from the harness's* failure mode: both present as a request that returns
nothing useful. The way through was to stop going over HTTP and resolve
`IOpenApiDocumentProvider` in-process, which lets the exception out with a stack trace naming
the offending line. **When a diagnosis has to explain away an empty response, reach for the path
that cannot swallow an exception.**

#### 2. It described sixteen routes that are not this API

`AddOpenApi` documents *every* endpoint with an API description, not the ones you had in mind.
The document titled "EntKube API" carried 46 operations: the 30 real ones, plus Identity's
`/Account/*` form posts, OTLP and RUM ingest, the git and ticket webhooks, and admin
backup/restore. Each of the sixteen was stamped `⚠️ UNAUTHENTICATED`, because each is guarded by
a signature, a cookie or antiforgery rather than by an API scope.

Both halves of that matter. A generated client would have grown a `LogoutAsync`; and the warning
that exists to flag a genuinely unguarded API route was **crying wolf sixteen times**, which is
how a real one goes unnoticed.

`PublicApiOpenApi.ApiPathPrefixes` now bounds the document to `api/v1/` and `scim/v2/`.

⚠️ **Filtering by path, not by "has a scope", is deliberate.** Selecting on the scope metadata
would be self-maintaining — and would make an API route that forgot `RequireApiScope` *vanish
from the document silently*, which is the opposite of what the UNAUTHENTICATED marker is for. The
cost of the path list is that it can go stale, so the test asserts that **nothing carrying
`ApiScopeMetadata` falls outside the prefixes**: a new scoped surface at a new prefix fails the
suite rather than quietly going undocumented.

#### What the test compares, and the one thing it must exempt

`GeneratedOpenApiDocumentTests` boots the real application, generates the real document, and reads
the real route table out of `EndpointDataSource` — then compares the two **as sets, in both
directions**. Nothing scoped may be missing; nothing documented may be unscoped.

One exemption, by exact path: **the document does not describe itself.** `MapOpenApi`'s route
carries `ApiScopeMetadata` — it is served behind the same token as everything else — but it has no
API description, so its absence is by design. Matching on the exact path rather than a prefix
keeps that exemption from widening.

#### What this changes about Phase 1

Item 8 was blocked and did not look blocked. A generated client is only as good as the document,
and the document was a 500. It now generates 24 paths / 30 operations, every one of them stating
the scope it needs — so client generation is reachable, and is the next thing in this thread
rather than a later one.

### 6.6 Item 5 is a ceiling, and the leverage is the clients (measured 2026-10-28)

§6.4 concluded that the bottleneck was plan item 5 — promoting the existing module contracts to
HTTP. Five independent surveys, each adversarially re-checked, say that is only half true, and the
half that is false is the half the plan was steering by.

#### Item 5 is smaller than it looks

| | count |
|---|---|
| contract methods across the 9 `I<Module>Api` interfaces | **41** |
| reachable through an HTTP route today | **18** |
| not reachable | **23** |
| public API routes those 18 buy | **23** |

Three modules are already complete — Cost 4/4, Secrets 1/1, Telemetry 1/1 — and the remaining 23
methods are a **ceiling of 23 more routes**, not an open-ended programme. Several of the 23 should
never be routes: six contract methods have **no caller anywhere**, five of those not even in a
test, and one (`IAdvisorApi.AssignAsync`) is dead all the way down to the service behind it.

So "promote the contracts" is a finite, partly-pointless backlog. It is not the thing standing
between here and a usable product API.

#### What is actually in the way: the routes that exist have no clients

| client | reaches | measured by |
|---|---|---|
| CLI | 22 of 23 `/api/v1` routes | `CliEndpointParityTests` |
| **MCP server** | **17 of 23** routes, 17 of 30 operations | **nothing** |

The MCP server was in precisely the state `CliEndpointParityTests` was written to end — six silent
gaps (`GET /cost/months`, `GET /cost-rates`, `GET /cost-rates/{id}`, `PUT` and `DELETE` on that
path, `POST /apps/{appId}/secrets/sync`) and nobody measuring. That test's own header already
contains the argument: *an endpoint with no client is indistinguishable, from an operator's seat,
from an endpoint that does not exist — and worse, because it looks like progress.*

Three reads were added as tools. The three writes are exempted with a reason each, because the
caller here is a language model: deleting a price sheet re-prices a customer, and syncing secrets
applies credentials to live clusters. **Symmetry is not a reason to hand a model a capability.**

#### The regex was blind in five ways, and one of them hid seven operations

Both parity ratchets derived the API side by matching `api.MapGet("...")` in one source file. That
cannot see a route behind a sub-group, a route registered through a receiver not named `api`, a
template held in a const, a `MapPatch`, or a `MapMethods` — and because it pointed at
`PublicApiEndpoints.cs` alone it could not see `/scim/v2` **at all**. Seven operations sat outside
the ratchet while it reported itself healthy.

Both now read the **router** (`EndpointDataSource`, filtered to `ApiScopeMetadata`), through the
fixture built for the OpenAPI work in section 6.5. Asking the thing that serves requests is immune
to all five at once. A mutation proves it: a route added behind `api.MapGroup("/probe")` is named
by both ratchets, and the old regex could not have matched it.

An MCP tool's route lives inside its handler delegate, so the ratchet **runs** each tool against a
recording transport and asks what it requested. A declarative path template beside the handler
would have been the same fact written twice — the arrangement this codebase has already had to
undo once, for the scope a route requires.

**One host, shared.** The three suites use an xUnit *collection* fixture, not `IClassFixture` —
which is constructed once per test *class*, so three consumers would boot three
`WebApplicationFactory` hosts and hold them all alive. This suite has already exhausted a 7.8 GB CI
runner on rooted service providers; a host is far heavier than one of those.

#### An exemption now has to prove its own reason

The `PUT /cost-rates` exemption said a bodyless PUT would be read as "no fields supplied" and
quietly do nothing. **That was wrong, in the reassuring direction.** `CostRateInput` is a
non-nullable body parameter whose first four members are non-nullable `decimal`s, so binding
refuses the request — a 400, loud and safe. The dangerous shape is next door: a body of `{}` binds,
and every price takes `default(decimal)`.

A ratchet asserts its number and never its explanation, so a wrong reason stays green forever.
`A_bodyless_put_is_refused_rather_than_silently_accepted` now sends the request and asserts the
400 — the first exemption here whose **rationale** is executable.

#### What this changes about Phase 1

Stop treating item 5 as the gate. The sequence is: **measure each client against the router, close
or name every gap, then generate the clients from the document** (item 8, unblocked by section
6.5). Three hand-maintained clients against a 30-operation API is three chances to fall behind, and
two of the three had already fallen behind without anyone noticing.

### 6.7 The first route the measurement asked for: can we answer the SLA question? (2026-11-02)

§6.6 said to stop treating "promote the contracts" as the gate and instead work from what the API
cannot answer. The highest-value confirmed gap was blunt: **nothing on the public API could say
whether the SLAs were being met.** A search for `budget`, `availability`, `uptime`, `sla` and
`maintenance` across all 936 lines of the endpoint file returned **nothing**.

That is a zero worth distrusting, so it was proven capable of being non-zero — the same search
finds plenty once the route exists. The figure a support agreement is **invoiced against** was
readable in a browser and nowhere else: no CI gate, status page or ticketing system could ask.

`GET /api/v1/error-budgets` (`ops:read`) now answers it, with `deployments`, `withData`,
`breached` and `atRisk` counts over per-deployment rows.

#### ⚠️ A ratchet chose the design before a line was written

The obvious implementation injects `ErrorBudgetService` into the route handler. That would have
turned `PublicApiContractAdoptionTests` red, because `ModuleServiceMap` places that service in
`Module.Telemetry`, and **reaching past a contract into a module's own services is the same
boundary break as reading its tables, one layer further in** — a break the table count cannot see.

So the route asks `ITelemetryApi.GetErrorBudgetsAsync`, and Telemetry's implementation delegates to
its own service. `BaselineModuleServices` stayed at 4. Worth recording as a case where the ratchet
was not an obstacle to route around but the thing that produced the right shape.

Error budgets sit on **Telemetry's** contract although the objectives are Support's and the health
samples are Delivery's, because `ErrorBudgetService` owns no table and the repo had already placed
it there. The contract carries its own `ErrorBudgetSummary`, mapped from the service's
`ErrorBudgetStatus`.

#### ⚠️ A name test cannot see a transposition

The new surface is a hand-written 14-field mapping, and the arithmetic behind it was already
covered by `ErrorBudgetWindowTests` and `ErrorBudgetRoundingTests`. A test comparing the two
records' field **names** would pass happily while `BurnRateRecent` and `BudgetConsumedFraction`
were swapped — and this repo's defects are overwhelmingly **wrong numbers in untested
customer-facing figures**, not wrong structure.

`ErrorBudgetContractTests` therefore asserts values, through a scenario built so every number is
distinct — 99.0, 30, 99.5, 0.5, 2.0, 180.0 — so any transposition changes one. The transposition
mutation is caught.

Two further properties are asserted because a client cannot recover them:

- **`achievedPercent` is null, never 0, when nothing was sampled.** Unknown availability is not a
  total outage, and a client that renders a missing number as zero would page someone for a
  deployment registered yesterday.
- **`HasData` and `IsBreached` are carried, not left to the caller.** Both are computed on the
  source record, and a rounded percentage cannot reproduce the `>` versus `>=` distinction that
  once reported a *met* objective as a breach.

#### The loop from §6.6 closed on its own

The route needed no new ratchet entry. It arrived already described in the OpenAPI document with
its scope stated, already behaviourally verified (an `ops:read` token gets past, another scope is
refused), and already demanded of both clients — because §6.6's parity ratchets read the router,
so a new route is in scope the moment it is served.

Pointing the new CLI command or the new MCP tool at a different path fails the parity ratchets by
name. **That is what the previous unit bought: the cost of adding a route now includes its
clients, automatically, rather than by remembering.**

### 6.8 The clients were measured for reach, never for what they render (measured 2026-11-05)

§6.6 set the sequence: measure each client against the router, close or name every gap, then
generate the clients from the document. Both parity ratchets do the first part — they ask whether
a client can *reach* each route. **Nothing asked what the client does with the answer**, and six
columns across two commands named fields the API has never sent.

`TableWriter.Cell` resolves a column's dotted path against the row and renders `-` for anything
absent. That is right for a table — a null and a missing field mean the same thing to a reader —
and it means a column naming a field the server never sends is a column of dashes, forever, with
nothing failing.

| command | columns | of which dead |
|---|---|---|
| `entkube cost rates list` | 6 | **4** — `clusterName`, `cpuCorePerMonth`, `memoryGiBPerMonth`, `storageGiBPerMonth` |
| `entkube cost months` | 3 | **2** — `clusters`, `total` |

Two of the six are unit errors as well as name errors: the price sheet charges CPU and memory per
resource-**hour**, so a `cpuCorePerMonth` column would have been wrong even had it resolved. One
cannot be fixed by renaming at all — `ClusterRate` is keyed by cluster id and carries **no name**,
so a price-sheet listing cannot show which cluster each sheet belongs to without a change to the
response.

#### Why the reachability ratchets could not see it

Both commands reach their route correctly, send a valid request and get a 200 back. The defect is
entirely downstream of that, and a command that renders nothing is as useless to an operator as a
route with no command — but harder to notice, because dashes read as an empty fleet rather than as
a broken client.

⚠️ It is also invisible to the exit code. `TableWriter` reports **zero rows** when the rows
property itself does not resolve, and `--fail-on-results` reads that count for its exit code 3 —
so a CI gate built on a broken command passes rather than failing.

#### What the ratchet checks against, and what it cannot

`CliColumnParityTests` resolves every column against the schema the framework derives from the
**handler's own return type**, read out of the generated document. A list of expected field names
written in the test would have been a third copy of the contract, free to drift from both others.

The cost of that choice is a hard boundary: a route whose success body is not described has no
schema, so its command's columns cannot be checked at all.

| | count |
|---|---|
| CLI commands | 29 |
| commands rendering a table | 22 |
| whose route describes its success body — checked on every run | **20** |
| banked (named, not counted) | **2** |

⚠️ All thirteen were read by hand on this date and their columns matched the anonymous objects
their handlers return. That is an observation on one day, **not an assertion** — it stops being
true the moment one of those handlers is edited, which is exactly why the number is banked rather
than dismissed.

#### What this changes about item 8

`UndescribedSuccessBodies` was the remaining distance to a usable generated client, and that was
already a reason to drive it to zero. It is now also the share of the CLI's rendering that nothing
can verify. The two ratchets point the same way and move together: typing `GET /cost` took the
response-typing count 24 → 23 and moved one command out of `Unverifiable` in the same commit.
The fleet pair took it 23 → 21 and moved two; `apps` and `deployments` 21 → 19 and two more.

So the §6.6 sequence gains a middle step. Measure each client against the **router** (can it reach
the route), then against the **document** (does it read what the route sends), then generate.

### 6.9 Item 8's remaining distance, measured — and the two things that make it not just typing (2026-11-05)

`ApiDocumentedResponsesTests.UndescribedSuccessBodies` is item 8's distance in a single number: a
generator fed an operation with no response schema emits a method returning `object`, so the count
IS how far the generated clients are from being usable. It stood at 23 after `GET /cost` — the last
and hardest of the cost routes, because every figure on its envelope is computed from the namespace
list and cannot be set directly by a fixture.

Counted by generating the real document and walking it, not by reading the constant:

| | count |
|---|---|
| paths | 25 |
| operations | 31 |
| success body described | 21 |
| bodyless by design (one 204) | 1 |
| **undescribed** | **9** — 2 handlers in `PublicApiEndpoints.cs`, **7 SCIM** |

⚠️ Only the last two of those are pinned by a test (`UndescribedSuccessBodies` and
`BodylessSuccesses`). The operation total is asserted merely as "more than fifteen", so 31 is a
measurement taken today rather than a number the suite defends — if it matters to a later reader,
pin it.

#### The 16 are transcribed and adversarially verified — 14 applied, 2 banked

One agent per route group produced the record in house style, the handler's replacement signature
and return, and the **anonymous-object oracle cut verbatim from the handler**; a second agent then
attacked each transcription field by field against the source. All six groups came back with key
order and field coverage correct and **zero wire-breaking defects**, which is the useful part of
the result: the mechanical half of this work is sound and can be applied in a few sittings.

#### ⚠️ What the verification actually earned: a typing PR is never just the records

None of the six transcriptions mentioned the three companion edits, and each one turns the suite
red:

| file | why typing touches it |
|---|---|
| `ApiDocumentedResponsesTests.cs` | the constant is asserted with **exact equality**, not a ceiling, so it must drop by precisely the number of operations typed |
| `CliColumnParityTests.cs` | the typed route's command must leave `Unverifiable`, or the staleness assertion fires |
| `CliRenderedColumnTests.cs` | removing that name grows the described set, so a rendering fact must be added for it |

That coupling is deliberate — it is what §6.8 built — but it has a scheduling consequence worth
writing down: **all three are ratchet state, so a typing branch cannot be based on `main` while
another pull request is editing them.** A ratchet constant has no correct merge; git will resolve
one silently and wrongly. The sequence is merge, then type, not type in parallel.

#### ⚠️ And SCIM must be pinned BEFORE it is typed

The seven SCIM operations are the only part of the API that never serialises an object: every
payload is a hand-built `JsonObject` with literal keys, flattened with `ToJsonString()` and written
through `Results.Content(json, "application/scim+json", …)`. That is why they carry no response
schema, and it is also why they are the most dangerous routes on the list — transcribing those keys
into records breaks them **in ways that answer `200 OK`**:

| what typing would do | how it looks to an operator |
|---|---|
| `Resources` → `resources` | an empty page, 200 OK, nothing fails |
| `int Status` in the error envelope | `"status": 404` where RFC 7644 §3.12 requires `"404"` |
| a record without `WhenWritingNull` | `"scimType": null` where the member must be **absent** |
| `TypedResults.Ok(body)` | `application/json`, which some connectors reject outright |

RFC 7644 §3.4.2 spells the ListResponse member `Resources` with a capital R — the one member in the
specification that is not lowerCamelCase. Under `JsonSerializerDefaults.Web` a record property
`Resources` serialises as `resources`, and **nothing in this repository asserted it**: the SCIM
tests pinned the user resource and the error envelope, but there was no test of the list envelope
at all, so the rename turned nothing red while every connector read an empty directory.

`ScimWireShapeTests` pins it now, over real HTTP for the media type, the capital-R member and the
error envelope, and over its own database for the populated envelope. ⚠️ It deliberately does not
rebuild a `JsonObject` with the same literals in the test, which would have stayed green through
exactly that rename — and an earlier draft that asserted SOURCE TEXT was replaced, because §6.5's
lesson applies here too: a source scan passes on a call that never runs.

#### ⚠️ A list route's ORDER is verifiable or not depending on the fixture, not the table

Typing a list route raises the same three questions every time: does the handler's `OrderBy`
survive, does a derived count come from the right key, does a filter filter. The first one took
two wrong theories, fifteen mutations and a rewritten fixture, and the answer is more useful than
"it cannot be tested here".

**The fleet group concluded that `GET /clusters` could not pin its sort** because
`KubernetesClusters` has a unique index on `(TenantId, Name)` and SQLite collates TEXT as BINARY,
so rows already arrive in ordinal name order. True — but recorded as a fact about *that table*,
and that framing produced a wrong prediction for the next group. `Apps` and `AppDeployments` have
no index declared in `ApplicationDbContext`, so a scan should return insertion order, so a seed
inserted against the expected order should catch a deleted sort. **Both mutations passed.**

`DeliveryModel.cs:122,175` declares the indexes — `(CustomerId, Name)` on `Apps` and
`(AppId, Name)` on `AppDeployments`, both unique — in `Data/Modules/`, which is why grepping the
context for `HasIndex` finds nothing. The fixture put every app under one customer and every
deployment under one app, so the index's **leading column was constant** and an index scan walked
exactly the rows under test, in `Name` order. The host was answering the question on the handler's
behalf.

**The fix is in the fixture, not the test.** Split the rows across two parents chosen so the
natural walk disagrees with the answer: the customer named `aaa-…` owns the `apple-…` app and
`zzz-…` owns `Zebra-…`, so walking customers by `(TenantId, Name)` and then each one's apps by
name yields `apple` before `Zebra` — the opposite of ordinal order. Insertion order is arranged to
agree with that, so a plain table scan is wrong too. Both plausible query plans now disagree with
the correct answer, and the handler's sort is the only thing that can produce it.

| mutation (per route, one at a time) | before the split | after |
|---|---|---|
| `GET /apps` sort deleted | passed — not caught | **failed ✓** |
| `GET /deployments` sort deleted | passed — not caught | **failed ✓** |
| `GET /apps` comparer weakened to the default overload | — | failed ✓ |
| `GET /deployments` comparer weakened | — | failed ✓ |
| count made tenant-wide | — | failed ✓ |
| grouping key changed to the deployment's own id | failed ✓ | failed ✓ |
| `appId` filter dropped | failed ✓ | failed ✓ |
| an enum published as its number | failed ✓ | failed ✓ |

⚠️ **Attribution matters here.** The first round weakened both comparers in one mutation, so the
single failure it produced could not say which route's half of the assertion did the work. The
second round changes one route at a time, which is the only reason the table above can claim
anything per route.

#### Which makes `GET /clusters` the genuinely unverifiable one, for a reason worth naming

The same trick cannot rescue the fleet route, and it is not a matter of effort. The index there is
`(TenantId, Name)`, so its **leading column is the tenant** — and a request carries one API token,
which fixes one tenant. There is no second parent to split across: every row the route can return
shares the leading column by construction, so the index always supplies ordinal name order. The
fleet note's conclusion stands; its stated reason ("this table has an index") was the wrong one,
and the right one is that the index's leading column is the thing the request holds constant.

That is the general test, and it is cheap to apply to the remaining groups:

> Is there a unique index whose trailing column is the sort column, and can the fixture make the
> leading columns **vary** across the rows under test? If it can, the sort is verifiable. If the
> request itself pins them, it is not — and the test must say so, because a reader who deletes the
> sort will otherwise see a green suite.

⚠️ On Postgres a query without `ORDER BY` has undefined row order, so every one of these sorts is
load-bearing in production whatever this host can see.

⚠️ **The habit this is the third instance of:** a test can be vacuous for a reason its author did
not consider even having chosen the discriminator deliberately and written a comment defending it.
Here the defending comment was wrong twice over — once about which tables carry indexes, once
about what the mutations had shown — and both drafts would have shipped reading as measurement.
The rule that keeps surviving: **write the claim, then run the mutation, then write the claim
again.**

#### ⚠️ One response on this API is declared, not derived — and three readings of why were wrong

Typing `POST /apps/{appId}/secrets/sync` broke the file's own rule, and the exception is written
down here because the alternative is contradicting it silently.

That route answers **one body under two statuses**: 200 when every binding synced, **207
Multi-Status** when any did not. Partial success is the normal case by design — one binding
failing must not stop its siblings — so the status is the machine-readable half of one answer
rather than a different answer.

Three positions were held on how to document it, and **all three were wrong**:

| position | what the document actually did |
|---|---|
| the transcription: `JsonHttpResult<T>` contributes a spurious **200**, so declare the 207 to correct it | it contributes **nothing at all** |
| the banked plan: drop the `.Produces`, the 207 appears anyway now that references dedupe | the 207 was **absent entirely** — 200/404/401/403 and nothing else |
| the earlier worry: `ProblemHttpResult` in a union declares a false **500** | no 500 appeared; the six shipped VM routes prove it, and the real 422 is simply **undocumented** |

Generating the document settled it in one run. `JsonHttpResult<T>` as a union member contributes
no response, so the 207 needs `.Produces<AppSecretSyncResponse>(207)` — and
`SecretSyncDocumentedStatusTests` now asserts the document carries it with the **same component
id** as the 200, because a declaration nothing asserts is precisely the two-copies-of-one-fact the
rule exists to prevent. Deleting that line has exactly one symptom: a generated client is never
told about the normal outcome of a partial sync. Since `IsSuccessStatusCode` is true for 207, such
a client reads a partial failure as clean — the defect the shipped CLI actually had (§6.8).

⚠️ **Still undocumented, measured and deliberately not fixed here:** `/sync` answers 422, `/restart`
answers 400 and 422, and the document declares none of them. That is not new and not a lie — the
document is silent, not wrong — and it matches six VM routes and `GET /cost`, which answers 503
and declares nothing. Declaring them means an explicit `.ProducesProblem` per route, which is a
decision for every route group at once rather than for the two that happen to be in this change.

#### ⚠️ A typed route makes a hand-written test fixture checkable, and one was wrong

`CliPartialFailureTests` pinned the CLI's handling of a 207 against a literal JSON body described
in its own comment as "shaped the same" as the route's. It carried `"kind":"OAuthClient"` — not a
member of `SecretBindingKind`, which is Database, Cache, Messaging, Storage, Identity. The API
could never have sent it, and nothing could notice: a literal string is a third copy of the
contract, free to drift from both the server and the client, and until the route was typed there
was no schema to check it against. The fixture is now **generated** from `AppSecretSyncResponse`,
so that drift is no longer expressible.

⚠️ The same test also showed where the coverage really sat: it pinned what the CLI does when
*handed* a 207, so the client half was covered while **"does the route answer 207 at all" was
covered nowhere**. A handler returning `TypedResults.Ok` on both branches passed the entire suite.
The decision is now a named function with a fact of its own, and the mutation that flattens it
fails.

#### What this changes about the sequence

Item 8 is no longer blocked on a question, only on work, and the work has an order:

1. **Pin SCIM** (done) — so the seven most dangerous routes cannot be typed into silence.
2. **Merge the ratchet changes**, then type the 16 in groups, each group carrying its three
   companion edits and a wire-shape test against the verbatim oracle.
3. **Type SCIM last**, deliberately, carrying the media type and the capital-R member rather than
   letting `TypedResults.Ok` decide them.
4. Then generate the clients — at which point the two ratchets that count this debt both read 0,
   and the CLI's rendering is verifiable for every command rather than twenty of twenty-two.

### 6.10 The last two non-SCIM routes are typed, and what remains is only SCIM (2026-11-13)

`GET /rollouts` and `GET /disaster-recovery` were the final two operations in
`PublicApiEndpoints.cs` with no described success body. Typing them takes
`ApiDocumentedResponsesTests.UndescribedSuccessBodies` from **9 to 7**, and the remaining seven are
**entirely SCIM** — which is the state §6.9 step 3 described: everything that can be typed by
transcription has been, and what is left is the part that has to be done deliberately.

| | before | after |
|---|---|---|
| `UndescribedSuccessBodies` | 9 | **7** (all SCIM) |
| `CliColumnParityTests.Unverifiable` | `{rollouts, dr}` | **empty** |
| commands with a rendering fact | 20 of 22 | **22 of 22** |

#### Both routes publish an enum whose ordinal-0 member is the consequential one

This is the third group in a row where that was true, and it is no longer a coincidence worth
noting per route — it is what these enums are for. `RolloutVerdictStatus.Watching` is 0: the state
of every release still inside its analysis window, which is the row a client polling for an outcome
reads most often. `DrSeverity.Critical` is 0: *"there is no usable backup; a cluster loss right now
is unrecoverable"*. Typing either member as its enum rather than as `string` would publish a `0` on
exactly the value that matters, and a client sorting by it would put the worst findings first by
accident rather than by meaning.

⚠️ `ApiRolloutStatusTests` already pinned the seven status strings against a hard-coded list, which
is why that half was safe. Its two sibling facts are **not** that: they compare the contract enum to
the *entity* enum and to the blueprint enum, so a rename applied to both declarations keeps them
green. That is the eighth instance of the enum-parity shape this file has recorded, and the rule
holds — **a published enum needs a literal, not a comparison between two declarations.**

#### `restorable` is not "a backup finished", and the field beside it disagrees with it

`restorable` is `LastUsableBackup is not null`, and `VeleroBackup.IsUsable` excludes
`PartiallyFailed` on purpose: Velero stamps those with a completion time although they skipped
resources, so anything asking merely "did it finish" reports an unrestorable cluster as protected.
That is the error this subsystem is most likely to discover **during a restore**.

⚠️ And the pair is not consistent. `IsUsable` requires a clean `Completed` phase and no errors; it
does **not** require a `CompletedAt`. So `restorable: true` with `lastUsableBackupAt: null` is
reachable, and a client inferring protection from the timestamp rather than from the flag reads a
protected cluster as unprotected — the CLI prints a dash under `LAST BACKUP` beside a `RESTORABLE`
of True. Reproduced as shipped, named on the record and pinned by a fact, rather than quietly
"fixed" inside a typing change.

#### One clock per response, and why the record takes it as an argument

`DrReadiness.Evaluate` judges staleness against a `now`, so `DrStatusResponse.From` takes the
instant rather than reading `DateTime.UtcNow` itself. A version that read the clock per cluster
would compile, would pass every byte comparison in the wire-shape test, and would compare two
clusters on one page to two different instants — with `StaleAfter` potentially between them, on the
one finding the route exists to report. The fact that pins it asserts the gap set **differs**
between two supplied instants, which a hard-coded `UtcNow` cannot satisfy.

#### Still undeclared, and the list has grown by one

`/disaster-recovery` answers **503** when no scan has completed for the tenant, and the document
declares it nowhere. That joins `/sync`'s 422, `/restart`'s 400 and 422, `GET /cost`'s 503 and the
six VM routes. The union return type is correct and deliberate — §6.9 established that
`ProblemHttpResult` as a union member contributes no response to the document, so this is silence,
not a false claim — but it is now **five routes and six statuses**, and the decision to add an
explicit `.ProducesProblem` per route is still owed to every group at once rather than to whichever
two happen to be in a change.

### 6.11 `UndescribedSuccessBodies` is 0 — SCIM typed, item 8 unblocked (2026-11-14)

The seven SCIM operations are typed, and every operation on the API now describes the body it
answers with. A client generated from the document returns a named type for every call instead of
`object`, which is the thing item 8 was waiting for.

| | before | after |
|---|---|---|
| `UndescribedSuccessBodies` | 7 (all SCIM) | **0** |
| `BodylessSuccesses` | 1 | 2 (the SCIM deletion joins the public API's) |
| operations with a named `$ref` success schema | 36 of 43 | **43 of 43**, less the two 204s |

#### What typing SCIM actually cost, as opposed to what §6.9 predicted

§6.9 predicted four hazards and they were all real: capital-R `Resources`, `status` as a string,
`scimType` absent rather than null, and no member declared that is not meant to be emitted. Each
is now carried by an **attribute on the record** rather than by a convention, because a convention
is what a later reader changes without seeing what it was holding.

The fifth hazard is the one §6.9 named but did not cost out: **the media type**. It is worse than
"use `TypedResults.Json` instead of `TypedResults.Ok`", because there are two distinct failures
and the obvious fix only closes one.

| attempt | the wire | the document |
|---|---|---|
| `TypedResults.Ok(record)` | `application/json` — connectors reject it | `application/json` |
| `TypedResults.Json(record, contentType: scim)` | `application/scim+json` ✓ | **`application/json`** — it is hardcoded in `JsonHttpResult<T>.PopulateMetadata` |
| `ScimOk<T>` / `ScimCreated<T>` / `ScimNoContent` | `application/scim+json` ✓ | `application/scim+json` ✓ |

⚠️ The middle row is the trap, and it would have passed every ratchet in the repository. The
document would have told a generated client to send `Accept: application/json` to a route that
answers `application/scim+json`, and **`UndescribedSuccessBodies` cannot see it** — that count
asks whether a schema is present, never what media type it is declared under. `ScimDocumentedResponseTests.Every_scim_success_is_declared_as_scim_json` is the assertion that
closes it, and it is new: nothing in this repository compared a declared media type to a written
one before.

⚠️ **Three result types, not one, because `PopulateMetadata` is `static`.** It cannot read a status
off an instance, so a single `ScimJson<T>` would have to declare one status for all seven
operations — documenting every creation as 200 and the deletion as a 200 with no schema. The
second of those is the undescribed body this whole programme was about, arriving back through the
fix for it.

⚠️ **A 204 must declare no content type.** A metadata entry naming a media type while carrying no
type is inconsistent and the description layer drops it **silently** — which left the deletion
documented as answering only its two ways of failing, and was only visible because
`UndescribedSuccessBodies` refused to reach 0.

#### The pre-pin paid for itself, and so did reading the serializer rather than the object

`ScimWireShapeTests` was written in §6.9 before the change it guards. Of eleven mutations applied
to the finished work, ten were caught by tests and the eleventh — `status` as an `int` — does not
compile in its naive form and is caught by eight tests in the form that does. No hazard §6.9
predicted got through.

⚠️ The existing assertions had to change in one respect, and it made them stronger: they read keys
off the hand-built `JsonObject`, and now read what **the server's own serializer** writes. Every
hazard above lives in serialisation, not in an object's in-memory keys — and a test serialising
with its own `JsonSerializerOptions` would keep passing through a change to the real ones, which is
where the camelCase that renames `Resources` comes from in the first place.

#### What is next, and what is still not declared

Item 8 proper: generate `EntKube.ApiClient`, the CLI and the MCP server from the document instead
of hand-maintaining three clients.

⚠️ **Error responses are still undeclared per route.** `401` and `403` are declared centrally in
`RequireApiScope`, so every operation carries them. But the `503` on `GET /cost` and
`GET /disaster-recovery`, the `422` on `/sync`, the `400` and `422` on `/restart`, the VM routes
and SCIM's own envelope are described nowhere, and the ten `ProblemHttpResult` union members
contribute **nothing** to the document. A generated client needs error shapes as much as success
shapes, so this is the next piece of item 8 rather than a separate concern — and it has now been
deferred five times.

### 6.12 Item 8 is done: the document is an artifact, the client is generated, and the refusals are declared (2026-11-15)

Three steps, in order, each ratcheted by its own test so a failure says which link moved:
**application → `docs/openapi/v1.json` → `src/EntKube.ApiClient/Generated/EntKubeApi.g.cs`.**

| | before | after |
|---|---|---|
| the document | existed only inside a test process | committed, 5,339 lines, regenerated with `ENTKUBE_UPDATE_OPENAPI=1` |
| `operationId` | **0 of 43** | 43 of 43, unique |
| typed client methods | 0 — three clients interpolated route strings | **43**, with 54 response types |
| undeclared reachable statuses | **28** | 0 |

#### The document had to become a file before anything else was worth doing

Everything this repository knew about the API's described shape was asserted by a *count*. A count
says something changed, never **what** — so an API change arrived in review as a one-line diff to a
ratchet constant, and the wire difference was invisible. The committed document is that difference.
It is also why §6.12's own status work needed no bespoke pin: a change to any declared status now
shows up as a diff in a reviewable file.

#### ⚠️ Two facts about naming, and only one of them is compiler-checked

`RequireApiScope` now requires the operation's name, so the **compiler** refuses an unnamed route,
and ASP.NET Core throws at startup on two endpoints sharing a name — a stronger guarantee than this
repository could write for itself.

That is a different fact from *the name reaching the document*, and the second failed silently:
with all 43 routes named through `WithName`, the generated document came back with **0 of 43**
operationIds. `PublicApiOpenApi` now sets `OperationId` explicitly from `IEndpointNameMetadata`.
Nothing about the compiler check would have noticed, so it has its own test.

#### Why the generator is in this repository rather than NSwag or Kiota

Measured, not assumed. Two properties of this document defeat a stock generator:

- .NET 10 writes a number as `type: ["integer","string"]` with a `pattern` constraining the string
  form — **109 properties here**, including every count and every price. Read as a literal union
  that is "integer or string", which resolves to `object`: the `UndescribedSuccessBodies` problem
  arriving again through the tool meant to solve it.
- **six success bodies are `application/scim+json`**, which a generator keyed on
  `application/json` skips entirely — wasting §6.11 in full.

⚠️ **Money is `decimal`, not `double`, and that is a deliberate departure from a literal reading of
the document.** The server declares `decimal` (`ClusterRateResponse.CpuCoreHourCost`); .NET's
OpenAPI generation writes `format: double` and loses it. A client reading a price into a `double`
would put binary floating-point error into a billing API, and this repository has already shipped
eight cost-arithmetic defects. Pinned by a test rather than left as a comment.

⚠️ The DTOs are **init-only properties, not positional records**. A positional record makes the
document's field ORDER part of the client's source compatibility, so reordering two members of a
response — which changes nothing on the wire — would break every caller.

⚠️ **A bad generation wedges the build.** The generated file is compiled by the project the
generator's own test depends on, so a syntax error in the output breaks the thing that would fix
it. Recovery is delete-and-regenerate; both generator defects found here were found that way.

#### The refusals: 28 reachable statuses were described nowhere

⚠️ **A `ProblemHttpResult` in a handler's `Results<…>` union contributes NOTHING to the document.**
So routes that could refuse were described as only ever answering 200, 401 or 403, and a generated
client would deserialise a `ProblemDetails` into whatever the 200 was meant to be — the same defect
the central 401/403 declarations fixed, one level further in.

| | routes | statuses |
|---|---|---|
| `503` — "no sweep has completed yet", which a client should RETRY rather than report | 7 | 7 |
| `422` — the cluster refused | 9 | 9 |
| `400` | 2 | 2 |
| SCIM's own `400`/`404`/`409` | 6 | 10 |

⚠️ **SCIM's refusals are NOT `ProducesProblem`.** A SCIM route answers its own envelope at
`application/scim+json`, but its 401 and 403 come from `ApiTokenAuthFilter`, which returns
`Results.Problem` — so for those two it really does answer `application/problem+json`. Declaring
every refusal the same way is wrong in one direction or the other whichever way it is done.
`ProducesScimError` exists for that, and `ProducesProblem` on a SCIM route is the easy mistake: it
compiles, it looks right, and it tells a client to parse a `ProblemDetails` out of a SCIM envelope.

⚠️ **The earlier note that this was "five routes and six statuses" was a stale undercount** — it
was 28. And the first measurement of it was WRONG in a way worth recording: scanning handler source
for status codes attributed a shared helper's `422` to the route registered above it, and
`SyncAppSecrets`'s `207` to the route before that. The statuses are pinned as an explicit table for
that reason, and the pin asserts **both directions** — a route advertising a refusal it cannot
answer sends a client writing dead error handling, which is as wrong as hiding one.

#### What this does NOT yet do

Generation added a layer; it has not yet removed one. **The CLI and MCP server still interpolate
route strings and parse JSON by hand** — 922 and 867 lines. Migrating them onto `EntKubeApi` is
where the hand-maintained client code actually shrinks, and it is the next step rather than part of
this one.

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
- ~~**No lease primitive.** No leader election, no advisory lock, no distributed lock.~~
  **Corrected 2026-11-04: there is one, and there has been since the two-instance work.**
  `MigrationLock` takes a real advisory lock — `pg_try_advisory_lock` on Postgres,
  `sp_getapplock` with `@LockOwner = 'Session'` on SQL Server, none on SQLite because two
  processes sharing one file is not a supported deployment. It was hardcoded to one key, which is
  the only reason it read as absent. `AdvisoryLock` now takes the key as a parameter and
  `SingletonWorkWitness` holds a second one.
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
3. **Fix `planCache`.** ~~A TTL at minimum;~~ **the TTL is in — 30 seconds, pinned by
   `KubeconfigResolver.DefaultFreshness` so a production value left at zero or at a day would
   fail a test.** Cross-instance invalidation is still outstanding: a TTL *bounds* the window in
   which a revoked credential still works on another instance, it does not coordinate. The Redis
   connection `Presence:Provider` already optionally registers is the place for the bus.
4. ~~**Claims on the four dangerous services**, following `CostLedgerWriter`'s CAS.~~ **Done.**
   All four take a per-row claim, each with its own test — `RolloutJudgementClaimTests`,
   `BootstrapRunClaimTests`, `ObservedSecretRefreshClaimTests`, `TelemetryIncidentClaimTests`.
   So a mis-set placement flag is now wasteful rather than destructive.

   ⚠️ **What was still missing is that nothing noticed the flag was wrong**, in either
   direction — and the worse direction is `false` everywhere, where no background work happens
   anywhere and the symptom is the absence of events rather than an error. `SingletonWorkWitness`
   holds a second advisory lock and reports *contended* (two sweepers) and *unclaimed* (none).
   It reports rather than enforces: standing down on a lost race would let a database hiccup stop
   all background work, which is the direction `RunsSingletonWork` deliberately refuses to fail
   in.
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
