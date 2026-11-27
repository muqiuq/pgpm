#!/usr/bin/env bash
# A full backup in one archive named after the moment it was taken (PNMTD's backup.sh, as one file).
#
#   ./backup.sh                       -> backups/pgpm-20261127-033000.tar
#   ./backup.sh --out /srv/pgpm-bak   another destination
#   ./backup.sh --keep 14             then delete all but the newest 14 archives there (default: PGPM_BACKUP_KEEP in .env)
#   ./backup.sh --quiet               only complain, for cron
#   ./backup.sh --no-files            without the attachments, for a host that backs up their folder itself
#
# The archive (tar, 0600, in a 0700 folder: it holds password hashes) contains:
#   manifest.txt      what was backed up: the release, the image, the time, the sync head
#   SHA256SUMS        a checksum per part; ./restore.sh --verify-only checks an archive without touching anything
#   database.pgdump   pg_dump --format=custom: one consistent snapshot of the database, taken while PGPM runs
#   keys.tar.gz       the Data Protection key ring: without it sessions end and the 2FA secrets can't be read
#   files.tar         the attachments' folder (PGPM_FILES_DIR), without uploads in progress (.tmp/); uncompressed,
#                     as images and documents are compressed already
# The files come after the database: an upload in between is an extra file, which the hourly cleanup removes as one no row
# names; a file purged from the trash in between may be named by the dump and missing from the tar (the hourly file
# check lists it). It does not hold .env — keep a copy of that elsewhere (the database password, the Entra secret).
#
# Cron, daily at 03:30:
#   30 3 * * * /srv/pgpm/deploy/backup.sh --quiet --keep 14 >>/srv/pgpm/deploy/logs/backup.log 2>&1
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

OUT="" KEEP="" QUIET=0 FILES=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out)   OUT="${2:?--out needs a folder}"; shift 2 ;;
    --keep)  KEEP="${2:?--keep needs a number}"; shift 2 ;;
    --quiet) QUIET=1; shift ;;
    --no-files) FILES=0; shift ;;
    -h|--help) usage "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done
[[ -z "$KEEP" || "$KEEP" =~ ^[1-9][0-9]*$ ]] || die "--keep: a number of backups, 1 or more."

require_env
require_compose
OUT="${OUT:-$(backup_dir)}"
OUT="${OUT%/}"
KEEP="${KEEP:-$(env_value PGPM_BACKUP_KEEP)}"
[[ -z "$KEEP" || "$KEEP" =~ ^[1-9][0-9]*$ ]] || die "PGPM_BACKUP_KEEP in .env: a number of backups, 1 or more."

is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
stamp="$(date +%Y%m%d-%H%M%S)"
name="pgpm-${stamp}"
(umask 077 && mkdir -p "$OUT")
work="${OUT}/.${name}.part"
archive="${OUT}/${name}.tar"
[[ ! -e "$archive" ]] || die "${archive} exists already (two backups in the same second?)."
(umask 077 && mkdir -p "$work")
trap 'rm -rf "$work" "${archive}.part"' EXIT

# 1. The database: pg_dump reads one snapshot, so the dump is consistent while users keep working.
compose exec -T "$DB_SERVICE" pg_dump -U pgpm -d pgpm --format=custom >"$work/database.pgdump"
[[ -s "$work/database.pgdump" ]] || die "pg_dump wrote nothing."
head_seq="$(sql "SELECT value FROM sync_counter WHERE id = 1" 2>/dev/null || true)"

# 2. The key ring, as the app's user who owns it (0700).
in_app tar -czf - -C "$(dirname "$KEYS_MOUNT")" "$(basename "$KEYS_MOUNT")" >"$work/keys.tar.gz"
[[ -s "$work/keys.tar.gz" ]] || die "keys.tar.gz is empty: is the volume mounted?"

# 3. The attachments, as the app's user through the service's own mount; .tmp/ holds uploads in progress only. A file the
# cleanup removes while tar walks the folder is no failure: GNU tar exits 1 for it, 2 only for a real one.
if (( FILES )); then
  if [[ -d "$(files_dir)" ]]; then
    status=0
    in_app tar --warning=no-file-removed --warning=no-file-changed -cf - --exclude=./.tmp -C "$FILES_MOUNT" . \
      >"$work/files.tar" || status=$?
    (( status <= 1 )) || die "files.tar failed (tar exited ${status}): no archive written."
  else
    say "No files folder at $(files_dir): files.tar left out."
  fi
fi

version="$(app_version)"
{
  echo "format=pgpm-backup 1"
  echo "created=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "version=${version:-unknown (the app was not running)}"
  echo "image=$(app_image)"
  echo "sync_head=${head_seq:-unknown}"
  echo "files=$([[ -f "$work/files.tar" ]] && echo yes || echo no)"
  echo "public_url=$(env_value PublicUrl)"
} >"$work/manifest.txt"
parts=(manifest.txt database.pgdump keys.tar.gz)
[[ -f "$work/files.tar" ]] && parts+=(files.tar)
for part in "${parts[@]}"; do
  printf '%s  %s\n' "$(sha256_of "$work/$part")" "$part"
done >"$work/SHA256SUMS"

(umask 077 && tar -cf "${archive}.part" -C "$work" SHA256SUMS "${parts[@]}")
chmod 600 "${archive}.part"
mv "${archive}.part" "$archive"
say "Backup written: ${archive} ($(du -h "$archive" | cut -f1))"

if [[ -n "$KEEP" ]]; then
  # Only archives named pgpm-<stamp>.tar are ever deleted, the oldest first.
  total="$(list_backups "$OUT" | wc -l | tr -d ' ')"
  if (( total > KEEP )); then
    list_backups "$OUT" | head -n "$(( total - KEEP ))" | while IFS= read -r old; do
      rm -f -- "$old"
      say "Deleted old backup: ${old}"
    done
  fi
fi
