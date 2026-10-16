#!/usr/bin/env bash
set -euo pipefail

: "${SHELL_ORIGIN:?required}"
: "${PROFILE_ORIGIN:?required}"
: "${ACCESS_ORIGIN:?required}"
: "${IDENTITY_ORIGIN:?required}"

for origin in "$SHELL_ORIGIN" "$PROFILE_ORIGIN" "$ACCESS_ORIGIN"; do
  status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$origin/health/live")"
  [[ "$status" == "200" ]] || {
    echo "A BFF health endpoint is unavailable (status $status)." >&2
    exit 1
  }
done

for path in /admin /admin/ /realms/master /realms/master/.well-known/openid-configuration /internal/v1/authz/resolve; do
  status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$IDENTITY_ORIGIN$path")"
  [[ "$status" == "404" ]] || {
    echo "A blocked identity path was externally reachable (status $status)." >&2
    exit 1
  }
done

for origin in "$SHELL_ORIGIN" "$PROFILE_ORIGIN" "$ACCESS_ORIGIN"; do
  status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' "$origin/internal/v1/authz/resolve")"
  [[ "$status" == "404" ]] || {
    echo "An internal API path was externally reachable (status $status)." >&2
    exit 1
  }
done

echo "Public boundary verification passed."
