# RPC-02058–02870 Host coverage handoff

## Status

Wire regression coverage is in dotnet/tests/Eve.Host.Tests/Rpc02058To02870HostWireTests.cs. The complete batch is not finished: map.GetBeaconCount still has only the empty fallback in MapService, and its production Host read hook has no active fleet-beacon provider. The map rows therefore need a state-backed implementation before this batch can be marked complete.

## V24.01 client evidence

- GetBeaconCount has three client callers: starModeHandler.py:627-640, mapViewColorHandler.py:665-680, and filters/mapFilterCynosuralFields.py:18-42. Each consumes the reply as a solar-system-ID keyed mapping: iteration, keys(), get(), and per-system counts.
- GetIndustryCostModifier(activityID) has three callers: starModeHandler.py:1237-1252, mapViewColorHandler.py:1061-1068, and filters/mapFilterCostIndex.py:18-24. Each consumes a solar-system-ID keyed numeric mapping for color scaling.
- LoadSharedOverviewProfile calls GetStoredPreset only on a cache miss, raises OverviewProfileLoadingError on None, parses the returned YAML string, then loads its data (presetservice.py:864-877). OnDropDataDelegate calls StoreLinkAndGetID(entry.data), rejects None, and reads hashvalue and sqID to build the share link (eveEditPlainText.py:156-163).
- structureHangarViewSvc._UpdateStructureHangarInfo calls GetMyHangarViewState only while docked and uncached (structureHangarViewSvc.py:18-29). It forwards OnHangarViewStateUpdated locally (:31-35); hangarView.py:208-218 passes the state to the active ship behaviour.
- Whole-tree Serena searches found no other callers of the two preset methods or GetMyHangarViewState; the three map callers above are the complete matches for each map RPC.

## EveJS comparison

- mapService.js:615-624 builds the beacon-count dictionary from active fleet beacon counts and returns it keyed by solar system; mapService.js:645-649 returns the industry-cost-index dictionary.
- overviewPresetMgrService.js:512-539 stores a client drag payload and returns its KeyVal key; lookup returns its payload string or null.
- structureHangarViewManagerService.js:25-50 returns null without a valid structure session/record and otherwise builds the view state. structureHangarViewState.js:145-170 shapes the StructureHangarViewState Ex2 fields from authoritative structure state.

## eve.rs state and tests

- MapService registers GetBeaconCount and GetIndustryCostModifier. The latter is connected through CharacterServicesComposition.ReadMapRpcDataAsync to industry job operations; IndustryComposition.cs:599-625 derives the cost map. The former still falls back to an empty PyDict; CharacterServicesComposition.DungeonRpc() installs that read hook (DungeonRpc.cs:43-52). Active cyno fields are persisted under space.deployed (SpaceContainers.Deployed.cs:12-13, 37-54), but this batch has not connected that owner to the map response.
- OverviewPresetMgrService is part of CoreServices.Create and writes through its existing GameStore callbacks. The existing DB persistence test exercises write/read/restart state through that store; this patch adds Host socket coverage for both RPCs, including machoVersion, deduplication and a missing key.
- StructureRpcService already registers structureHangarViewMgr.GetMyHangarViewState; Host composition wires PersistenceStructureProfilesRpcBackend.GetMyHangarViewStateAsync to the structures store. The existing PersistenceStructureHangarViewRpcTests called that service directly; this patch adds Host socket coverage for the Ex2 response and consumed state fields.

## RPC-02280–02287 and RPC-02304–02320 status (2026-10-26)

This assigned batch is not complete. The worktree started with a pending Host wire test at `dotnet/tests/Eve.Host.Tests/PetitionerRpcHostWireTests.cs`; it exercises the real registered Host route and the client transport's `machoVersion` kwarg for `PropertyPopulationInfo`, `UnClaimPetition`, and `UpdatePetitionRating`. The test checks only the existing EveJS-compatible empty-list/None replies. It does not provide a petition backend, mutation state, or persistence, so it must not be treated as end-to-end petition functionality.

