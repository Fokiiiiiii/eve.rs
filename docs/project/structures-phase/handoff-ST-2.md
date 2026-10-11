# ST-2 handoff (deployment and vulnerability RPCs, notices)

Owner: structures phase (plan: scratchpad `specs/structures_phase.md`, section ST-2). Worktree was `/home/user/wt-st2` (branch `wt-st2`, deleted at close). TODO section 48. Pushed: 042195f (a), 9648b3d (b-d). Clean on main.

## Done (tests green on the merged tree)
- (a) `UpdateReinforceTimesFromClient`, `RenameStructure`: `dotnet/src/Eve.Host/Persistence/Structures/StructureDeploymentAuthority.cs`, `StructureStore.SetNameAsync`. Tests: `dotnet/tests/Eve.Host.Tests/StructureCompositionTests.cs`.
- (b) `Anchor`: `Persistence/Structures/StructureAnchorAuthority.cs`; `StructureRulesSource.IsAnchorable`; `StructureAnchored` (`structure.anchored`, `Eve.Structures/StructureTimers.cs`); `SpaceStructures.PlaceAsync` (`dotnet/src/Eve.Services.Space/SpaceStructures.cs`). Tests: `StructureAnchorTests.cs`, `SpaceStructuresPlacementTests.cs` (Space project).
- (c) `Unanchor`, `CancelUnanchor` (Director or CEO, EveJS `requireStructureDecommissionSession`): `StructureDeploymentAuthority.cs`; state machine refuses `StructureState.CancelsUnanchor` states (`Eve.Structures/StructureStateMachine.cs`). Timer end (7 days, Flex 45 min) is the existing machine `Advance`. Tests: `StructureCompositionTests.cs`, `StateMachineTests.cs`.
- (d) `structure.notify`: `Persistence/Structures/StructureNotifications.cs`, subscribed in `Composition/StructureComposition.cs`. `StructureStateChanged` has optional `PreviousState`, `PreviousUpkeep`. Test: `StructureNotificationsTests.cs`.

Tests: Eve.Structures.Tests 83/83; Eve.Host.Tests `--filter Structure` 30/30; Eve.Services.Space.Tests `--filter SpaceStructures` 5/5 (all with EVE_PG_URL).

## Open, next steps (exact)
1. Unanchor completion (the drop). NOT started. Wiki (EVE University "Anchoring structures") says only: 7 days, "All of the rigs will be lost". Core, modules, fuel are not stated by the wiki; EveJS decides (`server/src/services/structure/structureAssetSafetyState.js`, `handOffStructureAttachedItemsForUnanchor`, lines 1075-1422):
   - hull: a space item of the structure's type, owner corp, at the structure's position, `launcherID` = structure id, marker customInfo `evejs:structure-unanchor:hull:{id}`;
   - container: type 10167 (`OFFICE_EJECT_CONTAINER_TYPE_ID`), singleton, in space near the hull, marker `...:fittings:{id}`;
   - moved into the container (flag HANGAR 4): items at the structure with flags 11-34, 125-132, 164-171, fighter flags, fuel bay 172 and `STRUCTURE_DEED` (its numeric flag is in EveJS `services/inventory/itemStore.js` ITEM_FLAGS; look it up, do not guess) (see `STRUCTURE_ATTACHED_ITEM_FLAGS`, line 66); fuel = items in location structure id flag 172 (`InventoryStructureFuelBay`);
   - quantum core: a new singleton item of the structure's core type in the container when the record's `core_installed` is true (EveJS `quantumCoreItemTypeID`; source of this type id in the SDE not yet found);
   - rigs (flags 92-99, `STRUCTURE_RIG_SLOT_FLAGS`) destroyed (delete the rows).
   - Service modules are `structure_services` rows here, not items: nothing to move for them.
   Where: the state change is `StructureTimers.ApplyAsync` / `Advance` (`dotnet/src/Eve.Structures/StructureTimers.cs`, `StructureStateMachine.cs`). Add a hook (delegate) that the `Eve.Host` composition supplies, called inside the same connection and transaction as the removal, so the job handler (`structure.timer`) writes the items and the removal together; the job is idempotent because the state is already Unanchored on replay. Item writes: `PgItemStore` (`dotnet/src/Eve.Inventory/Items.cs`: CreateAsync, DeleteAsync, InContainerAsync). Space items: `CharacterShips.Death.cs` uses location = system id with the in-space flag. The drop must show in space: `SpaceContainers.Deployed.cs` restores only cynosural records today, so a restore path for the drop container is also needed (or record as gap).
   Tests to write: fake clock (`Rig` pattern in `dotnet/tests/Eve.Structures.Tests/Rig.cs`) plus restart (`Rig.Restart`), and an item count check.
