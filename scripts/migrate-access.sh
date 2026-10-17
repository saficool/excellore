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

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh

psql_migrator() { neon_psql access migrator direct "$@"; }
apply_file() {
  psql_migrator -q -v ON_ERROR_STOP=1 -1 < \
    "services/access/Excellore.Access.Infrastructure/Migrations/$1"
}

schema_exists="$(psql_migrator -A -t -v ON_ERROR_STOP=1 \
  -c "SELECT to_regclass('public.tenants') IS NOT NULL" | tr -d '\r\n')"
if [ "$schema_exists" = f ]; then
  apply_file 001_initial.sql
elif [ "$schema_exists" != t ]; then
  echo 'Could not determine Access migration state.' >&2
  exit 1
fi

for migration in \
  002_register_apps.sql 003_membership_index.sql 004_outbox_publisher.sql \
  005_audit_append_only.sql 006_custom_role_lifecycle.sql \
  007_group_role_assignment_uniqueness.sql 008_membership_identity.sql \
  009_invitation_delivery_state.sql; do
  apply_file "$migration"
done

apply_once() {
  local migration="$1" condition="$2" applied
  applied="$(psql_migrator -A -t -v ON_ERROR_STOP=1 -c "$condition" | tr -d '\r\n')"
  if [ "$applied" = f ]; then
    apply_file "$migration"
  elif [ "$applied" != t ]; then
    echo "Could not determine Access migration state for $migration." >&2
    exit 1
  fi
}

apply_once 010_ownership_transfers.sql "SELECT to_regclass('public.ownership_transfers') IS NOT NULL"
apply_once 011_support_grants_rls.sql "SELECT relforcerowsecurity FROM pg_class WHERE oid = 'public.support_grants'::regclass"
apply_once 012_audit_export_requests.sql "SELECT to_regclass('public.audit_exports') IS NOT NULL"
apply_file 013_worker_expiry.sql
apply_file 014_worker_identity_drift.sql
apply_once 015_role_assignment_batches.sql "SELECT to_regclass('public.role_assignment_batches') IS NOT NULL"
apply_once 016_tenant_deletion_dispatch.sql "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='tenants' AND column_name='deletion_started_at')"
apply_once 017_tenant_erasure_receipts.sql "SELECT to_regclass('public.tenant_tombstones') IS NOT NULL"
apply_once 018_tenant_erasure_finalization.sql "SELECT to_regclass('public.audit_events_archive') IS NOT NULL"
apply_once 019_tenant_export_requests.sql "SELECT to_regclass('public.tenant_exports') IS NOT NULL"
apply_once 020_tenant_export_worker.sql "SELECT has_column_privilege('access_worker','tenant_exports','status','UPDATE')"
apply_once 021_user_erasure.sql "SELECT to_regclass('public.user_erasure_requests') IS NOT NULL"
echo 'Access Neon database migrations applied.'
