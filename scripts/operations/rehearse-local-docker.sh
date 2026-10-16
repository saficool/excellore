#!/usr/bin/env bash
set -euo pipefail

# Exercises recovery mechanics against disposable databases in the local Docker stack. This is
# development evidence only; it does not satisfy the managed-cloud staging acceptance criteria.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

for command in docker openssl sha256sum; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "$command is required for the local WP-14 rehearsal." >&2
    exit 1
  }
done
if [ ! -f .env ]; then
  echo 'Missing .env. Run scripts/dev-up.sh first.' >&2
  exit 1
fi
if ! docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
  echo 'Docker engine is unavailable.' >&2
  exit 1
fi

compose=(docker compose --env-file .env -f infra/compose/docker-compose.yml)
"${compose[@]}" config --quiet

scratch_entries=()
rotation_role=excellore_rotation_probe
cleanup() {
  set +e
  for entry in "${scratch_entries[@]}"; do
    IFS='|' read -r service user database <<<"$entry"
    "${compose[@]}" exec -T "$service" dropdb --if-exists --force -U "$user" "$database" >/dev/null 2>&1
  done
  "${compose[@]}" exec -T postgres-access psql -X -v ON_ERROR_STOP=1 -U postgres -d access >/dev/null 2>&1 <<SQL
REVOKE CONNECT ON DATABASE access FROM $rotation_role;
DROP ROLE IF EXISTS $rotation_role;
SQL
}
trap cleanup EXIT

started_at="$(date +%s)"

verify_loopback_ports() {
  local service container_id bindings host_ip
  while IFS= read -r service; do
    container_id="$("${compose[@]}" ps -q "$service")"
    if [ -z "$container_id" ]; then
      echo "$service is not running." >&2
      exit 1
    fi
    bindings="$(docker inspect --format '{{range $port, $entries := .HostConfig.PortBindings}}{{range $entries}}{{println .HostIp}}{{end}}{{end}}' "$container_id")"
    while IFS= read -r host_ip; do
      [ -z "$host_ip" ] && continue
      case "$host_ip" in
        127.0.0.1|::1) ;;
        *)
          echo "$service publishes a port outside loopback." >&2
          exit 1
          ;;
      esac
    done <<<"$bindings"
  done < <("${compose[@]}" config --services)
}

schema_hash() {
  local service="$1" user="$2" database="$3"
  "${compose[@]}" exec -T "$service" pg_dump -U "$user" \
    --schema-only --no-owner --no-privileges "$database" |
    sed -E '/^\\(un)?restrict /d' |
    sha256sum | cut -d ' ' -f 1
}

verify_forced_rls() {
  local service="$1" user="$2" database="$3" violations
  violations="$("${compose[@]}" exec -T "$service" psql -X -A -t -U "$user" -d "$database" -c "
    SELECT count(*)
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.relkind IN ('r', 'p')
      AND n.nspname NOT IN ('pg_catalog', 'information_schema')
      AND c.relname NOT IN ('outbox', 'user_tenant_index')
      AND EXISTS (
        SELECT 1 FROM pg_attribute a
        WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped
      )
      AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity);")"
  if [ "$violations" != 0 ]; then
    echo "$database restore contains tenant tables without forced RLS." >&2
    exit 1
  fi
}

