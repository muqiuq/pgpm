#!/usr/bin/env bash
# Every check, in one command (docs/03 §2.4, docs/05 §5; adapted from PNMTD's).
#
#   ./run-tests.sh                  everything
#   ./run-tests.sh --only web       one group: dotnet, web, contract, e2e, deploy, shell (several: --only dotnet,web)
#   ./run-tests.sh --list           what it would run
#
# Groups:
#   dotnet    restore (locked), build (writes PGPM.WebUI/src/api/openapi.json), no model change without its migration,
#             PGPM.Tests on a throwaway PostgreSQL (tools/test-db.sh), in a zone east and one west of UTC
#   web       the UI in a node container: the npm package check (with the registry's publish dates), npm ci, the
#             registry's signatures, the API types against openapi.json, vue-tsc, ESLint, Prettier, Vitest, the build
#   contract  PGPM.Contract.Tests against the image from the working tree (tools/contract.sh)
#   e2e       the browser tests (Playwright) against the image, incl. the offline shell (tools/e2e.sh)
#   deploy    deploy/ for real (tools/deploy-test.sh): the image built, init, up, sign-in, the user scripts, a note and a
#             file, backup, restore into a fresh stack, update, info, down — docker with compose (on a Mac: on Podman)
#   shell     bash -n and shellcheck (in a container) over every script, and the configuration packages
#             (tools/check-packages.py: the schema, references, scores, defaults)
#
# dotnet and python3 (standard library only) run on this machine; node never does: the web group copies PGPM.WebUI into
# a node container (the repository stays read-only), with npm's cache in a volume. Podman by default, ENGINE=docker for
# Docker.
# Each step's output goes to a log under .tmpcheck/, shown only when it fails.
set -uo pipefail
cd "$(dirname "$0")" || exit 1

ENGINE="${ENGINE:-podman}"
# dotnet first: its build writes the OpenAPI document the web group checks the UI's types against.
ALL_GROUPS=(dotnet web contract e2e deploy shell)
NODE_IMAGE="${NODE_IMAGE:-docker.io/library/node:24-slim}"
NPM_CACHE="${NPM_CACHE:-pgpm-npm-cache}"
WEB_HOLDER="pgpm-test-web-$$"
ONLY=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --only) ONLY="${2:?--only needs a group}"; shift 2 ;;
    --list) printf '%s\n' "${ALL_GROUPS[@]}"; exit 0 ;;
    -h|--help) awk 'NR>1 && /^#/ {sub(/^# ?/, ""); print; next} NR>1 {exit}' "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

selected=("${ALL_GROUPS[@]}")
if [[ -n "$ONLY" ]]; then
  IFS=',' read -r -a selected <<<"$ONLY"
fi

mkdir -p .tmpcheck
log_dir=$(mktemp -d "$PWD/.tmpcheck/run-tests.XXXXXX")
failures=()
trap '"$ENGINE" rm -f "$WEB_HOLDER" >/dev/null 2>&1 || true' EXIT

# step NAME COMMAND…: runs it with its output in a log file, shown only on failure.
step() {
  local name="$1"; shift
  local log start=$SECONDS
  log="$log_dir/$(echo "$name" | tr -c 'A-Za-z0-9' '_').log"
  printf '  %-58s' "$name"
  if "$@" >"$log" 2>&1; then
    printf 'ok  (%ss)\n' $(( SECONDS - start ))
  else
    printf 'FAILED  (%ss)\n' $(( SECONDS - start ))
    sed 's/^/      /' "$log" | tail -40
    annotate "$name" "$log"
    failures+=("$name")
  fi
}

# Playwright's failed tests from its list reporter: each one's title, its error, the line that failed and where.
playwright_failures() {
  awk '
    /^  [0-9]+\) \[/ { block = 1; head = 1; lines = 0; sub(/^ +/, ""); sub(/ +$/, ""); if (n++) print ""; print; next }
    !block { next }
    /^ +Call log:/ { head = 0; next }
    /^ +> +[0-9]+ \|/ { sub(/^ +/, ""); print; next }
    /^ +at .*:[0-9]+:[0-9]+$/ { sub(/^ +/, ""); print; block = 0; next }
    /^ +[0-9]+ \|/ || /^ +\|/ { head = 0; next }
    head && NF && lines < 25 { sub(/^ +/, ""); print; lines++ }
  ' "$1"
  grep -E '^  [0-9]+ (failed|flaky|passed)' "$1" | sed 's/^ *//'
}

# On GitHub Actions, a failed step's reason as an annotation on the run's page: from the FAIL: line the test scripts
# end with, else Playwright's failed tests with their errors, else the log's last lines.
annotate() {
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] || return 0
  local name="$1" log="$2" reason
  reason="$(sed -n '/^FAIL/,$p' "$log")"
  grep -qE '^  [0-9]+\) \[' "$log" && [[ -z "$reason" ]] && reason="$(playwright_failures "$log")"
  [[ -n "$reason" ]] || reason="$(tail -5 "$log")"
  reason="${reason//'%'/%25}"; reason="${reason//$'\r'/}"; reason="${reason//$'\n'/%0A}"
  name="${name//'%'/%25}"; name="${name//:/%3A}"; name="${name//,/%2C}"
  echo "::error title=${name}::${reason}"
}

# --- containers -------------------------------------------------------------------------------------------------------

in_node() { "$ENGINE" run --rm -v "$PWD":/w:ro -w /w "$NODE_IMAGE" "$@"; }

