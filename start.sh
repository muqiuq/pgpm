#!/usr/bin/env bash
# Build and run PGPM in containers (Podman by default) for debugging on a laptop: PostgreSQL, the migrations and the
# app, on http://localhost:7330/. Production runs from deploy/ with Docker; this is not how it is deployed (docs/03 §2.4,
# adapted from PNMTD's start.sh).
#
#   ./start.sh                     build and (re)start: http://localhost:7330/
#   WEB=dev ./start.sh             also Vite with HMR: http://localhost:5173/, edits of PGPM.WebUI are live (/api and
#                                  /healthz go to the app; no service worker in Vite's dev server)
#   PORT=7400 WEB_PORT=5174        other host ports
#   FILES_DIR=~/pgpm-files ./start.sh
#                                  the attachments' folder, bind-mounted as deploy/ mounts it (default .pgpm-files/ in the
#                                  repository, gitignored)
#   VERBOSE=1 ./start.sh           show the build log, which is otherwise only printed on failure
#   ENGINE=docker ./start.sh       Docker instead of Podman
#
# Settings come from dev.env (gitignored, 0600, deploy/.env format: PublicUrl=…, TZ=…, Entra__ClientId=…), or the file
# ENV_FILE names (tools/e2e.sh). The first run creates it with a generated database password and a password for the
# first admin (admin, Auth__InitialAdmin__Password – read from phase 1 on, while the database has no users).
# ./stop.sh stops everything again.
set -euo pipefail
cd "$(dirname "$0")"

ENGINE="${ENGINE:-podman}"
NAME="${NAME:-pgpm}"
IMAGE="${IMAGE:-pgpm:dev}"
PORT="${PORT:-7330}"
DB_NAME="${DB_NAME:-pgpm-db}"
NETWORK="${NETWORK:-pgpm-dev}"
PG_VOLUME="${PG_VOLUME:-pgpm-pgdata}"
KEYS_VOLUME="${KEYS_VOLUME:-pgpm-keys-dev}"
FILES_DIR="${FILES_DIR:-.pgpm-files}"
POSTGRES_IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
VERBOSE="${VERBOSE:-0}"
ENV_FILE="${ENV_FILE:-dev.env}"
WEB="${WEB:-}"
WEB_NAME="${WEB_NAME:-${NAME}-web}"
WEB_PORT="${WEB_PORT:-5173}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pgpm-web-node-modules}"
NPM_CACHE="${NPM_CACHE:-pgpm-npm-cache}"
NODE_IMAGE="${NODE_IMAGE:-docker.io/library/node:24-slim}"

die() { echo "$@" >&2; exit 1; }

case "$WEB" in
  ""|dev) ;;
  *) die "WEB=${WEB}: the only value is dev (Vite with HMR beside the app)." ;;
esac

# A setting of dev.env: its last line, without the quotes .env allows around a value.
setting() {
  sed -n "s/^$1=//p" "$ENV_FILE" | tail -1 | sed -E "s/^'(.*)'\$/\\1/; s/^\"(.*)\"\$/\\1/"
}

# A line appended to dev.env, which is edited by hand and may lack its final newline.
add_setting() {
  if [[ -s "$ENV_FILE" && -n "$(tail -c1 "$ENV_FILE")" ]]; then echo >> "$ENV_FILE"; fi
  printf '%s=%s\n' "$1" "$2" >> "$ENV_FILE"
}

# 32 hex digits; a fixed read, since tr on an endless stream ends in SIGPIPE.
random_secret() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

if [[ ! -f "$ENV_FILE" ]]; then
  (umask 077 && printf '%s\n' \
    '# Settings for ./start.sh, in deploy/.env format (ASP.NET names: PublicUrl=…, TZ=…, Entra__ClientId=…).' \
    '# Only what differs from the defaults needs a line.' > "$ENV_FILE")
  echo "$ENV_FILE created."
fi
if [[ -z "$(setting PGPM_DB_PASSWORD)" ]]; then
  add_setting PGPM_DB_PASSWORD "$(random_secret)"
fi
db_password="$(setting PGPM_DB_PASSWORD)"
# Read by the server while the database has no users (phase 1), as deploy/.env's.
if [[ -z "$(setting Auth__InitialAdmin__Password)" ]]; then
  add_setting Auth__InitialAdmin__Password "$(random_secret)"
