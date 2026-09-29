#!/usr/bin/env bash
# Run the headless client's live tests against the EVE.js reference server.
#
#   tools/dev/run-evejs-live.sh /path/to/evejs /path/to/sde-jsonl-dir
#
# The reference server needs the tables EVE.js derives from the SDE, so this
# generates them into a throwaway data root, boots the server on it, runs
# tests/test_live.py, and stops the server. Prerequisites: `npm ci` in
# <evejs>/server and <evejs>, and an SDE directory (sde/fetch.sh).
set -euo pipefail

EVEJS="$(cd "${1:?usage: run-evejs-live.sh /path/to/evejs /path/to/sde-dir}" && pwd)"
SDE="$(cd "${2:?usage: run-evejs-live.sh /path/to/evejs /path/to/sde-dir}" && pwd)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PORT="${EVE_LIVE_PORT:-26000}"
DATA="$(mktemp -d)"
LOG="$DATA/server.out"
server_pid=""

cleanup() {
  [ -n "$server_pid" ] && kill "$server_pid" 2>/dev/null || true
  rm -rf "$DATA"
}
trap cleanup EXIT

echo "generating the reference server's data from $SDE"
node "$EVEJS/tools/DatabaseCreator/database-creator.js" --sde-dir "$SDE" --out "$DATA/gameStore/data" --force

echo "starting the reference server (data root $DATA)"
( cd "$EVEJS/server" && EVEJS_DATA_ROOT="$DATA" node index.js >"$LOG" 2>&1 ) &
server_pid=$!

for _ in $(seq 1 120); do
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then break; fi
  kill -0 "$server_pid" 2>/dev/null || { echo "the server exited early:"; tail -30 "$LOG"; exit 1; }
  sleep 1
done
(exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null || { echo "the server never listened on $PORT:"; tail -30 "$LOG"; exit 1; }

cd "$HERE/tools/headless-client"
EVE_LIVE_HOST="127.0.0.1:$PORT" .venv/bin/python -m pytest -q tests/test_live.py
