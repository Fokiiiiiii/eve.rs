#!/usr/bin/env bash
# A local PostgreSQL 18 cluster for development and tests.
#
#   tools/dev/pg18.sh install   fetch the PostgreSQL 18 server binaries (npm: @embedded-postgres)
#   tools/dev/pg18.sh start     initdb on first use, then start on 127.0.0.1:${PGPORT:-5432}
#   tools/dev/pg18.sh stop | status
#   tools/dev/pg18.sh url [db]  print the connection URL of the `eve` database, or of database db (see docs/project/DATABASES.md)
#
# The binaries are the upstream PostgreSQL 18 build that the embedded-postgres
# project publishes to npm. Prefer the distribution package (PGDG `postgresql-18`)
# where it is reachable; this script exists for environments where it is not.
# PostgreSQL refuses to run as root, so a `pguser` account is used in that case.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CACHE="$HERE/.cache"
PGROOT="$CACHE/pg18"
BIN="$PGROOT/node_modules/@embedded-postgres/linux-x64/native/bin"
DATA="$CACHE/pgdata"
PORT="${PGPORT:-5432}"
VERSION="${EMBEDDED_POSTGRES_VERSION:-18.4.0-beta.17}"

as_pg() {
  if [ "$(id -u)" -eq 0 ]; then runuser -u pguser -- "$@"; else "$@"; fi
}

case "${1:-}" in
  install)
    mkdir -p "$PGROOT"
    ( cd "$PGROOT" && { [ -f package.json ] || npm init -y >/dev/null; } \
      && npm install --no-audit --no-fund "@embedded-postgres/linux-x64@$VERSION" )
    "$BIN/postgres" --version
    ;;
  start)
    [ -x "$BIN/postgres" ] || "$0" install
    if [ "$(id -u)" -eq 0 ]; then
      id pguser >/dev/null 2>&1 || useradd -m pguser
      mkdir -p "$DATA"; chown -R pguser "$DATA"
      # every directory on the way to the binaries must be traversable
      chmod -R a+rX "$PGROOT"
    fi
    [ -f "$DATA/PG_VERSION" ] || as_pg "$BIN/initdb" -D "$DATA" -U postgres --auth=trust -E UTF8 --locale=C.UTF-8 >/dev/null
    if ! as_pg "$BIN/pg_ctl" -D "$DATA" status >/dev/null 2>&1; then
      # a development cluster: the tests need no durability (a crash can corrupt it; initdb a new one then)
      as_pg "$BIN/pg_ctl" -D "$DATA" -l "$DATA/server.log" -w \
        -o "-p $PORT -c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp -c fsync=off -c full_page_writes=off -c synchronous_commit=off" start >/dev/null
    fi
    psql -h 127.0.0.1 -p "$PORT" -U postgres -tAc "select 1 from pg_database where datname='eve'" | grep -q 1 \
      || psql -h 127.0.0.1 -p "$PORT" -U postgres -qc "create database eve"
    psql -h 127.0.0.1 -p "$PORT" -U postgres -d eve -tAc "select 'PostgreSQL ' || current_setting('server_version')"
    ;;
  stop)   as_pg "$BIN/pg_ctl" -D "$DATA" -m fast stop ;;
  status) as_pg "$BIN/pg_ctl" -D "$DATA" status ;;
  url)
    db="${2:-eve}"
    [[ "$db" =~ ^[a-z0-9_]+$ ]] || { echo "pg18.sh: not a database name: $db" >&2; exit 2; }
    echo "postgres://postgres@127.0.0.1:$PORT/$db" ;;
  *) echo "usage: $0 install|start|stop|status|url" >&2; exit 2 ;;
esac
