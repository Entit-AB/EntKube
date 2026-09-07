#!/usr/bin/env bash
#
# Builds — and optionally publishes — every EntKube artifact.
#
# This replaces the five per-artifact build scripts that preceded it. They were 80% the same script:
# three of them were byte-for-byte identical dotnet-publish loops with a different project path, and
# none of them could build the management-plane image at all, so a "full build" meant running four
# commands and then remembering that the fifth artifact only ever gets built inside CI.
#
# EntKube is still not one artifact, and the pieces still release on different cadences — that part of
# docs/releasing.md was right and has not changed. What changed is that there is now one entry point
# that knows all of them, so a full build is one command and a single-artifact build is that same
# command with a target name.
#
# Usage:
#   scripts/release.sh                          # build everything, publish nothing
#   scripts/release.sh --push                   # build everything, publish what has a home
#   scripts/release.sh telemetry --push         # one target
#   scripts/release.sh cli mcp --rid linux-x64  # two targets, one platform
#   scripts/release.sh web --build-platform linux/arm64   # compile the images on that platform
#   scripts/release.sh --list                   # what the targets are
#
# Targets:
#   web         Management-plane container image        (pushes to the registry)
#   telemetry   Telemetry image + Helm chart            (pushes to the registry)
#   agent       Egress agent, self-contained binaries   (no push — see docs/releasing.md)
#   cli         CLI, self-contained binaries            (no push)
#   mcp         MCP server, self-contained binaries     (no push)
#   installer   Management-plane installer binaries     (no push)
#   gui         Installer GUI + the client tools it installs  (no push)
#   terraform   Terraform provider, Go binaries         (no push)
#   binaries    agent + cli + mcp + installer + terraform
#   all         everything (the default)
#
# Everything lands under artifacts/, in the same layout as before.
#
# Pushing needs an authenticated registry session. For an *.azurecr.io registry this script runs
# `az acr login` itself, so an `az login` is enough. For any other registry, log in first:
#   docker login <registry> && helm registry login <registry>

set -euo pipefail

cd "$(dirname "$0")/.."

# ── Defaults ─────────────────────────────────────────────────────────────────────────────────────────
REGISTRY="entit.azurecr.io"

# Where charts live under the registry. The catalog's HelmRepoUrl must match this exactly.
HELM_REPO_PATH="helm"

WEB_IMAGE_NAME="entkube"
WEB_DOCKERFILE="Dockerfile"
# Both architectures, matching deploy.yml. This image is no longer pulled only by one known server:
# entkube-install and the desktop installer put it on whatever host an operator has, and arm64
# servers are ordinary now. An amd64-only image fails their pull with "no match for platform in
# manifest", which reads like a registry fault and is not one.
WEB_PLATFORMS_DEFAULT="linux/amd64,linux/arm64"

TELEMETRY_CHART_DIR="charts/entkube-telemetry"
TELEMETRY_CHART_NAME="entkube-telemetry"
TELEMETRY_IMAGE_NAME="entkube-telemetry"
TELEMETRY_DOCKERFILE="src/EntKube.TelemetryNode/Dockerfile"
# Both architectures. A cluster's nodes are usually amd64 while a developer's laptop is increasingly
# arm64, and an image built only for the machine that ran the build fails at pull time with "no match
# for platform in manifest" — which reads like a registry problem and is not one.
TELEMETRY_PLATFORMS_DEFAULT="linux/amd64,linux/arm64"

CATALOG="src/EntKube.Web/Services/ComponentCatalog.cs"

# osx-arm64 covers Apple Silicon (M-series); osx-x64 covers Intel Macs and also runs on Apple Silicon
# under Rosetta, though the native build is preferable.
DOTNET_RIDS_DEFAULT=(osx-arm64 osx-x64 linux-x64 win-x64)
# The Go provider ships one more: Terraform runs on arm64 Linux CI far more often than .NET does.
GO_RIDS_DEFAULT=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64)

PUSH=false
VERSION=""
CONFIGURATIONS="Release"
RID_FILTER=""
PLATFORMS_OVERRIDE=""
BUILD_PLATFORM_OVERRIDE=""
TARGETS=()

# ── Argument parsing ─────────────────────────────────────────────────────────────────────────────────
# Prints the header comment above, stopping at the first line that is not one. A line range would be
# right until the header grows a paragraph, and then quietly wrong.
usage() { awk 'NR > 1 { if (!/^#/) exit; sub(/^# ?/, ""); print }' "$0"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --push)          PUSH=true; shift ;;
    --version)       VERSION="${2:?--version needs a value}"; shift 2 ;;
    --configuration) CONFIGURATIONS="${2:?--configuration needs a value}"; shift 2 ;;
    --rid)           RID_FILTER="${2:?--rid needs a value}"; shift 2 ;;
    --registry)      REGISTRY="${2:?--registry needs a value}"; shift 2 ;;
    --platforms)     PLATFORMS_OVERRIDE="${2:?--platforms needs a value}"; shift 2 ;;
    --build-platform) BUILD_PLATFORM_OVERRIDE="${2:?--build-platform needs a value}"; shift 2 ;;
    --list)          sed -n '/^#   web /,/^#   all /p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -h|--help)       usage; exit 0 ;;
    -*)              echo "Unknown option: $1" >&2; echo "Try --help." >&2; exit 2 ;;
    *)               TARGETS+=("$1"); shift ;;
  esac
