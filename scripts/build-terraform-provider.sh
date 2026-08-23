#!/usr/bin/env bash
#
# Builds the EntKube Terraform provider for every platform we ship.
#
# Output: artifacts/terraform-provider/<os>_<arch>/terraform-provider-entkube[.exe]

set -euo pipefail

cd "$(dirname "$0")/.."

SRC="tools/terraform-provider-entkube"
OUT_ROOT="artifacts/terraform-provider"

PLATFORMS=(darwin/arm64 darwin/amd64 linux/amd64 linux/arm64 windows/amd64)

echo "Building the EntKube Terraform provider"
echo

for platform in "${PLATFORMS[@]}"; do
  os="${platform%%/*}"
  arch="${platform##*/}"
  out="$OUT_ROOT/${os}_${arch}"
  binary="$out/terraform-provider-entkube"
  [[ "$os" == "windows" ]] && binary="$binary.exe"

  printf '  %-16s ... ' "$platform"
  mkdir -p "$out"
  (cd "$SRC" && GOOS="$os" GOARCH="$arch" go build -trimpath -o "../../$binary" .)
  echo "$(du -h "$binary" | cut -f1)"
done

echo
echo "Done. Binaries under $OUT_ROOT/"
echo "See $SRC/README.md for trying one locally with dev_overrides."
