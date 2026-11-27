#!/usr/bin/env bash
# deploy/ for real (PNMTD's deploy-test.sh, adapted): a scratch copy of deploy/ with a compose project and a port of its
# own runs init.sh, docker compose up, the first admin's sign-in and password change, add-user.sh and list-users.sh, a
# note and an attachment through the sync and file API, backup.sh (archive, checksums, retention), restore.sh refusing
# a damaged archive and a full files folder, the stack wiped, init.sh again and restore.sh into the fresh stack — the
# note, the file, the passwords and the sessions are back, a note written after the backup is not, and every device is
# reset —, update.sh to the same image, info.sh, and down.
#
#   tools/deploy-test.sh                       the image pgpm:dev (./start.sh, tools/contract.sh build it)
#   IMAGE=pgpm:h3 tools/deploy-test.sh         another image of this host
#   DEPLOY_TEST_PROJECT=pgpm-deploytest-2 DEPLOY_TEST_PORT=7391 tools/deploy-test.sh
#                                              another stack beside this one
#   KEEP=1 tools/deploy-test.sh                leave the stack and its scratch folder (.tmpcheck/deploy-test.*)
#
# Needs docker with compose (the plugin or docker-compose), curl and python3; on a Mac the docker CLI may talk to Podman.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="${DEPLOY_TEST_PROJECT:-pgpm-deploytest}"
PORT="${DEPLOY_TEST_PORT:-7390}"
KEEP="${KEEP:-0}"
export COMPOSE_PROJECT_NAME="$PROJECT"
expected_version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"

mkdir -p .tmpcheck
dir=$(mktemp -d "$PWD/.tmpcheck/deploy-test.XXXXXX")
# The scripts and the stack only: a deploy/ folder in use here may hold an .env, backups and files of its own.
cp deploy/*.sh deploy/docker-compose.yml deploy/.env.example "$dir/"
cd "$dir"
# shellcheck source=deploy/lib.sh
. ./lib.sh

# The files folder's contents are the app's user's (uid 1654) on Linux: emptied from a container of the image.
wipe_files() {
  [[ ! -d "$dir/files" ]] || docker run --rm --user 0 --entrypoint sh -v "$dir/files:/files" "$(app_image)" \
    -c 'find /files -mindepth 1 -delete' >/dev/null 2>&1 || true
}
cleanup() {
  if [[ "$KEEP" != "1" ]]; then
    compose down -v --remove-orphans >/dev/null 2>&1 || true
    wipe_files
    cd / && rm -rf "$dir"
  fi
}
trap cleanup EXIT

# The containers' state and the app's last lines, then the reason: last, where run-tests.sh's tail and a CI log show it.
fail() {
  { compose ps -a; compose logs --tail 30 pgpm; } >&2 2>/dev/null || true
  echo "FAIL: $*" >&2
  exit 1
}

# Podman names local images localhost/…, Docker docker.io/library/…. Exported before init.sh, which gives the files folder
# to the app's user from a container of the image.
ref="${IMAGE:-pgpm:dev}"
[[ "$ref" == *:* ]] || ref="${ref}:latest"
if [[ "$ref" != */* ]] && docker image inspect "localhost/${ref}" >/dev/null 2>&1; then ref="localhost/${ref}"; fi
docker image inspect "$ref" >/dev/null 2>&1 || fail "no image ${ref} on this host (build it first: run-tests.sh --only deploy does)"
image="${ref%:*}" tag="${ref##*:}"
export PGPM_IMAGE="$image" PGPM_IMAGE_TAG="$tag"

# A stack of a previous run (KEEP=1, or killed) under the same project name goes first.
compose down -v --remove-orphans >/dev/null 2>&1 || true

# --- helpers -----------------------------------------------------------------------------------------------------------

