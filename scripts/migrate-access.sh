#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Run scripts/dev-up.sh before applying the Access migration.' >&2
  exit 1
fi
if ! docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
  echo 'Docker engine is unavailable.' >&2
  exit 1
fi

# The container already has the local migrator password in its environment.
# Stream migrations over stdin so the password never reaches argv.
schema_exists="$(docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -A -t -v ON_ERROR_STOP=1 -U access_migrator -d access -c "SELECT to_regclass('\''public.tenants'\'') IS NOT NULL"' | tr -d '\r\n')"
if [ "$schema_exists" = f ]; then
  docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
    sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
    < services/access/Excellore.Access.Infrastructure/Migrations/001_initial.sql
elif [ "$schema_exists" != t ]; then
  echo 'Could not determine Access migration state.' >&2
  exit 1
fi

docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/002_register_apps.sql
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/003_membership_index.sql
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/004_outbox_publisher.sql
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/005_audit_append_only.sql
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/006_custom_role_lifecycle.sql
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/007_group_role_assignment_uniqueness.sql
echo 'Access database migrations applied.'
