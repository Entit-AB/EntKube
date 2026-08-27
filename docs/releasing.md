# Releasing EntKube

What ships, how each piece is built, and why each is packaged the way it is.

EntKube is not one artifact. It is a management-plane image, a second image that runs *inside* managed
clusters, a Helm chart, and four standalone binaries that run in places where neither a container runtime
nor a .NET runtime can be assumed. Those constraints are what shape the packaging, and they are why there
is no single "build everything" command: the pieces are released on different cadences and for different
reasons.

| Artifact | Ships as | Built by | Published by | Cadence |
| --- | --- | --- | --- | --- |
| Management plane | Container image `entit.azurecr.io/entkube` | `Dockerfile` | `.github/workflows/deploy.yml` | Every push to `main` |
| Telemetry plane | Image `entit.azurecr.io/entkube-telemetry` **+** Helm chart | `scripts/build-telemetry.sh` | `.github/workflows/release-telemetry.yml` | On a version tag |
| Egress agent | Self-contained binary, 4 platforms | `scripts/build-agent.sh` | By hand — see below | On demand |
| CLI | Self-contained binary, 4 platforms | `scripts/build-cli.sh` | By hand | On demand |
| MCP server | Self-contained binary, 4 platforms | `scripts/build-mcp.sh` | By hand | On demand |
| Terraform provider | Go binary, 5 platforms | `scripts/build-terraform-provider.sh` | By hand | On demand |

All build scripts write under `artifacts/` and take the same shape: run from anywhere, no arguments for
the common case, `--help` or the header comment for the rest.

---

## Management plane

The one that behaves like ordinary CI. `deploy.yml` builds `Dockerfile` on every push to `main`, tags it
with the short SHA plus `latest`, pushes to ACR, then SSHes to the server and runs `docker compose pull &&
up -d`. Newest-wins is correct here because there is exactly one deployment and it is the one CI just
built.

Nothing about a release is version-pinned, so nothing needs bumping.

---

## Telemetry plane

The one that does *not* behave like ordinary CI, for a reason worth understanding before changing it.

The telemetry plane runs inside clusters that operators install into on their own schedule. Its Helm chart
version is **pinned** in `ComponentCatalog.cs`, so:

- publishing on every push to `main` would silently replace a chart version operators have already
  installed — the same version number, different contents;
- a release only means anything when that pinned version changes.

So it is released on a tag (`telemetry-v*`) or a deliberate `workflow_dispatch`, never automatically.

### Three things must agree

This is the whole reason `scripts/build-telemetry.sh` exists rather than a pair of raw commands:

1. **`Chart.yaml` `version`** — what the chart is published as.
2. **`Chart.yaml` `appVersion`** — the image tag the chart deploys. `values.yaml` leaves `image.tag` empty
   on purpose so it falls through to `appVersion`; an appVersion pointing at a tag that was never pushed
   produces `ImagePullBackOff` on install and nothing earlier.
3. **`HelmChartVersion` on both `entkube-telemetry-*` catalog entries** — the version installs ask the
   registry for. If it still names the previous release, the new chart is published and then never used:
   the install *succeeds*, on the old chart, and the change appears simply not to have taken.

The script refuses to build when any of these disagree, and `EntKubeTelemetryCatalogTests` checks (3)
against (1) so the mismatch fails in the normal test run rather than at release time. `HelmRepoUrl` is
checked too — publishing to a path installs never look in is otherwise invisible.

### Cutting a release

```bash
# 1. Bump all three, together:
#      charts/entkube-telemetry/Chart.yaml   version: + appVersion:
#      src/EntKube.Web/Services/ComponentCatalog.cs   HelmChartVersion on BOTH entries
# 2. Verify locally — builds the image, lints and packages the chart, publishes nothing:
scripts/build-telemetry.sh

# 3. Publish, either by tag...
git tag telemetry-v0.2.0 && git push origin telemetry-v0.2.0

#    ...or from a laptop with a registry session:
az acr login --name entit
scripts/build-telemetry.sh --push
```

CI runs the same script, so a release made from a laptop and one made by CI are the same release.

### After publishing

Nothing else to change: the catalog entries already point at the new version, which is what step 1
guaranteed. Existing installs are untouched until an operator upgrades the component.

### Registry authentication — two separate surfaces

A private registry needs credentials in **two different places**, and they are not interchangeable. Getting
this wrong produces two failures that look similar and have nothing to do with each other.

**1. EntKube pulls the chart.** `helm` runs inside the management-plane container, so *that* container
needs a registry session. Configure it and EntKube logs in automatically before any `oci://` install:

```
Helm__Registries__entit_azurecr_io__Username=<acr user or SP id>
Helm__Registries__entit_azurecr_io__Password=<acr password or SP secret>
```

Dots in the host become underscores. Leave it unset for a public registry — EntKube then attempts an
anonymous pull rather than refusing, since plenty of registries need no login.

