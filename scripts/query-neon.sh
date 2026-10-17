#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
export MSYS_NO_PATHCONV=1

if [ "$#" -ne 4 ]; then
  echo 'Usage: scripts/query-neon.sh <service> <role> <direct|pooled> <sql>' >&2
  exit 2
fi

# shellcheck source=scripts/lib/neon-postgres.sh
source scripts/lib/neon-postgres.sh
neon_psql "$1" "$2" "$3" -A -t -v ON_ERROR_STOP=1 -c "$4"
