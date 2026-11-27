#!/usr/bin/env bash
# The browser tests (PGPM.WebUI/e2e, Playwright) against the real image, everything in containers (as PNMTD's):
#
#   1. ./start.sh with names, volumes and a port of their own (http://localhost:7331/), so the debugging stack and its
#      data stay untouched;
#   2. Playwright in its image, in the app container's network: the app is http://localhost:8080 for the browser, a
#      secure context as HTTPS is in production – a service worker exists only there;
#   3. the stack and its volumes removed again.
#
#   tools/e2e.sh                 all of it
#   KEEP=1 tools/e2e.sh          leave the stack running afterwards
#   tools/e2e.sh -g "offline"    arguments go to `playwright test`
#   E2E_NAME=pgpm-e2e-2 E2E_PORT=7431 tools/e2e.sh
#                                another stack beside this one (containers, volumes and folders named after it)
#
# Screenshots of failures land in PGPM.WebUI/test-results/.
set -euo pipefail
cd "$(dirname "$0")/.."

ENGINE="${ENGINE:-podman}"
KEEP="${KEEP:-0}"
WEB_MODULES_VOLUME="${WEB_MODULES_VOLUME:-pgpm-web-node-modules}"
version="$(sed -n 's/.*"@playwright\/test": "\([0-9.]*\)".*/\1/p' PGPM.WebUI/package.json)"
PLAYWRIGHT_IMAGE="${PLAYWRIGHT_IMAGE:-mcr.microsoft.com/playwright:v${version}-noble}"

E2E_NAME="${E2E_NAME:-pgpm-e2e}"
export ENGINE NAME="$E2E_NAME" DB_NAME="${E2E_NAME}-db" NETWORK="$E2E_NAME" PG_VOLUME="${E2E_NAME}-pgdata" \
  KEYS_VOLUME="${E2E_NAME}-keys" PORT="${E2E_PORT:-7331}" WEB="" FILES_DIR="$PWD/.tmpcheck/${E2E_NAME}-files"

mkdir -p .tmpcheck
export ENV_FILE="$PWD/.tmpcheck/${E2E_NAME}.env"

# The stack, its volumes, folder and settings: before a run (what KEEP=1 left, with its database password) and after.
remove_stack() {
  ./stop.sh >/dev/null 2>&1 || true
  "$ENGINE" volume rm -f "$PG_VOLUME" "$KEYS_VOLUME" >/dev/null 2>&1 || true
  # The attachments: on Linux the app's user's (uid 1654), so they go from a container of the image.
  if [[ -d "$FILES_DIR" ]] && ! rm -rf "$FILES_DIR" 2>/dev/null; then
    "$ENGINE" run --rm --user 0 --entrypoint sh -v "$FILES_DIR:/files" "${IMAGE:-pgpm:dev}" -c 'find /files -mindepth 1 -delete' >/dev/null 2>&1 || true
    rm -rf "$FILES_DIR"
  fi
  rm -f "$ENV_FILE"
}
remove_stack
trap '[[ "$KEEP" == "1" ]] || remove_stack' EXIT

# Settings of their own: start.sh adds the generated passwords.
(umask 077 && printf '%s\n' '# tools/e2e.sh' >"$ENV_FILE")

log="$(mktemp "$PWD/.tmpcheck/e2e.XXXXXX")"
echo "Starting ${NAME} on port ${PORT}…"
./start.sh >"$log" 2>&1 || { cat "$log" >&2; exit 1; }
rm -f "$log"

# The first admin's password, which start.sh generated into the stack's settings: the first test signs in with it.
admin_password="$(sed -n 's/^Auth__InitialAdmin__Password=//p' "$ENV_FILE" | tail -1)"

# node_modules (with @playwright/test) are those of tools/npm.sh: installed if the lock changed.
tools/npm.sh exec -- playwright --version >/dev/null

rm -rf PGPM.WebUI/test-results
"$ENGINE" run --rm --network "container:${NAME}" --ipc=host \
  -v "$PWD/PGPM.WebUI:/web" -v "${WEB_MODULES_VOLUME}:/web/node_modules" -w /web \
  -e "E2E_BASE_URL=http://localhost:8080" -e CI=1 -e "E2E_ADMIN_PASSWORD=${admin_password}" \
  "$PLAYWRIGHT_IMAGE" node_modules/.bin/playwright test "$@"
