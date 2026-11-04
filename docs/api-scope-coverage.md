# What an API token's scopes actually grant

Measured 2026-11-04 against `origin/main` at 2077cea. The API had **31 operations**: 24 under
`/api/v1` and 7 under `/scim/v2`. Thirty state a scope; one (`GET /api/v1/whoami`) is reachable
with any live token.

`ApiScopes.Describe(scope)` is not documentation. It is rendered as the label of the checkbox an
operator ticks in the token-creation form (`ApiTokensTab.razor:73`), so it is the only sentence
they read while deciding what to hand a third party. All eight labels were hand-written prose, and
all eight were wrong.

## What each scope grants

| scope | operations | the label said | measured against the routes |
|---|---|---|---|
| `fleet:read` | 2 | clusters, **nodes**, components, **workloads** | nodes and workloads have no route |
| `apps:read` | 2 | apps, **environments**, deployments | environments has no route |
| `apps:write` | 3 | "Trigger deployment syncs and restarts" | **also grants `POST /apps/{appId}/secrets/sync`** |
| `ops:read` | 11 | advisor findings, incidents, drift, vulnerability reports | **7 more, including the cost reports** |
| `ops:write` | 1 | "Acknowledge/snooze findings, **open and resolve incidents**" | acknowledge only; no incident route at all |
| `config:read` | 2 | price sheets, **rollout policies** | price sheets only |
| `config:write` | 2 | "Change platform configuration" | the cluster price sheets, nothing else |
| `scim:provision` | 7 | provision and deprovision users | also reads the directory, and the SCIM discovery doc |

### The two that are more than a wording error

**`apps:write` granted secret delivery without saying so.** The label read "Trigger deployment
syncs and restarts". The scope also admits `POST /apps/{appId}/secrets/sync`, which pushes an
app's vaulted secret material into its live cluster. An operator minting a token so CI could
restart a pod was also granting secret delivery, and nothing in the form said so. That is not a
documentation defect; it is consent for a privilege that was never described.

**`ops:read` granted the cloud bill.** The label listed four operational reports. The scope admits
eleven routes, among them `/cost`, `/cost/history` and `/cost/months` — total spend with
per-customer and per-environment attribution. A token issued to a monitoring integration so it
could read incidents can read what every customer is billed.

Neither is being re-scoped here, because adding a scope requirement to an existing route revokes
access for every token already issued. **Both are worth splitting and that is a decision for the
product owner, not a refactor:** a `cost:read` scope and an `apps:secrets` scope would each
isolate a privilege that has no business travelling with the one it currently does. Until then the
label tells the truth about the grant.

## How the labels are kept honest

The description is no longer written. Each scope declares its resources as data keyed by the route
segment that serves them, and the sentence is rendered from that list:

```csharp
[AppsWrite] = new("Trigger", [
    new("deployments", "a deployment sync or a restart"),
    new("secrets", "delivery of an app's vaulted secrets into its cluster"),
]),
```

`ApiScopeAdvertisementTests` boots the app, reads the generated OpenAPI document, and checks that
table against the routes in **both** directions:

- every declared resource is served by some route requiring that scope — no promise the server
  answers with a 404;
- every route a scope grants is named by one of its declared resources — **no grant the label
  hides**, which is the direction that found the two defects above.

⚠️ The second direction is the reason adding a route under an existing scope now requires adding
its resource to the table. That is the point of it.

This arrangement exists because of a failure this repo keeps repeating: a ratchet asserts a
number, never an explanation, so a wrong explanation stays green forever. `ApiTokenServiceTests`
already asserted `Describe(scope) != scope` — that a label exists — which every one of the eight
wrong labels satisfied. The only fix that holds is to build the explanation out of something
countable.

## What the scopes advertised and the API cannot do

These four were removed from the labels because no route serves them. They are the measured
backlog for `docs/decomposition.md`'s "the API is the product":

| promised, absent | what would have to exist |
|---|---|
| nodes (`fleet:read`) | a node listing per cluster; the data is already read for the workload browser |
| workloads (`fleet:read`) | the read-only workload browser's Pods/Deployments/ReplicaSets view |
| environments (`apps:read`) | environments are addressable in the UI and are a filter on deployments |
| rollout policies (`config:read`) | `/rollouts` returns history under `ops:read`; the policy itself has no route |
| open and resolve incidents (`ops:write`) | `/incidents` is read-only; only advisor findings can be acknowledged |

## ⚠️ `scim:provision` is not confined to the token's tenant

Found by review, verified against source, **not fixed here** — see below.

`ScimUserService` queries `db.ApplicationUsers` with no tenant predicate
(`ScimUserService.cs:89` for the list, `:130` for a single user) and **no SCIM method accepts a
tenant id**. It cannot: `ApplicationUser` has no tenant column, because tenancy is a separate
`TenantMembership` row. `ScimEndpoints` never calls `GetApiPrincipal()`.