Client evidence from the whole `/home/user/eve-logic` tree:
- `PropertyPopulationInfo`: petitionWindow.py:212-213 uses the result as dropdown entries (each row is formatted from an ID and label).
- `UnClaimPetition`: petition.py:1026-1029 invokes it and then switches to the claimed-petitions panel.
- `UpdatePetitionRating`: petition.py:445-447 sends petition ID, three rating values, and comment when a value changed.
- Searches for `petitioner.append` and `petitioner.has_key` found no remote-service dispatches. In the nearby caller flow, append is local list construction (petitionWindow.py:250-252) and has_key is local dictionary membership (petitionWindow.py:157-160); these labels do not identify server RPC methods.
- `GetControlTowers`: moonmining.py:359-361 consumes rows as (typeID, structureID, solarSystemID), grouping by system and skipping the current tower.
- `GetTowerSentrySettings`, `GetTowerNotificationSettings`, `GetConsumptionFactor`, `GetStarbasePermissions`, and `SetStarbasePermissions`: moonmining.py:540-541, 578-579, 589-590, 703-704, and 741-742 consume settings/permission values and persist edits.
- `GetMoonForTower`, `LinkResourceForTower`, `ChangeStructureProvisionType`, `RunMoonProcessCycleforTower`, and `GetMoonProcessInfoForTower`: moonmining.py:1228-1234, 1271-1274, 1315-1327, and 1341-1346 consume moon/process state, issue topology/provision mutations, or run a cycle.
- `SetTowerNotifications`: moonmining.py:1203-1209 sends calendar/fuel settings after change.
- `AnchorOrbital`: menusvc.py:1919-1921 exposes the action and :3665-3669 invokes the moniker method.
- `OnlineOrbital`: orbitalMaterialUI.py:161-163 invokes it and closes the window.
- `GetSiloCapacityByItemID`: invCache.py:1271-1273 and :1311-1314 uses the result as actual inventory capacity before falling back to type attributes; godma.py:347-348 also calls the same capacity helper for starbase silos.

EveJS comparison checked so far:
- petitionerService.js:178-183 returns an empty list for PropertyPopulationInfo; :266-271 and :295-300 return null for UnClaimPetition and UpdatePetitionRating (with audit records).
- posMgrService.js:488-503 derives GetControlTowers rows from its provider, normalizes them, and returns the rows.
- The remaining assigned posMgr handlers still need per-method EveJS comparison and authoritative Host wiring.

Additional EveJS evidence for the assigned posMgr methods (all in `server/src/services/planet/posMgrService.js`):
- `GetTowerSentrySettings` (:391-405) returns a mutable row with the five fields the client tuple-unpacks; `GetTowerNotificationSettings` (:415-420) returns a KeyVal-shaped notification object.
- `SetTowerNotifications` (:422-428) and `SetStarbasePermissions` (:467-473) validate access through the starbase configuration runtime and return None after mutation. `GetStarbasePermissions` (:450-465) returns a two-element value: deploy flags mapping and mutable usage-flags rowset.
- `GetConsumptionFactor` (:505-516) returns 1.0 for the current fixed-burn runtime. `GetMoonForTower` (:586-589) reads production state. `ChangeStructureProvisionType` (:591-596), `LinkResourceForTower` (:598-604), and `RunMoonProcessCycleforTower` (:606-610) mutate the production runtime; linking returns True. `GetMoonProcessInfoForTower` (:612-615) propagates the runtime's unavailable-process result.
- `AnchorOrbital` (:617-629) and `OnlineOrbital` (:645-661) mutate orbital state, consume upgrade items on online, and refresh the scene; both return None.
- `GetControlTowers` (:488-503) normalizes provider rows. A code search for `GetSiloCapacityByItemID` found no same-named handler in the pinned EveJS tree; its implementation contract still needs tracing through the inventory capacity path.

## Follow-up

Add the GetBeaconCount provider from the existing deployed-cyno-field owner to the map hook, then add a focused Host regression for live and absent field states. Do not represent a missing provider as active data.
