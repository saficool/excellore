#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Missing .env. Run scripts/dev-up.sh first.' >&2
  exit 1
fi

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh

compose=(docker compose --env-file .env -f infra/compose/docker-compose.yml)
running="$("${compose[@]}" ps --status running --services)"

for service in keycloak valkey rabbitmq objectstore mailpit; do
  if ! printf '%s\n' "$running" | grep -Fxq "$service"; then
    echo "$service is not running." >&2
    exit 1
  fi
done
for service in keycloak valkey rabbitmq; do
  container_id="$("${compose[@]}" ps -q "$service")"
  health="$(docker inspect --format '{{.State.Health.Status}}' "$container_id")"
  if [ "$health" != healthy ]; then
    echo "$service is not healthy." >&2
    exit 1
  fi
done

curl --silent --show-error --fail --max-time 10 \
  http://127.0.0.1:9000/health/ready >/dev/null
curl --silent --show-error --fail --max-time 10 \
  http://127.0.0.1:8025/api/v1/info >/dev/null
s3_response="$(curl --silent --show-error --max-time 10 --write-out '\n%{http_code}' http://127.0.0.1:8333/)"
s3_status="${s3_response##*$'\n'}"
case "$s3_status" in
  200|403) ;;
  *) echo "Object store S3 endpoint returned $s3_status." >&2; exit 1 ;;
esac

check_role() {
  local service="$1" role="$2" endpoint="$3" state
  state="$(neon_psql "$service" "$role" "$endpoint" -A -t -v ON_ERROR_STOP=1 -c "
    SELECT CASE WHEN r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolcreatedb
      AND NOT r.rolcreaterole AND NOT r.rolinherit AND NOT r.rolreplication
      AND NOT r.rolbypassrls
      AND NOT EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relowner = r.oid
          AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
      ) THEN 'ok' ELSE 'invalid' END
    FROM pg_roles r WHERE r.rolname = current_user;" | tr -d '\r\n')"
  if [ "$state" != ok ]; then
    echo "${service}_${role} is missing, privileged, or owns database objects." >&2
    exit 1
  fi
}

for service in access profile notifications; do
  check_role "$service" app pooled
  check_role "$service" publisher pooled
  if [ "$service" = access ]; then
    check_role "$service" worker direct
  else
    check_role "$service" worker pooled
  fi
done

keycloak_state="$(neon_psql keycloak app direct -A -t -v ON_ERROR_STOP=1 -c "
  SELECT CASE WHEN rolcanlogin AND NOT rolsuper AND NOT rolcreatedb
    AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls
    THEN 'ok' ELSE 'invalid' END
  FROM pg_roles WHERE rolname = current_user;" | tr -d '\r\n')"
[ "$keycloak_state" = ok ] || { echo 'keycloak_app is missing or privileged.' >&2; exit 1; }

echo 'Local stack healthy; Neon runtime roles have no elevated PostgreSQL privileges.'
