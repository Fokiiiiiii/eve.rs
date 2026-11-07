# Wreck salvage persistence review handoff

Date: 2026-11-07
Worktree: `/home/user/wt-cx4-salvage-audit`, branch `cx4-salvage-audit`
Base: `75e7de4668a9` (`origin/main`)

## Scope and verified findings

- Player ship death now treats absent wreck type or an empty SDE Salvaged Materials group as a logged empty salvage bundle, then saves that outcome in the death transaction. Host composition wires the diagnostic to stderr. Regression coverage is in `Eve.Ships.Tests/DeathTests.cs`.
- The module salvage handler logs and refuses a wreck whose outcome row is missing, leaving the wreck available. Regression coverage is in `Eve.Services.Space.Tests/WorkCycleHostTests.cs`.
- Migration 0023 filters `space_items.kind = 0`; `SpaceItemKind.ContainerOrWreck = 0` in `PgSpaceItemStore.cs`, so legacy generic space containers and wrecks both receive empty outcomes. This is the documented migration choice in `docs/reference/compat/DEVIATIONS.md` (around line 6329); old yields cannot be reconstructed.
- Per-wreck outcomes intentionally follow EVE University: loot and salvage are calculated at ship destruction and access bonuses affect success speed, not yield ([Salvaging](https://wiki.eveuniversity.org/Salvage#Salvaging_wrecks)). EveJS 261028.1 rolls rewards during `executeSalvagerCycle` (`/home/user/evejs-261028.1/src/server/src/space/modules/salvagerRuntime.js:1476-1484`); the intentional difference is already recorded in `DEVIATIONS.md`.
- `PgSpaceItemStore.RemoveAsync(connection, transaction, ...)` calls `PgWreckSalvageStore.DeleteAsync` before deleting the space item, in the same transaction (`PgSpaceItemStore.cs:97-102`). Finding 5 needs no code change.
- The client salvage path was checked across eve-logic: `eve/client/script/ui/services/menuSvcExtras/droneFunctions.py:18-34, 155-160` calls `CmdSalvage` and turns per-drone refusals into UI errors; `eve/client/script/ui/services/menusvc.py:3609-3610` is the menu wrapper. Salvage objective notifications are consumed in `objectives/client/objective_task/container.py:106-119`. No client wire shape or notification behavior is changed here.
- SimClient's abyssal await path returned a canceled Task for pre-cancellation but threw synchronously for a disconnected link. It now returns a faulted Task for the link failure. Tests assert the Task error channel and use names that match their actual scope: cancellation after accepted send, and direct `CancelPending` map replacement coverage. The proposed tokenless enqueue race is closed by existing locking: `AwaitAsync` holds `_pendingGate` across its connection check, pending registration, and enqueue (`SimClient.cs:668-693`), while `FailPendingAfterLinkLoss` takes that same gate before failing pending requests (`SimClient.cs:438-445`).
- No other agent is attached to this session, so job 866 was not directly coordinated.

## Implementation and validation record

Work in this worktree:
- Finding 1 (missing SDE data during ship death): code and regression test were pushed as `4c9cf157c500b1c2c7ff509748eebf6d02a997ff`.
- Finding 3 (missing saved outcome during salvage): refusal, log, and regression test were pushed as `e5e52a5d10482079e9f7efd046494008349a8d63`.
- SimClient cancellation finding from 9bf04318: disconnected abyssal calls now return a faulted Task; test scope names are corrected. The signed fix commit is `3a5fe2cc05595eaac4d1c30e0a6591f1c9634402`, pushed to main in merge commit `186c2335eb428b5a3dfb878c88c45e1bea603cd4`.
- Findings 2 and 4 are already covered by the compatibility note above. Finding 5 is already handled transactionally at `PgSpaceItemStore.cs:97-102`.

Local checks (PostgreSQL-backed tests used `eve_p4`):
- `dotnet build dotnet/src/Eve.Ships/Eve.Ships.csproj -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- `dotnet build dotnet/src/Eve.Space/Eve.Space.csproj -p:UseSharedCompilation=false -m:2`: passed, 0 warnings/errors.
- `Eve.Ships.Tests`, DeathTests and WreckTests: 19 passed, 0 skipped.
- `Eve.Host.Tests`, full AbyssalHostFlowTests class: 21 passed, 20 skipped; the specific `Tournament_death_chain_follows_committed_ship_and_pod_deaths_before_outbox_pump` test passed separately (1 passed, 0 skipped).
- `Eve.Services.Space.Tests`, AbyssalSpaceSessionsTests, PlayerDeathTests, NpcLootHostTests, DroneHostTests, SpaceContainerTests: 100 passed, 0 skipped.
- `Eve.Services.Space.Tests`, WorkCycleHostTests: 19 passed, 0 skipped. After the second fetch/merge, all 6 salvage-related Space classes (including WorkCycleHostTests) passed: 119 passed, 0 skipped.
- `Eve.Space.Tests`, SimClientTests: 36 passed, 0 skipped.
- An initial test build caught xUnit2012 in the missing-outcome assertion and CS0619 in the synchronous exception probe. Both were fixed; the later affected test projects built and passed.

Main CI:
- Run 1577 for base `75e7de4668a9` was green before work began. Finding 1 was pushed as `4c9cf157c500b1c2c7ff509748eebf6d02a997ff`; CI 1578 (`42331039469`) and Agent Test 1375 (`42331097595`) succeeded. Finding 3 was pushed as `e5e52a5d10482079e9f7efd046494008349a8d63`; CI 1579 (`42333649695`) and Agent Test 1376 (`42333680211`) succeeded. Main then advanced to `2641c2b88414`; its CI 1580 (`42335367171`) succeeded before the SimClient push. SimClient fix `3a5fe2cc05595eaac4d1c30e0a6591f1c9634402` was pushed in merge commit `186c2335eb428b5a3dfb878c88c45e1bea603cd4`; CI 1581 (`42336819637`) and Agent Test 1377 (`42336855419`) both completed successfully with no failed tests. `failures 186c2335eb428b5a3dfb878c88c45e1bea603cd4` reported no failed tests or jobs.
