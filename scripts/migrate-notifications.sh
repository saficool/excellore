#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Run scripts/dev-up.sh before applying the Notifications migration.' >&2
  exit 1
fi

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh

psql_migrator() { neon_psql notifications migrator direct "$@"; }
apply_file() {
  psql_migrator -q -v ON_ERROR_STOP=1 -1 < \
    "services/notifications/Excellore.Notifications.Infrastructure/Migrations/$1"
}

exists="$(psql_migrator -A -t -v ON_ERROR_STOP=1 \
  -c "SELECT to_regclass('public.notification_deliveries') IS NOT NULL" | tr -d '\r\n')"
if [ "$exists" = f ]; then
  apply_file 001_initial.sql
elif [ "$exists" != t ]; then
  echo 'Could not determine Notifications migration state.' >&2
  exit 1
fi

provider_index_exists="$(psql_migrator -A -t -v ON_ERROR_STOP=1 \
  -c "SELECT to_regclass('public.notification_provider_index') IS NOT NULL" | tr -d '\r\n')"
if [ "$provider_index_exists" = f ]; then
  apply_file 002_provider_webhooks.sql
elif [ "$provider_index_exists" != t ]; then
  echo 'Could not determine Notifications provider migration state.' >&2
  exit 1
fi
echo 'Notifications Neon database migrations applied.'
