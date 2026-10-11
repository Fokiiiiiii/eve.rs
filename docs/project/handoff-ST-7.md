# ST-7 handoff (Structure services: compression plant, cyno jammer E-03/E-15, cyno beacon)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-7). Branch `wt-st7`, worktree `/home/user/wt-st7`.

## Done and pushed
- Increment (a), service fuel: `dotnet/src/Eve.Structures/StructureFuel.cs` (`StructureFuel`, `StructureServiceIds`, `ServiceFuelRule`, `IServiceFuelRules`,
  `IStructureFuelBay`). Online needs the SDE online charge (2110 = 72 h of burn) and takes it from the fuel bay; the hourly job `structure.fuel_hour`
  (key `structure:{id}`) burns each online service's SDE 2109 at the next full hour; a short hour calls `StructureTimers.FuelOutAsync` (all services offline,
  Low Power). Host: `InventoryStructureFuelBay` (fuel blocks, group 1136, flag 172, over `PgItemStore`; `PgItemStore.InContainerAsync(connection, transaction, ...)`
  added for it), `ServiceFuelRulesSource` (SDE through `DogmaStatic`), wired in `StructureComposition.Structures()` right after the timers.
- Tests: `dotnet/tests/Eve.Structures.Tests/StructureFuelTests.cs` (7, on PostgreSQL with a fake fuel bay: refused below 72 h, charge and next hour, hourly burn of
  two services, short hour to Low Power, restart pays each hour once, unknown service / missing structure named, online again is free).

## Next (in order)
1. Increment (b): `structureCompressionMgr` in the Host (`CompressItemInStructure`, `DecompressGasInStructure`, `GetMyGasDecompressionEfficiency`), gated like
   EveJS: the session is docked at a structure (`structureid` session attribute; docking is ST-3, so only tests reach it), the structure has Reprocessing (4)
   online, the item is in the pilot's structure hangar or the active ship at the structure, type list 336 (compression) or the gas group (decompression).
2. Increment (c): E-03 `BeyonceService.CynoJammerOnlineSimTimeForSystem` from an online service 17 in a system; E-15 `OnCynoJammerChanged` from a bus subscriber
   on the service change; the cyno generator refusal (non-covert fields) in `SpaceSessions.Modules.cs` next to the fleet and high-sec checks.
3. Increment (d): `structureCynoBeaconMgr` backend (`StructureTravelRpcService`) with the Pharolux beacon (service 16) online.

## Gaps known now
- Fuel burns change inventory with no `OnItemChange` to the corporation's sessions (no session at burn time). The bay shows the new state when re-read.
- Fuel rig or skill reductions of the online charge are not applied (EveJS `getEffectiveStructureServiceModuleOnlineFuelNeed`).
- Services are only put online by `StructureFuel.OnlineAsync`; no client RPC calls it until the deployment and service RPCs (ST-2) or (b)-(d) use it.

## Gotchas
- `psql` takes the libpq form: `psql "host=127.0.0.1 user=postgres dbname=eve"`. `EVE_PG_URL` is the key=value form `Host=127.0.0.1;Username=postgres;Database=eve`.
- Builds and tests: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test <project> -p:UseSharedCompilation=false`.
- Bash `grep`, `find` and `cat` are denied in this repo; search with CBM/Serena, read outside-repo references (EveJS, eve-logic) with the Read tool.
- Serena was activated on `/home/user/wt-st7` (the worktree) for the repo reads and edits.
- The spec's group numbers for the jammer were wrong: SDE 35914 (Standup Cynosural System Jammer I) is group 1324; group 2016 is the Tenebrex Cyno Jammer
  (37534), group 2017 the Pharolux Cyno Beacon (35840). Recorded in DEVIATIONS "Structure services fuel (ST-7)".