2. Anchor refusals (none written yet). Sources: wiki Anchoring_structures ("cannot be anchored in shattered wormhole systems (including Thera)", "starter systems", "trade hubs"), then EveJS `sovPlayerDeployment.js`.
   - Thera: system 31000005 (EveJS `homeStationRuntime.js` THERA_SOLAR_SYSTEM_ID). Shattered: EveJS `wormholeSpaceAuthority.js` `isShatteredSystem` (Thera class 12 and SMALL_SHIP_SHATTERED_CLASS_ID). Wormhole class data: find in SDE `mapSolarSystems` or EveJS static data before coding.
   - Starter/career systems: not located yet (grep EveJS for starter/career system lists; none found in the first search, which hit only incursion and dailyGoals).
   - Sov index: read `PersistenceSovFwReadModel` (`StructureComposition.cs`, `sovereigntyReadModel`). Rule: a null-sec system not owned by the anchoring alliance adds 24 h per Strategic Index level (wiki). If unreadable, use 24 h and record in DEVIATIONS. The `StructureRules.Of(..., anchoringMs)` parameter already exists.
   - Default profile: EveJS `resolveUsableProfileIDForCorporation`. Today `StructureAnchorAuthority` stores `request.ProfileId ?? 0`.
   Where: `StructureAnchorAuthority.AnchorAsync` (refusals before `_items.DeleteAsync`). Each refusal named (CustomNotify text recorded in DEVIATIONS "Structure anchoring, unanchoring and notices (ST-2 b, c, d)").
3. Reinforcement-change notice (not written). EveJS `structureVulnerabilityService.js` `createReinforcementChangedNotifications` writes a notification box entry (typeID `STRUCTURES_REINFORCEMENT_CHANGED`) to each corporation member. eve.rs has only the reader (`Eve.Services.Core/NotificationMgrService.cs`, GameStore box `notifications/boxes/{characterId}`). Missing foundation: a notification writer. The smallest correct step is a new `Eve.Notifications` PostgreSQL module (its own schema, migration, `Add(character, typeId, senderId, data)` in the caller's transaction) read by `NotificationMgrService`'s reader. Not started; no existing module to reuse.

## Gotchas
- Bash `grep`, `cat`, `find` are denied in this environment; read with `python3 -I -` (quoted heredoc) or the Read tool. A `$2` in a double-quoted bash string is expanded to nothing (broke `SetNameAsync` once).
- `EVE_PG_URL` is key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- Heavy lock `/tmp/eve-heavy.lock` queues behind other agents (up to ~20 min). Use `-m:1` on dotnet build/test: parallel nodes died with MSB4166.
- Trade hubs: five names (EveJS `TRADE_HUB_SYSTEM_NAMES`). The SDE `hub` flag is not the trade hub list.
- `CharacterSessions` only sees sessions with `CharacterId` (`charid`). OfCorporation uses `corpid`; InStructure uses `structureid`.
- Anchor distance: 75 km upper bound for every size (wiki gives 50-75 km without per-size values).
- Notices to sessions whose ship id equals the structure: not sent (needs ST-5).