done

[[ ${#TARGETS[@]} -eq 0 ]] && TARGETS=(all)

# Expand the group names, then de-duplicate so `release.sh all cli` builds the CLI once.
EXPANDED=()
for t in "${TARGETS[@]}"; do
  case "$t" in
    all)      EXPANDED+=(web telemetry agent cli mcp installer terraform gui) ;;
    binaries) EXPANDED+=(agent cli mcp installer terraform) ;;
    web|telemetry|agent|cli|mcp|installer|terraform|gui) EXPANDED+=("$t") ;;
    *) echo "Unknown target: $t" >&2; echo "Try --list." >&2; exit 2 ;;
  esac
done

SELECTED=()
for t in "${EXPANDED[@]}"; do
  for seen in ${SELECTED[@]+"${SELECTED[@]}"}; do [[ "$seen" == "$t" ]] && continue 2; done
  SELECTED+=("$t")
done

selected() { for t in "${SELECTED[@]}"; do [[ "$t" == "$1" ]] && return 0; done; return 1; }

IFS=',' read -r -a CONFIGS <<< "$CONFIGURATIONS"

# ── Output plumbing ──────────────────────────────────────────────────────────────────────────────────
# Noisy steps write here instead of /dev/null. Redirecting a build and a registry push into nothing keeps
# the happy path tidy and makes every failure unreadable — "re-run without redirecting output" is not a
# diagnosis, it is a request to run the build again. The tail is printed on failure, the file is kept.
LOG="${TMPDIR:-/tmp}/entkube-release.$$.log"
: > "$LOG"

fail() { echo; echo "error: $*" >&2; exit 1; }

# Same, but shows what the failed command actually printed.
fail_with_log() {
  echo
  echo "error: $*" >&2
  echo >&2
  echo "--- last 40 lines of $LOG ---" >&2
  tail -40 "$LOG" >&2
  echo "--- full output: $LOG ---" >&2
  exit 1
}

step() { printf '  %-34s ... ' "$1"; }

# Collected as targets finish, printed as one table at the end. A 20-platform build scrolls the useful
# part off the screen otherwise.
SUMMARY=()
NOTES=()
summarise() { SUMMARY+=("$1|$2|$3"); }

# ── Platform helpers ─────────────────────────────────────────────────────────────────────────────────
# One RID vocabulary across .NET and Go, so --rid means the same thing for every target.
go_platform_for_rid() {
  case "$1" in
    osx-arm64)   echo "darwin/arm64" ;;
    osx-x64)     echo "darwin/amd64" ;;
    linux-x64)   echo "linux/amd64" ;;
    linux-arm64) echo "linux/arm64" ;;
    win-x64)     echo "windows/amd64" ;;
    *)           echo "" ;;
  esac
}

# Applies --rid to a target's default list, one RID per line.
#
# The defaults come in as one space-separated string rather than an array reference: macOS ships bash 3.2,
# which has no namerefs, and these scripts are run from a laptop at least as often as from CI.
#
# This deliberately does not fail on an empty result. Callers read it through process substitution, which
# runs it in a subshell — an `exit` here would end that subshell, print the error, and let the build carry
# on with no platforms, which is how `--rid linux-arm64 cli` got as far as invoking dotnet with an empty
# runtime. Emptiness is the caller's to detect, in the shell that can actually stop.
resolve_rids() {
  local defaults=$1
  if [[ -z "$RID_FILTER" ]]; then
    printf '%s\n' $defaults
    return
  fi
  local wanted
  IFS=',' read -r -a wanted <<< "$RID_FILTER"
  for w in "${wanted[@]}"; do
    for d in $defaults; do
      [[ "$w" == "$d" ]] && printf '%s\n' "$w"
    done
  done
}

