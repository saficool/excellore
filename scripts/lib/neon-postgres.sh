#!/usr/bin/env bash

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_file="${EXCELLORE_ENV_FILE:-$repo_root/.env}"
postgres_image="${EXCELLORE_POSTGRES_CLIENT_IMAGE:-postgres:16.12}"

neon_env_value() {
  local key="$1"
  sed -n "s/^${key}=//p" "$env_file" | tail -n 1 | tr -d '\r'
}

neon_require_value() {
  local key="$1" value
  value="$(neon_env_value "$key")"
  if [ -z "$value" ] || [ "$value" = '__GENERATE__' ]; then
    echo "Missing $key in $env_file." >&2
    return 1
  fi
  printf '%s' "$value"
}

neon_psql() {
  local service="$1" role="$2" endpoint="$3"
  shift 3

  local prefix host_key user_key password_key host database user password
  prefix="$(printf '%s' "$service" | tr '[:lower:]' '[:upper:]')"
  case "$endpoint" in
    direct) host_key="${prefix}_NEON_DIRECT_HOST" ;;
    pooled) host_key="${prefix}_NEON_POOLED_HOST" ;;
    *) echo "Unsupported Neon endpoint type: $endpoint" >&2; return 2 ;;
  esac

  case "$role" in
    admin)
      user_key="${prefix}_NEON_ADMIN_USERNAME"
      password_key="${prefix}_NEON_ADMIN_PASSWORD"
      ;;
    migrator|app|publisher|worker)
      user="${service}_${role}"
      if [ "$service" = keycloak ] && [ "$role" = app ]; then
        password_key=KEYCLOAK_DB_PASSWORD
      else
        password_key="${prefix}_$(printf '%s' "$role" | tr '[:lower:]' '[:upper:]')_PASSWORD"
      fi
      ;;
    *) echo "Unsupported Neon role: $role" >&2; return 2 ;;
  esac

  host="$(neon_require_value "$host_key")" || return 1
  database="$(neon_require_value "${prefix}_NEON_DATABASE")" || return 1
  if [ -n "${user_key:-}" ]; then
    user="$(neon_require_value "$user_key")" || return 1
  fi
  password="$(neon_require_value "$password_key")" || return 1

  case "$host" in
    *://*|*@*|*:*|*/*|*' '*) echo "$host_key must contain a hostname only." >&2; return 1 ;;
  esac

  PGPASSWORD="$password" PGSSLMODE=verify-full PGCHANNELBINDING=require PGCONNECT_TIMEOUT=15 \
    docker run --rm -i \
      -e PGPASSWORD -e PGSSLMODE -e PGCHANNELBINDING -e PGCONNECT_TIMEOUT \
      -e DB_APP_ROLE -e DB_APP_PASSWORD -e DB_SERVICE \
      -e DB_MIGRATOR_PASSWORD -e DB_PUBLISHER_PASSWORD -e DB_WORKER_PASSWORD \
      "$postgres_image" psql -X -h "$host" -p 5432 -U "$user" -d "$database" "$@"
}
