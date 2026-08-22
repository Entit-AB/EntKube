# EntKube platform roadmap — eight-feature program

Status legend: ☐ not started · ◐ in progress · ☑ shipped

| # | Feature | Phase | Status |
|---|---|---|---|
| 1 | Fleet upgrade & lifecycle planner | 1 | ☑ |
| 6 | Drift detection for managed components | 1 | ☑ |
| 4 | Supply-chain security (CVE + signing) | 2 | ☑ |
| 2 | Public API, tokens, CLI, MCP server | 2 | ◐ |
| 3 | Cost & chargeback | 3 | ☑ |
| 5 | Progressive delivery + auto-rollback | 3 | ◐ |
| 7 | OIDC/SSO + SCIM for the portal | 4 | ◐ |
| 8 | Velero cluster DR | 4 | ☑ |

## Why this order

Three of the eight (1, 4, 6) are **finding producers** — they all terminate in the
same place: `OperationsFinding` records surfaced by `OperationsAdvisorService` and
digested by `AdvisorScanService`. Building #1 first establishes the shared
"desired vs actual, on a schedule, into the Advisor" spine that #4 and #6 reuse
almost verbatim. Doing them in any other order means building that spine twice.

Features 2, 3, 5, 7 and 8 are independent of that spine and of each other, so they
are sequenced by value rather than dependency.

## Shared foundations (build once, used by many)

| Foundation | Introduced by | Reused by |
|---|---|---|
| `SemVer` parse/compare | #1 | #4 (CVE version ranges), #5 |
| Helm repo index client (`index.yaml` → available versions) | #1 | #6 |
| Periodic desired-vs-actual reconcile background service | #1 | #6, #4 |
| New `AdvisorCategory.Maintenance` / `.SupplyChain` + finding sources | #1 | #4, #6 |
| Scoped API token auth (hash, scopes, tenant binding) | #2 | MCP server, CLI, Terraform provider |
| Usage rollup store (tenant × cluster × window) | #3 | customer portal invoicing |

The existing `IClusterChangeGate` (ack-before-apply dry-run diff) is the mutation
path for #1, #5, #6 and #8 — none of these introduce a second apply route.

---

## Phase 1 — Keep the fleet current

### 1. Fleet upgrade & lifecycle planner  ☑

**Problem.** 30+ curated components carry pinned `HelmChartVersion` values in
`ComponentCatalog.cs`, and `ComponentLifecycleService` can `helm upgrade` — but
nothing tells an operator an upgrade exists. Chart currency, Kubernetes version
EOL, and pre-upgrade API deprecations are all invisible today.

**Design.**
- *Installed side* is already solved: `ComponentScanService` decodes Helm release
  secrets from each cluster and yields `ChartVersion` / `AppVersion` per release.
  No new cluster access is required.
- *Available side*: fetch `{HelmRepoUrl}/index.yaml` over HTTPS and parse the
  entry list per chart. This needs no `helm` binary and no cluster round-trip, so
  it runs on the management plane and caches per repo URL.
- *Comparison*: an internal `SemVer` type (parse, compare, pre-release aware,
  `v`-prefix tolerant). No new NuGet dependency for ~120 lines of logic.
- *Classification*: patch / minor / major behind, plus "pinned catalog version is
  itself stale" (the catalog pin is behind upstream) — the second is a signal for
  *us*, not for the operator, and is reported separately.
- *Findings*: emitted as `Source = "upgrade"` under a new
  `AdvisorCategory.Maintenance`, horizon derived from lag severity (major behind
  or EOL → `ThisWeek`; otherwise `Later`).
- *K8s EOL*: a small static table of minor-version EOL dates, compared against the
  live server version already collected per cluster.
- *Deprecated APIs* — **shipped with #6** as `DeprecatedApiScanner`: scans the stored
  desired manifests for API versions upstream has removed, keyed by the removing K8s
  minor, and resolves each cluster's actual server version so a usage reads as
  "already removed" rather than merely "upcoming".
- *Action*: "Upgrade" routes into the existing `ComponentLifecycleService`
  upgrade path behind the change gate — no new apply mechanism.

