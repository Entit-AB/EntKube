# Running two instances

A worked deployment: **two web instances plus one worker**, behind Caddy, sharing one Postgres,
one vault root key, one DataProtection key ring and one object-storage bucket.

The files:

| | |
|---|---|
| [`examples/docker-compose.two-instances.yml`](examples/docker-compose.two-instances.yml) | the stack |
| [`examples/Caddyfile.two-instances`](examples/Caddyfile.two-instances) | the load balancer, and the sticky-session directive |
| [`examples/env.two-instances.example`](examples/env.two-instances.example) | the installation-wide `.env` |

This is the end of steps 1 and 2 of the sequence in [`decomposition.md`](decomposition.md) §9.5 —
the point where the audit becomes something an operator can run. **It is not the end of §9.** Read
[What this does not make safe](#what-this-does-not-make-safe) before putting it in front of real
customer clusters; one of the items there is not a theoretical risk but a thing the UI will get
visibly wrong.

---

## The shape, and why it is this shape

Three EntKube containers from the same image, differing in one setting:

```
                        ┌──────────────────┐
    browsers ──────────▶│      Caddy       │◀────────── collectors, RUM
                        └────────┬─────────┘
       sticky by cookie ─────────┼───────── /ingest/* pinned by path
         ┌───────────────────────┼──────────────────────┐
         ▼                       ▼                      ▼
 ┌────────────────┐     ┌────────────────┐     ┌────────────────┐
 │ entkube-web-1  │     │ entkube-web-2  │     │ entkube-worker │
 │   Singleton=   │     │   Singleton=   │     │   Singleton=   │
 │     false      │     │     false      │     │      true      │
 └───────┬────────┘     └───────┬────────┘     └───────┬────────┘
         └──────────────────────┼──────────────────────┘
                                ▼
       Postgres · Redis · object storage · shared key-ring volume
       (each container keeps its OWN /app/Data)
```

`Workers:RunSingletonWork` (merged) decides whether an instance starts the hosted services that
must happen on exactly one of them. `HostedWork.AddSingletonWork` wraps each of them; a web
instance constructs them and never starts them. `AddPerInstanceWork` is the other kind — the
presence heartbeat refreshes the circuits *this process* holds, so a single leader would report
everybody else offline.

⚠️ **The flag is placement, not a lease.** Nothing checks that exactly one container has it true.
Set it true on two and §9.3 is live again, silently: two `RolloutWatcherService`s both call
`kubectl rollout undo` with no `--to-revision`, and the second undo rolls *forward* to the bad
release while both record `RolledBack`.

---

## The environment matrix

### Must be IDENTICAL on all three containers

| Variable | Why, and what a mismatch does |
|---|---|
| `Vault__RootKey` | ⭐ **This, plus one database, _is_ the shared vault.** The root key unseals a per-tenant DEK from the database, which decrypts a value from the database. Nothing secret is generated per process and nothing is written to local disk, so N instances with one key and one Postgres are already N live replicas of the decrypt path — no new component needed (§9.1). A container with a different key does not degrade; every secret on it fails to decrypt. |
| `DatabaseProvider`, `ConnectionStrings__DefaultConnection` | One database, or they are not one installation. |
| `DataProtection__KeyPath` (or `ConnectionStrings__Redis`, once [#374](https://github.com/Entit-AB/EntKube/pull/374) lands) | A per-container key ring means an antiforgery token, auth cookie or OIDC correlation value minted by one instance cannot be unprotected by another. Login fails about half the time and looks *flaky*, not misconfigured (§9.2). |
| `ConnectionStrings__Redis`, `Presence__Provider` | "Online now" on the admin users page is fed per circuit; without a shared tracker each instance reports only its own users. |
| `IMAGE_TAG` | Every instance runs EF migrations at boot. Two tags against one database is a race between two different migration sets. |
| `Telemetry__ObjectStorage__*` or `Telemetry__StorageLinkId` | The sealed tier is the only telemetry every instance can read. |
| `Telemetry__PublicIngestUrl`, OIDC, `Helm__Registries__*`, `Auth__AllowRegistration` | Ordinary configuration; drift here produces inconsistent behaviour depending on which instance you landed on. |

In the compose file all of these live in one `x-entkube-shared-env` YAML anchor, so copying a
service block to add a third web instance carries them along and cannot half-copy them.

### Must DIFFER

| | |
|---|---|
| `Workers__RunSingletonWork` | `true` on the worker, **`false` on every web instance**. ⚠️ It defaults to **true** — deliberately, so an existing single-instance install keeps sweeping after an upgrade. The mistake is therefore never "I forgot to turn it on", it is "I forgot to turn it off on the new instance". |
| The `/app/Data` volume | ⚠️ **One per container. Never one shared mount.** It holds the telemetry active index, a Lucene `IndexWriter`: one RWX volume shared by three processes fails on `write.lock`, and separate volumes split the data. There is no third option, which is precisely why telemetry needs its own process rather than a setting (§9.5 step 5). |
| Container name / LB upstream list | The worker is deliberately **not** in the round-robin pool. |

⚠️ **`DataProtection__KeyPath` is in the first table and `/app/Data` is in the second, and the
default puts the first inside the second.** The reference compose file defaults the key ring to
`/app/Data/keys`. Keep that default here and each container gets its own ring — the §9.2 login
failure, arrived at by doing the *right* thing for the index. The example mounts a separate
`entkube-keys` volume at `/app/keys` and points `DataProtection__KeyPath` there.

---

## ⭐ Sticky sessions

**Mandatory, and the part most likely to be got wrong.**

A Blazor Server circuit lives in one process. A SignalR backplane does not change that — it
distributes *messages*, not circuits. Starting one is two requests:

```
POST /_blazor/negotiate       → instance A mints a connection token
GET  /_blazor?id=<token>      → must reach instance A, which alone knows that token
```

Under Caddy's **default** load-balancing policy — `random` — the second request reaches the other
instance about half the time, and then the circuit never starts. The symptom is the nastiest one in
this document: **the page renders perfectly and responds to nothing.** Correct HTML, correct CSS,
no error, no login required to reproduce it, and nothing on screen that says why. Every page in
EntKube is interactive server rendering, so every page is affected.

The repo's single-instance Caddyfile is:

```caddyfile
{env.DOMAIN} {
	reverse_proxy entkube:8080
}
```

Adding a second upstream to that line is **not** the change. The change is the policy:

```caddyfile
reverse_proxy entkube-web-1:8080 entkube-web-2:8080 {
	lb_policy cookie entkube_lb
	lb_retries 2
	fail_duration 30s
}
```

`lb_policy cookie <name>` makes Caddy set a cookie on the first response whose value is an HMAC of
the chosen upstream's dial address, and routes every request carrying it back to that upstream.
Measured against caddy `v2.11.7`:

```
over TLS      Set-Cookie: entkube_lb=<64 hex>; Path=/; HttpOnly; Secure; SameSite=None
plain HTTP    Set-Cookie: entkube_lb=<64 hex>; Path=/; HttpOnly; SameSite=Lax
```

⚠️ Serve it over HTTPS, and not only for the usual reason. `SameSite=None` is what carries the
cookie through an OIDC provider's cross-site POST back to `/signin-oidc`; the plain-HTTP `Lax`
form is withheld on exactly that request, so the login callback lands on an arbitrary instance —
which has neither your circuit nor your correlation state.

This exact block was run against two stand-in upstreams before being written down, because the
failure it prevents is invisible in a browser and would be just as invisible in a document. Without
a cookie jar, 20 requests split 11/9 across the two upstreams and produced exactly **2** distinct
cookie values — one HMAC per upstream, which is the positive control that makes the next number
mean something. With a cookie jar, **20 of 20** requests reached the same upstream and Caddy sent
no further `Set-Cookie`. `/ingest/*` answered from the pinned upstream throughout. The checks in
[Verifying it worked](#verifying-it-worked) are those same commands.

Then the `lb_policy` line was deleted and the same run repeated, because a check that cannot fail
proves nothing: the two upstreams were still both reachable (11/9, so the harness had not changed),
but with a cookie jar the 20 requests split **14/6** across them. That split is the dead circuit.

⚠️ A second argument after the name is the **HMAC secret**, not a second cookie name
(`lb_policy cookie entkube_lb <secret>`). It is optional, and it stops a client picking its own
upstream by forging the cookie — it is not a security boundary, since any client may open a circuit
on any instance anyway.

⚠️ `health_uri` is **not** configured, and that is not an oversight. EntKube exposes no health
endpoint — there is no `MapHealthChecks` anywhere in `src/` — so there is nothing to probe, and
aiming an active check at `/` would test an authentication redirect rather than the application.
`fail_duration` plus `lb_retries` is passive health checking, which reacts to a *failed dial* and
not to an instance that is up but broken.

### Why ingest is pinned to the worker

The example routes `/ingest/*` to `entkube-worker` alone, ahead of the balanced `handle` block.
This is not load shaping — it is the only arrangement that terminates.

Ingest appends to a **process-local** Lucene index. `SegmentSealService` — the only thing that ever
uploads that index to object storage and the only thing that trims the local warm tier — is
registered through `AddSingletonWork`, so it runs on the worker and nowhere else. Let ingest land
on a web instance and it writes an active index that is never sealed: invisible to every other
instance, never uploaded to the bucket, never trimmed, growing until the volume does. Pinning
ingest puts the writer and the sealer in the same process.

The ingest endpoints authenticate with a stateless HMAC capability token (`IngestTokenService`), so
pinning them costs no session state.

⚠️ This makes telemetry **consistently delayed** rather than randomly inconsistent. It does not
make it correct. See below.

---

## Running it

```bash
mkdir entkube-ha && cd entkube-ha
cp .../docs/examples/docker-compose.two-instances.yml docker-compose.yml
cp .../docs/examples/Caddyfile.two-instances            Caddyfile
cp .../docs/examples/env.two-instances.example          .env
$EDITOR .env          # DOMAIN, ACME_EMAIL, POSTGRES_PASSWORD, VAULT__ROOTKEY, MINIO_ROOT_PASSWORD
```

Generate the vault root key **once for the installation**, not once per container:

```bash
openssl rand -base64 32
```

Then bring it up in two steps, worker first:

```bash
docker compose up -d postgres redis minio minio-init entkube-worker
docker compose logs -f entkube-worker      # wait for "Application started"
docker compose up -d
```

⚠️ **The two steps are the migration guard, and they are a convention, not a mechanism.** Every
instance runs EF migrations at boot and `MigrationReconciler` can re-stamp; the race between them
is unguarded (§9.3). Starting the worker alone lets it finish the schema before anything else
opens the database. Nothing enforces this — if you `docker compose up -d` in one shot it will
usually work and will occasionally not.

⚠️ **`entkube-install` does not know about this deployment.** It renders the single-instance
`docker-compose.yml` and `Caddyfile` and replaces both *wholesale* on every run
(`EntKube.Installer.Core/Renderer.cs:75`). Run the two-instance stack from its own directory. The
generated Caddyfile's header suggests importing hand-written config, but Caddy's `import` cannot
add upstreams to a `reverse_proxy` line that is already written, so a two-instance deployment has
to own its Caddyfile outright.

---

## Verifying it worked

Each check below pairs a positive with a negative. A check that only looks for an absence passes
just as well when the thing you are testing was never running.

### 1. Both instances are in the pool

```bash
for i in $(seq 10); do
  curl -sI "https://$DOMAIN/" | sed -n 's/^[Ss]et-[Cc]ookie: entkube_lb=\([^;]*\).*/\1/p'
done | sort -u | wc -l
```

**Expect `2`.** The cookie value is an HMAC of the upstream's dial address, so one distinct value
per instance actually being chosen. `1` means only one upstream is in the pool or only one is
healthy — and every stickiness check below would then pass vacuously.

### 2. The cookie pins

```bash
curl -s -c jar -o /dev/null "https://$DOMAIN/"            # first request: Caddy chooses, and sets it
for i in $(seq 10); do
  curl -s -b jar -o /dev/null -D - "https://$DOMAIN/" | grep -ci '^set-cookie: entkube_lb'
done
```

**Expect `0` ten times.** Caddy only sends `Set-Cookie` when it had to choose an upstream, so
silence means every one of those requests was routed by the cookie you already held.

⚠️ **This check is worthless unless check 1 printed `2`, and that is not a style point.** With
`lb_policy cookie` deleted entirely, this loop still prints `0` ten times — there is no cookie to
resend, so the absence it looks for is satisfied by the mechanism being *missing*. It was measured
both ways: with the policy, `2` distinct values and `0` resends; without it, `0` distinct values
and the same `0` resends. Only check 1's count distinguishes them.

### 3. ⭐ The circuit starts on the instance that minted it

This is the one that matters; 1 and 2 are necessary and not sufficient. Per-request logging is on
in the example for exactly this purpose.

```bash
docker compose logs -f entkube-web-1 entkube-web-2 | grep -E '_blazor'
```

Now load the UI in a browser. You will see two lines:

```
entkube-web-1  | ... Request starting HTTP/1.1 POST  https://.../_blazor/negotiate ...
entkube-web-1  | ... Request starting HTTP/1.1 GET   https://.../_blazor?id=...
```

**Both must carry the same container prefix.** Different prefixes is the defect, and it is the only
reliable way to see it — the browser shows a page that looks fine.

**The symptom when it is wrong:** the page renders and nothing on it works. Clicks do nothing,
navigation does nothing, no error appears. In the browser console you will see the `/_blazor`
connection fail or close immediately; in the Network tab, a `negotiate` that succeeded followed by
a WebSocket that did not.

### 4. The worker is the only instance sweeping

```bash
docker compose logs entkube-web-1 | grep -c 'Workers:RunSingletonWork=false'   # expect: many
docker compose logs entkube-worker | grep -c 'Workers:RunSingletonWork=false'  # expect: 0
```

`SingletonWorkHost` logs `"<Service> is not started on this instance (Workers:RunSingletonWork=false)"`
once per gated service at Information, so an operator who set the flag can confirm from the log
that it took, and an operator who did not can see which instance is the one sweeping. A `0` from
the *first* command is the failure: that web instance is a second worker.

### 5. The key ring is genuinely shared

```bash
docker compose exec entkube-web-1 ls /app/keys     # key-<guid>.xml
docker compose exec entkube-web-2 ls /app/keys     # the same filename
```

Then the behavioural test, which is the one that proves it:

1. Log in. Note from the logs which instance holds your circuit.
2. `docker compose stop entkube-web-1` (or whichever it is).
3. Reload the page.

**You should land on the other instance still logged in**, with a fresh circuit. Being bounced to
the login page is the §9.2 failure: your cookie was protected with a key the surviving instance
does not have.

### 6. The vault is shared

Open a tenant's secrets on `entkube-web-1`, reveal a value, then stop that instance and reveal the
same value from `entkube-web-2`. Both decrypt, or `Vault__RootKey` differs between them. (A
mismatched root key does not stop the container starting, so there is no startup error to look
for.)

---

## What this does not make safe

⚠️ **Do not read this document as "HA is done."** It removes the two failures that break the UI
outright and gives the sweeps somewhere single to live. It is not two interchangeable instances.

**Telemetry is wrong at two instances, and visibly so.** The active tier is a local Lucene index
(`SegmentManagerBase.cs:245`). Pinning ingest to the worker fixes the worst version of this — data
splitting randomly across three processes, so log lines appear and vanish as you refresh — but a
web instance still queries *its own* active index, which is empty, plus the sealed tier. So the
most recent telemetry is simply **absent from the UI** until it is sealed: up to `RollMaxDocs`
(1,000,000) or `RollMaxAge` (1 hour), per signal, per tenant. A user watching logs during an
incident is watching an hour-old picture. This is not fixable by configuration. It is §9.5 step 5,
`entkube-telemetry` as its own process, and it is still to come.

⚠️ A related consequence of the same gap: `TrimWarmTierAsync` is also singleton work, so a web
instance's sealed-segment **cache** is downloaded and never evicted. Watch `/app/Data` on the web
instances; nothing currently reclaims it.

**The four damaging services are protected by placement, not by a claim.** They run on the worker
because nothing else starts them — not because they would survive running twice. Claims following
`CostLedgerWriter`'s compare-and-set (`Cost/CostLedgerWriter.cs:112-124`, the only claim in the
codebase) are in flight for `BootstrapRunnerService`, `RolloutWatcherService` and
`TelemetryAlertEvaluator`. Until they land: one worker, and only one. Two workers installs the same
Helm releases twice on a real customer cluster.

**The worker is a single point of failure, and in this configuration it is two of them.** Stop it
and nothing sweeps, nothing bootstraps, nothing watches rollouts, no alert is evaluated — and, with
ingest pinned to it, no telemetry is received at all. There is no failover. What two instances buys
is availability of the *web tier*.

**Startup migrations are still racy.** The two-step start is a convention. Nothing in the code
prevents three containers migrating at once.

**There is no readiness signal.** Without a health endpoint the load balancer cannot distinguish an
instance that is starting from one that is ready. A container that is up but still migrating
accepts connections and fails requests, and passive health checking will not notice because the
dial succeeded.

**Restarting an instance drops its users' circuits.** They get the reconnect overlay and lose
unsaved form state. Sticky sessions pin a user to an instance; they do not migrate anyone off a
dying one.

**This configuration is reasoned from the code and from the §9 audit, not from a measured
production run.** The failure modes above were found by reading; the ones that have not been found
yet were not ruled out.
