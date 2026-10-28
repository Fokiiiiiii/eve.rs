# Rescue handoff: wt-cx3 (2026-10-28)

## Starting state and merge

The worktree had no local commits ahead of origin/main. It was at 3b66e0f4 while origin/main was ef8c09c6. Fetch and fast-forward merge to ef8c09c6 succeeded without conflicts.

The dirty set contained four service files (CrateService, LoginInitializationRpcServices, DynamicResourceCacheMgrService, StationService), four existing Host test files, a new DeepServiceHostWireTests.cs, and 25 new DEEPCHECK_*.md reports. DEVIATIONS.md had been replaced by a short single-entry file, deleting 5,901 existing lines. That truncation was discarded and the origin/main copy restored.

## Client and EveJS review evidence

- Crate calls are in V24.01 `eve/client/script/ui/crate/controller.py:94-115,273-306,364-369` and `lootItem.py:54-55,91-92,103-104`. The v0.12.9 reference handlers are in `server/src/services/campaign/crateService.js:546-704`.
- Operations progress is consumed as a mapping and defaults to `{}` in `operations/client/operationscontroller.py:144`. EveJS v0.12.9 handles the RPC in `server/src/services/character/operationsManagerService.js:315-323`.
- ESS system details and reserve keys are called from `eve/client/script/ui/shared/agencyNew/contentPieces/essSystemContentPiece.py:19-23` and `eve/client/script/ui/shared/dynamicResource/dynamicResourceSvc.py:154-157`; EveJS handlers are in `server/src/services/dynamic/dynamicResourceCacheMgrService.js:32-38`.
- Station consumers include `eve/client/script/ui/shared/maps/mapSvc.py:527-531` and the alliance list at `eve/client/script/ui/shared/neocom/Alliances/all_ui_systems.py:31-32`. The EveJS station projection is `server/src/services/station/stationService.js:67-113`.

## Verification and disposition

The Host.Tests project build succeeded with 0 warnings and 0 errors. Six focused tests passed, with 0 failures and 0 skips. The required full Host.Tests shard ran without a database and failed: 100 failed, 608 passed, 228 skipped. Sample failures assert that EVE_PG_URL is present (`ShipKillCounterPersistenceTests.cs:16`, `MiningLedgerTests.cs:48,106`). This job forbids database use, so the database-backed shard could not be made green here.

Because the mandatory pre-push full-shard gate is red, the service and test changes were discarded rather than pushed. The 25 uncommitted service-audit reports and DeepServiceHostWireTests.cs were also discarded as one unreviewed batch. The only retained change is this rescue note; no service implementation or test assertion was weakened or promoted.
