#!/usr/bin/env bash
# A throwaway PostgreSQL for the integration tests (PGPM.Tests), as in PNMTD:
#
#   tools/test-db.sh [start]   start it (port 55432), or say it is running
#   tools/test-db.sh stop      remove it
#
# The tests use it by default; PGPM_TEST_DB names another server. Each test run creates its own databases (pgpm_test,
# pgpm_test_*) on it, so nothing has to be cleaned up between runs – and a server another project's script started on
# the same port (PNMTD's pnmtd-test-pg: the same image and password) serves just as well: then it is used as it is.
set -euo pipefail
ENGINE="${ENGINE:-podman}"
NAME="${TEST_DB_NAME:-pgpm-test-pg}"
PORT="${TEST_DB_PORT:-55432}"
IMAGE="${POSTGRES_IMAGE:-docker.io/library/postgres:18-alpine}"
case "${1:-start}" in
  start)
    if [[ -n "$("$ENGINE" ps -q --filter "name=^${NAME}$")" ]]; then
      echo "$NAME is running on port $PORT"
    else
      holder=$("$ENGINE" ps --format '{{.Names}} {{.Ports}}' | awk -v port=":${PORT}->" 'index($0, port) { print $1; exit }')
      if [[ -n "$holder" ]]; then
        if "$ENGINE" exec "$holder" pg_isready -q -h 127.0.0.1 -U postgres 2>/dev/null; then
          echo "Port $PORT is served by the container $holder (PostgreSQL): the tests use it, in databases of their own."
          exit 0
        fi
        echo "Port $PORT is taken by the container $holder, which is no PostgreSQL: stop it, or TEST_DB_PORT=… and PGPM_TEST_DB=…" >&2
        exit 1
      fi
      "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
      # Durability off: test data only, and much faster.
      "$ENGINE" run -d --name "$NAME" -p "127.0.0.1:${PORT}:5432" -e POSTGRES_PASSWORD=test \
        "$IMAGE" -c fsync=off -c synchronous_commit=off -c full_page_writes=off >/dev/null
      for _ in $(seq 1 60); do
        "$ENGINE" exec "$NAME" pg_isready -q -h 127.0.0.1 -U postgres 2>/dev/null && break
        sleep 0.5
      done
      echo "$NAME started on port $PORT"
    fi
    echo "PGPM_TEST_DB='Host=127.0.0.1;Port=${PORT};Username=postgres;Password=test' (the tests' default)"
    ;;
  stop)
    "$ENGINE" rm -f "$NAME" >/dev/null && echo "$NAME removed"
    ;;
  *) echo "usage: $0 [start|stop]" >&2; exit 2 ;;
esac
