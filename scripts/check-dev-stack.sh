#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Missing .env. Run scripts/dev-up.sh first.' >&2
  exit 1
fi

compose=(docker compose --env-file .env -f infra/compose/docker-compose.yml)
running="$("${compose[@]}" ps --status running --services)"

for service in keycloak-db keycloak postgres-access postgres-profile postgres-notifications valkey rabbitmq objectstore mailpit; do
  if ! printf '%s\n' "$running" | grep -Fxq "$service"; then
    echo "$service is not running." >&2
    exit 1
  fi
done
for service in keycloak-db keycloak postgres-access postgres-profile postgres-notifications valkey rabbitmq; do
  container_id="$("${compose[@]}" ps -q "$service")"
  health="$(docker inspect --format '{{.State.Health.Status}}' "$container_id")"
  if [ "$health" != healthy ]; then
    echo "$service is not healthy." >&2
    exit 1
  fi
done

if ! curl --silent --show-error --fail --max-time 10 http://127.0.0.1:9000/health/ready >/dev/null; then
  echo 'Keycloak readiness endpoint failed.' >&2
  exit 1
fi
if ! curl --silent --show-error --fail --max-time 10 http://127.0.0.1:8025/api/v1/info >/dev/null; then
  echo 'Mailpit API failed.' >&2
  exit 1
fi
s3_response="$(curl --silent --show-error --max-time 10 --write-out '\n%{http_code}' http://127.0.0.1:8333/)"
s3_status="${s3_response##*$'\n'}"
case "$s3_status" in
  200|403) ;;
  *) echo "Object store S3 endpoint returned $s3_status." >&2; exit 1 ;;
esac

for service in access profile notifications; do
  role_check="$("${compose[@]}" exec -T "postgres-$service" psql -X -A -t -U postgres -d "$service" -c "
    SELECT CASE WHEN r.rolcanlogin
      AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
      AND NOT r.rolreplication AND NOT r.rolbypassrls
      AND NOT EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relowner = r.oid AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
      ) THEN 'ok' ELSE 'invalid' END
    FROM pg_roles r WHERE r.rolname = '${service}_app';")"
  if [ "$role_check" != ok ]; then
    echo "${service}_app is missing, privileged, or owns database objects." >&2
    exit 1
  fi
  migrator_check="$("${compose[@]}" exec -T "postgres-$service" psql -X -A -t -U postgres -d "$service" -c "
    SELECT CASE WHEN rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
      THEN 'ok' ELSE 'invalid' END FROM pg_roles
    WHERE rolname = '${service}_migrator';")"
  if [ "$migrator_check" != ok ]; then
    echo "${service}_migrator is missing or privileged." >&2
    exit 1
  fi
  publisher_check="$("${compose[@]}" exec -T "postgres-$service" psql -X -A -t -U postgres -d "$service" -c "
    SELECT CASE WHEN r.rolcanlogin AND NOT r.rolinherit
      AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
      AND NOT r.rolreplication AND NOT r.rolbypassrls
      AND NOT EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relowner = r.oid AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
      ) THEN 'ok' ELSE 'invalid' END
    FROM pg_roles r WHERE r.rolname = '${service}_publisher';")"
  if [ "$publisher_check" != ok ]; then
    echo "${service}_publisher is missing, privileged, or owns database objects." >&2
    exit 1
  fi
  worker_check="$("${compose[@]}" exec -T "postgres-$service" psql -X -A -t -U postgres -d "$service" -c "
    SELECT CASE WHEN r.rolcanlogin AND NOT r.rolinherit
      AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole
      AND NOT r.rolreplication AND NOT r.rolbypassrls
      AND NOT EXISTS (
        SELECT 1 FROM pg_class c
        WHERE c.relowner = r.oid AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
      ) THEN 'ok' ELSE 'invalid' END
    FROM pg_roles r WHERE r.rolname = '${service}_worker';")"
  if [ "$worker_check" != ok ]; then
    echo "${service}_worker is missing, privileged, or owns database objects." >&2
    exit 1
  fi
done

echo 'Local stack healthy; service app, publisher, and worker roles have no bypass or ownership.'