**Shipped**: `SemVer`, `HelmRepoIndexClient`, `KubernetesReleaseCalendar`,
`ComponentUpgradeService`, advisor findings (`AdvisorCategory.Maintenance`,
`Source = "upgrade"`), `UpgradesTab` mounted under Ops → Lifecycle, 70 unit tests.
Validated against the live Traefik index (502 versions, 0 unparseable, 89 ms) and
all 23 catalog repositories — which surfaced a dead Submariner repo URL, now fixed.

**Outstanding**: none — the deprecated-API scan shipped with #6.

### 6. Drift detection for managed components  ☑

Once EntKube applies a deployment, a human `kubectl edit` went unnoticed. Drift is
now detected with the same primitive the change gate already uses — a server-side
`kubectl diff` of the desired manifest — so drift and the pre-apply preview agree by
construction.

**Key decision**: the desired-manifest rendering was *extracted* from the apply path
into `DeploymentManifestComposer` and shared, rather than re-implemented. Had drift
rendered its own manifest, every difference between the two implementations would
have shown up as permanent phantom drift that re-applying could never clear.

**Shipped**: `DeploymentManifestComposer` (extracted, shared with
`KubernetesOperationsService.ApplyYamlDeploymentAsync`), `DeprecatedApiScanner`,
`DriftDetectionService`, `DriftScanCache` + `DriftScanService` (2-hourly background
sweep), advisor findings (`Source = "drift"`), `DriftTab` under Ops → Lifecycle,
22 unit tests.

**Deliberate limits**:
- Helm-based deployments are excluded — Helm owns their reconciliation, and diffing
  raw manifests against a rendered chart would report noise as drift.
- Results are cached in-process, not persisted (no migration). A multi-instance
  deployment gives each instance its own view until its own sweep runs. Acceptable
  for an advisory signal; would not be for anything gating a mutation.
- The advisor reads the cache and never triggers a sweep, so a page render never
  forks a dry-run per deployment.

**Subprocess hardening found by testing against a real kubectl** (v1.36.2):
- kubectl *prompts on stdin* when a kubeconfig's user has no usable credentials.
  With inherited stdin that prompt never gets EOF in a server process and the diff
  hangs to the timeout, four at a time. stdin is now closed immediately — verified
  0.18 s instead of a hang.
- `WaitForExitAsync(ct)` stops awaiting but does not stop the process; a timed-out
  diff leaked a running kubectl per sweep. Now explicitly killed.

## Phase 2 — Trust what is running

**Outstanding from #4**: SBOM retrieval (Harbor's `additions/sbom` endpoint). The CVE
path covers the operational question ("what is vulnerable right now"); SBOM answers a
compliance/audit question and is a self-contained addition on the same Harbor client.

### 4. Supply-chain security  ☑

Harbor already ran **with Trivy** and Kyverno was already managed — but scan
results were never surfaced and image signatures were never verified.

**Key decision**: the CVE join runs *from the running workloads outward*, not from
the registry inward. Enumerating every Harbor project and repository would cost
hundreds of API calls to describe images nobody is running; starting from live
workloads bounds the work by what is actually deployed.

**The rule that shapes the whole feature**: an unscanned image is *not* a clean
image. `ImageScanState` separates `Unscanned` from `Clean`, the advisor raises a
finding for unscanned images, and the UI says so explicitly. Reporting "no findings"
for an image nobody scanned would be the most dangerous thing this feature could do.

**Shipped**: `ImageReference` (registry/repository/tag/digest parsing),
`SupplyChainService` (workload → registry join), Harbor scan-overview + CVE detail
support, `SupplyChainScanCache` + 6-hourly `SupplyChainScanService`,
`AdvisorCategory.SupplyChain` findings, `SupplyChainTab` under Ops → Lifecycle,
Kyverno `VerifyImageSignatures` policy (cosign key + keyless/OIDC) wired into the
existing governance UI, 40 unit tests.

**Notes**:
- The Kyverno `verifyImages` builder is written with explicit `StringBuilder`
  indentation rather than an interpolated raw string. The first version used
  interpolation and produced YAML that looked right but did not parse — caught by a
  test that actually parses the output, not by inspection.
