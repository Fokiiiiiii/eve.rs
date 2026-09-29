# Oracle provenance

The golden fixtures and the JS benchmark baseline in this directory are
**generated from the EVE.js reference implementation** and copied here. The
generators stay in the EVE.js repository (they `require` its modules), so this
repository only carries their output.

| Item | Source |
|---|---|
| Source repository | `Fokiiiiiii/evejs-v0.12.9`, branch `ccr-fd6b2132-4353h1` |
| Source commit | `3dc49bb275656c6d9df1ae6c3c0fb102a3b354c0` |
| `fixtures/marshal/cases.json` | `node tools/oracle/marshalFixtures.js --write` (543 cases) |
| `fixtures/destiny/cases.json` | `node tools/oracle/destinyFixtures.js --write` (4758 cases) |
| `bench/baseline.json` | `node tools/bench/run.js --write` (machine-specific: re-record before comparing) |
| `crates/eve-marshal/src/string_table.rs` | `node tools/oracle/genStringTable.js --write` |

To refresh from a newer reference, run `oracle/scripts/sync-oracle.sh <path to
evejs checkout>`, review the diff, and update the commit above.

Fixture format (EJSON): see `tools/oracle/ejson.js` in EVE.js, and the Rust
reader in `crates/eve-oracle/src/js.rs`.
