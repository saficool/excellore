#!/usr/bin/env bash
set -euo pipefail

# Restore active tenant rows from a provider-created PITR scratch database.
# Audit tables, tombstones, outbox/inbox rows, and global identity/profile rows are intentionally
# excluded. Run only while the tenant is suspended in Access.

: "${SCRATCH_DATABASE_URL:?required}"
: "${TARGET_DATABASE_URL:?required}"
: "${TENANT_ID:?required}"
: "${SERVICE:?required: access or profile}"
: "${WORK_DIR:?required: absolute path on an encrypted temporary volume}"
: "${TENANT_SUSPENDED_CONFIRMED:?required: set to yes after checking Access}"
: "${ENCRYPTED_WORK_DIR_CONFIRMED:?required: set to yes after checking the volume}"

[[ "$TENANT_SUSPENDED_CONFIRMED" == "yes" ]] || {
  echo "Tenant suspension has not been confirmed." >&2
  exit 1
}
[[ "$ENCRYPTED_WORK_DIR_CONFIRMED" == "yes" ]] || {
  echo "Encrypted temporary storage has not been confirmed." >&2
  exit 1
}
[[ "$TENANT_ID" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || {
  echo "TENANT_ID is not a UUID." >&2
  exit 1
}
[[ "$WORK_DIR" == /* && "$WORK_DIR" != "/" ]] || {
  echo "WORK_DIR must be a non-root absolute path." >&2
  exit 1
}
[[ -d "$WORK_DIR" ]] || {
  echo "WORK_DIR must already exist on an encrypted temporary volume." >&2
  exit 1
}
chmod 700 "$WORK_DIR"

case "$SERVICE" in
  access)
    tables=(
      tenant_settings roles role_permissions tenant_members groups group_members
      role_assignments group_role_assignments tenant_apps invitations access_requests
      ownership_transfers support_grants audit_exports role_assignment_batches tenant_exports
    )
    ;;
  profile)
    tables=(tenant_member_profiles)
    ;;
  *)
    echo "SERVICE must be access or profile." >&2
    exit 1
    ;;
esac

scratch_identity="$(psql "$SCRATCH_DATABASE_URL" --no-psqlrc --tuples-only --no-align \
  --command "SELECT current_database() || '@' || coalesce(inet_server_addr()::text, 'local') || ':' || inet_server_port();")"
target_identity="$(psql "$TARGET_DATABASE_URL" --no-psqlrc --tuples-only --no-align \
  --command "SELECT current_database() || '@' || coalesce(inet_server_addr()::text, 'local') || ':' || inet_server_port();")"
[[ "$scratch_identity" != "$target_identity" ]] || {
  echo "Scratch and target database identities are the same; refusing restore." >&2
  exit 1
}

psql "$TARGET_DATABASE_URL" --no-psqlrc --set ON_ERROR_STOP=1 --tuples-only --no-align <<'SQL' >/dev/null
DO $check$
BEGIN
  IF EXISTS (
    SELECT 1 FROM pg_roles
     WHERE rolname = current_user AND (rolsuper OR rolbypassrls)
  ) OR EXISTS (
    SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND pg_has_role(current_user, c.relowner, 'MEMBER')
  ) THEN
    RAISE EXCEPTION 'target restore role must not be superuser, BYPASSRLS, or a table owner';
  END IF;
END
$check$;
SQL

manifest="$WORK_DIR/manifest.sha256"
: >"$manifest"
for table in "${tables[@]}"; do
  export_file="$WORK_DIR/$table.csv"
  sql_file="$WORK_DIR/export-$table.sql"
  cat >"$sql_file" <<SQL
BEGIN;
SELECT set_config('app.tenant_id', :'tenant_id', true);
\\copy (SELECT * FROM public.$table WHERE tenant_id = :'tenant_id'::uuid) TO '$export_file' WITH (FORMAT csv, HEADER true)
COMMIT;
SQL
  chmod 600 "$sql_file"
  psql "$SCRATCH_DATABASE_URL" --no-psqlrc --set ON_ERROR_STOP=1 \
    --set "tenant_id=$TENANT_ID" --file "$sql_file" >/dev/null
  sha256sum "$export_file" >>"$manifest"
done
chmod 600 "$manifest"
sha256sum --check --quiet "$manifest"

restore_sql="$WORK_DIR/restore.sql"
{
  echo 'BEGIN;'
  echo "SELECT set_config('app.tenant_id', :'tenant_id', true);"
  for ((index = ${#tables[@]} - 1; index >= 0; index--)); do
    echo "DELETE FROM public.${tables[$index]} WHERE tenant_id = :'tenant_id'::uuid;"
  done
  for table in "${tables[@]}"; do
    echo "CREATE TEMP TABLE restore_$table (LIKE public.$table INCLUDING DEFAULTS) ON COMMIT DROP;"
    echo "\\copy restore_$table FROM '$WORK_DIR/$table.csv' WITH (FORMAT csv, HEADER true)"
    echo "INSERT INTO public.$table SELECT * FROM restore_$table;"
  done
  echo 'COMMIT;'
} >"$restore_sql"
chmod 600 "$restore_sql"

psql "$TARGET_DATABASE_URL" --no-psqlrc --set ON_ERROR_STOP=1 \
  --set "tenant_id=$TENANT_ID" --file "$restore_sql" >/dev/null

echo "Tenant rows restored for $SERVICE. Keep the tenant suspended until application validation completes."
