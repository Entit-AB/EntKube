# EntKube

**The Kubernetes control plane for platform teams.** EntKube is a self-hosted, multi-tenant developer portal that turns a fleet of Kubernetes clusters into self-service infrastructure — register clusters, deploy a curated catalog of production services in one click, manage secrets, ship applications, and observe everything through a built-in telemetry stack, all from a single pane of glass.

---

## Why EntKube

Most teams stitch together a dozen tools to run shared infrastructure: GitOps for delivery, a secrets manager, a monitoring stack, an incident tool, a status page, and a pile of Helm charts. EntKube brings all of it into one opinionated portal built on the Kubernetes ecosystem you already trust — so a small team can operate infrastructure for many customers without the operational grind.

- **Self-hosted** — your clusters, your data, your registry. Nothing leaves your infrastructure.
- **Multi-tenant to the core** — isolated tenants, customers, environments and groups with role-based access at every level.
- **Batteries included** — logs, traces, metrics, RUM, alerting, secrets and a service catalog ship in the box.

## Feature Overview

### Multi-tenant control plane
Model your organisation as **tenants → customers → environments**, with user **groups** and role-based access control throughout. Teams get self-service access to exactly the infrastructure they own, and nothing they don't.

### Cluster fleet management
Register any Kubernetes cluster and monitor **nodes, health, capacity and workloads** across the whole fleet from one control plane. Cluster kubeconfigs are stored **encrypted in the vault**, never in plaintext on disk.

### One-click service catalog
Pick a component, fill in a short form, and EntKube installs and wires up the Helm release — ingress, TLS, storage and credentials included. 30+ curated components across:

| Category | Components |
|---|---|
| **Ingress** | Traefik (Gateway API), Istio (control plane + external/internal gateways), Gateway API CRDs |
| **Certificates** | cert-manager, Let's Encrypt ClusterIssuer (HTTP-01 + DNS-01: Azure DNS, Cloudflare, Route53, Google Cloud DNS) |
| **Monitoring** | Kube Prometheus Stack, Grafana Loki, Mimir, Tempo, Grafana Alloy, EntKube Telemetry Collector (OTLP), eBPF trace auto-instrumentation |
| **Storage** | MinIO, MinIO Operator |
| **Databases** | CloudNativePG (PostgreSQL), MongoDB Community Operator |
| **Identity** | Keycloak (with theme editor) |
| **Registry** | Harbor |
| **Messaging** | RabbitMQ (Cluster + Topology operators), Strimzi Kafka |
| **Cache** | Redis Operator |
| **Security** | Kyverno |
| **Autoscaling** | KEDA |
| **Networking / VPN** | Tailscale, Headscale, WireGuard (wg-easy), StrongSwan IPsec, Submariner |

Assemble components and services into ordered **Cluster Blueprints** to bootstrap a brand-new cluster end to end, with resume-on-failure.

### Application delivery
Ship applications **per environment** with plain YAML deployments and **apply-or-recreate sync** (including an optional PVC-delete on recreate). The **deployment import wizard** scans a live cluster and adopts existing workloads into managed apps — moving secrets into the vault and detecting databases along the way.

### Encrypted secrets vault
A built-in vault encrypts secrets at rest and scopes them **per app and environment**. Manage plain secrets, **TLS certificates**, **OAuth/OIDC client secrets**, and **cluster kubeconfigs** — all with **expiry tracking** and safe, env-scoped sync to clusters.

### Native observability
Logs, distributed **traces / APM**, metrics, **real-user monitoring (RUM)**, dashboards and alerting ship in the box — powered by a self-built **S3 + Lucene.NET telemetry segment engine** plus **Prometheus** for metrics. Clusters send data over OTLP to a per-cluster, bearer-authenticated ingest endpoint. Grafana/Loki remain available as optional BYO backends.

### Governance & connectivity
Enforce **Kyverno** admission policies per app and environment, and author a **least-privilege connectivity model** that generates NetworkPolicies (L3/L4) and Istio ServiceEntry / AuthorizationPolicy (L7) — analysed and previewed before they are ever applied.

