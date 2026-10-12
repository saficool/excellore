#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if ! command -v docker >/dev/null 2>&1; then
  echo 'Docker CLI is required. Install and start Docker Desktop first.' >&2
  exit 1
fi
if ! docker info --format '{{.ServerVersion}}' >/dev/null 2>&1; then
  echo 'Docker engine is unavailable. Start Docker Desktop and retry.' >&2
  exit 1
fi

if [ ! -f .env ]; then
  if ! command -v openssl >/dev/null 2>&1; then
    echo 'OpenSSL is required to generate local credentials.' >&2
    exit 1
  fi
  umask 077
  temp_env=".env.tmp.$$"
  trap 'rm -f "$temp_env"' EXIT
  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in
      *=__GENERATE__) printf '%s=%s\n' "${line%%=*}" "$(openssl rand -hex 24)" ;;
      *) printf '%s\n' "$line" ;;
    esac
  done < .env.example > "$temp_env"
  mv "$temp_env" .env
  trap - EXIT
  echo 'Created local .env with random credentials.'
fi

if grep -q '=__GENERATE__' .env; then
  echo '.env still contains placeholders. Remove it and rerun dev-up.sh, or fill them in.' >&2
  exit 1
fi

access_key="$(sed -n 's/^OBJECTSTORE_ACCESS_KEY=//p' .env)"
secret_key="$(sed -n 's/^OBJECTSTORE_SECRET_KEY=//p' .env)"
for value in "$access_key" "$secret_key"; do
  if [ -z "$value" ] || [[ "$value" == *[!a-zA-Z0-9]* ]]; then
    echo 'Object store keys in .env must be nonempty alphanumeric values.' >&2
    exit 1
  fi
done

umask 077
mkdir -p .local/objectstore
printf '{"identities":[{"name":"local-dev","credentials":[{"accessKey":"%s","secretKey":"%s"}],"actions":["Admin","Read","Write"]}]}\n' \
  "$access_key" "$secret_key" > .local/objectstore/s3.json

compose=(docker compose --env-file .env -f infra/compose/docker-compose.yml)
"${compose[@]}" config --quiet
"${compose[@]}" up -d --wait --wait-timeout 300

for service in access profile; do
  "${compose[@]}" exec -T "postgres-$service" \
    psql -X -U postgres -d "$service" -f /opt/excellore/bootstrap-roles.sql >/dev/null
done

bash scripts/check-dev-stack.sh
