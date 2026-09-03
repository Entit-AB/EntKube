#!/usr/bin/env bash
#
# Builds the EntKube MCP server for every platform we ship.
#
# Each output is a single self-contained executable, for the same reason as the
# egress agent: an MCP client is configured with one absolute path to one file,
# and asking a user to install a .NET runtime first is a bad first impression.
#
# Usage:
#   scripts/build-mcp.sh                 # all platforms, Debug + Release
#   scripts/build-mcp.sh Release         # one configuration
#   scripts/build-mcp.sh Release osx-arm64
#
# Output: artifacts/mcp/<configuration>/<rid>/entkube-mcp[.exe]

set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT="src/EntKube.Mcp/EntKube.Mcp.csproj"
OUT_ROOT="artifacts/mcp"

ALL_RIDS=(osx-arm64 osx-x64 linux-x64 win-x64)
ALL_CONFIGS=(Debug Release)

CONFIGS=("${1:-}")
[[ -z "${CONFIGS[0]}" ]] && CONFIGS=("${ALL_CONFIGS[@]}")

RIDS=("${2:-}")
[[ -z "${RIDS[0]}" ]] && RIDS=("${ALL_RIDS[@]}")

echo "Building the EntKube MCP server"
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

    binary="$out/entkube-mcp"
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
echo "Configure an MCP client with the binary path and a scoped API token"
echo "(EntKube: tenant -> API tokens -> New token). See docs/mcp-server.md:"
echo
cat <<'JSON'
  {
    "mcpServers": {
      "entkube": {
        "command": "/usr/local/bin/entkube-mcp",
        "env": {
          "ENTKUBE_URL": "https://entkube.example.com",
          "ENTKUBE_TOKEN": "ekp_..."
        }
      }
    }
  }
JSON
echo
echo "Add --allow-write to the args to expose the cluster-changing tools."