fi

# Something else publishing a port would stop the app at the very end: said here, before the build.
ports=("$PORT")
[[ "$WEB" == dev ]] && ports+=("$WEB_PORT")
for port in "${ports[@]}"; do
  holder=$("$ENGINE" ps --format '{{.Names}} {{.Ports}}' \
    | awk -v port=":${port}->" -v app="$NAME" -v web="$WEB_NAME" '$1 != app && $1 != web && index($0, port) { print $1; exit }')
  [[ -z "$holder" ]] || die "Port ${port} is taken by the container ${holder}: stop it, or choose others (PORT=… WEB_PORT=… ./start.sh)."
done

# Under the repository: Podman on macOS cannot mount /tmp or /var/folders.
mkdir -p .tmpcheck
scratch=$(mktemp -d "$PWD/.tmpcheck/start.XXXXXX")
trap 'rm -rf "$scratch"' EXIT

echo "Building ${IMAGE}…"
if [[ "$VERBOSE" == "1" ]]; then
  "$ENGINE" build -t "$IMAGE" -f Dockerfile .
elif ! "$ENGINE" build -t "$IMAGE" -f Dockerfile . >"$scratch/build.log" 2>&1; then
  cat "$scratch/build.log" >&2
  die "Build failed."
fi

"$ENGINE" network create "$NETWORK" >/dev/null 2>&1 || true

if [[ -z "$("$ENGINE" ps -q --filter "name=^${DB_NAME}$")" ]]; then
  "$ENGINE" rm -f "$DB_NAME" >/dev/null 2>&1 || true
  # The password by name only: its value stays out of the process list.
  POSTGRES_PASSWORD="$db_password" "$ENGINE" run -d --name "$DB_NAME" --network "$NETWORK" \
    -e POSTGRES_USER=pgpm -e POSTGRES_DB=pgpm -e POSTGRES_PASSWORD \
    -v "${PG_VOLUME}:/var/lib/postgresql" \
    "$POSTGRES_IMAGE" >/dev/null
fi
# Over TCP: the image's first-boot server listens on a socket only.
ready=0
for _ in $(seq 1 120); do
  if "$ENGINE" exec "$DB_NAME" pg_isready -q -h 127.0.0.1 -U pgpm 2>/dev/null; then ready=1; break; fi
  sleep 0.5
done
if (( ! ready )); then
  "$ENGINE" logs --tail 30 "$DB_NAME" >&2 || true
  die "PostgreSQL in ${DB_NAME} did not become ready."
fi

echo "Applying migrations…"
"$ENGINE" run --rm --network "$NETWORK" --env-file "$ENV_FILE" \
  -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" \
  "$IMAGE" db migrate >"$scratch/migrate.log" 2>&1 || { cat "$scratch/migrate.log" >&2; die "Migrations failed."; }

# The attachments (docs/03 §10): a folder, not a volume, as deployed. Podman on macOS shares /Users, where the
# repository lies, and the app's user writes there as it is; on Linux the folder is given to it (uid 1654).
mkdir -p "$FILES_DIR"
files_abs="$(cd "$FILES_DIR" && pwd)"
if [[ "$(uname -s)" == Linux ]]; then
  "$ENGINE" run --rm --user 0 --entrypoint sh -v "${files_abs}:/files" "$IMAGE" -c 'chown 1654:1654 /files && chmod 0750 /files' >/dev/null 2>&1 \
    || echo "Could not give ${FILES_DIR} to the app's user (uid 1654): uploads may fail." >&2
fi

"$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
# As hardened as in deploy/docker-compose.yml, so what fails there fails here first.
"$ENGINE" run -d --name "$NAME" --hostname "$NAME" --network "$NETWORK" \
  -p "${PORT}:8080" \
  --env-file "$ENV_FILE" \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "Database__Host=${DB_NAME}" -e "Database__Password=${db_password}" \
  --read-only --tmpfs /tmp --cap-drop ALL --security-opt no-new-privileges \
  -v "${KEYS_VOLUME}:/var/lib/pgpm/keys" \
  -v "${files_abs}:/var/lib/pgpm/files" \
  "$IMAGE" >/dev/null