rehearse_restore() {
  local service="$1" database="$2" user="$3" enforce_rls="$4"
  local scratch="excellore_rehearsal_${database}" source_hash restored_hash table_count
  scratch_entries+=("$service|$user|$scratch")

  "${compose[@]}" exec -T "$service" dropdb --if-exists --force -U "$user" "$scratch" >/dev/null
  "${compose[@]}" exec -T "$service" createdb -U "$user" -T template0 "$scratch"
  "${compose[@]}" exec -T "$service" pg_dump -U "$user" \
    --format=custom --no-owner --no-privileges "$database" |
    "${compose[@]}" exec -T "$service" pg_restore -U "$user" \
      --dbname="$scratch" --exit-on-error --no-owner --no-privileges

  source_hash="$(schema_hash "$service" "$user" "$database")"
  restored_hash="$(schema_hash "$service" "$user" "$scratch")"
  if [ "$source_hash" != "$restored_hash" ]; then
    echo "$database schema differs after restore." >&2
    exit 1
  fi

  table_count="$("${compose[@]}" exec -T "$service" psql -X -A -t -U "$user" -d "$scratch" -c "
    SELECT count(*) FROM information_schema.tables
    WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
      AND table_type = 'BASE TABLE';")"
  if [ "$table_count" -le 0 ]; then
    echo "$database restore contains no application tables." >&2
    exit 1
  fi
  if [ "$enforce_rls" = true ]; then
    verify_forced_rls "$service" "$user" "$scratch"
  fi

  "${compose[@]}" exec -T "$service" dropdb --if-exists --force -U "$user" "$scratch" >/dev/null
}

rehearse_credential_rotation() {
  local old_password new_password access_container network postgres_image
  old_password="$(openssl rand -hex 24)"
  new_password="$(openssl rand -hex 24)"
  access_container="$("${compose[@]}" ps -q postgres-access)"
  network="$(docker inspect --format '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' "$access_container" | head -n 1)"
  postgres_image="$(docker inspect --format '{{.Config.Image}}' "$access_container")"

  "${compose[@]}" exec -T postgres-access psql -X -v ON_ERROR_STOP=1 \
    -v old_password="$old_password" -U postgres -d access >/dev/null <<'SQL'
DROP ROLE IF EXISTS excellore_rotation_probe;
CREATE ROLE excellore_rotation_probe LOGIN PASSWORD :'old_password'
  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
GRANT CONNECT ON DATABASE access TO excellore_rotation_probe;
SQL

  docker run --rm --network "$network" -e PGPASSWORD="$old_password" "$postgres_image" \
    psql -X -h postgres-access -U "$rotation_role" -d access -c 'SELECT 1;' >/dev/null

  "${compose[@]}" exec -T postgres-access psql -X -v ON_ERROR_STOP=1 \
    -v new_password="$new_password" -U postgres -d access >/dev/null <<'SQL'
ALTER ROLE excellore_rotation_probe PASSWORD :'new_password';
SQL

  if docker run --rm --network "$network" -e PGPASSWORD="$old_password" "$postgres_image" \
    psql -X -h postgres-access -U "$rotation_role" -d access -c 'SELECT 1;' >/dev/null 2>&1; then
    echo 'The previous probe credential remained valid after rotation.' >&2
    exit 1
  fi
  docker run --rm --network "$network" -e PGPASSWORD="$new_password" "$postgres_image" \
    psql -X -h postgres-access -U "$rotation_role" -d access -c 'SELECT 1;' >/dev/null

  "${compose[@]}" exec -T postgres-access psql -X -v ON_ERROR_STOP=1 -U postgres -d access >/dev/null <<SQL
REVOKE CONNECT ON DATABASE access FROM $rotation_role;
DROP ROLE $rotation_role;
SQL
  unset old_password new_password
}

verify_loopback_ports
rehearse_restore keycloak-db keycloak keycloak false
rehearse_restore postgres-access access postgres true
rehearse_restore postgres-profile profile postgres true
rehearse_credential_rotation

finished_at="$(date +%s)"
duration_seconds="$((finished_at - started_at))"
umask 077
mkdir -p .local
cat >.local/wp14-local-rehearsal.json <<JSON
{
  "environment": "local-docker",
  "completedAt": "$(date -u +'%Y-%m-%dT%H:%M:%SZ')",
  "durationSeconds": $duration_seconds,
  "loopbackOnly": true,
  "restoredDatabases": ["keycloak", "access", "profile"],
  "forcedRlsVerified": ["access", "profile"],
  "credentialRotation": {
    "kind": "disposable-database-role",
    "oldCredentialRejected": true,
    "newCredentialAccepted": true
  },
  "containsSensitiveData": false,
  "satisfiesManagedCloudAcceptance": false
}
JSON

trap - EXIT
cleanup
echo "Local Docker recovery rehearsal passed in ${duration_seconds}s."
