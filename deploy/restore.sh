#!/usr/bin/env bash
# Put a backup archive back: into this stack, or into a fresh one (a new host: git clone, ./init.sh, then this).
#
#   ./restore.sh backups/pgpm-20261127-033000.tar
#   ./restore.sh <archive> --yes           without asking
#   ./restore.sh <archive> --force         and replace what the files folder holds
#   ./restore.sh <archive> --verify-only   check the archive (checksums, the dump, the key ring, the files), change nothing
#
# The archive is checked first, before anything changes: every part against SHA256SUMS, the dump's table of contents
# (pg_restore --list), the tar files. Then — after the question — PGPM stops, the database is replaced by the dump, the key
# ring by the backup's, the files folder by files.tar (into an empty folder, or with --force in place of its contents; an
# archive without files.tar leaves the folder as it is), and PGPM starts again. Everything since the backup is lost.
#
# Every device starts over: the restore raises every user's access epoch, so each browser sends what it still has to
# send, drops its local copy and loads everything again at its next sync (a change made after the backup comes back as
# a pending change or a conflict on that device, never silently). An archive of an older release is migrated as PGPM
# starts; one of a newer release than this image is refused by PGPM (./update.sh --tag first).
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

ARCHIVE="" YES=0 FORCE=0 VERIFY_ONLY=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) YES=1; shift ;;
    --force) FORCE=1; shift ;;
    --verify-only) VERIFY_ONLY=1; shift ;;
    -h|--help) usage "$0"; exit 0 ;;
    -*) die "unknown option: $1 (try --help)" ;;
    *) [[ -z "$ARCHIVE" ]] || die "one archive, please"; ARCHIVE="$1"; shift ;;
  esac
done
[[ -n "$ARCHIVE" ]] || die "usage: ./restore.sh <backup archive> [--yes] [--force] [--verify-only]"
[[ -f "$ARCHIVE" && -r "$ARCHIVE" ]] || die "${ARCHIVE}: no such file."

require_env
require_compose

# --- 1. the archive ----------------------------------------------------------------------------------------------------

sha256_stream() { if command -v sha256sum >/dev/null 2>&1; then sha256sum | cut -d' ' -f1; else shasum -a 256 | cut -d' ' -f1; fi; }
member() { tar -xOf "$ARCHIVE" "$1"; }

listing="$(tar -tf "$ARCHIVE" 2>/dev/null)" || die "${ARCHIVE} is no tar archive (damaged, or not a backup of ./backup.sh)."
for required in SHA256SUMS manifest.txt database.pgdump keys.tar.gz; do
  grep -qx "$required" <<<"$listing" || die "${ARCHIVE} lacks ${required}: not a backup of ./backup.sh."
done
manifest="$(member manifest.txt)"
grep -qx 'format=pgpm-backup 1' <<<"$manifest" || die "${ARCHIVE}: an unknown backup format ($(head -1 <<<"$manifest"))."
sums="$(member SHA256SUMS)"
while read -r expected part; do
  [[ -n "$part" ]] || continue
  grep -qx "$part" <<<"$listing" || die "${ARCHIVE}: ${part} is named in SHA256SUMS but missing."
  actual="$(member "$part" | sha256_stream)"
  [[ "$actual" == "$expected" ]] || die "${ARCHIVE}: ${part} is damaged (its SHA-256 differs from SHA256SUMS). Nothing changed."
done <<<"$sums"
grep -q ' database.pgdump$' <<<"$sums" && grep -q ' keys.tar.gz$' <<<"$sums" || die "${ARCHIVE}: SHA256SUMS is incomplete."
[[ "$(member database.pgdump | head -c 5)" == "PGDMP" ]] || die "${ARCHIVE}: database.pgdump is no pg_dump archive."
member keys.tar.gz | tar -tzf - >/dev/null 2>&1 || die "${ARCHIVE}: keys.tar.gz cannot be read."
has_files=0
if grep -qx files.tar <<<"$listing"; then
  has_files=1
  member files.tar | tar -tf - >/dev/null 2>&1 || die "${ARCHIVE}: files.tar cannot be read."
