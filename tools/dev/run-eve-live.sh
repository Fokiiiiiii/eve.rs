#!/usr/bin/env bash
# Run the headless client's live tests against eve.rs: the C# service host, the Rust network front and the Rust
# space simulation, started on private sockets and a local port, then stopped.
#
#   tools/dev/run-eve-live.sh              the same tests/test_live.py that runs against the reference server
#   EVE_RUN='<command>' tools/dev/run-eve-live.sh   run <command> instead (EVE_LIVE_HOST is set)
#
# With EVE_PG_URL set the host keeps its accounts, characters and items in PostgreSQL (in scratch schemas, dropped
# at the end) and serves the character screen from the loaded static data (`eve-sde ensure`); without it accounts
# live in memory and there are no characters.
# EVE_LIVE_PORT (default 26100) is where the front listens. Passwords are checked (an unknown user name
# creates the account; the same name needs the same password afterwards), so the wrong-password test runs too.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PORT="${EVE_LIVE_PORT:-26100}"
SOCK="$(mktemp -u /tmp/eve-host.XXXXXX.sock)"
SIMSOCK="$(mktemp -u /tmp/eve-sim.XXXXXX.sock)"
LOG="$(mktemp -d)"
host_pid=""
net_pid=""
sim_pid=""

SCRATCH=""
if [ -n "${EVE_PG_URL:-}" ]; then
  SCRATCH="live_$$"
  export EVE_IDENTITY_SCHEMA="${SCRATCH}_identity" EVE_CHARACTERS_SCHEMA="${SCRATCH}_characters" EVE_INVENTORY_SCHEMA="${SCRATCH}_inventory" EVE_SKILLS_SCHEMA="${SCRATCH}_skills" EVE_WALLET_SCHEMA="${SCRATCH}_wallet" EVE_FITTING_SCHEMA="${SCRATCH}_fitting"
fi

cleanup() {
  if [ -n "$SCRATCH" ]; then
    for schema in "$EVE_IDENTITY_SCHEMA" "$EVE_CHARACTERS_SCHEMA" "$EVE_INVENTORY_SCHEMA" "$EVE_SKILLS_SCHEMA" "$EVE_WALLET_SCHEMA" "$EVE_FITTING_SCHEMA"; do
      psql "$EVE_PG_URL" -qc "DROP SCHEMA IF EXISTS $schema CASCADE" 2>/dev/null || true
    done
  fi
  [ -n "$net_pid" ] && kill "$net_pid" 2>/dev/null || true
  [ -n "$host_pid" ] && kill "$host_pid" 2>/dev/null || true
  [ -n "$sim_pid" ] && kill "$sim_pid" 2>/dev/null || true
  rm -f "$SOCK" "$SIMSOCK"
  if [ "${KEEP_LOGS:-0}" = "1" ]; then echo "logs in $LOG"; else rm -rf "$LOG"; fi
}
trap cleanup EXIT

echo "building the host, the front and the simulation"
(cd "$HERE/dotnet" && dotnet build -c Release src/Eve.Host -nologo -v q 2>&1 | tail -3)
(cd "$HERE" && cargo build -q --release -p eve-net -p eve-sim)

# the simulation reads the static data itself (EVE_SDE_DIR, or the copy `eve-sde ensure` keeps under .cache/sde)
(cd "$HERE" && exec env EVE_SIM_SOCKET="$SIMSOCK" "$HERE/target/release/eve-sim" >"$LOG/sim.out" 2>&1) &
sim_pid=$!
for _ in $(seq 1 300); do
  [ -S "$SIMSOCK" ] && break
  kill -0 "$sim_pid" 2>/dev/null || { echo "the simulation exited early:"; cat "$LOG/sim.out"; exit 1; }
  sleep 0.1
done

EVE_HOST="$SOCK" EVE_SIM_SOCKET="$SIMSOCK" dotnet "$HERE/dotnet/src/Eve.Host/bin/Release/net10.0/eve-host.dll" >"$LOG/host.out" 2>&1 &
host_pid=$!
for _ in $(seq 1 100); do
  [ -S "$SOCK" ] && break
  kill -0 "$host_pid" 2>/dev/null || { echo "the host exited early:"; cat "$LOG/host.out"; exit 1; }
  sleep 0.1
done

EVE_HOST="$SOCK" EVE_NET_LISTEN="127.0.0.1:$PORT" "$HERE/target/release/eve-net" >"$LOG/net.out" 2>&1 &
net_pid=$!
for _ in $(seq 1 100); do
  (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break
  kill -0 "$net_pid" 2>/dev/null || { echo "the front exited early:"; cat "$LOG/net.out"; exit 1; }
  sleep 0.1
done

export EVE_LIVE_HOST="127.0.0.1:$PORT"
if [ -n "${EVE_RUN:-}" ]; then
  # any other command against the running server, e.g. EVE_RUN='target/release/eve-loadgen $EVE_LIVE_HOST'
  cd "$HERE" && bash -c "$EVE_RUN"
else
  cd "$HERE/tools/headless-client"
  EVE_LIVE_SERVER=eve.rs EVE_LIVE_STRICT_PASSWORDS=1 .venv/bin/python -m pytest -q tests/test_live.py tests/test_login_to_character.py tests/test_undock.py tests/test_movement.py tests/test_warp.py tests/test_jump.py -rs
fi
