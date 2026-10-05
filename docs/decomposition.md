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

**`App` and `KubernetesCluster` are the two hubs of this schema** — 40 of the 94 structural
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
- `RemediationService`'s existing deliberate exemption becomes an explicit
  `Ungated("remediation")` instead of an emergent property of having no circuit.

This is a prerequisite, not a phase-4 nicety. Everything else in this document can
slip; this cannot.

---

## 6. Sequence

Each phase ships on its own and leaves the product working. No big-bang cutover.

### Phase 0 — Make the boundaries real (no new processes)

Nothing moves. The monolith becomes modular.

1. Split `ApplicationDbContext` into **12 module contexts over one connection and one
   migration history.** EF Core handles this fine; it is a compile-time boundary, not a
   database change. The day a module needs its own store, it already has a context.
2. One `IXxxApi` interface per module — the module's whole contract, in `EntKube.Contracts`.
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
4. Split `Program.cs` into 12 `AddXxxModule()` extensions.

*Exit criterion: the architecture test is green and `Program.cs` is under 200 lines.*

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