So a `scim:provision` token minted by one tenant can enumerate every account in the installation
— sign-in name, email address, enabled flag, 200 at a time with pagination — and rename, disable
or delete any of them, **platform administrators included**. Disabling is immediate and
session-killing: `SetEnabledAsync` sets a far-future lockout and stamps the security stamp
(`ScimUserService.cs:325-336`), so the user is signed out and stays out.

`ApiTokenService.CreateAsync` places no restriction on which scopes a tenant may grant
(`ApiTokenService.cs:52-77`), so any caller who can reach the token form can tick this box.

And the form said the opposite. Above the scope checkboxes, verbatim:

> Each token is scoped to this tenant and to the permissions you grant — **it can never reach
> another tenant's data.**

That sentence is true of all 24 `/api/v1` routes and false of the seven under `/scim/v2`. A form
that makes a categorical security promise while offering the exception to it on the same screen is
worse than one that promises nothing. `PublicApiEndpoints.cs:15-18` makes the same claim about
"every route", calling it "the single most important property of this file" — literally true,
since SCIM is a different file, but the paragraph introduces the public API as a whole.

**What was changed here:** the form now states the property for `/api/v1` and names
`scim:provision` as the exception, and the scope's label says "every user account in this
installation, including users of other tenants". `ApiScopeAdvertisementTests` pins both.

**What was not, and why it is a decision rather than a fix:**

1. *Scoping SCIM to the token's tenant* — joining through `TenantMembership` — changes what the
   endpoint returns for every connector already deployed against it, and requires deciding what
   `POST /Users` means: create an account, or create an account *and* a membership in the calling
   tenant? What should `PATCH active=false` do to a user who is also a member of three other
   tenants — lock the account installation-wide, or only drop the membership? Those are product
   answers, not refactors.
2. *Restricting who may grant the scope* — e.g. platform-owner only — is the smaller change and
   may be the right one, since a directory connector is installation infrastructure rather than
   a tenant's integration. Also a product decision.
3. The files involved (`ScimUserService.cs`, `ScimEndpoints.cs`) are owned by open PR #423, so
   editing them here would conflict with it.

Correcting `PublicApiEndpoints.cs:15-18`'s "every route" paragraph is held for the same reason:
that file is owned by open PR #422.

## How far the API is from being the product

`docs/decomposition.md` says "the API is the product": a separate frontend, in-cluster agents and
the compose deployment are all blocked on an API complete enough to drive the platform without a
browser. That blocker had never been measured. It is now.

Five agents, one per domain, read the Blazor pages and services for their area and compared them
against the verified 31-operation inventory:

| domain | capabilities found | no API route at all | would block a separate frontend |
|---|---|---|---|
| fleet — clusters, nodes, catalog, blueprints, workloads | 22 | 21 | 22 |
| identity — tenants, users, tokens, SCIM, tickets, agreements | 21 | 21 | 21 |
| apps — apps, environments, deployments, vault, routes, policy | 20 | 19 | 20 |
| ops — advisor, incidents, SLA, drift, supply chain, backups | 20 | 12 | 20 |
| cost — run rate, ledger, rate sheets, idle capacity | 11 | 4 | 5 |
| **total** | **94** | **77** | **88** |

So the API exposes roughly a quarter of what the product does, and 88 of 94 measured capabilities
could not be offered by a separate frontend without a new route. **Cost is the best-covered
domain by a wide margin** — unsurprisingly, since `/cost`, `/cost/history`, `/cost/months` and the
rate sheets were built out deliberately. Fleet and identity are the least covered: 43 capabilities
between them, none of which has a route.

The gaps are weighted towards reads, which is the useful direction: a read-only frontend is the
first milestone, and most of what is missing is a detail view of something the API can already
list. Representative examples, each cited in the agents' output:

- **fleet** — node inventory and capacity; live cluster health (ready/total nodes, pod states,
  CPU and memory pressure); the read-only workload browser; the component catalog an operator
  picks from; a tracked component's configuration and failure state; bootstrap run progress.
- **identity** — the customer registry; the support-ticket queue with its SLA clocks; a ticket's
  event timeline; a customer's timebank statement; a user's effective access; the tenant's own
  API token list.
- **apps** — one app with its environments; the environment list (every per-environment view in
  the UI starts from ids the API never returns); a deployment's definition and desired state; a
  Helm deployment's values; the live resource tree; pod logs.
- **ops** — the drift diff itself (the API returns a changed-line count, not the diff); the CVEs
  behind a vulnerable image; per-cluster Kubernetes support status; one incident in full with its
  timeline; Velero backup schedules.
- **cost** — one customer's run rate scoped server-side (the customer portal's own view); accrued
  spend per month for one customer; what drives a namespace's bill (CPU/memory/storage/network
  split).

⚠️ **Verification status, because the two halves differ.** The route side is solid: the
31-operation inventory was enumerated three times independently — by hand, by an agent, and by the
assertion in `ApiScopeAdvertisementTests` that each scope declares exactly what it grants — and all
three agree, so "no route serves X" is reliable. The capability side is one agent per domain
reading the UI, and the individual capability descriptions are **not** independently verified. Treat
the counts as a measurement of the shape and size of the gap, not as a specification. Each entry
needs its own reading before it becomes a route.