base="http://localhost:${PORT}"
# api JAR METHOD PATH [JSON] → the status, the body in $dir/body. Unsafe calls carry the XSRF-TOKEN cookie in X-XSRF-TOKEN.
api() {
  local jar="$dir/$1.jar" token
  token="$(awk '$6 == "XSRF-TOKEN" {print $7}' "$jar" 2>/dev/null | tail -1)"
  curl -s -o "$dir/body" -w '%{http_code}' -b "$jar" -c "$jar" -X "$2" -H 'Content-Type: application/json' \
    ${token:+-H "X-XSRF-TOKEN: $token"} ${4:+--data "$4"} "${base}$3"
}
# bytes JAR METHOD PATH FILE → the status: the file as the raw body (a chunk).
bytes() {
  local jar="$dir/$1.jar" token
  token="$(awk '$6 == "XSRF-TOKEN" {print $7}' "$jar" | tail -1)"
  curl -s -o "$dir/body" -w '%{http_code}' -b "$jar" -c "$jar" -X "$2" -H 'Content-Type: application/octet-stream' \
    -H "X-XSRF-TOKEN: $token" --data-binary "@$4" "${base}$3"
}
login() { rm -f "$dir/$1.jar"; api "$1" POST /api/v1/auth/login "{\"username\":\"$2\",\"password\":\"$3\"}"; }
body() { cat "$dir/body"; }
# A UUID v7 (the sync wants one for a new row), and a value of the body's JSON by a Python expression over `d`.
uuid7() {
  python3 -I -c 'import os, time, uuid
b = bytearray(int(time.time() * 1000).to_bytes(6, "big") + os.urandom(10))
b[6] = (b[6] & 0x0F) | 0x70
b[8] = (b[8] & 0x3F) | 0x80
print(uuid.UUID(bytes=bytes(b)))'
}
json() { python3 -I -c 'import json, sys; d = json.load(open(sys.argv[1])); print(eval(sys.argv[2], {"d": d}))' "$dir/body" "$1"; }
now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
# sync JAR CLIENT SINCE EPOCH PULL [MUTATIONS] → the status, the answer in $dir/body.
sync() {
  api "$1" POST /api/v1/sync "{\"clientId\":\"$2\",\"appVersion\":\"deploy-test\",\"since\":$3,\"accessEpoch\":$4,\"limit\":500,\"pull\":$5,\"mutations\":[${6:-}],\"resolved\":[],\"pending\":0,\"conflicts\":0}"
}
insert() {  # insert TABLE ID ROW-JSON BATCH
  printf '{"mutationId":"%s","batch":"%s","table":"%s","id":"%s","op":"insert","baseRev":null,"changes":null,"row":%s,"clientChangedAt":"%s"}' \
    "$(uuid7)" "$4" "$1" "$2" "$3" "$(now)"
}
all_accepted() { [[ "$(json 'all(r["status"] == "accepted" for r in d["results"])')" == "True" ]]; }
# Every item title a new device of the user pulls, one per line (all pages).
pulled_titles() {
  local client since=0 more=True
  client="$(uuid7)"
  : >"$dir/titles"
  while [[ "$more" == "True" ]]; do
    [[ "$(sync "$1" "$client" "$since" 0 true)" == "200" ]] || fail "a pull: $(body)"
    json '"\n".join(r.get("title") or "" for r in d["changes"].get("items", []) if not r.get("deleted"))' >>"$dir/titles"
    since="$(json 'd["cursor"]')"
    more="$(json 'd["more"]')"
  done
  cat "$dir/titles"
}
mode_of() { stat -c %a "$1" 2>/dev/null || stat -f %Lp "$1"; }

# --- init, up ----------------------------------------------------------------------------------------------------------

