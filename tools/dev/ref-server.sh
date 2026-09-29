#!/usr/bin/env bash
# The EVE.js reference server, running in the background on data generated from the SDE, so a scenario can
# be recorded from it or compared against it.
#
#   tools/dev/ref-server.sh start     generate the data if needed, start the server, wait until it listens
#   tools/dev/ref-server.sh status
#   tools/dev/ref-server.sh stop
#   tools/dev/ref-server.sh reset     stop and forget its accounts and characters (the generated tables stay)
#
# EVEJS (default /home/user/evejs-v0.12.9) is the reference checkout; it listens on EVE_REF_PORT (default 26000).
# Its state lives in .cache/ref. It takes about five minutes to listen: it loads every table first.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EVEJS="${EVEJS:-/home/user/evejs-v0.12.9}"
PORT="${EVE_REF_PORT:-26000}"
ROOT="$HERE/.cache/ref"
PIDFILE="$ROOT/server.pid"
LOG="$ROOT/server.out"

listening() { (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; }
running() { [ -f "$PIDFILE" ] && kill -0 "$(cat "$PIDFILE")" 2>/dev/null; }

stop() {
  if running; then
    kill "$(cat "$PIDFILE")" 2>/dev/null || true
    for _ in $(seq 1 20); do running || break; sleep 0.5; done
    running && kill -9 "$(cat "$PIDFILE")" 2>/dev/null || true
  fi
  rm -f "$PIDFILE"
}

case "${1:-}" in
  start)
    running && { echo "already running (pid $(cat "$PIDFILE"))"; exit 0; }
    mkdir -p "$ROOT"
    if [ ! -d "$ROOT/gameStore/data" ]; then
      SDE="$(cd "$HERE" && env -u EVE_PG_URL cargo run -q --release -p eve-sde -- ensure --cache "$HERE/.cache/sde")"
      echo "generating the reference's tables from $SDE"
      node "$EVEJS/tools/DatabaseCreator/database-creator.js" --sde-dir "$SDE" --out "$ROOT/gameStore/data" --force >"$ROOT/data.out" 2>&1
    fi
    (cd "$EVEJS/server" && EVEJS_DATA_ROOT="$ROOT" EVEJS_GAMESTORE_DATA_DIR="$ROOT/gameStore/data" \
      nohup node index.js >"$LOG" 2>&1 & echo $! >"$PIDFILE")
    for _ in $(seq 1 1200); do
      listening && { echo "listening on $PORT (pid $(cat "$PIDFILE"))"; exit 0; }
      running || { echo "the reference exited early:"; tail -20 "$LOG"; exit 1; }
      sleep 1
    done
    echo "the reference did not listen in 20 minutes"; exit 1
    ;;
  status)
    if running; then echo "running (pid $(cat "$PIDFILE"))$(listening && echo ', listening on '"$PORT")"; else echo "not running"; fi
    ;;
  stop)
    stop; echo stopped
    ;;
  reset)
    stop
    rm -f "$ROOT/gameStore/gamestore.sqlite" "$ROOT/gameStore/gamestore.sqlite-"* 2>/dev/null || true
    echo "state forgotten"
    ;;
  *)
    echo "usage: ref-server.sh start|status|stop|reset" >&2; exit 2
    ;;
esac
