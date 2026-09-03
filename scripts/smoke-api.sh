#!/usr/bin/env bash
#
# End-to-end smoke test for the public API, the CLI and the MCP server against a
# RUNNING EntKube instance.
#
# These three clients are otherwise only covered by unit tests and stubs, so this is
# what proves the wiring — auth, scopes, serialization, the 503 contract — actually
# holds against the real application.
#
# Usage:
#   ENTKUBE_URL=http://127.0.0.1:5220 ENTKUBE_TOKEN=ekp_... scripts/smoke-api.sh
#
# The token needs: fleet:read apps:read ops:read
# Optionally also a read-only token as ENTKUBE_READONLY_TOKEN (scopes: ops:read) to
# exercise scope enforcement.
#
# Exits non-zero on the first failed expectation.

set -uo pipefail

cd "$(dirname "$0")/.."

: "${ENTKUBE_URL:?set ENTKUBE_URL}"
: "${ENTKUBE_TOKEN:?set ENTKUBE_TOKEN}"

AUTH="Authorization: Bearer $ENTKUBE_TOKEN"
PASS=0
FAIL=0

check() {                       # check <label> <expected> <actual>
  if [[ "$2" == "$3" ]]; then
    printf '  ok    %-52s %s\n' "$1" "$3"
    PASS=$((PASS + 1))
  else
    printf '  FAIL  %-52s expected %s, got %s\n' "$1" "$2" "$3"
    FAIL=$((FAIL + 1))
  fi
}

status() {                      # status <method> <path> [token]
  curl -sS -o /dev/null -w '%{http_code}' -m 30 \
    -X "$1" -H "Authorization: Bearer ${3:-$ENTKUBE_TOKEN}" "$ENTKUBE_URL$2"
}

echo "EntKube API smoke test against $ENTKUBE_URL"
echo
echo "Authentication"
check "no token is refused" 401 \
  "$(curl -sS -o /dev/null -w '%{http_code}' -m 30 "$ENTKUBE_URL/api/v1/whoami")"
check "an unknown token is refused" 401 "$(status GET /api/v1/whoami ekp_definitely_not_a_token)"
check "a valid token is accepted" 200 "$(status GET /api/v1/whoami)"

echo
echo "Read endpoints"
for ep in clusters apps deployments advisor/findings incidents upgrades rollouts; do
  check "GET /$ep" 200 "$(status GET "/api/v1/$ep")"
done

echo
echo "Sweep-backed endpoints answer 503 until a sweep has run"
echo "  (503 means UNMEASURED, which is not the same as nothing being wrong —"
echo "   a 200 with an empty list here would be a false all-clear)"
for ep in drift supply-chain cost disaster-recovery; do
  code="$(status GET "/api/v1/$ep")"
  if [[ "$code" == "503" || "$code" == "200" ]]; then
    printf '  ok    %-52s %s\n' "GET /$ep" "$code"
    PASS=$((PASS + 1))
  else
    printf '  FAIL  %-52s expected 503 or 200, got %s\n' "GET /$ep" "$code"
    FAIL=$((FAIL + 1))
  fi
done

if [[ -n "${ENTKUBE_READONLY_TOKEN:-}" ]]; then
  echo
  echo "Scope enforcement (using ENTKUBE_READONLY_TOKEN, ops:read only)"
  check "ops:read may read advisor findings" 200 \
    "$(status GET /api/v1/advisor/findings "$ENTKUBE_READONLY_TOKEN")"
  check "ops:read may NOT read clusters" 403 \
    "$(status GET /api/v1/clusters "$ENTKUBE_READONLY_TOKEN")"
  check "ops:read may NOT trigger a sync" 403 \
    "$(status POST /api/v1/deployments/00000000-0000-0000-0000-000000000000/sync \
       "$ENTKUBE_READONLY_TOKEN")"
fi

CLI="src/EntKube.Cli/bin/Debug/net10.0/entkube"
if [[ -x "$CLI" ]]; then
  echo
  echo "CLI"
  "$CLI" clusters list > /dev/null 2>&1
  check "entkube clusters list succeeds" 0 "$?"

  # An unswept fleet must NOT look clean to a pipeline.
  "$CLI" drift > /dev/null 2>&1
  code=$?
  if [[ "$code" -ne 0 ]]; then
    printf '  ok    %-52s %s\n' "entkube drift on an unswept fleet is non-zero" "$code"
    PASS=$((PASS + 1))
  else
    printf '  FAIL  %-52s exit 0 would let CI read unmeasured as clean\n' "entkube drift"
    FAIL=$((FAIL + 1))
  fi
fi

MCP="src/EntKube.Mcp/bin/Release/net10.0/entkube-mcp"
if [[ -x "$MCP" ]]; then
  echo
  echo "MCP server"
  out="$(printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"entkube_whoami","arguments":{}}}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"entkube_sync_deployment","arguments":{"deploymentId":"00000000-0000-0000-0000-000000000000"}}}' \
    | "$MCP" 2>/dev/null)"

  check "handshake and two calls answered" 3 "$(printf '%s\n' "$out" | grep -c '"jsonrpc"')"
  # Matched unquoted: the payload is a JSON string INSIDE the JSON-RPC envelope, so
  # the field arrives escaped as \"tenantId\" rather than "tenantId".
  check "whoami reaches the API" 1 \
    "$(printf '%s\n' "$out" | grep -c 'tenantId')"
  # Default read-only: the write tool must be refused locally, whatever the token allows.
  check "write tool refused in read-only mode" 1 \
    "$(printf '%s\n' "$out" | grep -c 'allow-write')"
fi

echo
echo "$PASS passed, $FAIL failed"
[[ "$FAIL" -eq 0 ]]