### Operations
The operations layer is built in: **alert rules and routing**, **notification channels**, **on-call schedules**, **incident management**, **SLA targets**, and **maintenance windows**.

### Customer-facing portal
Give your customers a self-service view scoped strictly to their data: **status dashboards, SLA reports, observability, maintenance notices and incident history**.

### Backup & restore
Export the full platform state and restore it on new hardware — migrate servers or recover from disaster without hand-rebuilding your configuration.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Blazor (.NET 10) — Server + WebAssembly |
| Backend | ASP.NET Core 10 |
| Auth | ASP.NET Core Identity with passkey support |
| Database | SQLite (dev) / PostgreSQL (prod) |
| ORM | Entity Framework Core 10 |
| Telemetry | Self-built S3 + Lucene.NET segment engine (logs/traces) · Prometheus (metrics) |
| Reverse proxy | Caddy (automatic Let's Encrypt TLS) |

## Getting Started (local development)

```bash
git clone https://github.com/Entit-AB/EntKube.git
cd EntKube
dotnet run --project src/EntKube.Web
```

The app launches at `https://localhost:7136` (see `src/EntKube.Web/Properties/launchSettings.json`). In development it uses a local SQLite database, so no external services are required to start. Registration is open by default — the first account you create can be promoted to Admin (see `Seed__AdminEmail` below).

## Project Structure

```
EntKube/
├── EntKube.slnx              # Solution file
├── docker-compose.yml        # Production stack (Caddy + Postgres + app, optional MinIO)
├── .env.example              # Copy to .env and fill in
├── src/
│   ├── EntKube.Web/          # Server-side Blazor host, Identity, data layer, services
│   └── EntKube.Web.Client/   # WebAssembly client project
└── tests/
    └── EntKube.Web.Tests/    # Test project
```

## Docker

### Pulling the image

The published image lives at `entit.azurecr.io/entkube`. **`entit.azurecr.io` is a public Azure Container Registry — pulls require no authentication and no `docker login`.** Docker Compose pulls it automatically.

### Building your own image (optional)

> **Apple Silicon (M-series) Mac:** servers are typically `linux/amd64`. Use `docker buildx` to cross-compile and push in one step — the `--push` flag is required because multi-platform images cannot be loaded into the local Docker daemon.

```bash
# One-time setup
docker buildx create --use --name multiarch

# Build for amd64 and push directly to the registry
docker buildx build --platform linux/amd64 \
  -t entit.azurecr.io/entkube:latest \
  --push .

# Tag a versioned release
docker buildx build --platform linux/amd64 \
  -t entit.azurecr.io/entkube:latest \
  -t entit.azurecr.io/entkube:1.0.0 \
  --push .
```

Replace `entit.azurecr.io/entkube` with your own registry and image path if you are not pushing to the public one.

### Run with Docker Compose

Copy `.env.example` to `.env` and fill in the required values, then start the stack:

```bash
docker compose up -d
```

The app will be available at `https://<DOMAIN>` (Caddy terminates TLS on ports 80/443).

> **Data safety** — `docker compose down` does **not** delete the database volume.
> Only `docker compose down -v` removes volumes.

## Configuration

Configuration comes from environment variables (12-factor style). In Compose, host-level values live in `.env` and are mapped onto the app's settings inside `docker-compose.yml`. Nested settings use the ASP.NET Core double-underscore convention (`Section__Key`).

### Required

| Variable | Description |
|---|---|
| `DOMAIN` | Public domain pointing at the server (e.g. `entkube.example.com`). Caddy obtains a Let's Encrypt certificate for it on first start. |
| `ACME_EMAIL` | Email for the Let's Encrypt account and expiry notices. |
| `POSTGRES_PASSWORD` | Password for the bundled Postgres database. Used by both the `postgres` service and the app connection string. |
| `VAULT__ROOTKEY` | 32-byte base64 key for secret encryption. Generate with `openssl rand -base64 32`. **Losing this makes existing vault secrets unrecoverable — back it up.** |

### Database

| Setting | Default | Description |
|---|---|---|
| `DatabaseProvider` | `Postgres` (compose) / `Sqlite` (dev) | Which provider EF Core uses. |
| `ConnectionStrings__DefaultConnection` | derived from `POSTGRES_PASSWORD` | Full connection string. In Compose it is built for you from the Postgres service; override to point at an external database. |

### Auth & bootstrap

| Setting | Default | Description |
|---|---|---|
| `Auth__AllowRegistration` | `true` | Whether new users can self-register. Set to `false` once your admin account exists to lock down sign-ups. |
| `Seed__AdminEmail` | _(unset)_ | If set, this user is granted the **Admin** role on every startup (no-op if already admin). Useful for the first admin and for recovering access. |

### Data protection & reverse proxy

| Setting | Default | Description |
|---|---|---|
| `DataProtection__KeyPath` | `/app/Data/keys` | Where the DataProtection key ring is persisted. Keep it on a durable volume so auth/antiforgery cookies survive restarts (otherwise every deploy logs everyone out). |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` (compose) | Trust `X-Forwarded-*` headers from Caddy. Required for Blazor Server SignalR (WebSockets) behind the proxy. |

### Telemetry / observability

The native telemetry engine stores logs and traces as sealed segments in object storage (metrics go to Prometheus). See `docker-compose.yml` for the full annotated block.

| Setting | Default | Description |
|---|---|---|
| `Telemetry__PublicIngestUrl` | `https://${DOMAIN}` | Base URL clusters use to reach this server; the app appends `/ingest/otlp`. Must be externally reachable from your clusters. |
| `Telemetry__RetentionDays` | `14` | Days of telemetry to retain before sealed segments older than this are dropped. (`TELEMETRY_RETENTION_DAYS` in `.env`.) |
| `Telemetry__DataPath` | `/app/Data/telemetry` | Local state: active index + sealed-segment cache. Keep on a durable volume. |

**Object storage for sealed segments** (priority: StorageLink → flat config → local disk):

| Setting | Description |
|---|---|
| `Telemetry__StorageLinkId` | **Primary/production.** GUID of a registered StorageLink (AWS S3 / Cleura S3 / MinIO) whose credentials live in the vault. |
| `Telemetry__ObjectStorage__Endpoint` / `__Bucket` / `__Region` / `__AccessKey` / `__SecretKey` / `__ForcePathStyle` | **Simple/dev alternative.** Flat S3/MinIO config used when no StorageLink is set. |

If neither is configured, sealed segments live on local disk under `Telemetry__DataPath` (**single-node only**).

The repo bundles an optional MinIO for local/dev object storage, started with the `objectstore` profile:

```bash
docker compose --profile objectstore up -d
```

| Variable | Default | Description |
|---|---|---|
| `MINIO_ROOT_USER` | `entkube` | Root user for the bundled MinIO. |
| `MINIO_ROOT_PASSWORD` | `changeme-minio` | Root password for the bundled MinIO. |
| `TELEMETRY_BUCKET` | `entkube-telemetry` | Bucket created for sealed segments. |

### Email / SMTP (optional)

Configure outbound email to deliver alert notifications and account emails.

| Setting | Description |
|---|---|
| `Smtp__Host` | SMTP server hostname. |
| `Smtp__Username` | SMTP username. |
| `Smtp__Password` | SMTP password. |
| `Smtp__FromAddress` | From address for outgoing mail (e.g. `alerts@entkube.io`). |

### TLS / HTTPS

Caddy is the reverse proxy and handles TLS automatically. On first start it obtains a Let's Encrypt certificate for `DOMAIN` and renews it before expiry. Ports **80** and **443** must be open and your DNS must point at the server before starting the stack.

## License

See [LICENSE](LICENSE).
