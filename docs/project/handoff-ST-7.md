# ST-7 handoff (Structure services: compression plant, cyno jammer E-03/E-15, cyno beacon)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-7). Branch `wt-st7`, worktree `/home/user/wt-st7`.

## Done and pushed
- Increment (a), service fuel (commit 4f8c1ec): `dotnet/src/Eve.Structures/StructureFuel.cs` (`StructureFuel`, `StructureServiceIds`, `ServiceFuelRule`,
  `IServiceFuelRules`, `IStructureFuelBay`). Online needs the SDE online charge (2110 = 72 h of burn) and takes it from the fuel bay; the hourly job
  `structure.fuel_hour` (key `structure:{id}`) burns each online service's SDE 2109 at the next full hour; a short hour calls `StructureTimers.FuelOutAsync`.
  Host: `InventoryStructureFuelBay` (fuel blocks, group 1136, flag 172, over `PgItemStore`; `PgItemStore.InContainerAsync(connection, transaction, ...)`),
  `ServiceFuelRulesSource` (SDE through `DogmaStatic`; a module without static data is not offered, the Host still starts).
  Tests: `StructureFuelTests` (7). Host 164 of 164, Inventory 55 of 55 at that commit.
- Increment (b), structure compression (this push): `dotnet/src/Eve.Host/Persistence/Structures/StructureCompressionAuthority.cs` (compress, decompress, the
  efficiencies; EveJS gates; named refusals; the output joins or is created, the source is deleted; `ShipNotifications.SendItemChange`). Wired in
  `StructureComposition.Structures()` as the three `structureRpcCallbacks`, and `StructureRpc.cs` passes them to `structureCompressionMgr`.
  Tests: `dotnet/tests/Eve.Host.Tests/StructureCompressionTests.cs` (6). Host 170 of 170.

## Next (in order)
1. Increment (c): E-03 `BeyonceService.CynoJammerOnlineSimTimeForSystem` (`dotnet/src/Eve.Services.Space/BeyonceService.cs:128-140`) from an online service 17
   in the system (`StructureServiceIds.CynoJammer`): needs the time the service came online, so the store's `structure_services` gets a column (migration
   0002, `online_since_ms`), set by `StructureFuel.OnlineAsync`/`StructureTimers.SetServiceAsync`. E-15 `OnCynoJammerChanged` from a bus subscriber on the
   service change (EveJS `sovNotifications.js:281`), to the sessions in the system. The non-covert cyno generator refused in a jammed system, host side in
   `SpaceSessions.Modules.cs` next to the fleet and high-sec checks (a named `ModuleResult.Refusal`, not the sim: the sim has no structure body until ST-1).
   Note the jammer may only be onlined with a sovereignty hub's Cynosural Suppression upgrade (SDE 35914 description): the onlining check on that upgrade is
   not built; record it as a gap if the sov read model has no upgrade state.
2. Increment (d): `structureCynoBeaconMgr` backend in place of the user error (`dotnet/src/Eve.Services.Space/StructureTravelRpcService.cs:47-87`; the
   wiring is `AgentRpc.cs:59`): the Pharolux beacon (group 2017, type 35840) with service 16 online; `CmdJumpToStructureBeacon` and the group and bridge
   variants (EveJS `structureCynoBeaconMgrService.js`).

## Gaps known now
- Fuel burns and the corporation-office compression path: see DEVIATIONS "Structure services fuel (ST-7, increment a)" and "Structure compression (ST-7, increment b)".
- No client run: docking (ST-3) is what gives a session `structureid`, so the compression RPCs are reachable headless only through the test session.
- The worktree `/home/user/wt-st7` showed uncommitted ST-1 files (`crates/eve-space/src/world.rs`, `scene/structures.rs`, protocol 59) that were not written
  by this package; they are left untouched and not committed. The same files are modified in `/home/user/wt-st1`.

## Gotchas
- `psql` takes the libpq form: `psql "host=127.0.0.1 user=postgres dbname=eve"`. `EVE_PG_URL` is the key=value form `Host=127.0.0.1;Username=postgres;Database=eve`.
- Builds and tests: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test <project> -p:UseSharedCompilation=false`.
- Bash `grep`, `find` and `cat` are denied in this repo; search with CBM/Serena, read outside-repo references (EveJS, eve-logic) with the Read tool.
- `DogmaStatic` loads types on demand: call `EnsureTypesAsync` before `Type(id)` (the first increment of (b) read unloaded types and got zeros).
- `PyInt` is 32-bit; structure and ship ids are 64-bit, use `PyLong` in tests.
- The spec's group numbers for the jammer were wrong: SDE 35914 (Standup Cynosural System Jammer I) is group 1324; group 2016 is the Tenebrex Cyno Jammer
  (37534), group 2017 the Pharolux Cyno Beacon (35840).
