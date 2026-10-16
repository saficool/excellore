#!/usr/bin/env bash
set -euo pipefail

# Provider rotation jobs emit metadata only. Never place old/new credentials in this evidence.

: "${ROTATION_EVIDENCE_FILE:?required}"
: "${MAX_ROTATION_SECONDS:=900}"

jq -e --argjson max "$MAX_ROTATION_SECONDS" '
  type == "object" and
  .environment == "staging" and
  .containsSecretMaterial == false and
  (.drillId | type == "string" and length > 0 and length <= 80) and
  (.rotations | type == "array" and length == 2) and
  ([.rotations[].kind] | sort == ["bff-client-secret", "database-credential"]) and
  all(.rotations[];
    (.startedAt | fromdateiso8601) as $started |
    (.finishedAt | fromdateiso8601) as $finished |
    $finished >= $started and ($finished - $started) <= $max and
    .externalSecretReady == true and
    .workloadRolledOut == true and
    .newCredentialAccepted == true and
    .oldCredentialRejected == true and
    .workloadHealthy == true)
' "$ROTATION_EVIDENCE_FILE" >/dev/null

echo "Staging secret-rotation drill evidence satisfies the Excellore rotation contract."
