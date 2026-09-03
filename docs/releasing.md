# Releasing EntKube

What ships, how each piece is built, and why each is packaged the way it is.

EntKube is not one artifact. It is a management-plane image, a second image that runs *inside* managed
clusters, a Helm chart, and four standalone binaries that run in places where neither a container runtime
nor a .NET runtime can be assumed. Those constraints are what shape the packaging.

**Everything is built by one script.** `scripts/release.sh` takes target names, builds all of them when
given none, and publishes what has somewhere to be published when given `--push`.

```bash
scripts/release.sh                          # build everything, publish nothing
scripts/release.sh --push                   # build everything, publish what has a home
scripts/release.sh telemetry --push         # one target
scripts/release.sh cli mcp --rid linux-x64  # two targets, one platform
scripts/release.sh --list                   # what the targets are
```

| Target | Ships as | Published by | Cadence |
| --- | --- | --- | --- |
| `web` | Container image `entit.azurecr.io/entkube` | `.github/workflows/deploy.yml` | Every push to `main` |
| `telemetry` | Image `entit.azurecr.io/entkube-telemetry` **+** Helm chart | `.github/workflows/release-telemetry.yml` | On a version tag |
| `agent` | Self-contained binary, 4 platforms | By hand — see below | On demand |
| `cli` | Self-contained binary, 4 platforms | By hand | On demand |
| `mcp` | Self-contained binary, 4 platforms | By hand | On demand |
| `installer` | Self-contained binary, 4 platforms | By hand | On demand |
| `gui` | Desktop app + bundled client tools, 4 platforms | By hand | On demand |
| `terraform` | Go binary, 5 platforms | By hand | On demand |

`binaries` is shorthand for the four binary targets; `all` (the default) is everything, `gui`
included.

Output lands under `artifacts/`, in the same per-target layout as before. Common options:

| Option | Effect |
| --- | --- |
| `--push` | Publish the image/chart targets. Binaries have no registry — see the agent section. |
| `--version <v>` | Override the version. Defaults to the chart's own for `telemetry`, the short SHA for `web`. |
| `--rid <list>` | Build only these platforms, e.g. `linux-x64,osx-arm64`. Fails if the target ships none of them. |
| `--configuration <list>` | `Release` (default) or `Debug`, or `Debug,Release` for both. Binary targets only. |
| `--registry <host>` | Default `entit.azurecr.io`. |
| `--platforms <list>` | Override the container image platforms. |
| `--build-platform <p>` | Which platform the images *compile* on. Defaults to the builder's own, except on an Apple Silicon Mac — see below. |

> **A note if you remember the old scripts.** `release.sh` replaces `build-agent.sh`, `build-cli.sh`,
> `build-mcp.sh`, `build-terraform-provider.sh` and `build-telemetry.sh`. Two behaviours changed:
> configuration and platform are now named options rather than positional arguments, and the binary
> targets default to **Release only** instead of Debug *and* Release — a release build does not need the
> Debug copy, and building it doubled every run for nothing. Pass `--configuration Debug,Release` for the
> old behaviour.

Being one script does not make the pieces one release: they still go out on different cadences, for the
different reasons set out below. It only means there is one place that knows how to build each of them.

---

## Management plane

The one that behaves like ordinary CI. `deploy.yml` builds `Dockerfile` on every push to `main`, tags it
with the short SHA plus `latest`, pushes to ACR, then SSHes to the server and runs `docker compose pull &&
up -d`. Newest-wins is correct here because there is exactly one deployment and it is the one CI just
built.

Nothing about a release is version-pinned, so nothing needs bumping.

`scripts/release.sh web` does the same build from a laptop, tagging the short SHA so an image built there
and one built by CI from the same commit carry the same tag. With `--push` it also moves `latest`, because
that is the tag `docker-compose.yml` on the server pulls — a push that updated only the SHA tag would
build an image the server never sees. It does not deploy: the server still has to pull, which is the SSH
step in `deploy.yml`.

### The compiler and the image no longer have to share an architecture

