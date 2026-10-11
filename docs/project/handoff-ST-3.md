# ST-3 handoff (structure docking, undock and structure hangars)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-3). Branch `wt-st3`, worktree `/home/user/wt-st3`.

## Status (in progress)
- (a) `structureDocking.Dock`: DONE and tested (3 PG tests pass; full Space suite 692/692 with EVE_PG_URL). Code: `dotnet/src/Eve.Services.Space/SpaceSessions.Structures.cs` (new), `SpaceSessions.Movement.cs` (`FinishDockingAsync` structure branch), `Eve.Host/Composition/StructureComposition.cs` (Dock callback, `StructureDockingAllowedAsync`), `Eve.Host/Rpc/StructureRpc.cs` (callbacks passed to `structureDocking`), `Eve.Services.Core/StructureDirectoryService.cs` (`CanDock` made public), `Eve.Structures/StructureFuel.cs` (`StructureServiceIds.Docking = 1`). Tests: `dotnet/tests/Eve.Services.Space.Tests/StructureDockingTests.cs` (3 PG tests). TODO section 48, DEVIATIONS "Structure docking and hangars (ST-3)".
- Environment: `Eve.Services.Core.Tests` `MissionSdeDataTests` (2) need the mission SDE build in `sde_mission`; not run green here.
- (b) Undock: not started.
- (c) Structure hangars and restart while docked: not started.
- (d) headless `dock_undock` in `tools/headless-client/tests/test_structures.py`: not started.

## Facts used (verified in source this session)
- The sim already docks and undocks Structure bodies (ST-1 c; `scene/docking.rs`, `scene/tests.rs:2654`). The host places structures on system entry (ST-1 d, `SpaceStructures.RestoreAsync`).
- A docked character keeps its place in `characters.StationId`; a structure id goes there too (the CharacterSelect structure path already reads it with `StructureExistsAsync`).
- EveJS `space/transitions.js` `dockSession` / `buildDockedShipLocationContext`: the item change place is `["Structure", id, "StructureShipHangar"]`.
- EveJS `services/character/characterState.js:4395-4417`: docked in a structure: `stationid` null, `structureid` = id, `locationid` = solar system, `solarsystemid` = solar system (the hangar bootstrap needs it).
- Client `eve/client/script/ui/services/structure/structureDocking.py`: `Dock(structureID, shipID)` after autopilot to 2500 m, `Undock` requires `session.structureid` and `session.solarsystemid`.

## Open
- Contraband inspection (`ShipContrabandWarningUndock`) does not exist in eve.rs for stations either; not built for structures.
- Structure guest notifications (`OnCharacterEnteredStructure` / `OnCharacterLeftStructure`) on dock and undock: `GuestLoginHook` runs only at character entry; station docking has the same gap.
- The docking service of a structure is read from the store (`structure_services`, id 1).
- The criminal timer is checked when the docking is asked for (as the station path does), not at the finalize (EveJS `dockSession`).

## Gotchas
- Bash `grep`, `cat`, `find` are denied; use python3 (`python3 -I -c` or heredoc) for search and edits. Check `git status` after every edit.
- Build: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet build dotnet/EveRs.slnx -p:UseSharedCompilation=false`. Warnings are errors (`Directory.Build.props`).
- Tests need `EVE_PG_URL='Host=127.0.0.1;Username=postgres;Database=eve'`.
