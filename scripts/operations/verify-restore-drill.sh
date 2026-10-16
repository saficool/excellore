#!/usr/bin/env bash
set -euo pipefail

# The cloud adapter emits this evidence after isolated staging restores. This validator receives
# timings and opaque resource IDs only; database URLs, tenant IDs, and row data are forbidden.

: "${RESTORE_EVIDENCE_FILE:?required}"
: "${MAX_RPO_SECONDS:=300}"
: "${MAX_RTO_SECONDS:=7200}"

jq -e --argjson maxRpo "$MAX_RPO_SECONDS" --argjson maxRto "$MAX_RTO_SECONDS" '
  type == "object" and
  .environment == "staging" and
  .containsSensitiveData == false and
  (.drillId | type == "string" and length > 0 and length <= 80) and
  (.completedAt | fromdateiso8601 | type == "number") and
  (.fullRestores | type == "array" and length == 3) and
  ([.fullRestores[].database] | sort == ["access", "keycloak", "profile"]) and
  all(.fullRestores[];
    .passed == true and
    (.rpoSeconds | type == "number" and . >= 0 and . <= $maxRpo) and
    (.rtoSeconds | type == "number" and . > 0 and . <= $maxRto) and
    .forcedRlsVerified == true) and
  (.tenantRestores | type == "array" and length == 2) and
  ([.tenantRestores[].service] | sort == ["access", "profile"]) and
  all(.tenantRestores[];
    .passed == true and
    (.durationSeconds | type == "number" and . > 0 and . <= $maxRto) and
    .crossTenantIsolationVerified == true and
    .tenantRemainedSuspended == true)
' "$RESTORE_EVIDENCE_FILE" >/dev/null

echo "Staging restore drill evidence satisfies the Excellore recovery contract."