- An incomplete signature config emits **no policy at all** rather than one that
  verifies nothing, and the UI says the policy is not applied. A signature policy
  that silently passes everything is worse than none, because it reads as protection.
- `mutateDigest: true` means what is admitted is exactly what was verified, so a
  mutable tag cannot be repointed after verification.
- SBOM retrieval is **not** implemented — see below.

### 2. Public API, tokens, CLI, MCP server  ◐

**Shipped: the API and its auth model.** `/api/v1` with scoped bearer tokens.

**Key property, enforced structurally**: no route accepts a tenant id from the
caller. Every handler reads the tenant from the authenticated token, so a token
cannot be pointed at another tenant's data by changing a parameter. Where an entity
has no `TenantId` of its own (incidents belong to a cluster; apps belong to a
customer) the query filters through that relationship rather than trusting the id.

**Auth is a per-route endpoint filter, not global middleware.** A route added
without `RequireApiScope` does not authenticate — and therefore is simply not
reachable as an API route — rather than silently defaulting to public.

**Token design**: 256 bits of CSPRNG output, `ekp_` prefixed (greppable in logs,
recognisable to secret scanners), stored only as a SHA-256 hash. SHA-256 rather than
a password hash on purpose — the input is full-entropy random, so there is no
dictionary to attack and a slow hash would only add latency to every request.
Plaintext is shown exactly once. Revocation keeps the row so the audit trail
survives; tokens cascade-delete with their tenant so a deleted tenant leaves no
working credentials.

**Endpoints**: whoami · clusters · cluster components · apps · deployments ·
deployment sync · deployment restart · advisor findings · finding acknowledge ·
incidents · upgrades · drift · supply-chain.

`/drift` and `/supply-chain` return **503, not an empty 200**, when no sweep has
completed — "not scanned yet" and "nothing wrong" are different answers and a
polling caller must not confuse them.

**Shipped artifacts**: `ApiToken` entity + migrations (SQLite/Postgres/SQL Server),
`ApiScopes`, `ApiTokenService`, `ApiTokenAuthFilter`, `PublicApiEndpoints`,
`ApiTokensTab` UI, 39 unit tests.

**Shipped: the MCP server.** `src/EntKube.Mcp` — a self-contained `entkube-mcp`
binary exposing the tenant to any MCP client over stdio. See `docs/mcp-server.md`.

- **Two independent gates.** The token's scopes are the real authority, enforced
  server-side. On top of that the server is read-only unless started with
  `--allow-write`, and write tools are then *absent from `tools/list`* rather than
  advertised-and-refused — a model never plans around a capability it lacks. The
  caller is a language model, not a reviewed script, so a broadly-scoped token
  should not by itself let it change a cluster.
- **Protocol hand-rolled, no dependency.** MCP stdio is newline-delimited JSON-RPC
  with a handful of methods, and the rule that actually matters — never write
  anything but a JSON-RPC message to stdout — is easier to guarantee owning the
  writer than sharing it with a library.
- **Semantics carried into tool descriptions**, so the model sees them: `503` means
  *unknown, not clean*; an unscanned image is *not* a clean image; sync overwrites
  out-of-band edits.
- Argument mistakes return tool errors, not JSON-RPC errors, so the model can retry
  rather than lose the turn.
- Verified end to end against the built binary: correct handshake, notifications
  answered with silence, 10 tools read-only / 13 with `--allow-write`, unreachable
  API surfaced as a readable tool error, and a malformed input line answered with a
  parse error without killing the session.

**Shipped: the CLI.** `src/EntKube.Cli` → an `entkube` binary over the same API.
See `docs/cli.md`.

- The API client is **extracted** into `EntKube.ApiClient`, shared with the MCP
  server, so the HTTP-status→advice mapping lives in one place. Two copies would
  drift, and the wording for a 403 or a 503 is exactly what should be identical
  everywhere.
- Exit codes are chosen for CI: 0 success, 1 request failed, 2 bad usage, 3 rows
  returned with `--fail-on-results`. That last one is the pipeline gate —
  `entkube drift --fail-on-results` fails a build on drift without parsing output.
