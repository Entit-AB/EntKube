# Just-in-time customer access

Time-boxed, namespace-scoped `kubectl` access for a customer, to one app in one
environment, brokered entirely through EntKube.

## The problem

A customer occasionally needs to see their own workload the way an operator does
— describe a pod, read an event, tail a log that the portal does not surface.
Today the only answers are "read it in the portal" or "an operator does it for
them". Standing cluster credentials are the wrong fix: they are permanent, they
are reused, and once issued nobody can say what they were used for.

JIT access is the narrow version: a grant that names one app, one environment,
one namespace and one person, expires by itself, and leaves a record of every
request made under it.

## The boundary

The unit of access is **an app in an environment**, which already pins a
namespace — [`AppEnvironment.Namespace`](../src/EntKube.Web/Data/AppEnvironment.cs#L20),
enforced on every deployment write in
[`DeploymentService.EnforceNamespaceAsync`](../src/EntKube.Web/Services/DeploymentService.cs#L40-L52).
An environment may host several clusters, so a grant names its cluster
explicitly rather than deriving it.

### The precondition that makes this safe

Namespace-scoped RBAC is only as strong as namespace exclusivity, and nothing in
the schema enforces it today: two apps — potentially two customers — can be
assigned the same namespace. If that happens, a correctly-scoped grant silently
exposes the other app's workloads and Secrets.

So the grant path **refuses to issue** when the target namespace is claimed by
any other app, and the check runs again at approval time, not only at request
time. This is a precondition of the feature, not a nice-to-have.

## How it works

EntKube never hands the customer a credential for the cluster, and never
publishes the cluster's API server. The customer's kubeconfig points at EntKube;
EntKube forwards to the API server using the grant's own ServiceAccount token.

```
   customer                    EntKube                        cluster
  ┌──────────┐            ┌──────────────────┐         ┌────────────────────┐
  │ kubectl  │ ── https ─▶│  /jit/{grantId}  │         │  API server        │
  │          │  ekj_…     │                  │         │                    │
  │          │            │ 1 validate grant │         │  ns: acme-billing  │
  │          │            │ 2 check ns match │── SA ──▶│   sa/jit-…         │
  │          │            │ 3 audit request  │  token  │   role/jit-…       │
  │          │◀───────────│ 4 stream back    │◀────────│   rolebinding/jit-…│
  └──────────┘            └──────────────────┘         └────────────────────┘
```

Three properties are what make it acceptable to offer:

- **No cluster credential leaves EntKube.** The customer holds an EntKube token
  (`ekj_…`), validated the same way an API token is —
  [`ApiTokenService.ValidateAsync`](../src/EntKube.Web/Services/PublicApi/ApiTokenService.cs#L98).
  Only the SHA-256 hash is stored.
- **Two independent enforcement points.** The proxy rejects any request whose
  namespace is not the grant's, *and* the SA's RoleBinding is namespace-scoped.
  A bug in either one is not sufficient to cross the boundary.
- **Expiry is enforced by the cluster, not by a background job.** The SA token is
  a bound token minted with an explicit duration. If the reaper never runs, the
  credential still dies. (The API server may cap the requested duration below
  what was asked for; treat the returned expiry as authoritative.)

### Why per-grant Kubernetes objects

Each grant creates its own ServiceAccount, Role and RoleBinding — named
`jit-{shortGrantId}` in the app's namespace — rather than reusing a shared
"customer-readonly" SA.

A bound token cannot be invalidated before it expires. So "revoke now" has to
mean **delete the RoleBinding**, which takes effect on the next API call. That
only works if the binding belongs to exactly one grant. Per-grant objects also
make the audit trail unambiguous: an API server audit line names the SA, and the
SA names the grant.

The manifests are built the same way the existing per-app RBAC is, in
[`AppGovernanceService`](../src/EntKube.Web/Services/AppGovernanceService.cs#L717-L765),
and applied through
[`IKubernetesClientFactory.ApplyManifestAsync`](../src/EntKube.Web/Services/IKubernetesClientFactory.cs#L17).

## Access levels

A deny-list, not a trimmed allow-list. Inside a single namespace, several verbs
break the boundary on their own:

| Verb | Why it is not "just read access" |
| --- | --- |
| `get secrets` | The app's own credentials, in the clear |
| `create pods/exec` | Yields the pod's ServiceAccount token — escalation to whatever the app can do |
| `create pods/portforward` | Reach anything the pod can reach, including other namespaces |
| `impersonate` | Becomes another subject entirely |
| any cluster-scoped read | Nodes and namespaces are shared; listing them enumerates other customers |

Three levels, each a separate approval:

| Level | Rules |
| --- | --- |
| **Observe** (default) | `get,list,watch` on pods, services, deployments, replicasets, statefulsets, events, ingresses; `get` on `pods/log` |
| **Troubleshoot** | Observe, plus `get,list` on configmaps (**not** secrets) and `create` on `pods/exec` for a named container allowlist |
| **Operate** | Troubleshoot, plus `delete` on pods and `patch` on deployments/scale |

Observe is what the feature is for. The other two exist so that "we need more"
has an answer that is still time-boxed and audited, rather than an operator
pasting a kubeconfig into chat.

Troubleshoot's `pods/exec` is only defensible because the app's own ServiceAccount
is now constrained: the same deny-list is enforced on write by
[`AppRbacRuleValidator`](../src/EntKube.Web/Services/AppRbacRuleValidator.cs), on
the apply path by
[`ManifestKindPolicy`](../src/EntKube.Web/Services/ManifestKindPolicy.cs), and at
admission by the `restrict-rbac` Kyverno policy. Exec yields the target pod's
token, so the level is worth no more than those safeguards together are worth.

## Lifecycle

1. **Request** — a customer portal user asks for access to an app+environment,
   with a required reason and an optional ticket reference.
2. **Approve** — a tenant operator approves, choosing level and duration (capped;
   default 1h, maximum 8h). Approval runs through
   [`IClusterChangeGate`](../src/EntKube.Web/Services/ClusterChanges/IClusterChangeGate.cs)
   like any other cluster mutation, so the operator sees the exact RBAC being
   applied before it lands.
3. **Mint** — namespace exclusivity is re-checked; SA/Role/RoleBinding are
   applied; a bound SA token is requested; an `ekj_` token is generated and shown
   **once**, with a ready-to-use kubeconfig.
4. **Use** — every proxied request is audited via
   [`AuditService.RecordAsync`](../src/EntKube.Web/Services/AuditService.cs#L13)
   (verb, resource, namespace, outcome) and stamps `LastUsedAt`.
5. **End** — on expiry or explicit revoke, the RoleBinding, Role and SA are
   deleted and the grant row is kept (never deleted) so the trail survives.

Self-approval is rejected: the requester and the approver must be different users —
and the subject cannot approve either, since approving your own request and approving
one somebody filed on your behalf are the same thing from the cluster's side.

The approver must hold **Manage** on `TenantFeature.JitAccess`. That is a permission
of its own rather than a reuse of Deployments, which was the obvious candidate and is
too wide: deploying an app changes what runs in a namespace, while approving this
hands a person outside the organisation a shell into it. It is also deliberately not
[`CustomerAccessRole.Admin`](../src/EntKube.Web/Data/CustomerAccess.cs), which is a
portal role held by the customer themselves.

Nobody holds the new permission until a tenant admin grants it under
**Admin → Roles**, so a fresh deployment has a visible queue and no approvers until
somebody decides who they are. For this particular permission that is the right way
round.

## Data model

One table, `JitGrant`, modelled on
[`ApiToken`](../src/EntKube.Web/Data/ApiToken.cs) — hash-only storage, explicit
`ExpiresAt`/`RevokedAt`, `LastUsedAt`, rows retained after revocation.

| Group | Fields |
| --- | --- |
| Scope | `TenantId`, `CustomerId`, `AppId`, `EnvironmentId`, `KubernetesClusterId`, `Namespace` (snapshot, not a lookup) |
| Subject | `UserId`, `Level` |
| Justification | `Reason` (required), `TicketRef` |
| Workflow | `RequestedAt/By`, `ApprovedAt/By`, `ExpiresAt`, `RevokedAt/By`, `RevokeReason` |
| Credential | `ServiceAccountName`, `TokenHash`, `DisplayPrefix`, `LastUsedAt`, `TokenExpiresAt` |

`Namespace` is a snapshot on purpose. If governance later re-points the app at a
different namespace, the live grant must keep meaning what it meant when it was
approved, and the proxy must compare against that frozen value.

### Two ways of naming a person

A grant names people twice, and the two are not interchangeable.

`UserId` is an **account id** — a foreign key to `AspNetUsers`, and the value every
later decision is made against: the approver's own id is compared to it to refuse
self-approval, and tenant membership (and so the approver permission) is keyed to one
as well. In a Blazor component it comes from the `NameIdentifier` claim.

`RequestedBy`, `ApprovedBy`, `DeniedBy` and `RevokedBy` are **display names** — the
email a person recognises, read straight off `Identity.Name`, stored so the history
says who decided rather than which key did.

`Identity.Name` is the username, which is an email here, so using it as an account id
fails in two ways that both look like nothing happening: the insert violates a
foreign key, and a membership lookup matches no row, leaving a queue with no
approvers. `RequestAsync` therefore refuses a subject that is not a known account,
and `JitAccessService.SubjectName` is the one place that turns the key back into a
name for a screen or an audit row.

## Configuration

Two things must be done before a grant can be issued: set `Jit:PublicBaseUrl`, and
grant somebody **Manage** on *JIT cluster access* under **Admin → Roles**.

`Jit:PublicBaseUrl` is the externally reachable URL of this EntKube instance, and it
is also the switch. Setting it turns the feature on; leaving it empty leaves the
request and approval workflow in place but refuses at the point of minting.

It doubles as the switch on purpose. The kubeconfig a grant hands out points at that
URL, so a grant issued without it could not work in any case — and a feature that
issues cluster credentials should not come alive merely because somebody deployed a
new version.

## What was built

| Piece | Where |
| --- | --- |
| Grant model and lifecycle | [`JitGrant`](../src/EntKube.Web/Data/JitGrant.cs), [`JitAccessService`](../src/EntKube.Web/Services/Jit/JitAccessService.cs) |
| Per-grant RBAC | [`JitRbacBuilder`](../src/EntKube.Web/Services/Jit/JitRbacBuilder.cs) |
| Minting and teardown | [`KubernetesJitProvisioner`](../src/EntKube.Web/Services/Jit/KubernetesJitProvisioner.cs) |
| Bound-token storage | [`JitClusterTokenStore`](../src/EntKube.Web/Services/Jit/JitClusterTokenStore.cs) |
| Expiry and orphan sweep | [`JitGrantReaperService`](../src/EntKube.Web/Services/Jit/JitGrantReaperService.cs) |
| Namespace enforcement | [`JitPathPolicy`](../src/EntKube.Web/Services/Jit/JitPathPolicy.cs) |
| The proxy | [`JitProxyService`](../src/EntKube.Web/Services/Jit/JitProxyService.cs), [`JitUpstreamClientPool`](../src/EntKube.Web/Services/Jit/JitUpstreamClientPool.cs) |
| UI | `PortalJitAccessPanel`, `TenantJitAccessTab` |

### The trap in the upstream connection

Worth stating separately, because it is invisible at the call site and would have
been a cluster-admin hole rather than a bug.

EntKube's stored kubeconfig usually authenticates with a client certificate, and
Kubernetes runs its authenticators with x509 first. A request presenting both a
valid client certificate and a bearer token is authenticated as the *certificate* —
the token is never examined. Forwarding a customer's request over a connection built
from the ordinary kubeconfig would therefore have executed it as cluster-admin, while
every line of code around it said it was executing as the grant's ServiceAccount.

[`JitUpstreamClientPool`](../src/EntKube.Web/Services/Jit/JitUpstreamClientPool.cs)
therefore builds its own handler: the CA comes from the kubeconfig so the server is
still verified, and the client certificate deliberately does not. The only credential
that can reach the API server through it is the bearer token the caller attaches.

### What is not verified

The unit tests cover the lifecycle, the RBAC that gets rendered, the reaper's
decisions and every refusal path in the proxy. Three things can only be confirmed
against a live cluster:

- the `TokenRequest` call and whether a given API server caps the requested duration
- forwarding itself, including whether `watch` and `logs -f` stream correctly
The migrations *are* verified. Postgres was applied end to end against a real
`postgres:17` — the table, all 31 columns and all 9 indexes land correctly. SQL Server
cannot be verified the same way because its migration chain has been broken since the
fifth migration in May (see [migrations-defect.md](migrations-defect.md), "SQL Server
does not work at all"), so this migration's generated T-SQL was executed on its own
against a real SQL Server 2022 instead: 31 columns, 9 indexes, six foreign keys with
exactly one `CASCADE`. That last number is the one that matters — SQL Server rejects a
table reached by multiple cascade paths, and `JitGrants` has six parents.

## Cluster-scoped RBAC

The three safeguards above all operate inside a namespace. A Helm chart creating a
ClusterRoleBinding sits outside every one of them: `ManifestKindPolicy` only sees
stored YAML manifests and
[`HelmInstallOrUpgradeAsync`](../src/EntKube.Web/Services/KubernetesOperationsService.cs#L2342)
never renders templates for inspection, and `restrict-rbac` is a namespaced Kyverno
`Policy`, which cannot match a cluster-scoped resource.

The obvious fix does not work. EntKube installs catalog components and customer
charts with the same cluster credential, so admission sees one subject for both and
has nothing to tell them apart. Nor is there a namespace to exclude — a
ClusterRoleBinding has none.

What separates the two cases is not who created the binding but **who it grants to**.
A component's ClusterRoleBinding names a ServiceAccount in the component's own
namespace; the escalation this exists to stop names one in an app namespace. So the
`restrict-cluster-rbac` ClusterPolicy carries the list of app namespaces — which
EntKube already computes in order to know where to apply the namespaced policies —
and denies on the subject. Components need no exclusion list at all, because they
were never matched in the first place. Bindings named `system:*` are excluded, since
the API server recreates `system:basic-user` and `system:discovery` against
`system:authenticated` on startup and would otherwise fight the policy.

Creating a ClusterRole is deliberately left alone: one that is bound to nothing
grants nothing, and denying the kind would break every operator chart in the catalog
for no gain.

Two properties of it being one shared object are worth stating, because both are
ways it could have been quietly wrong:

- **The namespace list is cluster-wide, not tenant-wide.** Built from the applying
  tenant's namespaces, it would drop every other tenant's from the deny-list the
  moment that tenant applied.
- **The strongest mode wins.** Last-writer-wins would let one tenant's apply drop
  another's Enforce to Audit, and the two would flip it back and forth on every
  deploy.

The list is data, not configuration, so it is rewritten after every deploy as well as
on every policy apply — a namespace created by a deploy is absent from the policy
written before it existed.

## What this deliberately does not do

- **No OIDC-based cluster auth.** Wiring customer identities into the API server
  as an OIDC provider would be the more standard answer, but it makes the cluster
  trust EntKube's IdP for authentication and moves enforcement to a place we do
  not audit per-request. The proxy keeps both in one place.
- **No direct API server exposure.** Publishing a reachable API server endpoint
  to make JIT work is a far larger change in exposure than the feature earns.
- **No secret reads at any level.** If a customer needs a secret value, that is
  the vault's job, with its own scoping — not a kubectl grant.
- **No resolution of a custom ClusterRole's rules.** A RoleBinding in an app
  namespace pointing at a wildcard ClusterRole that is not `cluster-admin`, `admin`
  or `edit` is still admitted; matching it would mean an API call from the policy to
  read the ClusterRole's rules at admission time.
