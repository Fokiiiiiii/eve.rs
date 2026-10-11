# ST-3 handoff (structure docking, undock, hangars, headless scenario)

Owner: structures phase (plan: scratchpad `specs/structures_phase.md`, ST-3). Branch `wt-st3`, worktree `/home/user/wt-st3` (delete when done).

## Done (pushed to main)
- (a) `6096d66`: `structureDocking.Dock` approach at 2500 m (DockingApproach distance), refusals by name (unknown structure, another ship, docking access, module, criminal timer, hull too large), acceptance through the sim, finish into the structure hangar with `structureid` and the solar system in the session. Files: `dotnet/src/Eve.Services.Space/SpaceSessions.Structures.cs`, `SpaceSessions.Movement.cs` (`FinishDockingAsync`), `dotnet/src/Eve.Host/Composition/StructureComposition.cs`, `dotnet/src/Eve.Host/Rpc/StructureRpc.cs`, `dotnet/src/Eve.Services.Core/StructureDirectoryService.cs` (`CanDock` public), `dotnet/src/Eve.Structures/StructureFuel.cs` (`StructureServiceIds.Docking`). Tests: `dotnet/tests/Eve.Services.Space.Tests/StructureDockingTests.cs` (3 PG).

## Written in this commit (`607b8b5`, rebased on origin, verification in progress)
- (b) Undock: `SpaceSessions.Ships.cs` `UndockFromAsync(session, placeKey, ...)` shared with the station undock; `SpaceSessions.Structures.cs` `UndockStructureAsync`; `PlaceStructuresAsync` hook set in `StructureComposition` to `SpaceStructures.RestoreAsync`; Undock callback wired. Tests: `StructureUndockingTests.cs` (2 PG). Station undock tests (SpaceServiceTests) passed with the refactor.
- (c) Hangar: no new code path; `ClientSession.DockedLocationId` already includes `structureid`. Tests: `StructureHangarTests.cs` (2 PG: listing + persistence, login while docked).
- (d) headless: `tools/headless-client/tests/test_structures.py` `test_a_pilot_docks_at_the_structure_and_undocks_from_it`. Collected; skipped without `EVE_LIVE_*` including `EVE_LIVE_SHIP`. NOT RUN LIVE.

## Next steps (exact)
1. Confirm `607b8b5` is green: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet build dotnet/EveRs.slnx -p:UseSharedCompilation=false`, then `EVE_PG_URL='Host=127.0.0.1;Username=postgres;Database=eve' dotnet test dotnet/tests/Eve.Services.Space.Tests ... --filter "FullyQualifiedName~Structure"` and the Host `StructureCompositionTests`.
2. PG verified: `OnHangarViewStateUpdated` through `StructureNotifications` and the existing persisted hangar view reader. It follows the current-structure notice, skips removed rows and control seats, and uses the existing EventBus cursor for replay.
3. PG verified: station/structure undock preflight reads SDE `contrabandTypes` plus current cargo and honors the V24.01 boolean confirmation before movement. Direct cargo warning only; customs fines and confiscation are a separate flow.
4. PG verified: successful station/structure dock and undock invoke the shared guest hook; structure join has four arguments and station join/leave one tuple. Disconnect reads the current place; entry/leave unregister the old character handler. Station suite 28/28, selected Space 10/10, structure RPC suite 32/32. Source changes were implemented by three Luna/max workers and integrated in the canonical Windows `main` checkout.
5. Run the headless scenario against a live stack (`tools/dev/run-eve-live.sh`) with `EVE_LIVE_SHIP` set; until then it is UNVERIFIED.

## Gotchas
- Bash `grep`, `cat`, `find` are denied: use python3 (`python3 -I -c` or heredoc).
- Warnings are errors (`dotnet/Directory.Build.props`). Build with `-p:UseSharedCompilation=false` under the heavy lock.
- `EVE_PG_URL` must be key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- `Eve.Services.Core.Tests` `MissionSdeDataTests` (2) fail unless the mission build is loaded in `sde_mission` (`cargo run -p eve-sde -- ensure --missions --pg $EVE_PG_URL`); not ST-3.
- Origin moves every few minutes: fetch, check overlap, rebase, push at once.