- The sweep-backed commands return 503 when no sweep has run, and the CLI exits 1
  rather than 0, so a pipeline cannot mistake an unmeasured fleet for a clean one.
- Tables by default (a person is reading), `--json` for `jq`.

**Outbound webhooks: hardened rather than rebuilt.** A Webhook notification channel
already existed, so the work was to make it safe rather than to add a second one.

- **SSRF fix.** The channel posted to an operator-supplied URL with no destination
  validation at all. Since the management plane can reach every managed cluster, its
  own loopback and the cloud metadata service — while the URL is supplied by a
  *tenant* user — "send my alerts here" was a request for EntKube to dial anything it
  could reach. `OutboundUrlGuard` now default-denies everything not publicly routable,
  including IPv4-mapped IPv6 forms of private ranges, and resolves hostnames requiring
  every returned address to be public. Applied to the Slack, Teams and generic webhook
  senders; the hard-coded Graph endpoint needs no check. Instance-wide opt-in for
  operators with a genuine internal receiver, deliberately not per-tenant.
- **HMAC signing.** Deliveries can now carry `X-EntKube-Signature-256` over
  `{timestamp}.{body}`. The timestamp is inside the signed material so a captured
  delivery cannot be replayed with a fresh one. Verification uses a fixed-time compare.

Residual risk, stated in `docs/webhooks.md`: DNS rebinding between validation and the
request still gets through. Closing it needs the connection pinned to the validated
address via a `SocketsHttpHandler` connect callback.

**Rollout outcomes now notify.** The rollout policy offered an "Alert" failure action
that did nothing but write a log line, while sitting in the UI next to "Roll back"
implying somebody got told — a defect in #5 as originally shipped, since an option
that promises a notification and delivers none is worse than not offering one.
Outcomes now dispatch through the tenant's notification channels (which, after the
hardening above, are SSRF-guarded and signable): failed rollback and automatic
rollback as critical, failed analysis as warning, unverifiable release as info.
Promoted and superseded stay silent — a channel firing on every successful deploy is
muted within a week, and the rollback notification is muted along with it.

**Outstanding**: the Terraform provider, and webhook events for the remaining new
signals (drift detected, DR gap opened) — advisor findings already reach channels
through the daily digest.

## Phase 3 — Prove and control value

### 3. Cost & chargeback  ☑

Turns consumption into money and attributes it to the customer consuming it.

**Charging is on requests, not usage, by default.** Requests are what the scheduler
reserves and therefore what a customer genuinely denies to everyone else — the
defensible basis for a chargeback. Billing on actual usage lets an over-requesting
team push the cost of their own waste onto their neighbours. Configurable per
cluster; storage is always charged on provisioned capacity either way, since a
half-empty volume still denies its full size to others.

**Fixed cluster overhead is spread in proportion to compute, not evenly** — a
namespace running one pod should not carry the same share of a control-plane fee as
one running half the cluster. When nothing consumes compute it falls back to an even
split rather than being dropped, so the reported total always reconciles to the real
bill. Pinned by a test.

**Unattributed namespaces are costed and shown, not hidden.** Platform namespaces
(ingress, monitoring, kube-system) cost real money. Dropping them would make the
tenant total understate the bill; silently attributing them to a customer would
misreport whose cost it is. They appear as "platform overhead" in the ops view and
are excluded entirely from the customer view.

**A 730-hour month**, so a run rate does not jump 10% between February and March for
reasons unrelated to consumption — the same convention cloud providers publish on.

**Shipped**: `ClusterCostRate` entity + migrations (all three providers, with explicit
`decimal(18,6)` precision — the SQL Server default of `(18,2)` would round a
per-core-hour rate to zero), `CostAllocation` (pure, no DB or Prometheus),
`CostReportService`, `CostRateService`, `CostScanCache` + hourly `CostScanService`,
`CostTab` with price-sheet editing, `CustomerCostPanel` in the portal (Operator role
and above — cost is commercial information), `/api/v1/cost`, an `entkube_cost` MCP
tool, and 20 unit tests on the allocation arithmetic.

