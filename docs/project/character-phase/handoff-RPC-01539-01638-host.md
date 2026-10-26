# Host coverage: RPC-01539–RPC-01638

## Scope and contract

This batch covers the existing `fighterMgr` and `fwVictoryPointMgr` routes. Production Host wiring and state owners already exist: fighter RPCs use `SpaceFighters` and `CharacterShips`/the simulation; victory-point reads use faction-warfare state. The Host wire regression runs packets through `HostServerTests.Start`, `PacketDispatcher`, and a registered service. Its fighter provider is a fixture so the test isolates route and marshal behavior; state mutations and notifications remain covered by `SpaceFightersTests` and faction-warfare composition tests. Production registration is in `CoreRpc.cs`.

## Whole-client trace and EveJS comparison

The following callsites/consumers were checked across `/home/user/eve-logic` with whole-tree Serena search and source reads.

| Rows | V24.01 callers and reply consumers |
|---|---|
| RPC-01539–01540 `CmdAbandonFighter` | `eve/client/script/parklife/fightersSvc.py:229-232` prompts then sends the fighter ID. EveJS delegates to the fighter controller at `server/src/services/fighter/fighterMgrService.js:453-459`. |
| RPC-01541 `CmdActivateAbilitySlots` | `fightersSvc.py:188-199` sends fighter IDs, slot and optional target/point; iterates per-fighter errors and marks success pending. EveJS: `fighterMgrService.js:420-435`. |
| RPC-01542 `CmdDeactivateAbilitySlots` | `fightersSvc.py:201-212` iterates per-fighter errors and marks successful deactivation pending. EveJS: `fighterMgrService.js:437-451`. |
| RPC-01543–01544 `CmdScoopAbandonedFighterFromSpace` | `fightersSvc.py:234-235` forwards fighter and destination flag IDs. EveJS: `fighterMgrService.js:461-468`. |
| RPC-01545–01546 `ExecuteMovementCommandOnFighters` | Orbit/follow/stop/goto wrappers and remote call: `fightersSvc.py:73-95`; EveJS: `fighterMgrService.js:409-418`. |
| RPC-01547–01548 `GetFightersForShip` | Remote call: `fightersSvc.py:97-99`; `shipFighterState.py:210-235` unpacks tube rows, space rows, and fighter-keyed ability states, then updates local/UI state. `tacticalNavigation/tacticalNavigationService.py:342` also reads the space rows. EveJS returns the same three-part shape at `fighterMgrService.js:267-307`. |
| RPC-01549–01550 `LaunchFightersFromTubes` | `fightersSvc.py:110-118` filters ready tubes and consumes tube-keyed errors; UI entry paths include `squadrons/squadronManagementController.py:20-22` and `squadrons/squadronsUI.py:400-403`. EveJS: `fighterMgrService.js:387-393`. |
| RPC-01551–01552 `LoadFightersToTube` | `fightersSvc.py:100-102`; UI callers include `squadrons/squadronManagementController.py:13-14`, `squadrons/squadronsUI.py:397`, and `squadronManagementCont.py:127,245`. EveJS: `fighterMgrService.js:309-350`. |
| RPC-01553–01554 `RecallFightersToTubes` | `fightersSvc.py:121-144` filters fighters by tube state, consumes fighter-keyed errors and marks successful tubes recalling; caller `squadrons/squadronManagementController.py:25-26`. EveJS: `fighterMgrService.js:395-407`. |
| RPC-01555–01556 `UnloadTubeToFighterBay` | `fightersSvc.py:103-108`; callers include `squadrons/squadronManagementController.py:17-18`, `squadrons/squadronsUI.py:400-403`, and `environment/invControllers.py:188`. EveJS: `fighterMgrService.js:352-385`. |
| RPC-01557–01558 `iteritems` | These are local Python dictionary iterations, not service methods. The client iterates launch/recall/ability error maps at `fightersSvc.py:116-117,137-143,192-197,205-210` and ability-state mappings at `shipFighterState.py:228-229`. The registered route test decodes actual `PyDict` replies and iterates their entries. EveJS returns ordinary dictionaries for the per-ID maps. |
| RPC-01634–01635 `GetAllVictoryPointStates` | `eve/client/script/ui/services/fwVictoryPointSvc.py:43-47` indexes the system-keyed map and returns `None` when absent; location UI uses local state at `ui/shared/infoPanels/infoPanelLocationInfo.py:389-390`. EveJS: `server/src/services/faction/fwVictoryPointMgrService.js:112-125`. |
| RPC-01636–01638 `GetLocalVictoryPointState` | `fwVictoryPointSvc.py:30-41` consumes `[solarSystemID, state-or-None]`, retries a mismatched system, and caches only a matching system. The info panel reads it at `infoPanelLocationInfo.py:389-390`; `fwVictoryPointSvc.py:52-66` consumes updates and refreshes FW UI/system status. EveJS: `fwVictoryPointMgrService.js:127-140`. |

EveJS references are from `Fokiiiiiii/evejs-v0.12.9`. No protocol deviation is introduced. The local `iteritems` rows are not registered as RPCs because the V24.01 client applies that operation to returned dictionaries.

## Host wiring and state

- `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:41-45,143-144` registers `FighterMgrService` with production `SpaceFighters`; the service delegates at `dotnet/src/Eve.Services.Core/ClientStartupServices.cs:67-163`.
- `dotnet/src/Eve.Services.Space/SpaceFighters.cs:26-30,86-192` uses ship inventory and simulation state and emits inventory, tube, fighter, and ability updates. Ship inventory is persisted by its existing owner.
- `CoreRpc.cs:123` registers victory-point reads; `dotnet/src/Eve.Host/Composition/FactionWarfareComposition.cs:290-292` reads authoritative states and `FactionActivityRpcServices.cs:227-294` builds the client reply. Victory-point updates use the same payload in `FactionWarfareComposition.cs:261-272,297-304`.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore -p:BuildProjectReferences=false`: passed, 0 warnings/errors (6m 23s).
- Focused real-Host wire test `Fighter_manager_maps_and_state_shapes_cross_the_registered_host_rpc`: passed, 1 passed, 0 failed, 0 skipped.
- A prior selected Actions run for task commit `922b3f1d` passed (run 40282911210); push CI for that commit passed (run 40282887458). No PostgreSQL database or graphical-client session was used.
- This audit added no production code; the inherited Host routes and state transitions were already implemented. The local build and wire test above were rerun at the current worktree state.


## Latest push verification

- Commit `316cba77` was pushed to `main`. Selected Actions run [40287323412](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40287323412) completed successfully: focused Host test passed with no failed tests or jobs.
- `python3 tools/dev/ci-status.py failures 316cba77` reported that no `ci.yml` run was found for this documentation-and-verification commit.
