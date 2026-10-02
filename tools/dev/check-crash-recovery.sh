#!/usr/bin/env bash
# A pilot in space, the host killed with signal 9, the host started again: the ship the last host left in space is put back in its station.
#
#   EVE_PG_URL="$(tools/dev/pg18.sh url)" EVE_MARKET_SEED=skip tools/dev/run-eve-live.sh   # with EVE_RUN='bash tools/dev/check-crash-recovery.sh'
#
# Run by tools/dev/run-eve-live.sh (EVE_RUN), which exports the schemas of the scratch database. Exits 1 when the ship is not back in a hangar.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
(cd "$HERE/tools/headless-client" && EVE_LIVE_SERVER=eve.rs .venv/bin/python scripts/crash_pilot.py "$WORK/pilot.txt" >"$WORK/pilot.log" 2>&1) &
pilot=$!
for _ in $(seq 1 200); do [ -s "$WORK/pilot.txt" ] && break; sleep 0.5; done
read -r character ship <"$WORK/pilot.txt" || [ -n "${ship:-}" ] || { echo "the pilot did not get into space"; cat "$WORK/pilot.log"; exit 1; }
where() { psql "$EVE_PG_URL" -Atc "select flag from ${EVE_INVENTORY_SCHEMA}.items where id = $ship"; }
echo "character $character, ship $ship: flag $(where) in space (0)"
host="$(pgrep -f '^dotnet .*eve-host.dll' | head -1)"
sock="$(tr '\0' '\n' </proc/"$(pgrep -x eve-net | head -1)"/environ | grep '^EVE_HOST=' | cut -d= -f2)"
simsock="$(tr '\0' '\n' </proc/"$host"/environ | grep '^EVE_SIM_SOCKET=' | cut -d= -f2)"
kill -9 "$host"; sleep 1; kill "$pilot" 2>/dev/null
[ "$(where)" = "0" ] || { echo "the ship was put back by something other than the next host: flag $(where)"; exit 1; }
rm -f "$sock"
EVE_HOST="$sock" EVE_SIM_SOCKET="$simsock" dotnet "$HERE/dotnet/src/Eve.Host/bin/Release/net10.0/eve-host.dll" >"$WORK/host2.out" 2>&1 &
restarted=$!
for _ in $(seq 1 300); do [ -S "$sock" ] && break; sleep 0.2; done
sleep 1
state="$(where)"
kill "$restarted" 2>/dev/null
grep -i "left in space" "$WORK/host2.out"
[ "$state" = "4" ] && echo "ok: the ship is in the hangar again" || { echo "FAILED: the ship's flag is $state"; exit 1; }
