#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Missing .env. Copy .env.example, then add the four Neon project settings.' >&2
  exit 1
fi
if ! docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
  echo 'Docker engine is unavailable.' >&2
  exit 1
fi

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh

target="${1:-all}"
if [ "$target" != all ] && [ "$target" != keycloak ] && \
   [ "$target" != access ] && [ "$target" != profile ] && \
   [ "$target" != notifications ]; then
  echo 'Usage: scripts/bootstrap-neon.sh [all|keycloak|access|profile|notifications]' >&2
  exit 2
fi

bootstrap_keycloak() {
  local password
  password="$(neon_require_value KEYCLOAK_DB_PASSWORD)"
  DB_APP_ROLE=keycloak_app DB_APP_PASSWORD="$password" \
    neon_psql keycloak admin direct -q \
      -v ON_ERROR_STOP=1 \
      -f - < infra/db/bootstrap-keycloak.sql

  local state
  state="$(neon_psql keycloak admin direct -A -t -v ON_ERROR_STOP=1 -c \
    "SELECT CASE WHEN rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls AND NOT EXISTS (SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid) THEN 'ok' ELSE 'invalid' END FROM pg_roles WHERE rolname='keycloak_app'" | tr -d '\r\n')"
  [ "$state" = ok ] || { echo 'keycloak_app is missing or privileged.' >&2; return 1; }
  echo 'Keycloak Neon role and schema ready.'
}

bootstrap_service() {
  local service="$1" prefix migrator app publisher worker state
  prefix="$(printf '%s' "$service" | tr '[:lower:]' '[:upper:]')"
  migrator="$(neon_require_value "${prefix}_MIGRATOR_PASSWORD")"
  app="$(neon_require_value "${prefix}_APP_PASSWORD")"
  publisher="$(neon_require_value "${prefix}_PUBLISHER_PASSWORD")"
  worker="$(neon_require_value "${prefix}_WORKER_PASSWORD")"

  DB_SERVICE="$service" DB_MIGRATOR_PASSWORD="$migrator" DB_APP_PASSWORD="$app" \
    DB_PUBLISHER_PASSWORD="$publisher" DB_WORKER_PASSWORD="$worker" \
    neon_psql "$service" admin direct -q -v ON_ERROR_STOP=1 -f - \
      < infra/db/bootstrap-roles.sql
  DB_SERVICE="$service" neon_psql "$service" migrator direct -q \
    -v ON_ERROR_STOP=1 -f - < infra/db/bootstrap-default-privileges.sql

  state="$(neon_psql "$service" admin direct -A -t -v ON_ERROR_STOP=1 -c "
    SELECT count(*) = 4
      AND bool_and(rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
        AND NOT rolcreaterole AND NOT rolinherit AND NOT rolreplication
        AND NOT rolbypassrls
        AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.member=pg_roles.oid))
    FROM pg_roles
    WHERE rolname IN ('${service}_migrator','${service}_app','${service}_publisher','${service}_worker');" | tr -d '\r\n')"
  [ "$state" = t ] || { echo "$service Neon roles are missing or privileged." >&2; return 1; }
  echo "${service^} Neon roles ready."
}

if [ "$target" = all ] || [ "$target" = keycloak ]; then bootstrap_keycloak; fi
for service in access profile notifications; do
  if [ "$target" = all ] || [ "$target" = "$service" ]; then
    bootstrap_service "$service"
  fi
done
