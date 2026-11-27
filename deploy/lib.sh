#!/usr/bin/env bash
# Shared helpers for the deploy scripts (PNMTD's deploy/lib.sh, adapted). Sourced, never run.
#
#   . "$(dirname "$0")/lib.sh"
#
# Every script works the same way: read .env without sourcing it, find the app container, and run the `pgpm` command
# line (or tar, du, …) in it — in the running app, or in a one-shot container over the same volumes and mounts.

SERVICE=pgpm
DB_SERVICE=db
# The app's user in the image (Dockerfile: the .NET image's `app`), and where it keeps its state.
# shellcheck disable=SC2034 # used by the scripts that source this file
APP_UID=1654
# shellcheck disable=SC2034
FILES_MOUNT=/var/lib/pgpm/files
# shellcheck disable=SC2034
KEYS_MOUNT=/var/lib/pgpm/keys
# The release this deploy/ folder belongs to: docker-compose.yml's default tag.
DEFAULT_TAG=v1.0.0
DEFAULT_IMAGE=uisach/pgpm

say() { (( ${QUIET:-0} )) || echo "$@"; }
die() { echo "$@" >&2; exit 1; }

# The comment block at the top of a script, as its help.
usage() { awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$1"; }

# A setting of .env: its last line, without the quotes .env allows around a value. Never sourced: a generated password
# with a backtick or $( would run.
env_value() {
  [[ -f .env ]] || return 0
  sed -n "s/^[[:space:]]*$1=//p" .env | tail -1 | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'$/\1/"
}

# NAME=VALUE in .env: replaced where it is (also a commented "# NAME=" line), else appended. Through a 0600 temporary
# file, so .env is never half written. VALUE is hex, a tag, a port or a URL: never a |, & or backslash.
env_set() {
  [[ "$2" != *[\|\&\\]* ]] || die "env_set $1: the value must not contain |, & or a backslash."
  (
    umask 077
    if grep -q "^[[:space:]]*$1=" .env; then
      sed "s|^[[:space:]]*$1=.*|$1=$2|" .env >.env.tmp
    elif grep -q "^#[[:space:]]*$1=" .env; then
      awk -v name="$1" -v line="$1=$2" '!done && $0 ~ "^#[[:space:]]*" name "=" { print line; done = 1; next } { print }' .env >.env.tmp
    else
      cat .env >.env.tmp
      [[ -z "$(tail -c1 .env)" ]] || echo >>.env.tmp
      printf '%s=%s\n' "$1" "$2" >>.env.tmp
    fi
  ) || return 1
  chmod 600 .env.tmp
  mv .env.tmp .env
}

# Hex from the kernel's generator; a fixed read, since tr on an endless stream ends in SIGPIPE.
random_hex() { od -An -N"${1:-16}" -tx1 /dev/urandom | tr -d ' \n'; }

# A secret on its own line, easy to copy, and never in a log file.
show_secret() {
  echo
  echo "    $1: $2"
  echo
}

require_env() { [[ -f .env ]] || die "deploy/.env is missing — run ./init.sh first."; }

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

# A command as the app's user in its container, with the service's own volumes and mounts: in the running app, or in a
# one-shot container beside nothing. Its user owns the files folder and the key ring, which nobody else may read.
in_app() {
  if is_running "$SERVICE"; then
    compose exec -T "$SERVICE" "$@"
  else
    compose run --rm --no-deps -T --entrypoint "$1" "$SERVICE" "${@:2}"
  fi
}

# SQL as the database's owner, one value per line (psql -tA).
sql() { compose exec -T "$DB_SERVICE" psql -X -q -v ON_ERROR_STOP=1 -U pgpm -d pgpm -tAc "$1"; }

# As docker-compose.yml reads it: the environment first, then .env, then the defaults.
app_image() {
  local image="${PGPM_IMAGE:-}" tag="${PGPM_IMAGE_TAG:-}"
  [[ -n "$image" ]] || image="$(env_value PGPM_IMAGE)"
  [[ -n "$tag" ]] || tag="$(env_value PGPM_IMAGE_TAG)"
  echo "${image:-$DEFAULT_IMAGE}:${tag:-$DEFAULT_TAG}"
}

app_port() {
  local port="${PGPM_PORT:-}"
  [[ -n "$port" ]] || port="$(env_value PGPM_PORT)"
  echo "${port:-7330}"
}

# The published address, as this host reaches it (0.0.0.0 is reached at 127.0.0.1).
local_url() {
  local bind="${PGPM_BIND_ADDRESS:-}"
  [[ -n "$bind" ]] || bind="$(env_value PGPM_BIND_ADDRESS)"
  [[ -n "$bind" && "$bind" != 0.0.0.0 ]] || bind=127.0.0.1
  echo "http://${bind}:$(app_port)"
}

# The attachments' folder (docs/03 §10): a folder on this host, mounted at FILES_MOUNT. Like docker-compose.yml:
# PGPM_FILES_DIR from the environment, then .env, else ./files.
files_dir() {
  local dir="${PGPM_FILES_DIR:-}"
  [[ -n "$dir" ]] || dir="$(env_value PGPM_FILES_DIR)"
  echo "${dir:-./files}"
}

# The folder, created and given to the app's user (uid 1654, 0750): directly as root, else as root in a one-shot container
# of the image, which maps the uid as the app's container sees it (rootless Podman too). Fails when neither can.
ensure_files_dir() {
  local dir abs
  dir="$(files_dir)"
  mkdir -p "$dir" || return 1
  abs="$(cd "$dir" && pwd)"
  if [[ "$(id -u)" == "0" ]]; then
    chown "${APP_UID}:${APP_UID}" "$abs" && chmod 0750 "$abs"
  else
    docker run --rm --user 0 --entrypoint sh -v "${abs}:/files" "$(app_image)" \
      -c "chown ${APP_UID}:${APP_UID} /files && chmod 0750 /files" >/dev/null 2>&1
  fi
}

backup_dir() {
  local dir="${PGPM_BACKUP_DIR:-}"
  [[ -n "$dir" ]] || dir="$(env_value PGPM_BACKUP_DIR)"
  echo "${dir:-backups}"
}

# The backups in a folder, oldest first: pgpm-<yyyymmdd>-<hhmmss>.tar sorts by time.
list_backups() {
  local dir="${1:-$(backup_dir)}"
  [[ -d "$dir" ]] || return 0
  find "$dir" -maxdepth 1 -type f -name 'pgpm-[0-9]*-[0-9]*.tar' 2>/dev/null | sort
}

# SHA-256 of a file (Linux and macOS).
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# The running release, as /api/v1/info answers it (anonymous): 1.0.0, or nothing.
app_version() {
  curl -fsS --max-time 5 "$(local_url)/api/v1/info" 2>/dev/null | sed -n 's/.*"version":"\([^"]*\)".*/\1/p'
}

# Waits until /healthz answers 200 (at most $1 seconds, 180 by default): the container's health check sees the same.
wait_healthy() {
  local deadline=$(( SECONDS + ${1:-180} ))
  while (( SECONDS < deadline )); do
    curl -fsS -o /dev/null --max-time 5 "$(local_url)/healthz" 2>/dev/null && return 0
    sleep 2
  done
  return 1
}