fi
manifest_value() { sed -n "s/^$1=//p" <<<"$manifest" | head -1; }

# The dump's table of contents, read by the database's own pg_restore (started for it if needed; --verify-only only
# when it runs anyway).
if (( ! VERIFY_ONLY )) || is_running "$DB_SERVICE"; then
  is_running "$DB_SERVICE" || compose up -d --wait "$DB_SERVICE" >/dev/null
  member database.pgdump | compose exec -T "$DB_SERVICE" pg_restore --list >/dev/null 2>&1 \
    || die "${ARCHIVE}: pg_restore cannot read database.pgdump. Nothing changed."
fi
echo "${ARCHIVE}: complete and intact — taken $(manifest_value created) from PGPM $(manifest_value version), sync head $(manifest_value sync_head)$( (( has_files )) && echo ", with files" || echo ", without files")."
(( ! VERIFY_ONLY )) || exit 0

# --- 2. this stack -----------------------------------------------------------------------------------------------------

# Before anything changes: the files folder must take the backup's files.
if (( has_files )); then
  [[ -d "$(files_dir)" ]] || ensure_files_dir || die "Could not create the files folder $(files_dir) for the app's user (uid ${APP_UID})."
  files_state() { in_app sh -c "test -w ${FILES_MOUNT} || echo unwritable; ls -A ${FILES_MOUNT} | grep -vx .tmp | head -1"; }
  state="$(files_state)"
  if [[ "$state" == unwritable* ]]; then
    ensure_files_dir || true
    state="$(files_state)"
  fi
  [[ "$state" != unwritable* ]] || die "The app's user (uid ${APP_UID}) cannot write $(files_dir): sudo chown ${APP_UID}:${APP_UID} $(files_dir) && sudo chmod 0750 $(files_dir)"
  [[ -z "$state" || "$FORCE" == 1 ]] || die "The files folder $(files_dir) is not empty: --force replaces its contents with the backup's. Nothing changed."
fi

if (( ! YES )); then
  users="$(sql "SELECT count(*) FROM users" 2>/dev/null || echo "no")"
  echo "This replaces the database (now: ${users} users), the key ring$( (( has_files )) && echo " and the files") with the backup of $(manifest_value created)."
  echo "Everything since then is lost; every device loads its data again."
  read -r -p "Type restore to go on: " reply
  [[ "$reply" == "restore" ]] || die "Nothing changed."
fi

# --- 3. the restore ----------------------------------------------------------------------------------------------------

say "Stopping PGPM…"
compose stop "$SERVICE" >/dev/null 2>&1 || true
say "Restoring the database…"
compose exec -T "$DB_SERVICE" dropdb -U pgpm --if-exists --force pgpm
compose exec -T "$DB_SERVICE" createdb -U pgpm pgpm
member database.pgdump | compose exec -T "$DB_SERVICE" pg_restore -U pgpm -d pgpm --no-owner --exit-on-error
# Every device starts over (docs/03 §11.6: a changed epoch is a reset), so none keeps rows or a cursor the database lost.
sql "UPDATE users SET access_epoch = access_epoch + 1" >/dev/null
say "Restoring the key ring…"
member keys.tar.gz | in_app sh -c "find ${KEYS_MOUNT} -mindepth 1 -delete && tar -xzf - -C $(dirname "$KEYS_MOUNT")"
if (( has_files )); then
  say "Restoring the files…"
  # As the app's user, through the service's own mount: what it extracts is its own.
  member files.tar | in_app sh -c "find ${FILES_MOUNT} -mindepth 1 -delete && tar -xf - --no-overwrite-dir -C ${FILES_MOUNT}"
else
  echo "No files.tar in ${ARCHIVE}: the files folder is left as it is."
fi
say "Starting PGPM…"
compose up -d "$SERVICE" >/dev/null
if wait_healthy 240; then
  echo "Restored ${ARCHIVE}: PGPM $(app_version) is healthy at $(local_url)/."
else
  compose logs --tail 30 "$SERVICE" >&2 || true
  die "Restored ${ARCHIVE}, but PGPM did not become healthy (the log above; ./info.sh)."
fi
