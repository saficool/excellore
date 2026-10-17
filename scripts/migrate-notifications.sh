#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1
if [ ! -f .env ]; then
  echo 'Run scripts/dev-up.sh before applying the Notifications migration.' >&2
  exit 1
fi
exists="$(docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-notifications \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -A -t -v ON_ERROR_STOP=1 -U notifications_migrator -d notifications -c "SELECT to_regclass('\''public.notification_deliveries'\'') IS NOT NULL"' | tr -d '\r\n')"
if [ "$exists" = f ]; then
  docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-notifications \
    sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U notifications_migrator -d notifications' \
    < services/notifications/Excellore.Notifications.Infrastructure/Migrations/001_initial.sql
elif [ "$exists" != t ]; then
  echo 'Could not determine Notifications migration state.' >&2
  exit 1
fi
echo 'Notifications database migration applied.'