# Reads resolve_rids into the named array, and stops the build when --rid selected nothing this target
# ships — rather than silently building zero platforms and reporting success.
read_rids() {
  local target=$1 defaults=$2
  RESOLVED_RIDS=()
  local r
  while IFS= read -r r; do
    [[ -n "$r" ]] && RESOLVED_RIDS+=("$r")
  done < <(resolve_rids "$defaults")
  [[ ${#RESOLVED_RIDS[@]} -gt 0 ]] || fail "$target does not ship any of: $RID_FILTER
   It ships: $defaults"
}

# ── Registry session ─────────────────────────────────────────────────────────────────────────────────
# Done once, before anything is built, because the alternative is a full multi-platform build followed by
# an "unauthorized" on push.
#
# The registry credential is NOT the Azure CLI session. `az acr login` mints a registry refresh token and
# hands it to the Docker credential store (the macOS keychain here); that token lives ~3 hours. `az login`
# renews the Azure session and leaves the registry token exactly as stale as it was — so "my az login
# worked" and "the push is unauthorized" are both true at the same time, which is what makes this failure
# so confusing. Refresh it here rather than documenting it and hoping.
ensure_registry_session() {
  step "registry session"
  if [[ "$REGISTRY" == *.azurecr.io ]] && command -v az > /dev/null 2>&1; then
    az acr login --name "${REGISTRY%%.*}" >> "$LOG" 2>&1 \
      || fail_with_log "az acr login failed for ${REGISTRY%%.*}.
   If the Azure session itself has expired, run: az login"
    echo "refreshed"
  else
    # Non-ACR registry, or no az CLI: nothing to refresh, so the credential is whatever docker login and
    # helm registry login last left behind. The push step reports it if it is not enough.
    echo "assuming docker login / helm registry login"
  fi
}

# ── Self-contained binaries: agent, CLI, MCP ─────────────────────────────────────────────────────────
# All three ship as a single self-contained executable per platform, for the same reason: they run on a
# jump host, a CI runner or a laptop, and requiring a .NET runtime first would rule out most of the places
# they are useful. For the egress agent it is stronger than a preference — see docs/egress-agent.md, the
# networks it runs in frequently cannot pull an image or install a runtime at all.
#
# RuntimeIdentifier is set here at publish time rather than in the csproj, so the projects stay buildable
# and referenceable without pinning a platform.
build_dotnet_binaries() {
  local target=$1 project=$2 binary_name=$3
  local out_root="artifacts/$target"

  read_rids "$target" "${DOTNET_RIDS_DEFAULT[*]}"
  local rids=("${RESOLVED_RIDS[@]}")

  echo "▸ $target — self-contained binaries"

  local built=0
  for config in "${CONFIGS[@]}"; do
    for rid in "${rids[@]}"; do
      local out="$out_root/$config/$rid"
      step "$config $rid"

      dotnet publish "$project" \
        --configuration "$config" \
        --runtime "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=embedded \
        --output "$out" \
        --nologo \
        --verbosity quiet >> "$LOG" 2>&1 \
        || fail_with_log "dotnet publish failed for $target ($config/$rid)."

      local binary="$out/$binary_name"
      [[ "$rid" == win-* ]] && binary="$binary.exe"
      [[ -f "$binary" ]] || fail_with_log "$target published without producing $binary."

      chmod +x "$binary" 2>/dev/null || true
      echo "$(du -h "$binary" | cut -f1)"
      built=$((built + 1))
    done
  done

  echo
  summarise "$target" "$built binaries" "$out_root/"
}

# ── Installer GUI ────────────────────────────────────────────────────────────────────────────────────
# A desktop front-end for the same install the console installer performs — over SSH to a server —
# which can also drop the client tools onto the machine it runs on.
#
# Those client tools are bundled beside the app rather than embedded in it or downloaded at run time.
# Embedding four self-contained .NET binaries adds ~250 MB to an executable for a feature not every
# user wants, and downloading needs a release host, which these binaries deliberately do not have
# (docs/releasing.md: they are distributed by hand). A folder next to the app costs neither.
#
# The GUI is NOT built as a single file. Avalonia carries native libraries — Skia, the platform
# windowing shims — and self-extracting them on every launch is both slower and a known source of
# platform-specific breakage, for no benefit here: the app ships as a directory anyway because of
# the tools/ folder beside it.
build_gui() {
  local out_root="artifacts/gui"
  local project="src/EntKube.Installer.Gui/EntKube.Installer.Gui.csproj"

  read_rids gui "${DOTNET_RIDS_DEFAULT[*]}"
  local rids=("${RESOLVED_RIDS[@]}")

  echo "▸ gui — installer GUI + bundled client tools"

  local built=0
  for config in "${CONFIGS[@]}"; do
    for rid in "${rids[@]}"; do
      local out="$out_root/$config/$rid"
      step "$config $rid"

      dotnet publish "$project" \
        --configuration "$config" \
        --runtime "$rid" \
        --self-contained true \
        -p:DebugType=embedded \
        --output "$out" \
        --nologo \
        --verbosity quiet >> "$LOG" 2>&1 \
        || fail_with_log "dotnet publish failed for gui ($config/$rid)."

      local binary="$out/entkube-installer"
      [[ "$rid" == win-* ]] && binary="$binary.exe"
      [[ -f "$binary" ]] || fail_with_log "gui published without producing $binary."

      chmod +x "$binary" 2>/dev/null || true

      # The tools the GUI offers to install locally, for this same platform. Built here rather than
      # assumed present, so `release.sh gui` alone produces something complete.
      bundle_client_tools "$config" "$rid" "$out/tools"

      # Per-platform packaging, so the result is something you launch rather than something you
      # first have to work out how to launch.
      case "$rid" in
        osx-*)   wrap_macos_app "$out" ;;
        linux-*) write_desktop_entry "$out" ;;
      esac

      echo "$(du -sh "$out" | cut -f1)"
      built=$((built + 1))
    done
  done

  echo
  summarise "gui" "$built builds" "$out_root/"
}

# Wraps the published output in a .app bundle.
#
# Without one, macOS has a bare Mach-O executable rather than an application: `open` hands it to
# Terminal, so a terminal window appears alongside the GUI, it cannot be double-clicked from Finder,
# and the Dock shows a generic entry with no name. A bundle is only a directory layout and an
# Info.plist, and it turns all three of those into the behaviour people expect.
#
# Everything published goes in Contents/MacOS, including tools/ — AppContext.BaseDirectory resolves
# there, which is where ToolBundle looks for the client binaries.
wrap_macos_app() {
  local out=$1
  local app="$out/EntKube Installer.app"
  local macos="$app/Contents/MacOS"

  rm -rf "$app"
  mkdir -p "$macos" "$app/Contents/Resources"

  # Move rather than copy: the published output is ~370 MB and one copy of it is enough.
  find "$out" -mindepth 1 -maxdepth 1 ! -name "EntKube Installer.app" -exec mv {} "$macos"/ \;

  cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>              <string>EntKube Installer</string>
  <key>CFBundleDisplayName</key>       <string>EntKube Installer</string>
  <key>CFBundleIdentifier</key>        <string>se.entit.entkube.installer</string>
  <key>CFBundleExecutable</key>        <string>entkube-installer</string>
  <key>CFBundlePackageType</key>       <string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key> <string>6.0</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key>           <string>1.0</string>
  <key>LSMinimumSystemVersion</key>    <string>11.0</string>
  <!-- Without this the window is bitmap-scaled on a Retina display and every label is blurry. -->
  <key>NSHighResolutionCapable</key>   <true/>
  <!-- It is a windowed app, not a background agent: it belongs in the Dock and takes focus. -->
  <key>LSBackgroundOnly</key>          <false/>
</dict>
</plist>
PLIST

  chmod +x "$macos/entkube-installer" 2>/dev/null || true
}

# A .desktop entry, so the app can be added to a Linux launcher rather than only run by path.
#
# Exec is left as the bare name on purpose: the absolute path depends on where the operator unpacks
# this, and a path baked in at build time would be wrong for everyone but the machine that built it.
# The docs say to edit it, which is honest, rather than guessing /opt or ~/.local and being wrong.
write_desktop_entry() {
  local out=$1

  cat > "$out/entkube-installer.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=EntKube Installer
Comment=Install the EntKube management plane on a server
# Replace with the absolute path to entkube-installer after unpacking, then copy this file
# to ~/.local/share/applications/ and run: update-desktop-database ~/.local/share/applications
Exec=entkube-installer
Terminal=false
# One main category only: listing two makes the app appear twice in some menus, which
# desktop-file-validate warns about.
Categories=System;
DESKTOP
}

# Builds the four client tools for one platform and lays them out flat next to the GUI, which is the
# layout ToolBundle looks for first.
bundle_client_tools() {
  local config=$1 rid=$2 dest=$3
  mkdir -p "$dest"

  local suffix=""
  [[ "$rid" == win-* ]] && suffix=".exe"

  local name project
  for entry in "entkube:src/EntKube.Cli/EntKube.Cli.csproj" \
               "entkube-mcp:src/EntKube.Mcp/EntKube.Mcp.csproj" \
               "entkube-agent:src/EntKube.Agent/EntKube.Agent.csproj"; do
    name="${entry%%:*}"
    project="${entry##*:}"

    dotnet publish "$project" \
      --configuration "$config" \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=embedded \
      --output "$dest/.stage-$name" \
      --nologo --verbosity quiet >> "$LOG" 2>&1 \
      || fail_with_log "could not build $name for the gui bundle ($config/$rid)."

    mv "$dest/.stage-$name/$name$suffix" "$dest/$name$suffix"
    rm -rf "$dest/.stage-$name"
    chmod +x "$dest/$name$suffix" 2>/dev/null || true
  done

  # The Terraform provider is Go and uses its own platform naming.
  local platform os arch
  platform="$(go_platform_for_rid "$rid")"
  os="${platform%%/*}"
  arch="${platform##*/}"

  (cd tools/terraform-provider-entkube \
    && GOOS="$os" GOARCH="$arch" go build -trimpath \
         -o "../../$dest/terraform-provider-entkube$suffix" .) >> "$LOG" 2>&1 \
    || fail_with_log "could not build the terraform provider for the gui bundle ($platform)."

  chmod +x "$dest/terraform-provider-entkube$suffix" 2>/dev/null || true
}

# ── Terraform provider ───────────────────────────────────────────────────────────────────────────────
build_terraform() {
  local src="tools/terraform-provider-entkube"
  local out_root="artifacts/terraform-provider"

  read_rids terraform "${GO_RIDS_DEFAULT[*]}"
  local rids=("${RESOLVED_RIDS[@]}")

  echo "▸ terraform — Go provider binaries"

  local built=0
  for rid in "${rids[@]}"; do
    local platform os arch out binary
    platform="$(go_platform_for_rid "$rid")"
    os="${platform%%/*}"
    arch="${platform##*/}"
    out="$out_root/${os}_${arch}"
    binary="$out/terraform-provider-entkube"
    [[ "$os" == "windows" ]] && binary="$binary.exe"

    step "$rid ($platform)"
    mkdir -p "$out"
    (cd "$src" && GOOS="$os" GOARCH="$arch" go build -trimpath -o "../../$binary" .) >> "$LOG" 2>&1 \
      || fail_with_log "go build failed for $platform."
    echo "$(du -h "$binary" | cut -f1)"
    built=$((built + 1))
  done

  echo
  summarise "terraform" "$built binaries" "$out_root/"
}

# ── Container image helpers ──────────────────────────────────────────────────────────────────────────
# A multi-platform image exists only as a manifest list in a registry — it cannot be loaded into the local
# daemon, which holds one image per tag. So a verify-only run builds just the first platform; the full set
# is built on push, where it can go straight to the registry.
effective_platforms() {
  local requested=$1
  if ! $PUSH && [[ "$requested" == *,* ]]; then
    echo "${requested%%,*}"
  else
    echo "$requested"
  fi
}

# Which platform the SDK stage compiles on. Empty means "whatever the builder runs on", which is the
# Dockerfiles' own default and what every CI runner wants.
#
# An Apple M-series Mac is the exception, and it is not a small one: its Linux VM runs under
# Hypervisor.framework, which advertises the ARMv9 SME2 CPU features to the guest and then cannot execute
# them. The .NET toolchain detects them, emits them, and dies with SIGILL — `csc exited with code 132`,
# or `Illegal instruction` when the crash takes the whole publish — a few seconds into compiling the
# linux/arm64 leg. It is not intermittent and it is not this repository's code: the same commit compiles
# on a native arm64 runner. Nothing inside the container avoids it either — with hardware intrinsics
# disabled the crash simply moves from csc to the MSBuild worker (containers/podman#28312,
# dotnet/runtime#122608).
#
# The way out is to not run an arm64 compiler at all: compile the amd64 way, under Rosetta, which works
# here, and let `dotnet publish --arch` cross-publish for whichever architecture the image is for. The
# runtime stage still runs natively as its own architecture — it only unpacks tarballs and runs apt.
compiler_platform() {
  [[ -n "$BUILD_PLATFORM_OVERRIDE" ]] && { echo "$BUILD_PLATFORM_OVERRIDE"; return; }
  [[ "$(uname -s)" == "Darwin" && "$(uname -m)" == "arm64" ]] && echo "linux/amd64"
  return 0
}

# The same answer, phrased for someone reading the build output. Worth printing: a compile that does not
# happen on the platform it is producing for is surprising, and finding that out from a build log beats
# finding it out from this comment.
compiler_platform_label() {
  local platform
  platform="$(compiler_platform)"
  [[ -n "$platform" ]] && { echo "$platform"; return; }
  echo "the builder's own platform"
}

build_image() {
  local dockerfile=$1 image=$2 platforms=$3
  local args=(build --file "$dockerfile" --tag "$image" --platform "$platforms")

  local build_platform
  build_platform="$(compiler_platform)"
  [[ -n "$build_platform" ]] && args+=(--build-arg "BUILD_PLATFORM=$build_platform")

  # No provenance attestation: it adds a manifest entry with platform "unknown/unknown", which several
  # container runtimes report as a platform mismatch rather than ignoring. Nothing here consumes it.
  args+=(--provenance=false)
  $PUSH && args+=(--push) || args+=(--load)

  if ! docker buildx "${args[@]}" . >> "$LOG" 2>&1; then
    diagnose_sigill
    fail_with_log "docker buildx build failed for $image."
  fi
}

# A compile that dies with exit code 132 is a crashed process, not a broken build: 132 is 128+4, SIGILL —
# the CPU refused an instruction the .NET toolchain emitted. The same crash surfaces in two shapes, and
# matching only one of them is why this diagnostic used to stay silent on the runs that needed it most:
#
#   MSBuild outlives the compiler      →  `"csc" exited with code 132.`, and buildx then reports exit 1
#   the crash takes the whole publish  →  no csc line at all, and buildx reports `exit code: 132`
#
# compiler_platform() above is what keeps this from happening on the host where it is expected, so
# reaching here means the compile crashed somewhere that workaround does not cover. Print the knob.
diagnose_sigill() {
  # `Illegal instruction` is what the shell inside the build prints when the compiler dies, and it is the
  # one marker present in both shapes; the exit codes are matched too because buildx does not always
  # forward that line.
  grep -qE 'Illegal instruction|exited with code 132|exit code: 132' "$LOG" || return 0

  local where
  where="$(compiler_platform_label)"
  cat >&2 <<EOF

  ── the compiler crashed with SIGILL ────────────────────────────────────────────
  Exit code 132 is SIGILL: the .NET toolchain hit an illegal instruction while
  compiling. It is a host problem — the CPU features the container's VM advertises
  are not ones it can execute — and not a problem with this code.

  This build compiled on: $where

  Try compiling on the other one:
      scripts/release.sh <target> --build-platform linux/amd64
      scripts/release.sh <target> --build-platform linux/arm64
  The image still comes out for whatever --platforms asks for; only the compiler
  moves. Failing that, publish from CI — .github/workflows/deploy.yml builds each
  architecture on a native runner and merges the digests.
  ────────────────────────────────────────────────────────────────────────────────
EOF
}

# `latest` is a manifest list that arm64 and amd64 hosts both pull. Repointing it at a single-platform
# image is invisible here and fails at pull time over there ("no matching manifest for linux/arm64/v8"),
# so a partial build publishes its own tag and leaves the shared one where it is. The one command likely
# to be run after a SIGILL — --platforms linux/amd64 — is exactly the one that would otherwise break it.
should_move_latest() {
  local built=$1 default=$2
  [[ "$built" == "$default" ]] && return 0
  NOTES+=("web: \`latest\` was NOT moved — this image is $built, and $default is what
  hosts pull. Publish from CI (deploy.yml) to update it.")
  return 1
}

# The failure this catches is remote and late: the chart installs, the Secret is right, and every pod then
# reports "no match for platform in manifest". Cheap to check here, expensive to discover in a cluster.
verify_image_platforms() {
  local image=$1 present
  if $PUSH; then
    # Two shapes come back here. A multi-platform push lands as an index and the platforms hang off
    # .Manifest.Manifests. A single-platform push (--platforms linux/amd64) lands as a plain
    # manifest — mediaType .manifest.v2+json, no index — where that range matches nothing and the
    # check below would report "carries no linux/amd64" for an image that is entirely correct.
    # The platform then lives on the image config instead, as .Image.OS (capitalised: it is a Go
    # field name, and .Image.Os fails to evaluate).
    present="$(docker buildx imagetools inspect "$image" \
      --format '{{range .Manifest.Manifests}}{{.Platform.OS}}/{{.Platform.Architecture}} {{end}}' 2>/dev/null || true)"
    if [[ -z "${present// /}" ]]; then
      present="$(docker buildx imagetools inspect "$image" \
        --format '{{.Image.OS}}/{{.Image.Architecture}}' 2>/dev/null || true)"
    fi
  else
    present="$(docker image inspect "$image" --format '{{.Os}}/{{.Architecture}}' 2>/dev/null || true)"
  fi

  case "$present" in
    *linux/amd64*) echo "$present" ;;
    *) fail "$image carries no linux/amd64 (found: ${present:-nothing}).
   Most cluster nodes are amd64 and will fail the pull with \"no match for platform in manifest\"." ;;
  esac
}