# PGPM.WebUI copied into one container that lives for the run: npm ci once, then every check in the same node_modules,
# without touching the working tree. spec/ next to it, as in the repository: the test vectors the UI's tests share with
# the C# tests (spec/score-vectors.json).
web_holder() {
  "$ENGINE" container inspect "$WEB_HOLDER" >/dev/null 2>&1 && return 0
  "$ENGINE" run -d --rm --name "$WEB_HOLDER" -v "$PWD":/w:ro -v "${NPM_CACHE}":/root/.npm \
    -e NPM_CONFIG_UPDATE_NOTIFIER=false -e NPM_CONFIG_FUND=false -e NPM_CONFIG_AUDIT=false -e NO_COLOR=1 \
    "$NODE_IMAGE" sleep 3600 >/dev/null || return 1
  "$ENGINE" exec "$WEB_HOLDER" sh -c 'mkdir -p /tmp/web /tmp/spec && cp -R /w/spec/. /tmp/spec/ && cd /w/PGPM.WebUI \
    && tar --exclude=node_modules --exclude=dist --exclude=test-results -cf - . | tar -C /tmp/web -xf -'
}
in_web() { web_holder && "$ENGINE" exec -w /tmp/web "$WEB_HOLDER" "$@"; }

# npm audit signatures, up to three times when the registry fails to answer (PNMTD saw a broken attestation answer on
# GitHub's runners). Each try has an empty npm cache of its own, so a cached broken answer is never read again. A
# missing or invalid signature is npm's verdict, not a registry error: it fails at once.
audit_signatures() {
  local try out
  for try in 1 2 3; do
    out="$(in_web npm audit signatures --cache "/tmp/npm-audit-$try" 2>&1)" && { printf '%s\n' "$out"; return 0; }
    printf '%s\n' "$out"
    grep -qE 'code (FETCH_ERROR|E429|E5[0-9][0-9]|ECONNRESET|ECONNREFUSED|ETIMEDOUT|EAI_AGAIN)' <<<"$out" || return 1
    if [[ $try -lt 3 ]]; then
      echo "a registry error, again in $(( try * 15 )) s"
      sleep $(( try * 15 ))
    fi
  done
  return 1
}

# --- groups -----------------------------------------------------------------------------------------------------------

group_dotnet() {
  echo "dotnet"
  step "test database (tools/test-db.sh)" tools/test-db.sh start
  step "tools (dotnet-ef)" dotnet tool restore
  step "restore (locked)" dotnet restore PGPM.slnx --locked-mode
  step "build (warnings are errors)" dotnet build PGPM.slnx --no-restore -v q
  step "no pending model changes" dotnet ef migrations has-pending-model-changes --project PGPM/PGPM.csproj --no-build
  step "tests, Europe/Zurich" env PGPM_TEST_TZ=Europe/Zurich TZ=Europe/Zurich \
    dotnet test --project PGPM.Tests/PGPM.Tests.csproj --no-build
  step "tests, America/New_York" env PGPM_TEST_TZ=America/New_York TZ=America/New_York \
    dotnet test --project PGPM.Tests/PGPM.Tests.csproj --no-build
}

group_web() {
  echo "web"
  step "npm packages: pinned, npmjs.org, open-source licences" in_node node tools/check-npm-packages.mjs
  step "npm ci (locked, no install scripts)" in_web npm ci --ignore-scripts --loglevel=error
  step "npm registry signatures and provenance" audit_signatures
  step "API types generated from openapi.json" in_web npm run --silent check:api
  step "typecheck (vue-tsc, app and service worker)" in_web npm run --silent typecheck
  step "lint (ESLint)" in_web npm run --silent lint
  step "format (Prettier)" in_web npm run --silent format:check
  step "unit tests (Vitest)" in_web npm test --silent
  step "build (Vite + service worker)" in_web npm run --silent build
}

group_contract() {
  echo "contract"
  step "contract tests against the image" env ENGINE="$ENGINE" tools/contract.sh
}

group_e2e() {
  echo "e2e"
  step "browser tests (Playwright) against the image" env ENGINE="$ENGINE" tools/e2e.sh
}

# The image under IMAGE (pgpm:dev, as ./start.sh builds it), then deploy/ against it with a compose project and port of
# its own (DEPLOY_TEST_PROJECT, DEPLOY_TEST_PORT: pgpm-deploytest, 7390).
group_deploy() {
  echo "deploy"
  local image="${IMAGE:-pgpm:dev}"
  step "image from the working tree (${image})" "$ENGINE" build -q -t "$image" -f Dockerfile .
  step "init, up, users, note and file, backup, restore, update" env IMAGE="$image" tools/deploy-test.sh
}

group_shell() {
  echo "shell"
  local scripts=(start.sh stop.sh run-tests.sh tools/*.sh)
  shopt -s nullglob
  scripts+=(deploy/*.sh)
  shopt -u nullglob
  step "bash -n" bash -c 'for f in "$@"; do bash -n "$f" || exit 1; done' _ "${scripts[@]}"
  step "shellcheck" "$ENGINE" run --rm -v "$PWD:/w:ro" -w /w docker.io/koalaman/shellcheck:stable \
    --severity=warning --external-sources "${scripts[@]}"
  step "packages (tools/check-packages.py)" python3 -I tools/check-packages.py
}

for group in "${selected[@]}"; do
  case "$group" in
    dotnet|web|contract|e2e|deploy|shell) "group_$group" ;;
    *) echo "unknown group: $group (${ALL_GROUPS[*]})" >&2; exit 2 ;;
  esac
done

rm -rf "$log_dir"
echo
if (( ${#failures[@]} )); then
  echo "FAILED: ${failures[*]}"
  exit 1
fi
echo "All green."
