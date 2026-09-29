# Oracle provenance

The golden fixtures and the JS benchmark baseline in this directory are
**generated from the EVE.js reference implementation** and copied here. The
generators stay in the EVE.js repository (they `require` its modules), so this
repository only carries their output.

| Item | Source |
|---|---|
| Source repository | `Fokiiiiiii/evejs-v0.12.9`, branch `ccr-fd6b2132-4353h1` |
| Source commit | `70ea5d807a328fa6d00bf07c2fb6a24ddea5e816` |
| `fixtures/marshal/cases.json` | `node tools/oracle/marshalFixtures.js --write` (543 cases) |
| `fixtures/destiny/cases.json` | `node tools/oracle/destinyFixtures.js --write` (4758 cases) |
| `fixtures/gamestore/{tables,roles,rowcodec}.json` | `node tools/oracle/gamestoreFixtures.js --write` (114 runtime tables, 59 row-group tables, 241 row-codec cases) |
| `fixtures/gamestore/reference.sqlite`, `reference.expected.json` | `node tools/oracle/gamestoreSqliteFixture.js`: a gamestore.sqlite written by the real JS store (not byte-reproducible; committed as is) |
| `fixtures/services/dispatch.json` | `node tools/oracle/serviceFixtures.js --write`: request packets and the response packets the real dispatcher sends for fixture services (21 scenarios) |
| `fixtures/services/wave1.json` | `EVEJS_ORACLE_WRITE=1 node tools/run-isolated-tests.js server/tests/oracle_real_services.test.js`: the real pingService, browserLockdownSvc and subscriptionMgr behind the real dispatcher, 50 scenarios (must run under the isolated store) |
| `bench/baseline.json` | `node tools/bench/run.js --write` (machine-specific: re-record before comparing) |
| `crates/eve-marshal/src/string_table.rs` | `node tools/oracle/genStringTable.js --write` |

To refresh from a newer reference, run `oracle/scripts/sync-oracle.sh <path to
evejs checkout>`, review the diff, and update the commit above.

Fixture format (EJSON): see `tools/oracle/ejson.js` in EVE.js, and the Rust
reader in `crates/eve-oracle/src/js.rs`.