# ── Management plane ─────────────────────────────────────────────────────────────────────────────────
# The artifact that behaves like ordinary CI. Nothing about it is version-pinned anywhere, so there is
# nothing to bump and "newest wins" is correct: there is exactly one deployment of it.
#
# The version defaults to the short SHA, matching what deploy.yml tags, so an image built here and one
# built by CI from the same commit carry the same tag.
build_web() {
  local version platforms image
  version="${VERSION:-$(git rev-parse --short HEAD 2>/dev/null || echo dev)}"
  platforms="$(effective_platforms "${PLATFORMS_OVERRIDE:-$WEB_PLATFORMS_DEFAULT}")"
  image="$REGISTRY/$WEB_IMAGE_NAME:$version"

  echo "▸ web — management-plane image"
  echo "    image:     $image"
  echo "    platforms: $platforms"
  echo "    compiler:  $(compiler_platform_label)"

  step "build image"
  build_image "$WEB_DOCKERFILE" "$image" "$platforms"
  echo "$platforms$($PUSH && echo ", pushed")"

  step "verify platforms"
  verify_image_platforms "$image"

  # `latest` is what docker-compose.yml on the server pulls, so a push that updates the SHA tag and not
  # this one builds an image the server never sees.
  if $PUSH; then
    step "tag and push latest"
    # Compared against the canonical set, never against --platforms: the override is the thing being
    # guarded against, so letting it define "complete" would make the guard agree with every caller.
    if should_move_latest "$platforms" "$WEB_PLATFORMS_DEFAULT"; then
      docker buildx imagetools create --tag "$REGISTRY/$WEB_IMAGE_NAME:latest" "$image" >> "$LOG" 2>&1 \
        || fail_with_log "could not tag $image as latest."
      echo "pushed"
    else
      echo "skipped — $platforms only"
    fi
  fi

  echo
  summarise "web" "$version" "$($PUSH && echo "pushed to $REGISTRY" || echo "local image only")"

  if $PUSH; then
    NOTES+=("web: the server still has to pull it — deploy.yml does that over SSH, or run
  docker compose pull && docker compose up -d  on the host.")
  fi
}

# ── Telemetry plane ──────────────────────────────────────────────────────────────────────────────────
# Unlike everything else here, this ships as a container image *plus* a Helm chart, because it runs inside
# a Kubernetes cluster EntKube manages rather than on a laptop or the management-plane server.
#
# The two artifacts are versioned together and must be published together. The chart's appVersion is the
# image tag it deploys, and the catalog entries in ComponentCatalog.cs pin the chart version — so a chart
# published without its image deploys a tag that does not exist, and an image published without its chart
# is never deployed at all. The checks below keep them in step and refuse to proceed when they are not.
build_telemetry() {
  echo "▸ telemetry — image + Helm chart"

  # The chart is the source of truth. Anything else would let the image and the chart disagree about what
  # a release is.
  local chart_version chart_app_version version
  chart_version="$(grep -E '^version:' "$TELEMETRY_CHART_DIR/Chart.yaml" | head -1 | awk '{print $2}')"
  chart_app_version="$(grep -E '^appVersion:' "$TELEMETRY_CHART_DIR/Chart.yaml" | head -1 | awk '{print $2}' | tr -d '"')"
  version="${VERSION:-$chart_version}"

  # The chart deploys image tag = appVersion (values.yaml leaves image.tag empty on purpose), so an
  # appVersion that is not the version being built would deploy a different image than the one produced
  # here.
  step "chart/appVersion agree"
  [[ "$chart_app_version" == "$version" ]] || fail \
    "Chart.yaml appVersion ($chart_app_version) is not the version being built ($version).
   The chart deploys image tag = appVersion, so these must match or the release deploys the wrong image."
  echo "$version"

  # The catalog asks the registry for an exact chart version. If it still names the previous one, the new
  # chart is published and then never used — an install that silently keeps the old behaviour.
  #
  # -A20, not -A6: the entry grew a longer Description and the version line drifted out of range, which
  # made this check silently pass rather than fail — the exact drift it exists to catch. The next entry is
  # ~130 lines away, so the window cannot spill into it.
  step "catalog pins this version"
  local pinned
  pinned="$(grep -A20 'Key = "entkube-telemetry-indexer"' "$CATALOG" \
            | grep 'HelmChartVersion' | head -1 | sed 's/.*"\(.*\)".*/\1/' || true)"
  if [[ -n "$pinned" && "$pinned" != "$version" ]]; then
    fail "ComponentCatalog pins chart version $pinned but this release is $version.
   Update HelmChartVersion on both entkube-telemetry-* entries, or the catalog will keep
   installing $pinned. EntKubeTelemetryCatalogTests covers this too."
  fi
  echo "${pinned:-none pinned}"

  # The catalog's HelmRepoUrl decides where installs look for the chart; publishing elsewhere is invisible
  # until an install fails to find it.
  step "catalog repo URL matches"
  local expected_repo="oci://$REGISTRY/$HELM_REPO_PATH"
  grep -q "HelmRepoUrl = \"$expected_repo\"" "$CATALOG" || fail \
    "ComponentCatalog's HelmRepoUrl does not match $expected_repo.
   Publishing there would put the chart somewhere installs never look."
  echo "$expected_repo"

  local image="$REGISTRY/$TELEMETRY_IMAGE_NAME:$version"
  local chart_repo="oci://$REGISTRY/$HELM_REPO_PATH"
  local platforms
  platforms="$(effective_platforms "${PLATFORMS_OVERRIDE:-$TELEMETRY_PLATFORMS_DEFAULT}")"

  # Cheap, and it fails on exactly the values an install would fail on. Dummy identity values because the
  # chart deliberately refuses to render without them.
  step "lint chart"
  helm lint "$TELEMETRY_CHART_DIR" \
    --set node.tenantId=00000000-0000-0000-0000-000000000001 \
    --set node.clusterId=00000000-0000-0000-0000-000000000002 \
    --set node.ingestToken=lint --set node.queryToken=lint \
    >> "$LOG" 2>&1 || fail_with_log "helm lint failed."
  echo "ok"

  # Helm parses YAML numbers as float64 and Go renders large ones in scientific notation, so a setting
  # like 1000000 reaches the pod as "1e+06". .NET's Int64 binder rejects that and the container dies at
  # startup with an error that names the config key and never mentions Helm. Small numbers render fine, so
  # this only appears once someone raises a limit — which is exactly why it is checked, not remembered.
  step "check rendered values"
  local rendered scientific
  rendered="$(helm template rel "$TELEMETRY_CHART_DIR" \
    --set node.tenantId=00000000-0000-0000-0000-000000000001 \
    --set node.clusterId=00000000-0000-0000-0000-000000000002 \
    --set node.ingestToken=check --set node.queryToken=check \
    --set objectStorage.bucket=b --set objectStorage.accessKey=a --set objectStorage.secretKey=s \
    --set querier.enabled=true 2>>"$LOG")" || fail_with_log "helm template failed."

  scientific="$(printf '%s' "$rendered" | grep -nE 'value: "[0-9.]+e\+[0-9]+"' || true)"
  [[ -z "$scientific" ]] || fail "a value renders in scientific notation and will fail to bind in the pod:
$scientific
   Pipe the value through | int64 in the template."
  echo "ok"

  step "build image"
  build_image "$TELEMETRY_DOCKERFILE" "$image" "$platforms"
  echo "$platforms$($PUSH && echo ", pushed")"

  step "verify platforms"
  verify_image_platforms "$image"

  local out_dir="artifacts/telemetry"
  mkdir -p "$out_dir"
  step "package chart"
  helm package "$TELEMETRY_CHART_DIR" --version "$version" --app-version "$version" \
    --destination "$out_dir" >> "$LOG" 2>&1 || fail_with_log "helm package failed."
  local package="$out_dir/$TELEMETRY_CHART_NAME-$version.tgz"
  [[ -f "$package" ]] || fail "expected $package to exist after packaging."
  echo "$(du -h "$package" | cut -f1)"

  if $PUSH; then
    step "push chart"
    helm push "$package" "$chart_repo" >> "$LOG" 2>&1 \
      || fail_with_log "helm push failed — is there a registry session? Try: az acr login --name ${REGISTRY%%.*}"
    echo "pushed"
  fi

  echo
  summarise "telemetry" "$version" "$($PUSH && echo "image + chart pushed" || echo "$package")"

  if $PUSH; then
    NOTES+=("telemetry: published $version. The catalog entries install it as-is; nothing else to change.
  Existing installs are untouched until an operator upgrades the component.")
  fi
}

# ── Run ──────────────────────────────────────────────────────────────────────────────────────────────
echo "EntKube release"
echo "  targets:  ${SELECTED[*]}"
echo "  registry: $REGISTRY"
echo "  push:     $PUSH"
selected agent || selected cli || selected mcp || selected installer || selected gui \
  && echo "  configs:  ${CONFIGS[*]}"
[[ -n "$RID_FILTER" ]] && echo "  rids:     $RID_FILTER"
echo

# Only when something is actually going to be pushed. A binaries-only run has no registry to talk to, and
# prompting for one would be noise.
if $PUSH && { selected web || selected telemetry; }; then
  ensure_registry_session
  echo
fi

for target in "${SELECTED[@]}"; do
  case "$target" in
    web)       build_web ;;
    telemetry) build_telemetry ;;
    agent)     build_dotnet_binaries agent "src/EntKube.Agent/EntKube.Agent.csproj" "entkube-agent" ;;
    cli)       build_dotnet_binaries cli   "src/EntKube.Cli/EntKube.Cli.csproj"     "entkube" ;;
    mcp)       build_dotnet_binaries mcp   "src/EntKube.Mcp/EntKube.Mcp.csproj"     "entkube-mcp" ;;
    installer) build_dotnet_binaries installer "src/EntKube.Installer/EntKube.Installer.csproj" "entkube-install" ;;
    gui)       build_gui ;;
    terraform) build_terraform ;;
  esac
