# Host coverage: RPC-01539–RPC-01638

## Scope and result

The existing Host registers `fighterMgr` and `fwVictoryPointMgr`; their implementations and authoritative providers are present. This batch adds registered-Host Macho wire coverage in `dotnet/tests/Eve.Host.Tests/FighterVictoryPointRpcWireTests.cs`. The `iteritems` rows are a client-side Python dict operation, not a remote service method; the test exercises ordinary `PyDict` replies that the client iterates.

## Row-to-client evidence

| Rows | Contract and whole-client evidence |
|---|---|
| RPC-01539–01540 `CmdAbandonFighter` | `eve/client/script/parklife/fightersSvc.py:229-232` confirms before `RemoteSvc` call; EveJS delegates at `fighterMgrService.js:452-459`. |
| RPC-01541 `CmdActivateAbilitySlots` | `fightersSvc.py:188-199` sends fighters, slot and optional target/point; iterates errors and marks successful activations pending. EveJS `fighterMgrService.js:419-434`. |
| RPC-01542 `CmdDeactivateAbilitySlots` | `fightersSvc.py:201-212` iterates errors and marks successful deactivation pending. EveJS `fighterMgrService.js:436-450`. |
| RPC-01543–01544 `CmdScoopAbandonedFighterFromSpace` | `fightersSvc.py:234-235`; EveJS `fighterMgrService.js:460-468`. |
| RPC-01545–01546 `ExecuteMovementCommandOnFighters` | Orbit, follow, stop and goto wrappers are at `fightersSvc.py:73-95`; EveJS `fighterMgrService.js:408-417`. |
| RPC-01547–01548 `GetFightersForShip` | RPC at `fightersSvc.py:97-99`; all three return parts are consumed in `ui/inflight/squadrons/shipFighterState.py:210-235`: tube rows, space rows, and fighter ability-state mapping. EveJS `fighterMgrService.js:266-306` returns the same shape. |
| RPC-01549–01550 `LaunchFightersFromTubes` | `fightersSvc.py:110-118` consumes tube-keyed errors; UI entry paths include `squadronManagementController.py:20-22`, `squadronsUI.py:400-403`. EveJS `fighterMgrService.js:386-392`. |
| RPC-01551–01552 `LoadFightersToTube` | `fightersSvc.py:100-102`; UI callers `squadronManagementController.py:13-14`, `squadronsUI.py:397`; EveJS `fighterMgrService.js:308-350`. |
| RPC-01553–01554 `RecallFightersToTubes` | `fightersSvc.py:121-144` consumes fighter-keyed errors and updates successful tube status; caller `squadronManagementController.py:25-26`. EveJS `fighterMgrService.js:394-407`. |
| RPC-01555–01556 `UnloadTubeToFighterBay` | `fightersSvc.py:103-108`; callers `squadronManagementController.py:17-18`, `squadronsUI.py:400-403`, `invControllers.py:188`; EveJS `fighterMgrService.js:351-385`. |
| RPC-01557–01558 `iteritems` | No `fighterMgr.iteritems` RPC exists in V24.01. Python 2 dict iteration over launch, recall, activate and deactivate results is at `fightersSvc.py:116-117,137-143,192-197,205-210`; ability-state iteration is at `shipFighterState.py:228-229`. The wire test decodes real registered-route replies as `PyDict` and enumerates entries. A remote method would misstate the contract. |
| RPC-01634–01635 `GetAllVictoryPointStates` | `ui/services/fwVictoryPointSvc.py:44-50` reads the system-keyed map and returns `None` for absent systems; UI consumes local state at `ui/shared/infoPanels/infoPanelLocationInfo.py:389-390`. EveJS `fwVictoryPointMgrService.js:111-125` emits the keyed map. |
| RPC-01636–01638 `GetLocalVictoryPointState` | `fwVictoryPointSvc.py:30-41` consumes `[solarSystemID, state-or-None]`, retries when the returned system mismatches the session, and caches only a matching system. UI reads it at `infoPanelLocationInfo.py:389-390`; `fwVictoryPointSvc.py:52-66` consumes updates and refreshes FW UI/system status. EveJS `fwVictoryPointMgrService.js:126-140`. |

## Host and authoritative state

Host registers `FighterMgrService` with production `SpaceFighters` in `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:41-45,143-144`; RPC methods delegate through `dotnet/src/Eve.Services.Core/ClientStartupServices.cs:67-163`. `SpaceFighters` uses `CharacterShips` for bay/tube moves, the simulation for in-space orders and transitions, and sends inventory, tube, fighter and ability notifications (`dotnet/src/Eve.Services.Space/SpaceFighters.cs:26-30,86-192`). Durable inventory is owned by the ship store. Existing state-transition tests are in `dotnet/tests/Eve.Services.Space.Tests/SpaceFightersTests.cs`.

Host registers victory-point reads at `CoreRpc.cs:123`; `FactionWarfareComposition.VictoryPointStatesAsync` reads occupied systems and attacker scores at `dotnet/src/Eve.Host/Composition/FactionWarfareComposition.cs:290-292`. `FwVictoryPointMgrService` returns the keyed map and client object payload at `dotnet/src/Eve.Services.Core/FactionActivityRpcServices.cs:244-294`. Battlefield and complex resolution notifications use that state payload at `FactionWarfareComposition.cs:261-272,297-304`.

EveJS comparisons: `Fokiiiiiii/evejs-v0.12.9/server/src/services/fighter/fighterMgrService.js:266-468` and `server/src/services/faction/fwVictoryPointMgrService.js:111-140`. Its shapes and per-ID refusal maps match the V24.01 client consumers. No behavior deviation is introduced.

## Verification

- After `git fetch origin main && git merge origin/main` (fast-forward plus conflict resolution in `Rpc01790HostCoverageTests.cs`), full Host test project build passed: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore -m:1`, 0 warnings/errors (20m 20s).
- Focused Host tests for the fighter/FW RPCs and the adjacent RPC-01790 route: 3 passed, 0 failed, 0 skipped.
- The merge exposed an adjacent Host test that omitted `SessionOpen`, sent an unframed marshal value, and asserted wire strings as `PyStr`. The test now opens the session, uses the Host packet frame, and reads string tokens with the existing decoder.
- No PostgreSQL database or graphical client was used. GitHub Actions verification remains pending.
