#!/usr/bin/env bash
# Linux: start the Windows client under Wine. Not run against a real client here.
# Usage: launch-client.sh /path/to/EVE/tq [port]
# Environment: EVE_RS_SERVER overrides the server address (default 127.0.0.1).
set -euo pipefail

tq=$(cd "$1" && pwd)
port=${2:-26000}
server=${EVE_RS_SERVER:-127.0.0.1}
exe="$tq/bin64/exefile.exe"
ini="$tq/start.ini"
res="$(dirname "$tq")/ResFiles"

[[ -f "$exe" && -f "$ini" ]] || { echo "Missing bin64/exefile.exe or start.ini under $tq" >&2; exit 1; }
[[ -d "$res" ]] || { echo "ResFiles folder is missing beside the tq folder: $res" >&2; exit 1; }
grep -Eiq "^[[:space:]]*server[[:space:]]*=[[:space:]]*${server//./\\.}[[:space:]]*\r?$" "$ini" \
  || { echo "start.ini does not say server = $server. Run patch_client.py first." >&2; exit 1; }
grep -Eiq '^[[:space:]]*cryptoPack[[:space:]]*=[[:space:]]*Placebo[[:space:]]*\r?$' "$ini" \
  || { echo "start.ini does not say cryptoPack = Placebo. Run patch_client.py first." >&2; exit 1; }

export EO_REMOTEFILECACHEFOLDER="$res"
cd "$(dirname "$exe")"
exec wine "$exe" "/port:$port" /noCrashReportUpload
