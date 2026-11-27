#!/usr/bin/env bash
# Is it running, what does it run, how big is it — and what needs attention (PNMTD's info.sh).
#
#   ./info.sh           the overview: release, health, database and files, sync head, settings, backups, warnings
#   ./info.sh --quiet   exit 0 when PGPM runs and /healthz answers, for monitoring
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

QUIET=0
case "${1:-}" in
  --quiet) QUIET=1 ;;
  -h|--help) usage "$0"; exit 0 ;;
  "") ;;
  *) die "unknown option: $1 (try --help)" ;;
esac

require_env
require_compose

url="$(local_url)"
health="not running"
if is_running "$SERVICE"; then
  health="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}running{{end}}' "$(container_id "$SERVICE")")"
fi
if (( QUIET )); then
  [[ "$health" == "healthy" || "$health" == "running" ]] && curl -fsS -o /dev/null --max-time 5 "${url}/healthz"
  exit $?
fi

on_off() { case "$(tr '[:upper:]' '[:lower:]' <<<"$1")" in true|1|yes) echo on ;; *) echo off ;; esac; }
public_url="$(env_value PublicUrl)"
version="$(app_version)"
healthz="$(curl -fsS --max-time 5 "${url}/healthz" 2>/dev/null || echo 'no answer')"

echo "PGPM"
echo "  Release:    ${version:-unknown (not answering)}"
echo "  Image:      $(app_image)"
echo "  State:      ${health}"
echo "  Health:     ${healthz} (${url}/healthz)"
echo "  Published:  ${url#http://}"
echo "  PublicUrl:  ${public_url:-not set}"
entra="$(on_off "$(env_value Entra__Enabled)")"
directory="$(on_off "$(env_value Directory__Enabled)")"
echo "  Microsoft:  SSO ${entra}, directory sync ${directory}"
mcp="$(env_value Mcp__Enabled)"
if [[ "$(on_off "${mcp:-true}")" == on ]]; then
  echo "  MCP:        on at ${public_url:-$url}/mcp (users switch their tokens on under Account → Security)"
else
  echo "  MCP:        off"
fi

db_size="" head_seq="" counts="" admins=""
if is_running "$DB_SERVICE"; then
  db_size="$(sql "SELECT pg_size_pretty(pg_database_size('pgpm'))" 2>/dev/null || true)"
  head_seq="$(sql "SELECT value FROM sync_counter WHERE id = 1" 2>/dev/null || true)"
  counts="$(sql "SELECT (SELECT count(*) FROM users WHERE is_active) || ' active users, '
                    || (SELECT count(*) FROM items WHERE NOT deleted) || ' items, '
                    || (SELECT count(*) FROM maps WHERE NOT deleted AND NOT is_template) || ' maps'" 2>/dev/null || true)"
  # Who could manage users with a password alone.
  admins="$(sql "SELECT string_agg(username, ', ' ORDER BY normalized_username) FROM users
                 WHERE is_admin AND is_active AND NOT is_totp_enabled AND password_hash IS NOT NULL" 2>/dev/null || true)"
fi
echo "  Database:   ${db_size:-unknown}${counts:+ — ${counts}}"
echo "  Sync head:  ${head_seq:-unknown}"
files="$(files_dir)"
files_size=""
# Measured as the app's user, who alone may read it; never a mount of a missing folder, which Docker would create as root.
[[ -d "$files" ]] && files_size="$(in_app du -sh "$FILES_MOUNT" 2>/dev/null | cut -f1 || true)"
echo "  Files:      ${files}${files_size:+ (${files_size})}"
backups="$(list_backups)"
latest="$(tail -1 <<<"$backups")"
count="$(grep -c . <<<"$backups" || true)"
echo "  Backups:    ${count} in $(backup_dir)${latest:+, newest $(basename "$latest")}"

warnings=()
if is_running "$SERVICE" && [[ "$healthz" == "no answer" ]]; then
  warnings+=("The app runs but does not answer /healthz: is the database up? docker compose logs --tail 50 pgpm db")
fi
[[ -n "$public_url" ]] || warnings+=("PublicUrl is empty: Microsoft sign-in and the live channel need the address users open.")
[[ "$public_url" == https://* || -z "$public_url" || "$public_url" == http://localhost* ]] \
  || warnings+=("PublicUrl is plain HTTP: passwords and session cookies travel unencrypted, and browsers install no PWA.")
[[ "$public_url" != "https://pgpm.example.com" ]] || warnings+=("PublicUrl is still the example's: set it in .env, then docker compose up -d.")
bind="$(env_value PGPM_BIND_ADDRESS)"
[[ -n "$(env_value Proxy)" || ( -n "$bind" && "$bind" != "127.0.0.1" ) ]] \
  || warnings+=("Bound to 127.0.0.1 (a reverse proxy?) but Proxy is empty: lockout and the audit log see the proxy's address.")
[[ -n "$latest" ]] || warnings+=("No backup yet: ./backup.sh, and a cron line (deploy/README.md).")
if [[ -n "$latest" ]]; then
  # Older than two days: the cron line may have stopped.
  if [[ -n "$(find "$latest" -mtime +2 2>/dev/null)" ]]; then
    warnings+=("The newest backup is older than two days: is the cron line still there?")
  fi
fi
if [[ ! -d "$files" ]]; then
  warnings+=("The files folder ${files} is missing: uploads fail. Create it for the app's user, then start again: sudo install -d -o ${APP_UID} -g ${APP_UID} -m 0750 ${files} && docker compose up -d (./update.sh does both).")
elif is_running "$SERVICE" && ! compose exec -T "$SERVICE" sh -c "test -w ${FILES_MOUNT}" >/dev/null 2>&1; then
  warnings+=("PGPM (uid ${APP_UID}) cannot write the files folder ${files}: sudo chown ${APP_UID}:${APP_UID} ${files} && sudo chmod 0750 ${files} (rootless Podman: podman unshare chown ${APP_UID}:${APP_UID} ${files}).")
fi
if [[ -n "$admins" ]]; then
  case "$(env_value Auth__TotpPolicy | tr '[:upper:]' '[:lower:]')" in
    required|requiredforadmins) warnings+=("Admins without 2FA yet: ${admins}. Auth__TotpPolicy makes them set it up before anything else.") ;;
    *) warnings+=("Admins without 2FA: ${admins}. They can set it up under their account; Auth__TotpPolicy=RequiredForAdmins makes them.") ;;
  esac
fi
if (( ${#warnings[@]} )); then
  echo
  echo "Warnings"
  printf '  - %s\n' "${warnings[@]}"
fi
