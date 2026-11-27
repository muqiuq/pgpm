#!/usr/bin/env bash
# Shared helpers for the deploy scripts (PNMTD's deploy/lib.sh, trimmed). Sourced, never run.
#
#   . "$(dirname "$0")/lib.sh"
#
# Every script works the same way: check for .env, find the app container, and run the `pgpm` command line in it — in
# the running app, or in a one-shot container over the same volumes.

SERVICE=pgpm
DB_SERVICE=db

die() { echo "$@" >&2; exit 1; }

require_env() { [[ -f .env ]] || die "deploy/.env is missing — copy .env.example to .env and fill it in first."; }

# `docker compose` (the plugin), or the standalone docker-compose.
compose() {
  if docker compose version >/dev/null 2>&1; then docker compose "$@"; else docker-compose "$@"; fi
}

require_compose() {
  command -v docker >/dev/null 2>&1 || die "docker is not installed."
  docker compose version >/dev/null 2>&1 || command -v docker-compose >/dev/null 2>&1 || die "docker compose is not available."
}

container_id() { compose ps -q "$1" 2>/dev/null; }

is_running() {
  local id
  id="$(container_id "${1:-$SERVICE}")"
  [[ -n "$id" ]] && [[ "$(docker inspect -f '{{.State.Running}}' "$id" 2>/dev/null)" == "true" ]]
}

# The `pgpm` command line: in the running app, or in a one-shot container beside the database.
pgpm_cmd() {
  if is_running "$SERVICE"; then
    compose exec -T "$SERVICE" pgpm "$@"
  else
    is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
    compose run --rm --no-deps -T "$SERVICE" "$@"
  fi
}

# The comment block at the top of a script, as its help.
usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$1"; }
