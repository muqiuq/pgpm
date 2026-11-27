#!/usr/bin/env bash
# Add a local user (PNMTD's add-user.sh). A temporary password is generated and shown once: it is valid for 7 days and
# must be changed at the first sign-in. Microsoft users need no script: their first sign-in creates them (Entra:AutoProvision).
#
#   ./add-user.sh <username> --groups Members      the rights of their groups (several: --groups Members,Viewers)
#   ./add-user.sh <username> --admin               every right, and users and groups
#   ./add-user.sh <username>                       no groups: they see what is shared with them, until an admin adds them to one
#   ./add-user.sh <username> --display-name "Ana Muster" --email ana@example.com
#   echo "$PASSWORD" | ./add-user.sh <username> --password-stdin
#                                                  a password of your choice (10–256 characters), not temporary
#
# Groups and their rights are managed in the UI (Administration → Groups & Rights); ./list-users.sh shows who is in which.
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

[[ $# -ge 1 && "$1" != -* ]] || { usage "$0"; exit 2; }
require_env
require_compose
pgpm_cmd users add "$@"
