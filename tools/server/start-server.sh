#!/usr/bin/env bash
# Starts the eve.rs server as one production-style stack, in the same order and with the same settings as
# tools/windows/start-server.ps1: PostgreSQL 18 (the database is created if missing), the static data
# (eve-sde ensure), then eve-sim, eve-host and eve-net, all over TCP. Each one is waited for until it listens.
# Ctrl-C or SIGTERM stops them: the front first, then the host, then the simulation.
#
#   tools/server/start-server.sh          production-style start
#   tools/server/start-server.sh --dev    also honours EVE_TRACE_RPC and EVE_STRICT_RPC (cleared otherwise)
#
# Settings: the environment, then server.env in the repository root (see server.env.example), then the defaults below.
# Logs go to .cache/server/logs. See tools/windows/README.md for the same flow on Windows.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
case "${1:-}" in
  "") DEV=0 ;;
  --dev) DEV=1 ;;
  *) echo "usage: $0 [--dev]" >&2; exit 2 ;;
esac

LOG="$HERE/.cache/server/logs"
RELEASE="${CARGO_TARGET_DIR:-$HERE/target}/release"
CHILD_NAMES=()
CHILD_PIDS=()

# server.env is read as data (KEY=VALUE), never sourced; the environment keeps precedence over it.
load_server_env() {
  local file="$HERE/server.env" line key value
  [ -f "$file" ] || return 0
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    line="${line%"${line##*[![:space:]]}"}"
    case "$line" in ""|\#*) continue ;; esac
    case "$line" in *=*) ;; *) echo "error: server.env line is not KEY=VALUE: $line" >&2; exit 2 ;; esac
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || { echo "error: server.env has a bad key: $key" >&2; exit 2; }
    if [ "${#value}" -ge 2 ]; then
      case "$value" in \"*\"|\'*\') value="${value:1:${#value}-2}" ;; esac
    fi
    if [ -z "${!key:-}" ]; then export "$key=$value"; fi
  done < "$file"
}

set_defaults() {
  : "${EVE_PG_URL:=postgres://postgres@127.0.0.1:5432/eve}"
  : "${EVE_HOST:=tcp:127.0.0.1:26010}"
  : "${EVE_SIM_LISTEN:=127.0.0.1:26011}"
  : "${EVE_SIM_SOCKET:=tcp:127.0.0.1:26011}"
  : "${EVE_NET_LISTEN:=127.0.0.1:26000}"
  : "${EVE_DEV_AUTO_CREATE:=1}"
  : "${EVE_DEV_SKIP_PASSWORD:=0}"
  # the first start would fetch the NPC market from CCP's ESI; docker-compose.yml and CI skip it too
  : "${EVE_MARKET_SEED:=skip}"
  export EVE_PG_URL EVE_HOST EVE_SIM_LISTEN EVE_SIM_SOCKET EVE_NET_LISTEN EVE_DEV_AUTO_CREATE EVE_DEV_SKIP_PASSWORD EVE_MARKET_SEED
}

# Shuts the children down: Ctrl-C to each (eve-net, eve-sim and eve-host handle SIGINT), last started first.
# A child still running after 20 s is killed.
shutdown() {
  trap - EXIT
  local i pid watchdog
  for ((i = ${#CHILD_PIDS[@]} - 1; i >= 0; i--)); do
    kill -INT "${CHILD_PIDS[$i]}" 2>/dev/null || true
  done
  for ((i = ${#CHILD_PIDS[@]} - 1; i >= 0; i--)); do
    pid="${CHILD_PIDS[$i]}"
    echo "==> stopping ${CHILD_NAMES[$i]}"
    ( sleep 20; kill -KILL "$pid" 2>/dev/null && echo "==> ${CHILD_NAMES[$i]} did not stop within 20 s; killed it" >&2 ) &
    watchdog=$!
    wait "$pid" 2>/dev/null || true
    kill "$watchdog" 2>/dev/null || true
  done
}

start_child() { # name command...
  local name="$1"
  shift
  # set -m (below) gives each child its own process group, so a terminal Ctrl-C reaches only this script.
  (cd "$HERE" && exec "$@") >"$LOG/$name.out.log" 2>"$LOG/$name.err.log" &
  CHILD_NAMES+=("$name")
  CHILD_PIDS+=("$!")
}

# Is the address accepting connections? (0.0.0.0 is probed on 127.0.0.1.)
listening() {
  local address="$1" host port
  host="${address%:*}"
  port="${address##*:}"
  [ "$host" = 0.0.0.0 ] && host=127.0.0.1
  (exec 3<>"/dev/tcp/$host/$port") 2>/dev/null
}

# Is the process still running (not exited, not a zombie)?
alive() {
  local state
  state="$(ps -o stat= -p "$1" 2>/dev/null || true)"
  [ -n "$state" ] && [ "${state:0:1}" != Z ]
}

wait_until() { # name pid seconds check...
  local name="$1" pid="$2" seconds="$3" deadline
  shift 3
  deadline=$(( $(date +%s) + seconds ))
  until "$@"; do
    if ! alive "$pid"; then echo "error: $name stopped during start-up; see $LOG/$name.err.log" >&2; exit 1; fi
    if [ "$(date +%s)" -ge "$deadline" ]; then echo "error: $name is not ready after $seconds s; see $LOG" >&2; exit 1; fi
    sleep 0.25
  done
  echo "==> $name is ready"
}

load_server_env
set_defaults
if [ "$DEV" = 1 ]; then
  echo "==> development start: EVE_TRACE_RPC=${EVE_TRACE_RPC:-} EVE_STRICT_RPC=${EVE_STRICT_RPC:-}"
else
  for v in EVE_TRACE_RPC EVE_STRICT_RPC; do
    if [ -n "${!v:-}" ]; then echo "warning: $v is ignored by a production start; use --dev" >&2; unset "$v"; fi
  done
fi

# Prerequisites
command -v dotnet >/dev/null || { echo "error: .NET SDK not found: install .NET 10 (https://dotnet.microsoft.com/download)" >&2; exit 1; }
case "$(dotnet --version 2>/dev/null || true)" in 10.*) ;; *) echo "error: a .NET 10 SDK is required (global.json pins 10.0.401)" >&2; exit 1 ;; esac
command -v psql >/dev/null || { echo "error: psql not found: install the PostgreSQL 18 client tools" >&2; exit 1; }
command -v curl >/dev/null || { echo "error: curl not found (eve-sde downloads the static data with it)" >&2; exit 1; }
if command -v cargo >/dev/null; then
  echo "==> building eve-net, eve-sim and eve-sde (cargo build --release)"
  (cd "$HERE" && cargo build --release -p eve-net -p eve-sim -p eve-sde)
fi
for binary in eve-net eve-sim eve-sde; do
  [ -x "$RELEASE/$binary" ] || { echo "error: $RELEASE/$binary is not built: install Rust (https://rustup.rs)" >&2; exit 1; }
done
echo "==> building eve-host (dotnet build -c Release)"
(cd "$HERE/dotnet" && dotnet build -c Release src/Eve.Host -nologo -v q)
HOST_DLL="$HERE/dotnet/src/Eve.Host/bin/Release/net10.0/eve-host.dll"

# PostgreSQL 18: the URL is split into the server (database postgres) and the eve database
[[ "$EVE_PG_URL" =~ ^postgres(ql)?:// ]] || { echo "error: EVE_PG_URL must be a postgres:// URL" >&2; exit 1; }
mapfile -t pg_parts < <(python3 -c '
import sys, urllib.parse as u
p = u.urlsplit(sys.argv[1])
userpart = p.netloc.rsplit("@", 1)[0] + "@" if "@" in p.netloc else ""
query = "?" + p.query if p.query else ""
print(p.path.lstrip("/"))
print(f"{p.scheme}://{userpart}{p.hostname}:{p.port or 5432}/postgres{query}")
' "$EVE_PG_URL")
DB_NAME="${pg_parts[0]}"
ADMIN_URL="${pg_parts[1]}"
[[ "$DB_NAME" =~ ^[A-Za-z0-9_]+$ ]] || { echo "error: database name '$DB_NAME' must be letters, digits and underscores" >&2; exit 1; }

echo "==> checking PostgreSQL at $(printf '%s' "$ADMIN_URL" | sed -E 's#(://[^:/@]+):[^@]*@#\1:***@#')"
server_version="$(psql "$ADMIN_URL" -tAc 'show server_version_num')" \
  || { echo "error: cannot connect to PostgreSQL (see the psql error above); check EVE_PG_URL" >&2; exit 1; }
case "$server_version" in 18*) ;; *) echo "error: PostgreSQL 18 is required, the server reports version_num $server_version" >&2; exit 1 ;; esac
if [ "$(psql "$ADMIN_URL" -tAc "select 1 from pg_database where datname = '$DB_NAME'")" != 1 ]; then
  echo "==> creating database $DB_NAME"
  psql "$ADMIN_URL" -qc "create database $DB_NAME"
fi

echo "==> loading the static data (eve-sde ensure; the pinned build is downloaded on the first start)"
(cd "$HERE" && "$RELEASE/eve-sde" ensure --pg "$EVE_PG_URL")

# Start order: simulation, host (it runs the migrations), front. Each is waited for before the next.
mkdir -p "$LOG"
set -m
trap shutdown EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

start_child eve-sim "$RELEASE/eve-sim"
wait_until eve-sim "${CHILD_PIDS[-1]}" 300 listening "$EVE_SIM_LISTEN"

start_child eve-host dotnet "$HOST_DLL"
# the first start loads the catalogs and runs the migrations: give it up to ten minutes
wait_until eve-host "${CHILD_PIDS[-1]}" 600 grep -q 'eve-host: listening on' "$LOG/eve-host.err.log"

start_child eve-net "$RELEASE/eve-net"
wait_until eve-net "${CHILD_PIDS[-1]}" 60 listening "$EVE_NET_LISTEN"

echo "==> eve.rs is up: point the client at $EVE_NET_LISTEN. Press Ctrl-C to stop."
# Returns when any child exits (or on a signal, which runs the trap and then shutdown).
if wait -n; then :; fi
for i in "${!CHILD_NAMES[@]}"; do
  if ! alive "${CHILD_PIDS[$i]}"; then echo "error: ${CHILD_NAMES[$i]} stopped; see $LOG" >&2; fi
done
exit 1
