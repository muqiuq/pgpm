#!/usr/bin/env bash
# The README's screenshots (docs/images) from demo data: PGPM.WebUI/e2e/screenshots.spec.ts on an e2e stack of its own
# (tools/e2e.sh, port 7334), the pictures copied from PGPM.WebUI/test-results/readme afterwards.
#
#   tools/screenshots.sh
#   KEEP=1 tools/screenshots.sh     leave the stack running (http://localhost:7334/, the users' passwords in the spec)
set -euo pipefail
cd "$(dirname "$0")/.."

E2E_SCREENSHOTS=1 E2E_NAME="${E2E_NAME:-pgpm-shots}" E2E_PORT="${E2E_PORT:-7334}" tools/e2e.sh screenshots.spec.ts
mkdir -p docs/images
cp PGPM.WebUI/test-results/readme/*.png docs/images/
ls -l docs/images
