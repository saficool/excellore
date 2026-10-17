#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ ! -f .env ]; then
  echo 'Run scripts/dev-up.sh before applying the Profile migration.' >&2
  exit 1
fi

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh

psql_migrator() { neon_psql profile migrator direct "$@"; }
apply_file() {
  psql_migrator -q -v ON_ERROR_STOP=1 -1 < \
    "services/profile/Excellore.Profile.Infrastructure/Migrations/$1"
}

exists="$(psql_migrator -A -t -v ON_ERROR_STOP=1 \
  -c "SELECT to_regclass('public.user_profiles') IS NOT NULL" | tr -d '\r\n')"
if [ "$exists" = f ]; then
  apply_file 001_initial.sql
elif [ "$exists" != t ]; then
  echo 'Could not determine Profile migration state.' >&2
  exit 1
fi

apply_file 002_worker_projections.sql
apply_file 003_identity_mirror.sql

apply_once() {
  local migration="$1" condition="$2" applied
  applied="$(psql_migrator -A -t -v ON_ERROR_STOP=1 -c "$condition" | tr -d '\r\n')"
  if [ "$applied" = f ]; then
    apply_file "$migration"
  elif [ "$applied" != t ]; then
    echo "Could not determine Profile migration state for $migration." >&2
    exit 1
  fi
}

apply_once 004_tenant_erasure.sql "SELECT to_regclass('public.profile_tenant_tombstones') IS NOT NULL"
apply_once 005_tenant_audit_archive.sql "SELECT to_regclass('public.profile_audit_archive') IS NOT NULL"
apply_once 006_personal_exports.sql "SELECT EXISTS (SELECT 1 FROM pg_policies WHERE schemaname='public' AND tablename='tenant_member_profiles' AND policyname='profile_worker_personal_export')"
apply_once 007_user_erasure.sql "SELECT to_regclass('public.profile_user_erasure_requests') IS NOT NULL"
echo 'Profile Neon database migrations applied.'
