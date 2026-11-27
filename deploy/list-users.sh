#!/usr/bin/env bash
# List the users: admin or their groups, active or not, 2FA, password or Microsoft, lockout or a pending password
# change, last sign-in.
#
#   ./list-users.sh
set -euo pipefail
cd "$(dirname "$0")"
# shellcheck source=deploy/lib.sh
. ./lib.sh

case "${1:-}" in
  -h|--help) usage "$0"; exit 0 ;;
  "") ;;
  *) die "list-users.sh takes no arguments (try --help)" ;;
esac
require_env
require_compose
pgpm_cmd users list
