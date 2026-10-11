# ST-7 handoff (Structure services: compression plant, cyno jammer E-03/E-15, cyno beacon)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-7). Branch `wt-st7`, worktree `/home/user/wt-st7`.

## Done
- (a) Service fuel (4f8c1ec): `dotnet/src/Eve.Structures/StructureFuel.cs`. Online needs the SDE online charge (2110 = 72 h of burn) from the fuel bay; the
  hourly job `structure.fuel_hour` burns each online service's 2109 at the next full hour; a short hour takes every service offline (Low Power).
  Host: `Persistence/Structures/StructureFuelBay.cs`, `ServiceFuelRulesSource.cs`. Tests: `StructureFuelTests` (7).
- (b) Structure compression (bd3af03 on main as dcbde56 before the rebase): `Persistence/Structures/StructureCompressionAuthority.cs`, wired as
  `structureCompressionMgr` through `structureRpcCallbacks`. Tests: `Eve.Host.Tests/StructureCompressionTests.cs` (6).
- (c, part 1) E-03 jammer state and E-15 announcement (de4f4e5): `Eve.Structures/StructureJammer.cs`, migration 0002 (`online_since_ms`), the topic
  `structure.service_changed`, `GetCynoJammerState` through `BeyonceService`, the subscriber `structure.cyno_jammer_notice`. Tests: `StructureJammerTests` (2).
- (c, part 2) the standard cynosural field refused in a jammed system (`CYNO_JAMMED`, after the fleet rule; the covert field immune). Tests:
  `CynoFieldHostTests` (4, two new). Not yet pushed at the time of writing; see the TODO rows.
- (d) the structure cyno beacon checks (`Eve.Services.Space/StructureBeaconTravel.cs`, EveJS `resolveUsableBeacon` order and texts). The jump itself is refused
  by name. Tests: `StructureBeaconTravelTests` (5). Not yet pushed at the time of writing.

## Open
1. The jump to a structure beacon: the access check (`characterHasStructureService`), the jump drive's fuel and range, and the sim cynosural beacon at the
   structure's position (a `SimPlaceCynoBeacon` with the structure's id, placed when service 16 comes online, removed when it goes offline).
2. The sov Cynosural Suppression upgrade check for onlining the jammer (EveJS `isCynoSuppressionUpgradeOnline`): the sov read model has no upgrade state read yet.
3. The `CYNO_JAMMED` text and the beacon refusals' texts are ours where EveJS's wording was not located (DEVIATIONS).
4. Fuel burns and the owner's `OnItemChange` (no session at burn time); the corporation-office compression path (ST-5).
5. No client run: docking (ST-3) gives a session `structureid`; the compression RPCs are reachable headless through the test session only.
6. The Host wiring of E-03/E-15 and the beacon checks has no RPC-level Host test yet (the rules are tested in Eve.Structures and Eve.Services.Space).

## Foreign files (not this package)
- The worktree `/home/user/wt-st7` held uncommitted ST-1 edits (protocol 59 files, `scene/structures.rs`) that this package did not write. They were saved to the
  session scratchpad (`st1-foreign/`) and the paths restored; ST-1 is on origin now (8eaccdb). Nothing of them is committed here.

## Gotchas
- `psql` takes the libpq form `psql "host=127.0.0.1 user=postgres dbname=eve"`; `EVE_PG_URL` is key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- Builds and tests: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test <project> -p:UseSharedCompilation=false`; a run can queue behind other agents on the lock.
- Bash `grep`, `find` and `cat` are denied; search with CBM/Serena, read outside-repo references (EveJS, eve-logic) with the Read tool.
- `DogmaStatic` loads types on demand: `EnsureTypesAsync` before `Type(id)`.
- `PyInt` is 32-bit; structure and ship ids are 64-bit, use `PyLong`.
- The spec's jammer group numbers were wrong: SDE 35914 is group 1324; group 2016 is the Tenebrex Cyno Jammer (37534), group 2017 the Pharolux Cyno Beacon (35840).