**Deliberately not built**: historical billing. This is a *run rate* — what current
reservations project to — not a ledger of what was consumed last month. Invoicing
from history needs a cost rollup table and a retention policy; the run rate is the
chargeback basis and the larger slice of the value. The UI and every API/MCP
description say "run rate, not a bill" so it is never mistaken for one.

**No advisor findings.** Cost is not a "what needs doing by when" signal with a
deadline, and forcing it into the Advisor would dilute a feed whose value is that
everything in it has one.

### 5. Progressive delivery + auto-rollback  ◐

**Shipped: automated release analysis and rollback.** Applying a deployment that has
a policy opens a watch; a background watcher measures over the window, judges, and
promotes, alerts, or rolls back. Judged against EntKube's own trace store
(`GetServiceRedAsync` → error rate, p95), Prometheus (restarts), health snapshots
(readiness) and `ErrorBudgetService` (burn rate) — no external analysis provider.

**The rule the feature is built on: an unmeasurable signal is never a pass.** A
configured threshold whose signal is missing makes the result *less* certain, not more
reassuring. A release where nothing could be measured is `Inconclusive`, never
`Healthy`, and **an inconclusive release is never rolled back** — rolling back
production on no evidence is worse than the risk it avoids. Unavailable signals are
reported even on a healthy verdict, so a policy that silently checks almost nothing is
visible rather than looking like a clean pass. A service with zero requests has an
*unknown* error rate, not a 0% one.

**Alert is the default failure action**, not rollback: automatically reverting a
production workload is something an operator opts into, never inherits.

**Other decisions**:
- The watch is decoupled from the apply. Blocking a sync — and therefore any CI job —
  for a ten-minute analysis window would make the feature unusable in the pipelines it
  exists to protect. A failure to open a watch never fails an apply that succeeded.
- Rollback uses `kubectl rollout undo`, restoring the revision Kubernetes actually
  recorded, rather than re-applying an older manifest from EntKube's history — which
  would be a *guess* at what was running, and an incident is the worst time to discover
  the guess was wrong.
- p95 is weighted by request volume across buckets; an idle bucket's p95 says as much
  as a busy one only if you pretend they carry equal traffic.
- Measurement starts after warm-up, so pods restarting during the rollout itself are
  not counted against the new release.
- A watch overdue by more than 6 hours (process was down) is abandoned as inconclusive
  rather than judged on traffic nobody observed.
- The apply path depends on a narrow `IRolloutStarter`, not the full service, so a
  plain deployment apply does not drag Prometheus, the trace store and the error-budget
  service into every caller and test.

**Shipped artifacts**: `RolloutPolicy` + `DeploymentRollout` entities and migrations
(all three providers), `RolloutAnalysis` (pure), `RolloutService`,
`RolloutWatcherService`, `RolloutsTab`, `/api/v1/rollouts`, an `entkube_rollouts` MCP
tool, and 20 unit tests on the judgement rules.

**Outstanding: the *progressive* half.** Weighted canary traffic-splitting — stepping
an HTTPRoute's `backendRefs` weights toward a canary Service and promoting on success —
is not built. It needs a canary workload to exist, which means either synthesising
renamed manifests or having the operator declare a canary service; that is a
substantial feature in its own right and was not worth half-building. What ships here
is the analysis and rollback engine, which is the part that needed the control plane
and the differentiated part versus Flagger.

## Phase 4 — Enterprise edges

### 7. OIDC/SSO + SCIM  ◐

**Shipped: OIDC login and group→tenant role mapping.** See `docs/sso.md`.

Registered as an ordinary external-login scheme, so it flows through the existing
`ExternalLoginPicker` and `ExternalLogin` callback that Identity already provides —
no second, parallel sign-in path to keep correct. Authorization code + PKCE.

**Opt-in and config-driven**: with no usable `Oidc` section, no scheme is registered
at all, so the login page is unchanged rather than showing a button to a
half-configured provider. Provider settings are configuration, not database rows —
a misconfigured provider must not be editable by whoever is already logged in.

**Access is recomputed on every SSO login, not just the first.** That is what makes
offboarding work: losing a group in the directory removes EntKube access at next
sign-in, with nobody having to remember to do it here too.

