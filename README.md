# EntKube

A multi-tenant platform for managing shared Kubernetes applications and infrastructure services.

## Vision

EntKube provides a unified developer portal for provisioning, configuring, and monitoring shared services running on Kubernetes — such as MinIO, CloudNativePG, Keycloak, and more. Teams get self-service access to the infrastructure they need without managing the underlying clusters directly.

## Key Features

- **Shared Application Management** — Deploy, configure, and lifecycle-manage shared services (MinIO, CNPG, Keycloak, etc.) across clusters.
- **Multi-Tenant SaaS** — Isolated tenants with role-based access, resource quotas, and per-team service instances.
- **Kubernetes Cluster Management** — Register, monitor, and operate multiple clusters from a single control plane.
- **Infrastructure Monitoring & Observability** — Health dashboards, alerts, and resource usage metrics for managed services.
- **Developer Portal** — Self-service UI for teams to request and manage their service instances.
- **CI/CD Pipeline Integration** — Trigger deployments, view pipeline status, and promote releases across environments.

## Tech Stack

| Layer | Technology |
|-------|-----------|
| Frontend | Blazor (.NET 10) — Server + WebAssembly |
| Backend | ASP.NET Core 10 |
| Auth | ASP.NET Core Identity with passkey support |
| Database | SQLite (dev) / PostgreSQL (prod) |
| ORM | Entity Framework Core 10 |

## Getting Started

```bash
git clone https://github.com/Entit-AB/EntKube.git
cd EntKube
dotnet run --project src/EntKube.Web
```

The app launches at `https://localhost:7136` (see `src/EntKube.Web/Properties/launchSettings.json`).

## Project Structure

```
EntKube/
├── EntKube.slnx              # Solution file
├── src/
│   ├── EntKube.Web/          # Server-side Blazor host, Identity, data layer
│   └── EntKube.Web.Client/   # WebAssembly client project
└── tests/
    └── EntKube.Web.Tests/    # Test project
```

## Docker

### Build and push

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

Replace `entit.azurecr.io/entkube` with your actual registry and image path.

### Run with Docker Compose

Copy `.env.example` to `.env` and fill in the required values, then start the stack:

```bash
docker compose up -d
```

The app will be available at `http://localhost:8080`.

> **Data safety** — `docker compose down` does **not** delete the database volume.
> Only `docker compose down -v` removes volumes.

### Required configuration

| Variable | Description |
|---|---|
| `DOMAIN` | Public domain pointing at the server (e.g. `entkube.example.com`). |
| `ACME_EMAIL` | Email for Let's Encrypt account and expiry notices. |
| `Vault__RootKey` | 32-byte base64 key for secret encryption. Generate with `openssl rand -base64 32`. |
| `POSTGRES_PASSWORD` | Password for the Postgres database (set in `.env`). |

### TLS / HTTPS

Caddy is the reverse proxy and handles TLS automatically. On first start it obtains a Let's Encrypt certificate for `DOMAIN` and renews it automatically before it expires. Ports **80** and **443** must be open and your DNS must point at the server before starting the stack.

## License

See [LICENSE](LICENSE).

