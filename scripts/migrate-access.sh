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
# Stream the reviewed migration over stdin so the password never reaches argv.
docker compose --env-file .env -f infra/compose/docker-compose.yml exec -T postgres-access \
  sh -c 'export PGPASSWORD="$DB_MIGRATOR_PASSWORD"; exec psql -X -q -v ON_ERROR_STOP=1 -1 -U access_migrator -d access' \
  < services/access/Excellore.Access.Infrastructure/Migrations/001_initial.sql

echo 'Access initial migration applied.'