done

# ── Summary ──────────────────────────────────────────────────────────────────────────────────────────
echo "─────────────────────────────────────────────────────────────────────────"
printf '  %-12s %-14s %s\n' "TARGET" "VERSION" "RESULT"
for row in ${SUMMARY[@]+"${SUMMARY[@]}"}; do
  IFS='|' read -r t v r <<< "$row"
  printf '  %-12s %-14s %s\n' "$t" "$v" "$r"
done
echo "─────────────────────────────────────────────────────────────────────────"

if [[ ${#NOTES[@]} -gt 0 ]]; then
  echo
  for n in ${NOTES[@]+"${NOTES[@]}"}; do echo "  $n"; done
fi

# The per-artifact configuration each binary needs before it does anything. Printed only for the targets
# actually built, so a full release does not bury the summary under four unrelated templates.
if ! $PUSH && { selected web || selected telemetry; }; then
  echo
  echo "  Nothing was published. To publish:  scripts/release.sh ${SELECTED[*]} --push"
fi

if selected agent; then
  cat <<'EOF'

  agent: each binary needs an agent.json beside it (docs/egress-agent.md). The allowlist is the
  security boundary and is local — EntKube cannot widen it.

    { "ServerUrl": "https://entkube.example.com",
      "Token": "<EntKube: Storage -> Egress Agents -> Add Agent>",
      "AllowedHosts": ["identity.example.com", "*.citycloud.com"],
      "AllowedPorts": [443] }
EOF
fi

if selected cli; then
  cat <<'EOF'

  cli: set ENTKUBE_URL and ENTKUBE_TOKEN, then run `entkube --help`. Create a token under the
  tenant's API tokens tab. See docs/cli.md.
EOF
fi

if selected mcp; then
  cat <<'EOF'

  mcp: point an MCP client at the binary with a scoped API token (docs/mcp-server.md). Add
  --allow-write to the args to expose the cluster-changing tools.

    { "mcpServers": { "entkube": {
        "command": "/usr/local/bin/entkube-mcp",
        "env": { "ENTKUBE_URL": "https://entkube.example.com", "ENTKUBE_TOKEN": "ekp_..." } } } }
EOF
fi

if selected gui; then
  cat <<'EOF'

  gui: a desktop installer that performs the same install over SSH, and can put the client tools on
  the machine it runs on. Everything it needs is inside the output directory — the tools/ folder
  must travel with the app. To run it:

    macOS    open "artifacts/gui/Release/osx-arm64/EntKube Installer.app"
    Linux    ./artifacts/gui/Release/linux-x64/entkube-installer
    Windows  artifacts\gui\Release\win-x64\entkube-installer.exe

  See docs/installing.md for what to have ready before you start.
EOF
fi

if selected installer; then
  cat <<'EOF'

  installer: copy the binary for the server's platform to that server and run it. It writes
  docker-compose.yml, Caddyfile and .env, then pulls and starts. Safe to re-run — an existing
  .env is read first and every answer defaults to what is already there. See docs/installing.md.

    scp artifacts/installer/Release/linux-x64/entkube-install server:/tmp/
    ssh server 'sudo /tmp/entkube-install --directory /opt/entkube'
EOF
fi

if selected terraform; then
  cat <<'EOF'

  terraform: see tools/terraform-provider-entkube/README.md for trying one locally with dev_overrides.
EOF
fi
