#!/usr/bin/env bash
set -euo pipefail

# Provider adapters write one JSON record per managed database after querying the provider API.
# This script validates the common contract without receiving cloud credentials itself.

: "${BACKUP_METADATA_FILE:?required}"
: "${MAX_RPO_SECONDS:=300}"

jq -e --argjson max "$MAX_RPO_SECONDS" '
  type == "array" and length == 3 and
  ([.[].name] | unique | length == 3) and
  all(.[];
    (.name | IN("keycloak", "access", "profile")) and
    .pitrEnabled == true and
    (.oldestRestorePoint | type == "string") and
    (.latestRestorePoint | type == "string") and
    ((.oldestRestorePoint | fromdateiso8601) <= (.latestRestorePoint | fromdateiso8601)) and
    (.observedLagSeconds | type == "number") and
    .observedLagSeconds >= 0 and .observedLagSeconds <= $max and
    .encrypted == true and .privateEndpoint == true)
' "$BACKUP_METADATA_FILE" >/dev/null

echo "Managed database backup metadata satisfies the Excellore RPO contract."