for _ in $(seq 1 120); do
  if curl -fsS -o /dev/null "http://localhost:${PORT}/healthz" 2>/dev/null; then break; fi
  if [[ -z "$("$ENGINE" ps -q --filter "name=^${NAME}$")" ]]; then
    "$ENGINE" logs --tail 40 "$NAME" >&2 || true
    die "Container ${NAME} exited during startup."
  fi
  sleep 0.25
done
curl -fsS -o /dev/null "http://localhost:${PORT}/healthz" || { "$ENGINE" logs --tail 40 "$NAME" >&2; die "${NAME} is up but /healthz did not answer."; }

# Vite with HMR (WEB=dev), its node_modules in a volume of their own (Linux binaries, not the Mac's). npm ci runs
# whenever package-lock.json changed since the volume was filled. Vite proxies the API to the app by its name on the
# network; tools/vite-dev.config.mjs adds a polling watcher, since the VM passes no file events through the mount.
start_web() {
  echo "Starting ${WEB_NAME} (Vite)…"
  "$ENGINE" rm -f "$WEB_NAME" >/dev/null 2>&1 || true
  # shellcheck disable=SC2016 # expanded in the container
  "$ENGINE" run -d --name "$WEB_NAME" --network "$NETWORK" -p "${WEB_PORT}:5173" \
    -e NPM_CONFIG_UPDATE_NOTIFIER=false -e NPM_CONFIG_FUND=false -e NPM_CONFIG_AUDIT=false \
    -e "PGPM_API_URL=http://${NAME}:8080" \
    -v "$PWD/PGPM.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -v "${NPM_CACHE}:/root/.npm" \
    -v "$PWD/tools/vite-dev.config.mjs:/pgpm/vite-dev.config.mjs:ro" -w /web \
    "$NODE_IMAGE" sh -c '
      if ! cmp -s package-lock.json node_modules/.installed-lock; then
        find node_modules -mindepth 1 -maxdepth 1 -exec rm -rf {} +
        npm ci --ignore-scripts --loglevel=error && cp package-lock.json node_modules/.installed-lock || exit 1
      fi
      mkdir -p node_modules/.pgpm-dev && cp /pgpm/vite-dev.config.mjs node_modules/.pgpm-dev/vite.config.mjs || exit 1
      exec node_modules/.bin/vite --config node_modules/.pgpm-dev/vite.config.mjs --host 0.0.0.0 --port 5173 --strictPort' >/dev/null
  for _ in $(seq 1 240); do
    if curl -fsS -o /dev/null "http://localhost:${WEB_PORT}/" 2>/dev/null; then return 0; fi
    if [[ -z "$("$ENGINE" ps -q --filter "name=^${WEB_NAME}$")" ]]; then break; fi
    sleep 0.5
  done
  "$ENGINE" logs --tail 30 "$WEB_NAME" >&2 || true
  die "${WEB_NAME} did not come up."
}

if [[ "$WEB" == dev ]]; then
  start_web
elif [[ -n "$("$ENGINE" ps -aq --filter "name=^${WEB_NAME}$" 2>/dev/null)" ]]; then
  "$ENGINE" rm -f "$WEB_NAME" >/dev/null 2>&1 || true
  echo "Removed ${WEB_NAME}: this start is without WEB=dev."
fi

echo
echo "─────────────────────────────────────────────"
echo "PGPM running on http://localhost:${PORT}/  (Development)"
if [[ "$WEB" == dev ]]; then
  echo "Vite (HMR):  http://localhost:${WEB_PORT}/  (edits of PGPM.WebUI are live)"
fi
echo "API:         http://localhost:${PORT}/api/v1/  (document: PGPM.WebUI/src/api/openapi.json)"
echo "Sign-in:     from phase 1 – admin, with Auth__InitialAdmin__Password from ${ENV_FILE}"
echo "Database:    ${DB_NAME} (volume ${PG_VOLUME}); psql: ${ENGINE} exec -it ${DB_NAME} psql -U pgpm"
echo "Files:       ${FILES_DIR} (attachments)"
echo "Logs:        ${ENGINE} logs -f ${NAME}"
echo "─────────────────────────────────────────────"
echo "Rerun for .NET changes$([[ "$WEB" == dev ]] || echo ' and web changes (or WEB=dev ./start.sh for live web edits)'). ./stop.sh stops it; ./stop.sh --data also offers to delete the data."
