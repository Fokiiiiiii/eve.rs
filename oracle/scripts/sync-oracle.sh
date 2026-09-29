#!/usr/bin/env bash
# Regenerate the golden fixtures from an EVE.js checkout and copy them here.
#   oracle/scripts/sync-oracle.sh /path/to/evejs
set -euo pipefail

EVEJS="${1:?usage: sync-oracle.sh /path/to/evejs}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

( cd "$EVEJS" \
  && node tools/oracle/marshalFixtures.js --write \
  && node tools/oracle/destinyFixtures.js --write \
  && node tools/oracle/genStringTable.js --write )

cp "$EVEJS/tools/oracle/fixtures/marshal/cases.json" "$HERE/oracle/fixtures/marshal/cases.json"
cp "$EVEJS/tools/oracle/fixtures/destiny/cases.json" "$HERE/oracle/fixtures/destiny/cases.json"
cp "$EVEJS/rust/crates/eve-marshal/src/string_table.rs" "$HERE/crates/eve-marshal/src/string_table.rs"

echo "synced from $(git -C "$EVEJS" rev-parse HEAD); update oracle/PROVENANCE.md"
