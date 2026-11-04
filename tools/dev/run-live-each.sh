#!/usr/bin/env bash
# Each test file of the headless client on its own, with a time limit, against one running eve.rs; the slow ones
# included (EVE_LIVE_SLOW=1). The exit status is that of the first file that failed.
#
#   tools/dev/run-live-each.sh                       run by tools/dev/run-eve-live.sh (it sets EVE_LIVE_HOST):
#   EVE_RUN=tools/dev/run-live-each.sh tools/dev/run-eve-live.sh
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$HERE/tools/headless-client"
status=0
for file in tests/test_*.py; do
  echo "=== $file"
  EVE_LIVE_SLOW=1 EVE_LIVE_SERVER=eve.rs EVE_LIVE_STRICT_PASSWORDS=1 timeout "${EVE_LIVE_FILE_TIMEOUT:-900}" \
    .venv/bin/python -m pytest -q "$file" -rs
  code=$?
  echo "exit $code"
  [ "$code" -ne 0 ] && [ "$status" -eq 0 ] && status=$code
done
exit "$status"
