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

## Follow-up

Add the GetBeaconCount provider from the existing deployed-cyno-field owner to the map hook, then add a focused Host regression for live and absent field states. Do not represent a missing provider as active data.
