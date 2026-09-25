# CMKS — Architecture Overview for Developers

> **Audience:** developers who want to publish an application into a CMKS Kubernetes cluster.
> **Scope:** what the platform is, who owns which part, and what you have to do — and ask for —
> to get your application running.

If you read nothing else, read [Three rules](#three-rules) and
[Reaching Capio internal systems](#reaching-capio-internal-systems-the-vpn).

---

## Three rules

**1. You will never get a kubeconfig.**
There is no `kubectl` access for developers — not in dev, not in test, not in prod.
This is deliberate and it is not negotiable per-app. Everything you would normally do
with `kubectl` you do through ArgoCD and Grafana instead — reached over the Capio
VPN via the Jumphost. See [Working without kubectl](#working-without-kubectl), and sort
your access out early.

**2. Git is the only way in.**
You do not deploy. You commit. ArgoCD watches your **deployment repo** and reconciles the
cluster to match it. Anything applied by hand is reverted automatically.

**3. Anything that touches the network outside your namespace is somebody else's change.**
Ingress hostnames, TLS certificates, egress rules and — above all — connectivity to
Capio internal systems are platform and networking changes with their own lead times.
Plan for them at design time, not the day before go-live.

---

## The platform in one picture

```
                         ┌───────────────────────────────────────────┐
     Internet            │           Cleura (OpenStack)              │
        │                │                                           │
        ▼                │   Gardener-managed Kubernetes clusters    │
  ┌───────────┐          │                                           │
  │ Azure DNS │          │   ┌─────────┐ ┌─────────┐ ┌─────────┐     │
  │ capio.eu  │────────► │   │  dev02  │ │ test02  │ │ prod01  │     │
  └───────────┘          │   └─────────┘ └─────────┘ └─────────┘     │
        │                │        │           │           │         │
        │                │   Istio service mesh + Gateway API        │
        │                │   ArgoCD · Prometheus / Grafana           │
        │                │   CNPG Postgres · RabbitMQ · Redis        │
        ▼                │   External Secrets · Kyverno · Airflow    │
  External / Internal    │                                           │
  Gateway (Envoy)        └─────────────────┬─────────────────────────┘
                                           │
                    ┌──────────────────────┼──────────────────────┐
                    ▼                      ▼                      ▼
            ┌───────────────┐   ┌────────────────────┐   ┌────────────────┐
            │ Azure         │   │  Cleura ↔ Capio    │   │ Azure DevOps   │
            │ • Key Vault   │   │  VPN service       │   │ • Git repos    │
            │ • Entra ID    │   │  (see below)       │   │ • Pipelines    │
            │ • DNS         │   └─────────┬──────────┘   │ • ACR registry │
            └───────────────┘             ▼              └────────────────┘
                                 Capio internal systems
                                 (SQL Server, file shares,
                                  journal systems, …)
```

The whole platform is declared as Terraform in the **CMKS - Infra** repository
(`dev.azure.com/capiomks/CMKS - Infra`). Nothing is clicked into existence.

### Clusters and environments

| Cluster  | Environment | Internal hostname pattern      | External hostname pattern |
|----------|-------------|--------------------------------|---------------------------|
| `dev02`  | dev         | `{service}.dev02.capio.eu`     | `{service}.capio.eu`      |
| `test02` | test        | `{service}.test02.capio.eu`    | `{service}.test.capio.eu` |
| `prod01` | prod        | `{service}.prod01.capio.eu`    | `{service}.prod.capio.eu` |

Each environment designates one cluster as its **homeCluster**, which runs the shared
platform services (ArgoCD, PostgreSQL, RabbitMQ). Your application is
scheduled onto whichever clusters its config lists — usually one per environment.

---

## Who owns what

This is the single most useful thing to internalise. **Three repositories, three jobs.**
Your source code and your deployment manifests do **not** live in the same place.

```
┌─────────────────────────┐  ┌──────────────────────────┐  ┌─────────────────────────┐
│ 1. YOUR SOURCE REPO     │  │ 2. CMKS DEPLOYMENT REPO  │  │ 3. CMKS - Infra         │
│    you own              │  │    you own               │  │    platform team owns   │
│ ─────────────────────   │  │ ──────────────────────   │  │ ─────────────────────   │
│ src/                    │  │ deployment/              │  │ Terraform describing    │
│ tests/                  │  │  ├── deployment.yaml     │  │ every tenant and app.   │
│ Dockerfile              │  │  ├── service.yaml        │  │                         │
│ azure-pipelines.yml     │  │  ├── httproute.yaml      │  │ Defines your:           │
│                         │  │  ├── externalsecret.yaml │  │  • namespace + quota    │
│ Builds and tests.       │  │  └── configmap.yaml      │  │  • ArgoCD project & app │
│ Pushes the image        │  │                          │  │  • gateway listener+TLS │
│ to your app's ACR.      │  │ CMKS-<Tenant>-<App>/     │  │  • Key Vault wiring     │
│                         │  │  ├── DEV   ← one repo    │  │  • databases & queues   │
│ ArgoCD never looks      │  │  ├── TEST     per env    │  │  • network policies     │
│ at this repo.           │  │  └── PROD                │  │                         │
│                         │  │                          │  │ You never edit this —   │
│ Anywhere you like.      │  │ This is what ArgoCD      │  │ you raise a request and │
│ Any layout you like.    │  │ syncs.                   │  │ the platform team does. │
└─────────────────────────┘  └──────────────────────────┘  └─────────────────────────┘
         │                              ▲                            │
         │  image: myapp:1.4.2          │                            │
         └──────────────────────────────┘                            ▼
              you bump the tag in the                     the namespace, routing
              deployment repo to release                  and services you deploy into
```

### Why the split matters

- **Building and deploying are separate acts.** A green build changes nothing in any
  cluster. A release happens when you change the image tag in a deployment repo.
- **One CMKS project per app, one repo per environment.** Your app gets a project
  `CMKS-<Tenant>-<App>` in the `capiomks` Azure DevOps organisation, containing a `DEV`,
  `TEST` and `PROD` repo. Promoting dev → test → prod means applying the same manifest
  change to the next environment's repo, not merging a branch inside one repo.
  Environments cannot drift by accident, and a prod release is always a deliberate,
  reviewable commit.
- **Your source repo is yours.** Language, layout, branching model, monorepo or not —
  the platform has no opinion and never reads it.
- **Only repos on your tenant's allowlist can be used as an ArgoCD source.** The
  allowlist is configured by the platform team; anything else is rejected at sync.

Rule of thumb: **if it is cluster-scoped, shared, or costs money, the platform team owns
it.** If it lives inside your namespace and only affects your app, you own it.

---

## How a deployment actually happens

Two independent pipelines. The first produces an image; the second releases it.

```
  ── BUILD ────────────────────────────────────────────────────────────
  you push to your SOURCE repo
        │
        ▼
  ┌──────────────┐   builds & tests   ┌─────────────────┐
  │ Azure        │ ─────────────────► │  Your ACR       │
  │ Pipelines    │    pushes image    │  myapp:1.4.2    │
  └──────────────┘                    └─────────────────┘
        │
        │  nothing has been deployed yet — no cluster was touched
        ▼

  ── RELEASE ──────────────────────────────────────────────────────────
  you commit `image: myapp:1.4.2` to the DEPLOYMENT repo for that environment
        │
        ▼
  ┌──────────────────────────┐  polls/webhook  ┌────────┐  applies  ┌─────────────────┐
  │ CMKS-<Tenant>-<App>/DEV  │ ──────────────► │ ArgoCD │ ────────► │ your namespace   │
  │   deployment/            │                 │        │           │ ns-<tenant>-...  │
  └──────────────────────────┘                 └────────┘           └─────────────────┘
                                                    │
                                                    │  syncPolicy:
                                                    │    automated:
                                                    │      prune:    true
                                                    │      selfHeal: true
                                                    ▼
                                      Anything in the cluster that does not
                                      match Git is deleted or reverted.
```

Consequences worth spelling out:

- **A green build is not a deployment.** Your pipeline pushes to ACR and stops. Nothing
  reaches a cluster until a deployment repo changes.
- **`prune: true`** — delete a manifest from the deployment repo and the object is
  deleted from the cluster. There is no orphan state.
- **`selfHeal: true`** — if anyone changes a live object out-of-band, ArgoCD reverts it
  within minutes. Hotfixing in the cluster does not work, by design.
- **Promotion is a commit to the next environment's repo**, not a `kubectl set image`
  and not a branch merge. Many teams automate this: the build pipeline opens a PR
  against `CMKS-TEST-…` with the new tag.
- **Avoid mutable tags.** `:latest` defeats the whole model — ArgoCD sees no change in
  Git, so nothing syncs, and `selfHeal` will not pull a new image for you. Use an
  immutable tag or a digest.

### Your ArgoCD Application

The platform bootstraps one ArgoCD `Application` per app per environment, named
`<tenant>-<app>`, pointed at:

| Field            | Source                                                     |
|------------------|------------------------------------------------------------|
| `repoURL`        | Your deployment repo for that environment                   |
| `targetRevision` | The branch it tracks — `main` unless you asked otherwise    |
| `path`           | The directory it syncs — `deployment` by convention         |
| `destination`    | your namespace, on the clusters you were assigned           |

After bootstrap, Terraform never touches the Application again
(`lifecycle { ignore_changes = all }`), so tenant admins can adjust it in the ArgoCD UI
within their project's RBAC boundary — including switching it to a different
[deployment style](#deployment-styles).

---

## Getting onboarded

You need a **tenant** (an organisational unit, e.g. `capioonline`, `capioinkop`) and an
**app** under it. You do not configure any of it yourself — you order it.

### Order it with the onboarding form

Both a new tenant and a new app are ordered through the same form:

**[CMKS onboarding form →](https://forms.cloud.microsoft/Pages/ResponsePage.aspx?id=tHliZmUfr0m27Osk2g2U5w4KPBsvz95End5tnuywcK9UOFdQWVRKWDZQQU9QREpWRlRFQVdUSlVDWS4u)**

Have the answers below ready before you open it. The form is the only route in — there
is no side channel, and a request that arrives some other way will be pointed back here.

### What the form asks for

| What they need | Example |
|----------------|---------|
| App name and tenant | `bookingapi`, tenant `mytenant` |
| Cost centre (KST) and legal entity | `1234`, `capio-sverige-ab` |
| Environments the app will run in | dev, test, prod — or a subset to start |
| Hostname(s), and whether each is internal or external | `bookingapi.prod01.capio.eu`, internal |
| Deployment style | plain manifests, Helm or Kustomize — see [Deployment styles](#deployment-styles) |
| Entra ID groups for admins and developers | object IDs — these become your ArgoCD access |
| Databases, queues or Redis needed | see [Platform services](#platform-services-you-can-use) |
| **Every outbound destination** | host, IP, port, protocol — see [08](#outbound-traffic) and [09](#reaching-capio-internal-systems-the-vpn) |
| Resource footprint | CPU, memory and pod count — this becomes your quota |

### What you get back

The platform team provisions all of this and tells you the names you need:

| You receive | Shape |
|-------------|-------|
| A namespace | `ns-<tenant>-<app>`, with quota, limits and network policies applied |
| An **Azure Container Registry** for the app | Created together with the config — this is where your build pushes |
| A **CMKS project** in Azure DevOps | `CMKS-<Tenant>-<App>`, containing one deployment repo per environment: `DEV`, `TEST`, `PROD` |
| An ArgoCD Application per environment | Named `<tenant>-<app>`, already pointed at the right repo and namespace |
| A Gateway listener and TLS certificate | One per hostname — issued and renewed automatically |
| An Azure Key Vault and a `SecretStore` | Per environment, wired up and ready for you to pull from |
| Credentials for any platform service you asked for | As Secrets in your namespace |

```
Azure DevOps organisation: capiomks
└── CMKS-<Tenant>-<App>/            ← one project per app
    ├── DEV                          ← one repo per environment
    ├── TEST
    └── PROD
```

Your source repository is separate and stays wherever your team already keeps it.

> **Your image goes to the ACR created for your app.** That registry is provisioned with
> your app config, and your build pipeline pushes there. Registry restrictions are
> enforced by policy, so build against the ACR you were given from the start rather than
> discovering the constraint later.

### Changing things later

Quotas, hostnames, egress rules, databases and queues are all platform-team changes —
raise a request the same way. What you can change freely, without asking anyone, is
anything inside your deployment repo.

---

## What you write in the deployment repo

Everything below goes in `deployment/` (or whatever `path` was configured) and is synced
by ArgoCD.

### A minimal set

```
deployment/
├── deployment.yaml       # your workload
├── service.yaml          # ClusterIP only — see guardrails
├── httproute.yaml        # only if you have a hostname
└── externalsecret.yaml   # only if you need secrets
```

### Deployment — the parts the platform will reject you for getting wrong

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: bookingapi
  namespace: ns-mytenant-bookingapi
spec:
  template:
    metadata:
      labels:
        app.kubernetes.io/name: bookingapi   # NetworkPolicies may select on this
    spec:
      automountServiceAccountToken: false          # enforced by Kyverno
      securityContext:
        seccompProfile:
          type: RuntimeDefault                     # ENFORCED — pod is rejected without it
      containers:
        - name: api
          image: cmksprodbookingapi-….azurecr.io/api:1.4.2
          securityContext:
            readOnlyRootFilesystem: true           # ENFORCED — pod is rejected without it
            allowPrivilegeEscalation: false
          resources:
            requests: { cpu: "100m", memory: "128Mi" }
            limits:   { cpu: "500m", memory: "512Mi" }
          volumeMounts:
            - name: tmp
              mountPath: /tmp                      # you need writable scratch? mount an emptyDir
      volumes:
        - name: tmp
          emptyDir: {}
```

`readOnlyRootFilesystem: true` is the single most common cause of a first deployment
failing. If your runtime writes to disk (temp files, caches, `.NET` data protection keys,
nginx pid files), mount an `emptyDir` at each of those paths.

---

## Deployment styles

ArgoCD detects what a `path` contains and renders it accordingly. You choose the style
per app, and you can change it later by editing the Application in the ArgoCD UI — the
platform does not force one.

| Style | ArgoCD detects it by | Use it when |
|-------|----------------------|-------------|
| **Plain manifests** | neither of the below | The default. One app, few resources, values differ little between environments. |
| **Helm** | `Chart.yaml` in the path | You want one templated chart with per-environment values, or you are consuming a third-party chart. |
| **Kustomize** | `kustomization.yaml` in the path | You want a shared base with small per-environment patches, without Helm templating. |
| **App-of-apps** | an Application that renders other `Application` resources | One app is really several deployable pieces you want to see and sync separately. |
| **ApplicationSet** | an `ApplicationSet` resource | You are generating many similar Applications from a list or from files in Git. |

---

### Plain manifests (directory)

What every app on the platform uses today. ArgoCD applies every YAML in the path,
recursively.

```
deployment/
├── deployment.yaml
├── service.yaml
├── httproute.yaml
└── externalsecret.yaml
```

Because there is a **separate deployment repo per environment**, you do not need
templating just to vary a hostname or an image tag — the files already differ per
environment. Start here. Move to Helm or Kustomize only when duplication actually hurts.

---

### Helm

Put a chart in the deployment repo and ArgoCD renders it with `helm template`:

```
deployment/
├── Chart.yaml
├── values.yaml
└── templates/
    ├── deployment.yaml
    └── service.yaml
```

ArgoCD uses `values.yaml` by default. To use a different file, or to set values inline,
edit the Application's `source.helm` block in the ArgoCD UI:

```yaml
source:
  repoURL: git@ssh.dev.azure.com:v3/capiomks/CMKS-MyTenant-MyApp/PROD
  path: deployment
  targetRevision: main
  helm:
    valueFiles:
      - values.yaml
      - values-prod.yaml
    parameters:
      - name: image.tag
        value: "1.4.2"
```

> **Prefer a committed values file over inline `parameters`.** A value set in the
> Application is invisible to anyone reading the repo, and it is the kind of state that
> gets lost when an Application is recreated.

**Community charts usually need trimming.** Most bundle things tenants may not create:

| Chart ships | Result |
|-------------|--------|
| CRDs | Rejected — all cluster-scoped resources are blocked |
| `ClusterRole` / `ClusterRoleBinding` | Rejected — same reason |
| `Service` of type `LoadBalancer` | Rejected by Kyverno |
| `Ingress` | Useless here — the platform uses Gateway API; write an HTTPRoute |
| No `seccompProfile` / writable rootfs | Pod rejected at admission |

Charts with a `crds/` directory or an `rbac.create: true` default are the usual
offenders. Check `helm template` output against the [guardrails](#guardrails--what-will-be-rejected)
before adopting a chart.

> Rendering runs with a **300-second repo-server timeout**. A chart with very large
> dependencies can exceed it.

---

### Kustomize

A `kustomization.yaml` in the path switches ArgoCD to `kustomize build`:

```yaml
# deployment/kustomization.yaml
resources:
  - ../../base          # only if the base is in the SAME repo
images:
  - name: myapp
    newTag: "1.4.2"
```

> **One repo per environment limits the usual overlay pattern.** The classic
> `base/` + `overlays/{dev,test,prod}` layout assumes all environments live in one repo;
> here they do not. Either keep a `base/` inside each deployment repo, or use Kustomize
> purely for its transformers — `images:`, `configMapGenerator`, `patches` — against
> local resources. ArgoCD cannot reach a base in a different repo without a multi-source
> Application.

`images:` is a clean way to do releases: the tag lives in one place and the bump is a
one-line, reviewable diff.

---

### App-of-apps

One ArgoCD Application whose job is to create other Applications. Useful when what the
platform onboarded as a single "app" is really several deployable components —
an API, a worker and a migration job — that you want to sync, health-check and roll back
independently.

```
deployment/
└── apps/
    ├── api.yaml          # kind: Application
    ├── worker.yaml       # kind: Application
    └── scheduler.yaml    # kind: Application
```

```yaml
# deployment/apps/api.yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: mytenant-myapp-api
  namespace: ns-mytenant-myapp          # your namespace, not argocd
spec:
  project: tenant-mytenant              # must be your tenant's project
  source:
    repoURL: git@ssh.dev.azure.com:v3/capiomks/CMKS-MyTenant-MyApp/PROD
    targetRevision: main
    path: components/api
  destination:
    server: https://kubernetes.default.svc
    namespace: ns-mytenant-myapp
  syncPolicy:
    automated: { prune: true, selfHeal: true }
```

This works because the platform enables it explicitly: ArgoCD runs with
`application.namespaces: "*"`, and every tenant namespace is labelled
`argocd.argoproj.io/managed-by: argocd`, so Application resources are picked up from
your own namespace.

**Constraints:**
- `project:` must be your tenant's AppProject (`tenant-<tenant>`). Another tenant's
  project is rejected.
- `repoURL` must be on your tenant's `repos:` allowlist.
- Child Applications inherit every AppProject restriction — an app-of-apps grants no
  extra privilege.
- Deleting the parent prunes the children. Be deliberate about it in prod.

---

### ApplicationSets

An `ApplicationSet` generates Applications from a template. Worth it when you have many
near-identical deployments — per-country, per-region or per-customer instances of the
same service.

```yaml
apiVersion: argoproj.io/v1alpha1
kind: ApplicationSet
metadata:
  name: mytenant-regions
  namespace: ns-mytenant-myapp
spec:
  generators:
    - list:
        elements:
          - region: north
          - region: south
  template:
    metadata:
      name: 'mytenant-myapp-{{region}}'
    spec:
      project: tenant-mytenant
      source:
        repoURL: git@ssh.dev.azure.com:v3/capiomks/CMKS-MyTenant-MyApp/PROD
        targetRevision: main
        path: 'regions/{{region}}'
      destination:
        server: https://kubernetes.default.svc
        namespace: ns-mytenant-myapp
      syncPolicy:
        automated: { prune: true, selfHeal: true }
```

> **SCM providers are disabled.** The ApplicationSet controller runs with
> `--enable-scm-providers=false`, so the **SCM Provider** and **Pull Request**
> generators do not work — you cannot generate an Application per Azure DevOps repo or
> per open PR. The **List**, **Git** (files and directories), **Cluster**, **Matrix**
> and **Merge** generators all work.

---

### Which should you pick?

- **One service, three environments** → plain manifests. Do not over-engineer.
- **Many near-identical values per environment** → Helm with a committed values file.
- **You only vary the image tag and a few fields** → Kustomize with `images:`.
- **Several components under one onboarded app** → app-of-apps.
- **Dozens of generated instances** → ApplicationSet with a List or Git generator.

Whatever you choose, the [guardrails](#guardrails--what-will-be-rejected) apply
identically. A Helm chart is not a way around Kyverno.

---

## Networking

### Inbound traffic

There are exactly two ways in, and you do not choose freely — `facing` in your config
decides:

| Gateway    | Namespace          | Load balancer        | Reachable from                       |
|------------|--------------------|----------------------|--------------------------------------|
| `internal` | `internal-ingress` | OpenStack internal LB| Capio network / VPN — staff & services |
| `external` | `external-ingress` | Public LB            | The open internet — patient-facing   |

The platform creates the **Gateway listener** and the **TLS certificate** for each
hostname you were granted. You create the **HTTPRoute** that binds your Service to that
listener:

```yaml
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: bookingapi
  namespace: ns-mytenant-bookingapi
spec:
  parentRefs:
    - name: internal                 # or "external"
      namespace: internal-ingress    # or "external-ingress"
      sectionName: mytenant-bookingapi   # listener name — ask if unsure
  hostnames:
    - bookingapi.prod01.capio.eu
  rules:
    - backendRefs:
        - name: bookingapi
          port: 8080
```

If your app has several hostnames, you get one listener per hostname and write one
HTTPRoute per hostname. The first uses the namespace-derived `sectionName`; subsequent
ones are suffixed `-1`, `-2`, …

You **cannot** create `Gateway` or `Certificate` resources — ArgoCD's AppProject blocks
them. TLS is issued automatically (Let's Encrypt via Azure DNS-01 for public names, the
internal CA for in-cluster names) and renewed for you.

### Outbound traffic

Every app namespace gets a default egress NetworkPolicy. It draws the line in one place
only: **inside the cluster versus outside it.**

| Where you are going | What is allowed |
|---------------------|-----------------|
| **Anything inside the cluster** — any pod, in any namespace | All ports. No restriction. |
| **Anything outside the cluster**, addressed by IP | Only 80, 443, 53, 8053, 5432, 5433, 5671, 5672 |

This is worth reading twice, because it is the opposite of what most people assume.

**In-cluster traffic is not port-restricted.** The platform's PostgreSQL, RabbitMQ and
Redis are reachable on whatever port they listen on — not because those ports are
individually allowlisted, but because egress to every namespace in the cluster is open.
The same applies to another team's service in another namespace. If you can resolve it
in-cluster, egress policy will not stop you reaching it.

**Leaving the cluster is where the restriction bites.** Traffic to an external IP is
limited to that port list. The `5432` and `5671` entries there are for reaching a
PostgreSQL or RabbitMQ that lives *outside* the cluster — they are not what makes the
platform's own instances work.

> **Anything else leaving the cluster is blocked, and it fails as a connection timeout,
> not a clean refusal.** SQL Server on 1433, LDAP on 389/636, SMB on 445, Oracle on 1521,
> a bespoke TCP protocol — each needs an egress rule added for your namespace by the
> platform team.

To request one, give them:

| | |
|---|---|
| **Destination** | hostname *and* IP or CIDR — rules are written against addresses |
| **Port and protocol** | e.g. TCP 1433 |
| **Environments** | each one is a separate rule |
| **What it is for** | one line; it ends up in the change record |

Know your destination hosts and ports early. This is an infrastructure change with a
review and an apply behind it, not a self-service toggle — and if the destination is a
**Capio internal system**, it needs considerably more than this rule. See the next
section.

---

## Reaching Capio internal systems (the VPN)

**This is the part that will surprise you, and the part with the longest lead time.**

CMKS clusters run at **Cleura**. Capio's internal systems do not. The link between them
is a **VPN service delivered by Cleura** — it is not part of the CMKS infrastructure
code, and neither you nor the CMKS platform team can change it alone.

> **You raise everything with Capio Support.** You will never contact Cleura, and you do
> not need to work out which party owns which piece. Capio Support is your single entry
> point; they coordinate the rest. The diagram below is there so you understand *why* it
> takes as long as it does — not so you can chase people.

```
  ┌────────────────────┐     ┌─────────────────────┐     ┌────────────────────────┐
  │ Your pod           │     │  Cleura             │     │  Capio                 │
  │ ns-<tenant>-<app>  │     │                     │     │                        │
  │                    │     │  ┌───────────────┐  │     │  ┌──────────────────┐  │
  │  egress            │────►│  │  VPN service  │◄─┼─────┼─►│ internal systems │  │
  │  NetworkPolicy     │     │  │   (tunnel)    │  │     │  │ SQL, shares, …   │  │
  └────────────────────┘     │  └───────────────┘  │     │  └──────────────────┘  │
                             └─────────────────────┘     └────────────────────────┘
           ▲                           ▲                            ▲
           │                           │                            │
   CMKS platform team          Cleura Networking            Capio Networking /
   (infrastructure code)       (configures Cleura side)     Capio Support
                                                            (configures Capio side)

   ── all three must be done, and they are coordinated behind the scenes ──

              You talk to ONE of them:  ►  Capio Support  ◄
```

### What this means in practice

- **Capio Support is your only contact.** Whatever the piece, whoever ends up doing the
  work, the request goes to Capio Support and they coordinate with the Capio Networking
  team and — where Cleura's side is involved — with Cleura. Developers do not raise
  anything with Cleura directly.
- **Three changes have to line up** for traffic to flow: the Cleura-side VPN and routing
  config, the Capio-side firewall and routing config, and the egress NetworkPolicy for
  your namespace. Any one missing produces the same symptom — a timeout.
- **Two organisations, two change processes.** That is the real reason for the lead time,
  and it is why nobody can simply toggle this for you.
- **Each environment is its own request.** A route that works from `dev02` says nothing
  about `prod01`.
- **Lead time is measured in weeks, not hours.** Raise it with Capio Support when you
  start designing the integration, not when you are ready to test it.

### What to bring to Capio Support

| Item | Why |
|------|-----|
| Source: cluster + namespace (`ns-<tenant>-<app>`) | identifies your egress side |
| Destination: FQDN **and** IP/CIDR | firewall rules are written on IPs |
| Port and protocol | e.g. TCP 1433 |
| Direction | almost always outbound from CMKS; inbound needs a much stronger case |
| Environment(s) | dev / test / prod are separate approvals |
| Data classification & purpose | patient data crossing a boundary attracts review |

> **Design advice:** every VPN dependency you add couples your release schedule to a
> multi-party change process you have no visibility into. Where the choice exists, prefer
> an HTTPS API on 443 over a direct database or file-share connection — 443 egress
> already works out of the box, and an API is far easier to get approved than SQL access
> across the boundary.

---

## Secrets

Secrets never live in Git and never live in your manifests. They live in **Azure Key
Vault**, one vault per app per environment, and are pulled into your namespace by the
**External Secrets Operator**.

```
  Azure Key Vault                External Secrets            Your namespace
  kv-prod-sc-myapp    ──────►    Operator            ──────► Secret: myapp-secrets
                                 (SecretStore                        │
                                  azurekv-<tenant>-<app>)            ▼
                                                              envFrom / volume
```

The platform creates the `SecretStore` in your namespace and the bootstrap credentials
that let it authenticate. **You** write the `ExternalSecret` that says which keys you
want:

```yaml
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: myapp-secrets
  namespace: ns-mytenant-bookingapi
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: azurekv-mytenant-bookingapi   # created by the platform
    kind: SecretStore
  target:
    name: myapp-secrets
  data:
    - secretKey: ApiKey
      remoteRef:
        key: payment-provider-api-key         # the secret name in Key Vault
```

To add a secret: put it in Key Vault (or ask whoever owns the vault), then reference it
from your `ExternalSecret`. No platform change needed.

---

## Platform services you can use

These run once per environment and are shared. You ask for them during onboarding or
later; the platform provisions them and the credentials arrive as a **Kubernetes Secret
in your namespace**, ready to mount or inject as environment variables.

| Service | What you get | What to ask for |
|---------|--------------|-----------------|
| **PostgreSQL** (CloudNativePG) | One or more databases in the shared cluster, each with its own owner role, backed up automatically | The database names you need, per environment |
| **RabbitMQ** | Queues, exchanges and bindings, reached over **AMQP** — there is no management UI access | Queue and exchange names, fanout or not, whether external consumers need access, message TTLs |
| **Redis** | A managed Redis instance | That you need Redis, and roughly how much you will store |
| **Airflow** | DAGs synced from a Git repo | Relevant for data workloads — talk to the platform team about fit |

Your database Secret contains `host`, `username`, `password` and two connection URIs:
one read-write and one pointing at the **read-only replica**. Use the read-only one for
reporting and read-heavy paths.

> **Availability differs per environment.** Not every platform service is enabled
> everywhere, and the list above is what you can normally expect. Confirm with the
> platform team before you design around a service — especially if you need it in
> production.

Always reach these over **internal cluster DNS**, never a public hostname. Traffic that
leaves and re-enters the cluster is slower, crosses the gateway unnecessarily, and may be
blocked by your egress policy.

---

## Guardrails — what will be rejected

Enforced by **ArgoCD AppProject RBAC** (blocked at sync) and **Kyverno** (blocked at
admission). These are not suggestions; a violating manifest simply will not become a
running pod.

| You cannot | Enforced by | Why |
|------------|-------------|-----|
| Create any cluster-scoped resource (`ClusterRole`, `ClusterRoleBinding`, CRDs, …) | AppProject | tenant isolation |
| Create `Namespace` | AppProject | namespaces are provisioned with quota and policy |
| Create or edit `ResourceQuota` / `LimitRange` | AppProject | capacity is allocated, not self-served |
| Create `Gateway` (Gateway API or Istio) | AppProject | ingress and TLS are platform-owned |
| Create `Certificate` | AppProject | issued automatically via cert-manager |
| Create `LoadBalancer` or `NodePort` Services | Kyverno | all ingress goes through the shared gateways |
| Run without `seccompProfile: RuntimeDefault` | Kyverno (**Enforce**) | baseline hardening |
| Run with a writable root filesystem | Kyverno (**Enforce**) | baseline hardening |
| Auto-mount the service account token | Kyverno | least privilege |
| Use a Git repo that is not on your tenant's allowlist | AppProject `sourceRepos` | tenant isolation |

Also in force:
- **Pod Security Admission** at `baseline` (enforce + audit + warn) on every tenant namespace.
- **Images must come from the ACR provisioned for your app.** That registry is created
  alongside your app config; your build pipeline pushes there and your manifests pull
  from there. Registry restrictions are enforced by policy — build against your own ACR
  from the start.
- **ResourceQuota** per namespace. Exceed it and pods stop scheduling — with a quota error,
  not a crash loop.
- **Istio sidecar injection** with mTLS between meshed workloads.

### What is *not* enforced by the AppProject

Worth knowing so you do not rely on a boundary that is not there: the AppProject's
`destinations` are currently `server: "*", namespace: "*"`, so it does not restrict
which namespace a tenant Application may target. Your Application is *pointed* at your
namespace by the platform, but nothing at that layer stops it being repointed.

Treat your own namespace as the boundary regardless. Kubernetes RBAC, NetworkPolicies
and Kyverno still apply everywhere, and deploying into a namespace you were not given is
a platform incident, not a shortcut.

---

## Working without kubectl

| What you would have done | What you do instead |
|--------------------------|---------------------|
| `kubectl get pods` | ArgoCD UI — your Application's resource tree, live sync and health status |
| `kubectl logs` | ArgoCD UI pod logs (your project role has explicit `logs, get` permission) |
| `kubectl describe` / events | ArgoCD resource view shows events and conditions |
| `kubectl apply` | commit to the deployment repo; ArgoCD syncs |
| `kubectl rollout restart` | ArgoCD **Sync** / **Refresh**, or commit a change |
| `kubectl rollout undo` | revert the commit; ArgoCD rolls forward to the previous state |
| `kubectl top` | Grafana — Kubernetes and namespace dashboards |
| `kubectl exec` | not available — build in health endpoints, structured logs and metrics |
| `kubectl port-forward` | not available — use the internal hostname from a VPN-connected client |

### Getting access — VPN, Jumphost, ArgoCD

**Sort this out before your first deployment, not after.** Because there is no
`kubectl`, ArgoCD is your only window into the cluster. A developer who can push to the
deployment repo but cannot open ArgoCD has no way to see whether the sync worked, read a
pod log, or tell a crash loop from a failed image pull.

None of the platform UIs are on the public internet — they are all published on the
**internal** gateway. Reaching them takes three separate things, and they are granted by
three different routes:

| # | What you need | How you get it |
|---|---------------|----------------|
| 1 | **Connected to the Capio VPN** | Your normal Capio client access — Capio Support |
| 2 | **Access to the Jumphost machine** | The platform UIs are reached *through* the Jumphost, not directly from your laptop |
| 3 | **ArgoCD access for that environment** | Your tenant's Entra ID group must be registered for that environment's ArgoCD |

```
   Your laptop ──► Capio VPN ──► Jumphost ──► https://argocd.<cluster>.capio.eu
                                                         │
                                                  Entra ID SSO
                                                         │
                                           your tenant's ArgoCD project
```

> **Access is per environment.** Being able to open dev's ArgoCD tells you nothing about
> test or prod — each environment has its own ArgoCD instance and its own group
> registration. Request every environment you are expected to support, and verify each
> one by actually logging in. Discovering on release day that nobody on the team can open
> prod's ArgoCD is a common and entirely avoidable delay.

Ask the platform team for the current Jumphost details and for ArgoCD registration; ask
Capio Support for VPN and Jumphost access itself.

### Your tools

All of these are reached the same way — VPN, then Jumphost — and all are SSO-protected
with **Microsoft Entra ID**:

| Tool | URL | What it is for |
|------|-----|----------------|
| **ArgoCD** | `https://argocd.<cluster>.capio.eu` | sync status, resource tree, pod logs, manual sync |
| **Grafana** | `https://grafana.<cluster>.capio.eu` | metrics, resource usage, cost per namespace |

`<cluster>` is `dev02`, `test02` or `prod01` — each environment has its own instance of
each tool, and you log in to the one for the environment you are working on.

That is the whole list. In particular there is **no RabbitMQ management UI** and **no
service-mesh UI** for developers: RabbitMQ is available to your application over AMQP
only. If you need to know how deep a queue is or how fast it is draining, your
application has to expose that as a metric — you cannot go and look.

### What you can do once you are in

Your tenant's ArgoCD project grants one of two roles:

- **`dev`** — view applications, view logs, trigger a sync. No delete, no create.
- **`admin`** — full control of applications and ApplicationSets within your project.

`exec` is not granted at any level, in any environment, to anyone.

> Because there is no `kubectl exec` and no `port-forward`, **observability is not
> optional**. Ship a real health endpoint, structured JSON logs to stdout, and Prometheus
> metrics. If you skip this, you will be debugging blind — and the one tool you do have
> is several access hops away.

---

## Checklist: publishing a new application

**Before you write any Kubernetes YAML**

- [ ] Tenant identified; you are in the right Entra ID group
- [ ] **Capio VPN access working**
- [ ] **Jumphost access working**
- [ ] **ArgoCD reachable and you can log in — verified separately for each environment**
- [ ] Onboarding form submitted for the tenant and app (see [Getting onboarded](#getting-onboarded))
- [ ] CMKS project and its `DEV`/`TEST`/`PROD` deployment repos handed over
- [ ] Your app's ACR handed over, and your build pipeline pushes to it
- [ ] Hostname(s) agreed, and `internal` vs `external` decided
- [ ] Key Vault populated for each environment
- [ ] Deployment style chosen — plain manifests unless you have a reason
- [ ] **Every outbound dependency listed — host, IP, port, protocol**
- [ ] **VPN / connectivity requests raised with Capio Support for anything reaching
      Capio internal systems — for each environment**
- [ ] Databases, queues or Redis requested
- [ ] Resource footprint agreed (CPU, memory, pod count)

**In the deployment repo**

- [ ] `deployment/` path exists on the configured branch of the deployment repo
- [ ] Image pulled from your app's ACR, by an immutable tag or digest — never `:latest`
- [ ] `seccompProfile: RuntimeDefault` set
- [ ] `readOnlyRootFilesystem: true` set, with `emptyDir` mounts for every writable path
- [ ] `automountServiceAccountToken: false`
- [ ] Resource requests **and** limits on every container
- [ ] Service is `ClusterIP`
- [ ] HTTPRoute references the correct gateway, namespace and `sectionName`
- [ ] `ExternalSecret` references the platform-created `SecretStore`
- [ ] No secrets, no `Gateway`, no `Certificate`, no cluster-scoped resources in Git
- [ ] Health/readiness endpoints, structured stdout logging, metrics endpoint

**Verify**

- [ ] ArgoCD shows the Application **Synced** and **Healthy**
- [ ] Hostname resolves and serves over HTTPS with a valid certificate
- [ ] Outbound dependencies actually connect (timeouts mean a missing NetworkPolicy or VPN rule)
- [ ] Metrics and logs are visible in Grafana

---

## Who to ask

There are only two places you ever send a request.

| Topic | Who |
|-------|-----|
| **A new tenant or a new app** | the [onboarding form](https://forms.cloud.microsoft/Pages/ResponsePage.aspx?id=tHliZmUfr0m27Osk2g2U5w4KPBsvz95End5tnuywcK9UOFdQWVRKWDZQQU9QREpWRlRFQVdUSlVDWS4u) |
| Namespace, quota, ArgoCD project, hostname, TLS, databases, queues, egress policy | **CMKS platform team** |
| ArgoCD registration for your tenant, per environment | **CMKS platform team** |
| Capio VPN access, Jumphost access | **Capio Support** |
| Connectivity to Capio internal systems — firewall rules, routing, anything VPN | **Capio Support** |
| Key Vault contents, Entra ID group membership | your own team / Capio IT |

> **You never contact Cleura.** Cleura operates the underlying cloud and their side of
> the VPN, but they are not a party you raise anything with. Everything that touches
> Cleura goes through **Capio Support**, who coordinate it.

---

## Further reading

This guide is deliberately standalone — it is everything a developer needs and nothing
more. The platform team maintains deeper documentation in the **CMKS - Infra**
repository under `docs/`, which is the authoritative reference if you need detail beyond
this page:

| Document | Contents |
|----------|----------|
| `architecture-overview.md` | Full platform design, topology and component versions |
| `networking-ingress.md` | Gateway API, Istio, routing patterns, PKI |
| `tenant-model.md` | Isolation, RBAC, cost allocation |
| `data-services.md` | PostgreSQL and RabbitMQ in depth |
| `security-compliance.md` | Policies, encryption, compliance mapping |
| `observability.md` | Metrics, dashboards, alerting |

Those documents describe the platform from the operator's side and assume access to the
infrastructure repository. Ask the platform team if you need something from them.