**The rule that keeps that safe**: revocation is confined to tenants that have at
least one group mapping. A membership in a tenant no mapping mentions was granted by
hand and is none of SSO's business — deleting an operator's hand-granted access
because an unrelated SSO login did not mention it would be a spectacular way to lock
people out of their own platform. Pinned by three separate tests.

**Other decisions**:
- Group matching is exact, not normalised. Entra emits object ids; case-folding would
  silently match groups the operator never intended to grant.
- Group claims are read from repeated claims *and* from a single JSON-array claim,
  because providers do both — reading zero groups from the array form would silently
  revoke everyone's access.
- Two groups mapping to one tenant resolve deterministically by group name, so a
  user's role cannot flicker with claim ordering.
- A user whose groups map to nothing is signed out with an explanation rather than
  dropped into an empty portal that looks broken (configurable).
- If the sync itself throws, login proceeds on existing access and the error is logged
  loudly. Locking everyone out because a query failed is the worse outcome, but stale
  access after a group change is a real security concern, not a cosmetic one.

**Shipped artifacts**: `OidcOptions`, `ExternalGroupSync`, `ExternalGroupMapping`
entity + migrations (all three providers), OIDC scheme registration, sync hooked into
the external-login callback, `AdminSso` page, `docs/sso.md`, 16 unit tests.

**Outstanding: SCIM.** Users are provisioned just-in-time at first SSO login and
reconciled on every login, which covers the common case. SCIM adds directory-*pushed*
provisioning and deprovisioning without requiring a login — which matters when access
must be revoked in minutes rather than at next sign-in. It is a substantial surface
(`/Users`, `/Groups`, filtering, PATCH semantics) and is not started.

### 8. Velero cluster DR  ☑

Platform state, CNPG and Mongo were backed up; customer PVs and namespaces were not.

**The judgement the feature is built on: a backup that EXISTS is not a backup that
WORKS.** Velero records a `PartiallyFailed` backup with a completion timestamp, so
anything that asks only "did it finish" treats a backup that silently skipped
resources as a success — and that gets discovered during a restore, at the worst
possible moment. Only a clean, error-free `Completed` backup counts as restorable,
in the readiness rules, the UI, and the API.

**Velero's own CRs are the source of truth.** EntKube keeps no parallel record of
what has been backed up: a second copy would drift the moment a backup expired or
someone ran `velero backup create` by hand, and a DR feature that lies about what is
restorable is worse than none. This also means no new entity and no migration.

**Readiness gaps** (→ `AdvisorCategory.DataProtection`): no usable backup (critical),
stale backup past 36 h (critical), unreachable storage location (critical), no
schedule, paused schedule, a schedule that skips volume data, and backups that have
never been restore-tested.

Two of those are easy to get wrong and are pinned by tests:
- A schedule with `snapshotVolumes: false` captures Kubernetes objects but not volume
  contents, so a restore returns *empty* volumes — which looks like success until
  someone opens the application. Velero defaults the field to true, so an **absent**
  field must not read as "volumes are not captured", or every correct schedule raises
  a false alarm.
- "Untested" is suppressed when there is no usable backup to test. Reporting both is
  noise; only one of them is actionable.

A cluster without Velero produces no gaps at all — it is outside the feature's scope
rather than failing it, and flagging every such cluster would drown the real gaps.

**Shipped**: Velero catalog component (chart 12.1.0 / Velero 1.18.1, verified against
the live chart repo; AWS plugin since it serves every S3-compatible store, node agent
on so volume *data* is captured, path-style addressing for MinIO), `VeleroService`
(read CRs, create/delete schedules, one-off backups), `DrReadiness` (pure),
`DrScanCache` + hourly `DrScanService`, advisor findings, `DisasterRecoveryTab`,
`/api/v1/disaster-recovery`, an `entkube_disaster_recovery` MCP tool, 28 unit tests.

**Deliberately not built**: triggering restores from EntKube. Reading restore history
as evidence of testing is safe; *performing* a restore overwrites live cluster state
and is the one operation where a UI mis-click is unrecoverable. It belongs behind a
deliberate, typed-confirmation flow rather than a button added at the end of a batch
of features.
