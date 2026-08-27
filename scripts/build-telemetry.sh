#!/usr/bin/env bash
#
# Builds — and optionally publishes — the in-cluster telemetry plane.
#
# Unlike the agent, the CLI and the MCP server, this ships as a container image plus a Helm chart, because
# it runs inside a Kubernetes cluster EntKube manages rather than on someone's laptop or jump host.
#
# The two artifacts are versioned together and must be published together. The chart's appVersion is the
# image tag it deploys, and the catalog entries in ComponentCatalog.cs pin the chart version — so a chart
# published without its image deploys a tag that does not exist, and an image published without its chart
# is never deployed at all. This script keeps them in step and refuses to proceed when they are not.
#
# Usage:
#   scripts/build-telemetry.sh                    # build image + package chart locally
#   scripts/build-telemetry.sh --push             # ...and push both to the registry
#   scripts/build-telemetry.sh --version 0.2.0    # override the version (default: chart's own)
#   scripts/build-telemetry.sh --registry my.acr.io --push
#
# Output: artifacts/telemetry/entkube-telemetry-<version>.tgz, plus a local image tag.
#
# Pushing needs an authenticated registry session first:
#   az acr login --name entit
#   # or: docker login entit.azurecr.io && helm registry login entit.azurecr.io

set -euo pipefail

cd "$(dirname "$0")/.."

REGISTRY="entit.azurecr.io"
CHART_DIR="charts/entkube-telemetry"
CHART_NAME="entkube-telemetry"
IMAGE_NAME="entkube-telemetry"
DOCKERFILE="src/EntKube.TelemetryNode/Dockerfile"
OUT_DIR="artifacts/telemetry"
# Where charts live under the registry. The catalog's HelmRepoUrl must match this exactly.
HELM_REPO_PATH="helm"

PUSH=false
VERSION=""
PLATFORMS="linux/amd64"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --push)      PUSH=true; shift ;;
    --version)   VERSION="$2"; shift 2 ;;
    --registry)  REGISTRY="$2"; shift 2 ;;
    --platforms) PLATFORMS="$2"; shift 2 ;;
    -h|--help)   sed -n '2,25p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *)           echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

# ── Version ──────────────────────────────────────────────────────────────────────────────────────────
# The chart is the source of truth. Anything else would let the image and the chart disagree about what
# a release is.
CHART_VERSION="$(grep -E '^version:' "$CHART_DIR/Chart.yaml" | head -1 | awk '{print $2}')"
CHART_APP_VERSION="$(grep -E '^appVersion:' "$CHART_DIR/Chart.yaml" | head -1 | awk '{print $2}' | tr -d '"')"
VERSION="${VERSION:-$CHART_VERSION}"

# ── Consistency checks, before anything is built ─────────────────────────────────────────────────────
fail() { echo "error: $*" >&2; exit 1; }

# The chart deploys image tag = appVersion (values.yaml leaves image.tag empty on purpose), so an
# appVersion that is not the version being built would deploy a different image than the one produced here.
[[ "$CHART_APP_VERSION" == "$VERSION" ]] || fail \
  "Chart.yaml appVersion ($CHART_APP_VERSION) is not the version being built ($VERSION).
   The chart deploys image tag = appVersion, so these must match or the release deploys the wrong image."

# The catalog asks the registry for an exact chart version. If it still names the previous one, the new
# chart is published and then never used — an install that silently keeps the old behaviour.
CATALOG="src/EntKube.Web/Services/ComponentCatalog.cs"
PINNED="$(grep -A6 'Key = "entkube-telemetry-indexer"' "$CATALOG" \
          | grep 'HelmChartVersion' | head -1 | sed 's/.*"\(.*\)".*/\1/' || true)"
if [[ -n "$PINNED" && "$PINNED" != "$VERSION" ]]; then
  fail "ComponentCatalog pins chart version $PINNED but this release is $VERSION.
   Update HelmChartVersion on both entkube-telemetry-* entries, or the catalog will keep
   installing $PINNED. EntKubeTelemetryCatalogTests covers this too."
fi

# The catalog's HelmRepoUrl decides where installs look for the chart; publishing elsewhere is invisible
# until an install fails to find it.
EXPECTED_REPO="oci://$REGISTRY/$HELM_REPO_PATH"
if ! grep -q "HelmRepoUrl = \"$EXPECTED_REPO\"" "$CATALOG"; then
  fail "ComponentCatalog's HelmRepoUrl does not match $EXPECTED_REPO.
   Publishing there would put the chart somewhere installs never look."
fi

IMAGE="$REGISTRY/$IMAGE_NAME:$VERSION"
CHART_REPO="oci://$REGISTRY/$HELM_REPO_PATH"

echo "Building the EntKube telemetry plane"
echo "  version:   $VERSION"
echo "  image:     $IMAGE"
echo "  chart:     $CHART_REPO/$CHART_NAME:$VERSION"
echo "  platforms: $PLATFORMS"
echo "  push:      $PUSH"
echo

# ── Chart lint ───────────────────────────────────────────────────────────────────────────────────────
# Cheap, and it fails on exactly the values an install would fail on. Dummy identity values because the
# chart deliberately refuses to render without them.
printf '  %-22s ... ' "lint chart"
helm lint "$CHART_DIR" \
  --set node.tenantId=00000000-0000-0000-0000-000000000001 \
  --set node.clusterId=00000000-0000-0000-0000-000000000002 \
  --set node.ingestToken=lint --set node.queryToken=lint \
  > /dev/null || fail "helm lint failed; run it directly to see why."
echo "ok"

# ── Image ────────────────────────────────────────────────────────────────────────────────────────────
printf '  %-22s ... ' "build image"
BUILD_ARGS=(--file "$DOCKERFILE" --tag "$IMAGE" --platform "$PLATFORMS")
$PUSH && BUILD_ARGS+=(--push)
docker build "${BUILD_ARGS[@]}" . > /dev/null 2>&1 \
  || fail "docker build failed; re-run without redirecting output to see why."
echo "$($PUSH && echo "built and pushed" || echo "built")"

# ── Chart ────────────────────────────────────────────────────────────────────────────────────────────
mkdir -p "$OUT_DIR"
printf '  %-22s ... ' "package chart"
helm package "$CHART_DIR" --version "$VERSION" --app-version "$VERSION" --destination "$OUT_DIR" > /dev/null
PACKAGE="$OUT_DIR/$CHART_NAME-$VERSION.tgz"
[[ -f "$PACKAGE" ]] || fail "expected $PACKAGE to exist after packaging."
echo "$(du -h "$PACKAGE" | cut -f1)"

if $PUSH; then
  printf '  %-22s ... ' "push chart"
  helm push "$PACKAGE" "$CHART_REPO" > /dev/null 2>&1 \
    || fail "helm push failed — is there a registry session? Try: az acr login --name ${REGISTRY%%.*}"
  echo "pushed"
fi

echo
if $PUSH; then
  echo "Published $VERSION. The catalog entries install it as-is; nothing else to change."
else
  echo "Built locally. Nothing was published."
  echo "  image:  $IMAGE"
  echo "  chart:  $PACKAGE"
  echo
  echo "To publish, authenticate and re-run with --push:"
  echo "  az acr login --name ${REGISTRY%%.*}"
  echo "  scripts/build-telemetry.sh --push"
fi
