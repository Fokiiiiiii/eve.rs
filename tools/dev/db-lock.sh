#!/usr/bin/env bash
# Run a command under the lock of its PostgreSQL database, so agents on different databases run in parallel.
#
#   EVE_PG_URL=$(tools/dev/pg18.sh url eve_p2) tools/dev/db-lock.sh dotnet test ...
#   EVE_PG_URL="Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p3" tools/dev/db-lock.sh tools/dev/run-eve-live.sh
#
# The database is the one named by EVE_PG_URL (postgres://... or an Npgsql string with Database=); unset means eve.
# eve keeps the historical lock /tmp/eve-heavy.lock; every other database locks /tmp/eve-heavy-<database>.lock.
# A waiter gives up after 2 hours; the command is killed after 30 minutes (a hung test must not block the queue).
set -euo pipefail

if [ "$#" -eq 0 ]; then
  echo "usage: EVE_PG_URL=... tools/dev/db-lock.sh <command> [args...]" >&2
  exit 2
fi

db="$(python3 -I -c "
import re, sys, urllib.parse
url = sys.argv[1].strip()
if not url:
    name = 'eve'
elif url.startswith(('postgres://', 'postgresql://')):
    name = urllib.parse.urlsplit(url).path.lstrip('/') or 'eve'
else:
    match = re.search(r'(?i)(?:^|;)\s*Database\s*=\s*([^;]+)', url)
    name = match.group(1).strip() if match else 'eve'
if not re.fullmatch(r'[a-z0-9_]+', name):
    sys.exit('db-lock: not a database name: ' + name)
print(name)
" "${EVE_PG_URL:-}")"

if [ "$db" = eve ]; then
  lock=/tmp/eve-heavy.lock
else
  lock="/tmp/eve-heavy-$db.lock"
fi
exec flock -o -w 7200 "$lock" timeout 1800 "$@"
