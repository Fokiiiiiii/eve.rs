# POS module association and posMgr follow-up

Date: 2026-11-13

Client: V24.01 build 3396210, full source tree `/home/user/eve-logic`

Executable reference: EveJS v0.12.9, `/home/user/evejs-261108.1`

Database: `eve_p3`, pinned SDE build 3396210 under `.cache/sde`

## Result

The prior audit had eight real `posMgr` gaps. This follow-up adds durable POS module deployment and tower association, then closes three rows after registered-Host PostgreSQL tests: `ChangeStructureProvisionType`, `GetStarbasePermissions`, and the supported silo graph behavior of `LinkResourceForTower`. Five rows remain `PARTIAL-TESTED`: sentry settings, force cycle, usage permission enforcement, and tower-password force-field admission. See `implement-posmgr.md` and `service-audit/EVE_RS_COVERAGE.csv` for the current row statuses.

## Client and EveJS evidence

The whole client checkout was searched for POS RPC and structure anchor callsites. Relevant V24 callsites are:

- `eve/client/script/ui/inflight/moonmining.py:704-743`: reads an indexable usage rowset and saves deploy and usage flags.
- `eve/client/script/ui/inflight/moonmining.py:1271-1316`: saves resource connections and calls `ChangeStructureProvisionType` after a silo or moon material selection.
- `eve/client/script/ui/inflight/moonmining.py:541-562,1128-1137`: reads and saves the five sentry policy values.
- `eve/client/script/ui/inflight/moonmining.py:1342-1357`: runs a forced moon process cycle and optionally reads process information.
- `eve/client/script/ui/inflight/moonmining.py:1195-1219` and `eve/client/script/parklife/playerOwned.py:173-180`: use the four-argument and two-argument tower-password call forms; `False` retains the existing password during the force-field edit.
- `eve/client/script/parklife/playerOwned.py:193-202`: invokes `AnchorStructure` for the client anchor-drop-for-structures effect; `eve/client/script/ui/services/menusvc.py:2469-2487` requests lift/unanchor.

EveJS implementation evidence:

- `server/src/services/starbase/starbaseModuleRuntime.js:190-207,283-370,390-440,490-518` owns explicit module-to-tower association, module anchoring and online eligibility/transitions.
- `server/src/services/planet/posMgrService.js:450-472` formats and persists permission rows; `server/src/services/starbase/starbaseAccessRuntime.js:173-181,296-311` includes associated modules and later enforces their usage flags.
- `server/src/services/planet/posMgrService.js:591-604` routes provision/link RPCs; `server/src/services/starbase/starbaseProductionRuntime.js:141-210` validates associated modules, silo ports, duplicate links, and cycles.
- `server/src/services/starbase/starbaseProductionCycles.js:188-206` refuses a forced cycle with no links. The active `config/pos-legacy-production.json` is `unconfigured`; moon abundance and reaction authority are absent.
- `server/src/services/starbase/starbaseDefenseController.js:149-152` reads sentry settings while evaluating battery behavior.
- `server/src/services/planet/posMgrService.js:430-447` persists tower password settings and refreshes force-field state; `server/src/services/starbase/starbaseAccessRuntime.js:162-170,216-263` supplies the harmonic/admission and ship-password checks.

## Implemented state and wire behavior

- `space.deployed` now stores `control_tower_id`; its kind check admits `starbase_module` and has a partial lookup index. `PgDeployedStore` reads/writes the association and queries modules by tower.
- V24 anchor-drop structures (category 23, effect 1022) are persisted as starbase modules. `AnchorStructure` validates ownership, skill, tower association/range, interaction distance, SDE delay, and deploy permission; completion and scene restoration preserve the association and slim state.
- `ChangeStructureProvisionType` accepts associated offline silos and only SDE/client-supported silo cargo groups; it persists selections, clears links that touch the module, and sends `OnMoonProcessChange` after commit. It also permits clearing a harvester with `None`, matching EveJS when no moon profile exists. Assigning a moon resource or reactor recipe fails closed because the active legacy authority is unconfigured.
- `GetStarbasePermissions` returns the tower and associated module rows in the V24 rowset shape. `SetStarbasePermissions` validates that submitted rows identify the tower or current modules, rejects duplicate rows, and persists all values under the tower row lock. Tower deploy flags still drive existing anchor/unanchor/online/offline checks.
- `LinkResourceForTower` validates associated silo pairs, matching provision types, unique edges, and acyclic graphs in the same tower transaction. It persists sorted links and sends `OnMoonProcessChange(towerID, [])` after commit. Harvester and reactor ports are not guessed.

## E2E test and failures observed before fixes

`dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs::PosMgr_anchors_POS_modules_and_roundtrips_module_permissions_and_production_links_over_host_wire` uses the real registered `PosMgrService`, loopback Host Macho sockets, the PostgreSQL `eve_p3` database, and pinned SDE types. It anchors two silos and a harvester to a tower, checks association persistence and restore hydration, applies two provision updates concurrently over separate registered Host connections, retries one update, mutates and reads permissions/links, verifies V24 row and notification wire shapes, and verifies failed inputs preserve saved state.

The test was added before module and graph behavior. It first failed at module launch because the Host had no POS module state. The follow-up then added a harvester clear scenario; it failed because eve.rs rejected `None` even though EveJS permits clearing it without a moon profile. A duplicate usage-row assertion then failed because the Host silently normalized duplicate rows. Those behaviors were corrected without weakening assertions.

Latest focused run:

```text
EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3 /root/.dotnet/dotnet test dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~PosMgr_anchors_POS_modules_and_roundtrips_module_permissions_and_production_links_over_host_wire -warnaserror
Passed: 1, Failed: 0, Skipped: 0
```

Follow-up verification after the final permission and persistence assertions:

- The full `ControlTowerHostTests` class on `eve_p3` passed: 7 passed, 0 failed, 0 skipped. This includes the new registered Host scenario.
- The focused Host persistence slice covering `AbyssalHostFlowTests`, `WarEligibilityOwnerSourcesTests`, `TournamentMatchPersistenceTests`, `NeedlejackActivationHostTests`, `TournamentArenaPersistenceTests`, and the two `PosMgr` methods passed with 39 passed and 0 failed. It reported 21 skipped tests because `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE` were not configured; those skips are not counted as passes.
- Release builds with warnings treated as errors succeeded with 0 warnings and 0 errors for `Eve.Ships`, `Eve.Services.Space`, and `Eve.Services.Space.Tests`.
- `cargo fmt -p eve-space`, `cargo check -p eve-space`, and `cargo clippy -p eve-space --all-targets -- -D warnings` succeeded. The focused control-tower deployable test passed (1 passed); its other 1,022 unit tests and integration-test cases were filtered out and not run.

## Remaining gaps and limits

- `GetTowerSentrySettings` and `SetTowerSentrySettings` persist and return V24 values, but there is no associated battery controller that consumes the sentry policy.
- `SetStarbasePermissions` persists module usage settings, but there is no POS array/container operation that checks `viewput`, `viewputtake`, or `use` before inventory mutations.
- `SetTowerPassword` stores a salted verifier and corp/alliance flags, but no force-field harmonic or movement/admission boundary consumes them.
- `RunMoonProcessCycleforTower` preserves the GM check and no-links refusal. It does not process a connected graph because module onlining/resource accounting and an authoritative production-cycle inventory runner are not available. The pinned legacy EveJS authority is unconfigured for reactions; the SDE has no historical moon abundance field.
- No graphical V24 client was launched. No complete EveJS POS flow was run; behavior was compared by source traces. Broader class builds/tests and CI gate are tracked separately from the focused result above.