Both image Dockerfiles compile in a stage that runs on `BUILD_PLATFORM` and cross-publish with
`dotnet publish --arch` for whichever architecture the image is being built for. `BUILD_PLATFORM`
defaults to the builder's own platform, so on CI — where `deploy.yml` gives each architecture a native
runner — nothing changes and `--arch` is a no-op.

It exists because of one host. On an Apple M-series Mac the `linux/arm64` leg used to die a few seconds
into `dotnet publish` with **`Illegal instruction`** and exit code **132** (128+4, SIGILL), reported
either as `"csc" exited with code 132` or, when the crash took the whole publish, as buildx's
`exit code: 132`. It reads like a source problem and is not one: macOS runs Linux containers under
Hypervisor.framework, which advertises the ARMv9 **SME2** CPU features to the guest and then cannot
execute them, so the .NET toolchain detects them, emits them, and is killed
([containers/podman#28312](https://github.com/containers/podman/issues/28312),
[dotnet/runtime#122608](https://github.com/dotnet/runtime/issues/122608)). Nothing inside the container
avoids it — with `DOTNET_EnableHWIntrinsic=0` the crash simply moves from `csc` to the MSBuild worker
node, and `DOTNET_EnableSVE=0`, `DOTNET_PROCESSOR_COUNT=2` and `GLIBC_TUNABLES=...-SME,-SME2,-SVE`
change nothing at all.

So `release.sh` does not run an arm64 compiler on such a host. It passes `BUILD_PLATFORM=linux/amd64`,
the compile runs under Rosetta — which works — and `--arch arm64` produces linux-arm64 output from it.
The runtime stage is still the target architecture; it only unpacks tarballs and runs `apt`, neither of
which trips the hypervisor. The build prints which platform it compiled on.

`scripts/release.sh web --push` therefore builds and publishes both architectures from a laptop, `latest`
included. Publishing from CI remains equally correct and is still what a push to `main` does.

`--build-platform <platform>` overrides the choice in either direction, and the SIGILL diagnostic points
at it if the crash ever appears somewhere this heuristic does not cover.

### Both architectures

`linux/amd64` **and** `linux/arm64`, matching `deploy.yml`.

That used to be amd64 only, on the reasoning that there was one server and its architecture was known.
That reasoning expired: `entkube-install` and the desktop installer put this image on whatever host an
operator has, and arm64 servers — Graviton, Ampere, Hetzner ARM — are ordinary. An amd64-only image
fails their pull with `no matching manifest for linux/arm64/v8`, which reads like a registry fault and
is not one.

`deploy.yml` builds each architecture on a **native runner** rather than cross-building one under QEMU.
This Dockerfile installs a .NET workload and runs a full publish, and emulating that turns a few minutes
into the better part of an hour; arm64 hosted runners are free for public repositories. The shape is
Docker's documented multi-platform pattern and each part of it is load-bearing:

- **Each leg pushes by digest, claiming no tag.** A tag points at one image, so two jobs pushing the
  same tag would race and the loser's work would be silently discarded.
- **Digests travel between jobs as artifacts, not job outputs.** A matrix job's outputs are one shared
  map written by every leg, so the last leg to finish wins and blanks the other's key — leaving the
  merge step to build a one-architecture manifest from a blank reference.
- **A separate `merge` job** assembles the manifest list and applies the tags.
- **Provenance attestations are disabled.** Buildx otherwise adds a manifest entry with platform
  `unknown/unknown`, which several container runtimes report as a platform mismatch instead of
  ignoring — the same class of failure this change exists to remove.
- **The manifest is inspected before the deploy runs**, and the workflow fails if either architecture
  is missing. A partial manifest is otherwise discovered on someone else's server.

`deploy` depends on `merge`, so a broken build for *either* architecture stops the deploy rather than
quietly republishing a single-architecture image. That is the intended trade — silent degradation back
to amd64-only is precisely the bug being fixed — but it does mean an arm64 failure blocks an amd64
deploy. If that is ever the wrong call, removing the `linux/arm64` entry from the `build` matrix is a
one-line rollback.

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

This is the whole reason the `telemetry` target does more than run a pair of raw commands:

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
scripts/release.sh telemetry

# 3. Publish, either by tag...
git tag telemetry-v0.2.0 && git push origin telemetry-v0.2.0

#    ...or from a laptop with a registry session:
az acr login --name entit
scripts/release.sh telemetry --push
```

CI runs the same script, so a release made from a laptop and one made by CI are the same release.

### After publishing

Nothing else to change: the catalog entries already point at the new version, which is what step 1
guaranteed. Existing installs are untouched until an operator upgrades the component.

### Numbers in Helm values

Helm parses YAML numbers as **float64**, and Go renders anything past about a million in scientific
notation. So a plain `{{ .Values.telemetry.segmentMaxDocs | quote }}` emits `"1e+06"` for `1000000`, and
`"8.589934592e+09"` for an 8 GiB byte count. .NET's `Int64` configuration binder rejects both, and the pod
dies at startup with a message that names the configuration key and never mentions Helm.

Every integer in the chart therefore goes through `| int64`. Doubles do not need it — .NET's double parser
accepts exponent form.

This only shows up above a certain magnitude, which is what makes it easy to reintroduce: `90` and `14`
render fine forever, and the bug appears the day someone raises a limit. Both `scripts/release.sh` and CI
render the chart and fail on any `value: "…e+…"`.

### Architecture

The image is built for **`linux/amd64` and `linux/arm64`**, and the script refuses to finish without
amd64. That guard exists because the failure is remote, late and misleading: an image built only for the
machine that ran the build pushes fine, the chart installs fine, the pull secret works — and then every
pod reports

```
no match for platform in manifest sha256:...: not found
```

which reads like a registry fault and is not one. It simply means the manifest list holds no entry for the
node's architecture. Building on an Apple Silicon laptop and deploying to amd64 nodes hits this every time.

Two mechanics worth knowing:

- **A multi-platform image only exists in a registry.** The local daemon holds one image per tag, so it
  cannot be `--load`ed. A verify-only run (`scripts/release.sh telemetry` with no `--push`) therefore
  builds just the first platform; the full set is built on `--push`, straight to the registry.
- **Provenance attestations are disabled** (`--provenance=false`). Buildx otherwise adds a manifest entry
  with platform `unknown/unknown`, which several container runtimes report as a platform mismatch instead
  of ignoring. Nothing here consumes the attestation.

`release-telemetry.yml` sets up QEMU so an amd64 runner can cross-build arm64. That is a
reasonable trade there — the telemetry image is small and released on a tag, not on every push —
whereas `deploy.yml` uses native arm64 runners because the management-plane image is far more
expensive to build and is built on every merge. Since the compile now happens on the *builder's*
platform and cross-publishes (see the management-plane section), QEMU there only has to emulate the
runtime stage — unpacking a layer and adding a user — rather than a full .NET publish.

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
scripts/release.sh agent                        # all platforms, Release
scripts/release.sh agent --rid linux-x64        # one platform
scripts/release.sh agent --configuration Debug  # a debug build
```

Output: `artifacts/agent/<configuration>/<rid>/entkube-agent[.exe]` for `osx-arm64`, `osx-x64`,
`linux-x64`, `win-x64`.

Consequences of that packaging, all deliberate:

- **`RuntimeIdentifier` is set at publish time, not in the csproj.** The project stays buildable and
  referenceable without pinning a platform; only the publish is platform-specific.
- **Distribution is by hand.** There is no registry to push to — the whole point is a network that cannot
  reach one. Copy the binary to the host that needs it.
- **Each binary needs an `agent.json` beside it**, carrying the server URL, the token generated in EntKube
  (Storage → Egress Agents → Add Agent), and the host/port allowlist. `release.sh` prints a template.
- **The allowlist is the security boundary, and it is local.** The agent refuses any host not on its own
  list regardless of what EntKube asks for, and EntKube cannot widen it. Changing it means editing that
  file on the host and restarting. That is what makes running the agent acceptable, so it is not
  something a release can configure centrally.

The CLI and MCP server are packaged the same way for a milder version of the same reason — they get
dropped onto a CI runner or a laptop, and requiring a runtime first would rule out most of the places they
are useful.

---

## CLI, MCP server, installer, Terraform provider

```bash
scripts/release.sh cli        # artifacts/cli/<config>/<rid>/entkube[.exe]
scripts/release.sh mcp        # artifacts/mcp/<config>/<rid>/entkube-mcp[.exe]
scripts/release.sh installer  # artifacts/installer/<config>/<rid>/entkube-install[.exe]
scripts/release.sh terraform  # artifacts/terraform-provider/<os>_<arch>/terraform-provider-entkube[.exe]
scripts/release.sh gui        # artifacts/gui/<config>/<rid>/  (a directory, not one file)
scripts/release.sh binaries   # the four binary targets, including the agent
```

The first two take the same options as the agent. The Terraform provider is Go and ships one platform
more — `linux-arm64`, because Terraform runs on arm64 CI far more often than .NET does — so its full set
is `darwin/arm64`, `darwin/amd64`, `linux/amd64`, `linux/arm64`, `windows/amd64`. `--rid` uses the same
vocabulary for both toolchains and is translated to `GOOS/GOARCH` for this target.

None of these is published automatically. Attach them to a release, or copy them where they are needed.

---

## Management-plane installer

`entkube-install` stands up the management plane on a server: it writes the compose file, the
Caddyfile and `.env`, pulls the images and starts everything. It is packaged like the agent and the
CLI — one self-contained executable per platform — for a stronger version of the same reason: it runs
on a freshly provisioned host *before* EntKube exists, so requiring a .NET runtime first would be the
exact step it exists to remove.

It is a terminal wizard rather than a windowed one because a server is reached over SSH far more often
than it is sat in front of, and a headless host frequently has no display server at all.

Two things about it are worth knowing before changing it:

- **It generates `docker-compose.yml` rather than shipping the repository's copy.** The choices it
  offers are structural — an external database has to remove the postgres service *and* the
  health-gated `depends_on` that references it — and a compose override file cannot reliably do that.
  So `docker-compose.yml` in the repository root stays the reference for a hand-rolled install, and
  the installer holds a second rendering of the same knowledge. `InstallerRendererTests` pins the
  parts that must not drift between them.
- **It never regenerates `VAULT__ROOTKEY` or `POSTGRES_PASSWORD`.** Both failures are silent: a new
  vault key leaves the app starting normally with every stored secret decrypting to nothing, and a new
  database password does not reach an already-initialised Postgres volume. Re-running the installer is
  a supported, routine operation, so this has to hold on every run and not merely by convention.

See [docs/installing.md](installing.md).

---

## Installer GUI

`entkube-installer` is a desktop front-end that performs the same install against a server over SSH,
and can put the client tools on the machine it runs on.

It shares the install with the console installer rather than reproducing it: preflight, the
compose/.env renderer, the pull/start sequence and the health probe are one code path parameterised
by an `IExecutor`, which is either a local shell or an SSH connection. The alternative — uploading
`entkube-install` and running it remotely — would have cost a ~76 MB transfer per install and two
architectures of that binary carried inside the GUI to cover a decision it cannot make in advance.

Three things are worth knowing before changing it:

- **It is not built as a single file.** Avalonia carries native libraries, and self-extracting them
  on every launch is slower and a known source of platform-specific breakage. The app ships as a
  directory anyway, because of the point below.
- **The client tools are bundled into `tools/` beside the executable** by `scripts/release.sh gui`,
  built for that same platform. That folder must travel with the app. They are not embedded (four
  self-contained .NET binaries are ~250 MB) and not downloaded (these have no release host).
- **Host keys are verified.** SSH.NET does not check `known_hosts`, so the application does, and an
  unrecognised key is a dialog rather than an assumption. The session carries a sudo password and
  writes the vault root key; accepting any key offered would put both on whatever answered.

The SSH path has integration tests that need a real `sshd` and no-op without one — see
[installing.md](installing.md#testing-the-ssh-path). They are worth running after any change to
`SshExecutor`: they are what caught a sudo pipeline that blocked forever, an SFTP permission call
that rejected its own argument, and a missing-`docker` check that reported the wrong cause.

---

## Third-party notices

`scripts/gen-third-party-notices.py` regenerates `THIRD-PARTY-NOTICES.txt` from the resolved dependency
graph. Run it when dependencies change — a new package brings a new licence, and the file is the record
that it was accounted for.