**2. The managed cluster pulls the image.** The kubelet in the *target* cluster fetches
`entit.azurecr.io/entkube-telemetry`, so EntKube's own session is no help to it — the cluster needs its
own credential. **EntKube creates it**, from the same two variables above: on install it writes a
`kubernetes.io/dockerconfigjson` Secret named `entkube-registry` into the release namespace and references
it from the chart's `imagePullSecrets`. Nothing to do by hand.

This happens only for catalog entries that declare an `ImageRegistryHost` — the two telemetry components.
Every third-party component pulls from a public registry, which is why none of them has ever needed a pull
secret, and the behaviour stays opt-in rather than blanket.

If you name a pull Secret yourself in the component's **Image Pull Secret** field, that wins: pointing at
an existing Secret is a legitimate choice and EntKube does not override it.

### Should these images be public instead?

They could be, and that would remove all of the above. It is a product decision rather than a technical
one, so it has deliberately not been made here:

- **Publishing publicly** (Docker Hub, GHCR) means no registry credentials, no pull Secret, no
  `helm registry login` — the two components install exactly like every third-party entry in the catalog.
  The cost is that the telemetry engine's binaries become downloadable by anyone. The image on its own is
  not the product — it refuses to start without a tenant, a cluster and tokens, and it is only useful with
  a management plane to query it — but .NET assemblies decompile readily, so this is a real question.
- **Staying private** keeps that closed, at the cost of the credential plumbing described above.

The two are not exclusive and the code does not have to change to switch. Credential handling is
conditional on credentials *being configured*: publish the images publicly, remove
`REGISTRY_USERNAME`/`REGISTRY_PASSWORD`, and both the chart login and the pull-Secret creation simply
stop happening. Point `ImageRegistryHost` at null (or leave the credentials unset) and the components pull
anonymously.

### Why `oci://` is not a Helm repo

OCI registries are addressed directly, not added as repositories. `helm repo add oci://...` fails with
*"not a valid chart repository or cannot be reached … invalid reference"*, which is what
`ComponentLifecycleService` used to do for every component with a repo URL. It now detects an `oci://`
prefix and skips the add entirely: the chart reference it already builds
(`oci://<registry>/<path>/<chart>` plus `--version`) is exactly the right form.

---

## Egress agent — why this one is different

The agent is the artifact with genuinely special handling, and the reason is in
[docs/egress-agent.md](egress-agent.md): it runs inside networks that are allowed to reach a provider's
IP-allowlisted API when EntKube is not. Those networks routinely **cannot pull a container image and
cannot install a .NET runtime** — that is frequently the whole reason the agent is needed rather than a
proxy or a cluster relay.

So it ships as a **single self-contained executable per platform**, built with
`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract` + `DebugType=embedded`:

```bash
scripts/build-agent.sh                    # all platforms, Debug + Release
scripts/build-agent.sh Release            # one configuration
scripts/build-agent.sh Release linux-x64  # one target
```

Output: `artifacts/agent/<configuration>/<rid>/entkube-agent[.exe]` for `osx-arm64`, `osx-x64`,
`linux-x64`, `win-x64`.

Consequences of that packaging, all deliberate:

- **`RuntimeIdentifier` is set at publish time, not in the csproj.** The project stays buildable and
  referenceable without pinning a platform; only the publish is platform-specific.
- **Distribution is by hand.** There is no registry to push to — the whole point is a network that cannot
  reach one. Copy the binary to the host that needs it.
- **Each binary needs an `agent.json` beside it**, carrying the server URL, the token generated in EntKube
  (Storage → Egress Agents → Add Agent), and the host/port allowlist. `build-agent.sh` prints a template.
- **The allowlist is the security boundary, and it is local.** The agent refuses any host not on its own
  list regardless of what EntKube asks for, and EntKube cannot widen it. Changing it means editing that
  file on the host and restarting. That is what makes running the agent acceptable, so it is not
  something a release can configure centrally.

The CLI and MCP server are packaged the same way for a milder version of the same reason — they get
dropped onto a CI runner or a laptop, and requiring a runtime first would rule out most of the places they
are useful.

---

## CLI, MCP server, Terraform provider

```bash
scripts/build-cli.sh                # artifacts/cli/<config>/<rid>/entkube[.exe]
scripts/build-mcp.sh                # artifacts/mcp/<config>/<rid>/entkube-mcp[.exe]
scripts/build-terraform-provider.sh # artifacts/terraform-provider/<os>_<arch>/terraform-provider-entkube[.exe]
```

The first two take the same `[configuration] [rid]` arguments as the agent. The Terraform provider is Go
and builds all five platforms unconditionally (`darwin/arm64`, `darwin/amd64`, `linux/amd64`,
`linux/arm64`, `windows/amd64`).

None of these is published automatically. Attach them to a release, or copy them where they are needed.

---

## Third-party notices

`scripts/gen-third-party-notices.py` regenerates `THIRD-PARTY-NOTICES.txt` from the resolved dependency
graph. Run it when dependencies change — a new package brings a new licence, and the file is the record
that it was accounted for.
