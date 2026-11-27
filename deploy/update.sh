#!/usr/bin/env bash
# Back up, then pull the release and recreate the containers, wait until PGPM is healthy and show what runs (PNMTD's
# update.sh). The new release migrates the database as it starts — after the backup, which is the way back.
#
#   ./update.sh                  the tag .env names (PGPM_IMAGE_TAG; a moving tag such as v1 gets its newest build)
#   ./update.sh --tag v1.0.1     pin another release first (written to .env)
#   ./update.sh --no-backup      without the backup
#   ./update.sh --no-pull        the image as it is on this host (built or loaded here, docker load < pgpm.tar)
#
# Connected browsers notice the new release by the live channel and offer to reload; their local data stays. Going back
# to an older release after the migrations ran: PGPM_IMAGE_TAG=<old> in .env, then ./restore.sh --force with the archive
# this script took (an older PGPM refuses a database a newer release migrated).
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

TAG="" BACKUP=1 PULL=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag) TAG="${2:?--tag needs a release}"; shift 2 ;;
    --no-backup) BACKUP=0; shift ;;
    --no-pull) PULL=0; shift ;;
    -h|--help) usage "$0"; exit 0 ;;
    *) die "unknown option: $1 (try --help)" ;;
  esac
done

require_env
require_compose
before="$(app_version)"
before_image="$(app_image)"
if [[ -n "$TAG" ]]; then
  [[ "$TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || die "\"$TAG\" is not an image tag."
  env_set PGPM_IMAGE_TAG "$TAG"
  echo "Pinned PGPM_IMAGE_TAG=${TAG}"
fi
if (( PULL )); then
  # Before the backup: a tag that does not exist stops here, with nothing changed.
  compose pull --quiet || die "Pulling $(app_image) failed: nothing was updated."
fi
if (( BACKUP )); then
  if is_running "$SERVICE"; then
    ./backup.sh || die "The backup failed: nothing was updated."
  else
    echo "PGPM is not running: no backup before the update (./backup.sh takes one of the database alone)."
  fi
fi
# A files folder that went missing is made for the app's user before the containers start: Docker would make it as root,
# and PGPM couldn't write it.
files="$(files_dir)"
if [[ ! -d "$files" ]]; then
  ensure_files_dir && echo "Created the files folder ${files} for the app's user (uid ${APP_UID}, 0750)." \
    || echo "Could not create the files folder ${files} for the app's user; uploads fail until it exists." >&2
fi
compose up -d --force-recreate --wait || {
  compose logs --tail 40 "$SERVICE" >&2 || true
  die "PGPM did not come up healthy (the log above). The backup in $(backup_dir) is the way back: ./restore.sh <archive>."
}
wait_healthy 120 || die "The containers run, but $(local_url)/healthz does not answer."
after="$(app_version)"
echo "Updated: ${before_image} (${before:-not running}) → $(app_image) (${after:-unknown})."
./info.sh
