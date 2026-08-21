#!/usr/bin/env bash
#
# Builds the EntKube CLI for every platform we ship.
#
# Each output is a single self-contained executable: a CLI gets dropped onto a CI
# runner or a laptop, and requiring a .NET runtime first would rule out most of
# the places it is useful.
#
# Usage:
#   scripts/build-mcp.sh                 # all platforms, Debug + Release
#   scripts/build-mcp.sh Release         # one configuration
#   scripts/build-mcp.sh Release osx-arm64
#
# Output: artifacts/cli/<configuration>/<rid>/entkube[.exe]

set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT="src/EntKube.Cli/EntKube.Cli.csproj"
OUT_ROOT="artifacts/cli"

ALL_RIDS=(osx-arm64 osx-x64 linux-x64 win-x64)
ALL_CONFIGS=(Debug Release)

CONFIGS=("${1:-}")
[[ -z "${CONFIGS[0]}" ]] && CONFIGS=("${ALL_CONFIGS[@]}")

RIDS=("${2:-}")
[[ -z "${RIDS[0]}" ]] && RIDS=("${ALL_RIDS[@]}")

echo "Building the EntKube CLI"
echo "  configurations: ${CONFIGS[*]}"
echo "  platforms:      ${RIDS[*]}"
echo

for config in "${CONFIGS[@]}"; do
  for rid in "${RIDS[@]}"; do
    out="$OUT_ROOT/$config/$rid"
    printf '  %-8s %-11s ... ' "$config" "$rid"

    dotnet publish "$PROJECT" \
      --configuration "$config" \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:DebugType=embedded \
      --output "$out" \
      --nologo \
      --verbosity quiet > /dev/null

    binary="$out/entkube"
    [[ "$rid" == win-* ]] && binary="$binary.exe"

    if [[ ! -f "$binary" ]]; then
      echo "FAILED (no binary at $binary)"
      exit 1
    fi

    chmod +x "$binary" 2>/dev/null || true
    echo "$(du -h "$binary" | cut -f1)"
  done
done

echo
echo "Done. Binaries under $OUT_ROOT/"
echo
echo 'Set ENTKUBE_URL and ENTKUBE_TOKEN, then run: entkube --help'
echo "Create a token in EntKube under the tenant's API tokens tab. See docs/cli.md."
