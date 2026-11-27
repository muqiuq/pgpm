#!/usr/bin/env bash
# Write a ready-to-run .env: every setting of .env.example, with generated secrets (PNMTD's init.sh).
#
#   ./init.sh --url https://pgpm.example.com   a new installation, with the address users open (PublicUrl)
#   ./init.sh                                  …PublicUrl left at the example: set it in .env before the first start
#   ./init.sh --force                          write it again; the database password is kept
#
# It also creates the attachments' folder (PGPM_FILES_DIR, ./files) for the app's user, uid 1654, 0750 — as root
# directly, otherwise from a container of the app's image — and backups/ (0700) and logs/.
#
# The secrets exist only in .env, and Auth__InitialAdmin__Password is read exactly once — while the database has no
# users. So an existing .env is never replaced without --force, and --force keeps PGPM_DB_PASSWORD: the database keeps
# the password it was created with. The Microsoft settings (Entra__*, Directory__*) stay commented out: docs/admin-guide.md.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

URL="" FORCE=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --url)   URL="${2:?--url needs an address}"; shift 2 ;;
    --force) FORCE=1; shift ;;
    -h|--help) usage "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done
URL="${URL%/}"
[[ -z "$URL" || "$URL" =~ ^https?://[^/[:space:]]+$ ]] || die "--url: an origin such as https://pgpm.example.com, without a path."

db_password=""
if [[ -f .env ]]; then
  (( FORCE )) || die ".env exists. ./init.sh --force writes it again (the database password is kept)."
  db_password="$(env_value PGPM_DB_PASSWORD)"
fi
[[ -n "$db_password" ]] || db_password="$(random_hex 24)"
admin_password="$(random_hex 12)"

(umask 077 && cp .env.example .env.tmp)
chmod 600 .env.tmp
mv .env.tmp .env
env_set PGPM_DB_PASSWORD "$db_password"
env_set Auth__InitialAdmin__Password "$admin_password"
[[ -z "$URL" ]] || env_set PublicUrl "$URL"

mkdir -p logs
(umask 077 && mkdir -p "$(backup_dir)")
files="$(files_dir)"
if ensure_files_dir; then
  files_note="The files folder ${files} belongs to the app's user (uid ${APP_UID}, 0750)."
else
  files_note="Could not give ${files} to the app's user: sudo chown ${APP_UID}:${APP_UID} ${files} && sudo chmod 0750 ${files}"
fi

echo "Wrote .env (0600). Start the stack with: docker compose up -d && ./info.sh"
[[ -n "$URL" ]] || echo "PublicUrl is still the example's: set it in .env to the address users open, before the first start."
echo "$files_note"
echo "The first admin, created at the first start (change the password at the first sign-in):"
show_secret "$(env_value Auth__InitialAdmin__Username)" "$admin_password"
echo "It is in .env as Auth__InitialAdmin__Password, read only while the database has no users."