./init.sh --url "$base" >"$dir/init.out" || fail "init.sh: $(cat "$dir/init.out")"
[[ "$(mode_of .env)" == "600" ]] || fail ".env is not 0600"
[[ -d files && -d backups ]] || fail "init.sh made no files or backups folder"
admin_password="$(env_value Auth__InitialAdmin__Password)"
[[ -n "$(env_value PGPM_DB_PASSWORD)" && ${#admin_password} -ge 20 ]] || fail "init.sh generated no secrets: $(cat .env)"
grep -q "^    admin: ${admin_password}$" "$dir/init.out" || fail "init.sh did not show the first admin's password"
if ./init.sh >/dev/null 2>&1; then fail "init.sh replaced an existing .env without --force"; fi
env_set PGPM_IMAGE "$image"
env_set PGPM_IMAGE_TAG "$tag"
env_set PGPM_PORT "$PORT"
env_set COMPOSE_PROJECT_NAME "$PROJECT"

compose up -d --wait >"$dir/up.out" 2>&1 || fail "the stack did not come up healthy: $(tail -3 "$dir/up.out")"
wait_healthy 120 || fail "/healthz does not answer"
./info.sh --quiet || fail "info.sh --quiet says it is not healthy"
[[ "$(curl -fsS "${base}/healthz")" == "ok" ]] || fail "/healthz"
[[ "$(app_version)" == "$expected_version" ]] || fail "/api/v1/info says $(app_version), not ${expected_version}"
[[ "$(curl -s -o /dev/null -w '%{http_code}' "${base}/api/v1/auth/me")" == "401" ]] || fail "/api/v1/auth/me without a session"

# --- the first admin, a user, a note with an attachment ---------------------------------------------------------------

[[ "$(login admin admin "$admin_password")" == "200" ]] || fail "the first admin's sign-in: $(body)"
[[ "$(json 'd["me"]["flags"]["mustChangePassword"]')" == "True" ]] || fail "the first admin need not change the password: $(body)"
[[ "$(api admin GET /api/v1/users)" == "403" ]] || fail "a session with a password to change lists users"
[[ "$(api admin POST /api/v1/account/password "{\"currentPassword\":\"$admin_password\",\"newPassword\":\"deploy-test-admin-1\"}")" == "204" ]] \
  || fail "changing the first admin's password: $(body)"
[[ "$(api admin GET /api/v1/users)" == "200" ]] || fail "users after the password change: $(body)"

./add-user.sh ana --groups Members --display-name "Ana Example" >"$dir/add-user.out" || fail "add-user.sh: $(cat "$dir/add-user.out")"
ana_temporary="$(awk '/^    [A-Za-z0-9]+$/ {print $1}' "$dir/add-user.out")"
[[ -n "$ana_temporary" ]] || fail "add-user.sh printed no password: $(cat "$dir/add-user.out")"
users="$(./list-users.sh)" || fail "list-users.sh: $users"
grep -Eq '^ana +.*Members' <<<"$users" || fail "list-users.sh: $users"
[[ "$(login ana ana "$ana_temporary")" == "200" ]] || fail "sign-in with add-user.sh's password: $(body)"
[[ "$(api ana POST /api/v1/account/password "{\"currentPassword\":\"$ana_temporary\",\"newPassword\":\"deploy-test-ana-1\"}")" == "204" ]] \
  || fail "ana's password change: $(body)"

device="$(uuid7)"
[[ "$(sync ana "$device" 0 0 true)" == "200" ]] || fail "ana's first sync: $(body)"
epoch="$(json 'd["accessEpoch"]')"
note="$(uuid7)" file="$(uuid7)" batch="$(uuid7)"
printf 'Attached before the backup.\n' >"$dir/attachment.txt"
size="$(wc -c <"$dir/attachment.txt" | tr -d ' ')"
sha="$(sha256_of "$dir/attachment.txt")"
mutations="$(insert items "$note" "{\"id\":\"$note\",\"type\":\"note\",\"title\":\"Before the backup\",\"tags\":[]}" "$batch"),$(insert files "$file" \
  "{\"itemId\":\"$note\",\"purpose\":\"attachment\",\"fileName\":\"attachment.txt\",\"contentType\":\"text/plain\",\"sizeBytes\":$size,\"sha256\":\"$sha\",\"width\":null,\"height\":null,\"thumbnail\":null}" "$batch")"
[[ "$(sync ana "$device" 0 "$epoch" false "$mutations")" == "200" ]] && all_accepted || fail "the note and its file row: $(body)"
[[ "$(bytes ana PUT "/api/v1/files/${file}/chunks/0" "$dir/attachment.txt")" == "200" ]] || fail "the chunk: $(body)"
[[ "$(api ana POST "/api/v1/files/${file}/commit" "{\"sha256\":\"$sha\",\"size\":$size}")" == "200" ]] || fail "the commit: $(body)"
[[ "$(json 'd["status"]')" == "available" ]] || fail "the committed file: $(body)"
[[ "$(api ana GET "/api/v1/files/${file}/attachment.txt")" == "200" && "$(body)" == "Attached before the backup." ]] || fail "the download: $(body)"
owner="$(compose exec -T pgpm sh -c "stat -c %u ${FILES_MOUNT}/*/*/${file}-*")" || fail "the stored file is not in the files folder"
[[ "$owner" == "$APP_UID" ]] || fail "the stored file belongs to uid ${owner}, not the app's ${APP_UID}"
[[ "$(sync ana "$device" 0 "$epoch" true)" == "200" ]] || fail "ana's pull: $(body)"
cursor="$(json 'd["cursor"]')"

# --- backup ------------------------------------------------------------------------------------------------------------

./backup.sh --quiet --keep 3 || fail "backup.sh"
archive="$(list_backups | tail -1)"
[[ -n "$archive" ]] || fail "backup.sh wrote no archive: $(ls -la backups)"
[[ "$(mode_of "$archive")" == "600" && "$(mode_of backups)" == "700" ]] || fail "the archive is not 0600 in a 0700 folder"
listing="$(tar -tf "$archive")"
for part in SHA256SUMS manifest.txt database.pgdump keys.tar.gz files.tar; do
  grep -qx "$part" <<<"$listing" || fail "the archive lacks ${part}: ${listing}"
done
grep -qx "version=${expected_version}" <<<"$(tar -xOf "$archive" manifest.txt)" || fail "manifest.txt: $(tar -xOf "$archive" manifest.txt)"
files_listing="$(tar -xOf "$archive" files.tar | tar -tf -)"
grep -q "/${file}-" <<<"$files_listing" || fail "files.tar lacks the attachment: ${files_listing}"
./restore.sh "$archive" --verify-only >"$dir/verify.out" 2>&1 || fail "restore.sh --verify-only: $(cat "$dir/verify.out")"
grep -q 'complete and intact' "$dir/verify.out" || fail "restore.sh --verify-only: $(cat "$dir/verify.out")"
# Retention: three more archives elsewhere, the newest two kept.
for _ in 1 2 3; do
  ./backup.sh --quiet --no-files --keep 2 --out "$dir/rotation" || fail "backup.sh --out --keep"
  sleep 1
done
[[ "$(list_backups "$dir/rotation" | wc -l | tr -d ' ')" == "2" ]] || fail "--keep 2 kept: $(ls "$dir/rotation")"
rotated="$(tar -tf "$(list_backups "$dir/rotation" | tail -1)")"
! grep -qx files.tar <<<"$rotated" || fail "--no-files wrote files.tar: ${rotated}"

# A damaged archive is refused before anything changes.
mkdir "$dir/damaged" && tar -xf "$archive" -C "$dir/damaged"
printf 'x' >>"$dir/damaged/database.pgdump"
tar -cf "$dir/damaged.tar" -C "$dir/damaged" SHA256SUMS manifest.txt database.pgdump keys.tar.gz files.tar
if ./restore.sh "$dir/damaged.tar" --yes --force >"$dir/damaged.out" 2>&1; then fail "restore.sh took a damaged archive"; fi
grep -q 'database.pgdump is damaged' "$dir/damaged.out" || fail "restore.sh of a damaged archive: $(tail -3 "$dir/damaged.out")"

# A note after the backup: gone after the restore.
later="$(uuid7)"
mutations="$(insert items "$later" "{\"id\":\"$later\",\"type\":\"note\",\"title\":\"After the backup\",\"tags\":[]}" "$(uuid7)")"
[[ "$(sync ana "$device" "$cursor" "$epoch" false "$mutations")" == "200" ]] && all_accepted || fail "the note after the backup: $(body)"
[[ "$(sync ana "$device" "$cursor" "$epoch" true)" == "200" ]] || fail "ana's pull after the backup: $(body)"
cursor="$(json 'd["cursor"]')"

# Into this stack, whose files folder is full: refused without --force, and nothing changed.
if ./restore.sh "$archive" --yes >"$dir/restore.out" 2>&1; then fail "restore.sh into a full files folder without --force"; fi
grep -q 'not empty' "$dir/restore.out" || fail "restore.sh without --force: $(tail -3 "$dir/restore.out")"
[[ "$(sql "SELECT count(*) FROM items WHERE title = 'After the backup'")" == "1" ]] || fail "the refused restore changed the database"

# --- wipe, restore into a fresh stack ----------------------------------------------------------------------------------

cp "$archive" "$dir/kept.tar"
compose down -v --remove-orphans >"$dir/down.out" 2>&1 || fail "compose down: $(cat "$dir/down.out")"
[[ -z "$(docker volume ls -q --filter "name=${PROJECT}_")" ]] || fail "volumes left after down -v: $(docker volume ls -q --filter "name=${PROJECT}_")"
wipe_files
rm -rf files backups .env
./init.sh --url "$base" >"$dir/init2.out" || fail "init.sh for the fresh stack: $(cat "$dir/init2.out")"
fresh_admin_password="$(env_value Auth__InitialAdmin__Password)"
env_set PGPM_IMAGE "$image"
env_set PGPM_IMAGE_TAG "$tag"
env_set PGPM_PORT "$PORT"
env_set COMPOSE_PROJECT_NAME "$PROJECT"
./restore.sh "$dir/kept.tar" --yes >"$dir/restore.out" 2>&1 || fail "restore.sh into the fresh stack: $(tail -8 "$dir/restore.out")"
grep -q "PGPM ${expected_version} is healthy" "$dir/restore.out" || fail "restore.sh: $(tail -3 "$dir/restore.out")"

# The key ring is the backup's: the admin's session from before the backup still holds.
[[ "$(api admin GET /api/v1/auth/me)" == "200" ]] || fail "the admin's session from before the backup after the restore: $(body)"
# The database is the backup's: the passwords changed before it count, the fresh .env's first admin does not.
[[ "$(login fresh admin "$fresh_admin_password")" == "401" ]] || fail "the fresh .env's first admin password signs in after the restore"
[[ "$(login admin admin deploy-test-admin-1)" == "200" && "$(json 'd["me"]["flags"]["mustChangePassword"]')" == "False" ]] \
  || fail "the admin's sign-in after the restore: $(body)"
[[ "$(login ana ana deploy-test-ana-1)" == "200" ]] || fail "ana's sign-in after the restore: $(body)"
# Every device starts over: ana's, past the backup's head, is reset.
[[ "$(sync ana "$device" "$cursor" "$epoch" true)" == "200" && "$(json 'd["reset"]')" == "True" ]] || fail "ana's device was not reset: $(body)"
titles="$(pulled_titles ana)"
grep -qx "Before the backup" <<<"$titles" || fail "the note is not back: ${titles}"
! grep -qx "After the backup" <<<"$titles" || fail "the note written after the backup is back"
[[ "$(api ana GET "/api/v1/files/${file}/attachment.txt")" == "200" && "$(body)" == "Attached before the backup." ]] || fail "the file after the restore: $(body)"

# --- update to the same image, info, down ------------------------------------------------------------------------------

before="$(list_backups | wc -l | tr -d ' ')"
./update.sh --tag "$tag" --no-pull >"$dir/update.out" 2>&1 || fail "update.sh: $(tail -8 "$dir/update.out")"
grep -q "Updated: .*(${expected_version}) → .*(${expected_version})" "$dir/update.out" || fail "update.sh: $(tail -5 "$dir/update.out")"
[[ "$(list_backups | wc -l | tr -d ' ')" == "$(( before + 1 ))" ]] || fail "update.sh took no backup first"
./info.sh --quiet || fail "not healthy after update.sh"
[[ "$(login admin admin deploy-test-admin-1)" == "200" ]] || fail "the admin's sign-in after update.sh: $(body)"
info="$(./info.sh)" || fail "info.sh: $info"
grep -q "^  Release:    ${expected_version}$" <<<"$info" || fail "info.sh: $info"
grep -Eq '^  Sync head:  [0-9]+$' <<<"$info" || fail "info.sh shows no sync head: $info"
grep -Eq '^  Database:   [0-9]+ [kMG]?B — 2 active users, ' <<<"$info" || fail "info.sh shows no database size: $info"
grep -q 'Admins without 2FA: admin' <<<"$info" || fail "info.sh does not warn about the admin without 2FA: $info"
compose down -v --remove-orphans >"$dir/down.out" 2>&1 || fail "compose down: $(cat "$dir/down.out")"
[[ -z "$(compose ps -aq)" ]] || fail "containers left after down"

echo "deploy/: init, up, sign-in, users, a note and a file, backup, restore into a fresh stack, update, info, down — all as expected."
