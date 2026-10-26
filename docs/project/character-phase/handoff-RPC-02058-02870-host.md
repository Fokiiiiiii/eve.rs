# RPC-02058–02870 Host coverage handoff

## Status

The map, overview preset and structure hangar regressions remain in `dotnet/tests/Eve.Host.Tests/Rpc02058To02870HostWireTests.cs`. The slash RPC entries in this batch share one registered endpoint, `slash.SlashCmd`; a strict Host wire regression now covers a client-shaped `/heal <itemID>` call with transport-added `machoVersion` in `dotnet/tests/Eve.Host.Tests/SlashHostWireTests.cs`.

The complete wider batch is still incomplete: map.GetBeaconCount still has only the empty fallback in MapService, and its production Host read hook has no active fleet-beacon provider. The map rows need a state-backed implementation before this handoff can be marked complete.

## V24.01 client evidence

- GetBeaconCount has three client callers: starModeHandler.py:627-640, mapViewColorHandler.py:665-680, and filters/mapFilterCynosuralFields.py:18-42. Each consumes the reply as a solar-system-ID keyed mapping: iteration, keys(), get(), and per-system counts.
- GetIndustryCostModifier(activityID) has three callers: starModeHandler.py:1237-1252, mapViewColorHandler.py:1061-1068, and filters/mapFilterCostIndex.py:18-24. Each consumes a solar-system-ID keyed numeric mapping for color scaling.
- LoadSharedOverviewProfile calls GetStoredPreset only on a cache miss, raises OverviewProfileLoadingError on None, parses the returned YAML string, then loads its data (presetservice.py:864-877). OnDropDataDelegate calls StoreLinkAndGetID(entry.data), rejects None, and reads hashvalue and sqID to build the share link (eveEditPlainText.py:156-163).
- structureHangarViewSvc._UpdateStructureHangarInfo calls GetMyHangarViewState only while docked and uncached (structureHangarViewSvc.py:18-29). It forwards OnHangarViewStateUpdated locally (:31-35); hangarView.py:208-218 passes the state to the active ship behaviour.
- Whole-tree searches found no other callers of the two preset methods or GetMyHangarViewState; the three map callers above are the complete matches for each map RPC.

### SlashCmd rows in this batch

All rows below resolve to the same RPC signature and route, `slash.SlashCmd(command)`. The listed callsites discard the method result and continue with local actions, progress UI, or command side effects:

- RPC-02567/02568: `eve/client/script/ui/control/eveImagePicker.py:491` sends `/create <typeID> 1 <stationID>`, then switches to asset mode and clears the available-type cache (:492-493).
- RPC-02606/02607: `eve/client/script/parklife/fightersSvc.py:56` sends `/fit me <fighterTypeID> 1`; the menu callback returns without reading the result (:57).
- RPC-02618/02619: `eve/client/script/ui/control/entries/certificate.py:60` sends `/giveskill me <typeID> <level>` once per certificate skill the character lacks; it does not read each reply (:56-60).
- RPC-02620/02621: `eve/client/script/ui/shared/info/infosvc.py:3508` sends `/giveskill me <typeID> <level>`; after the loop it updates progress and forces a skills refresh (:3500-3510).
- RPC-02622/02623: `eve/client/script/ui/shared/info/shipInfoPanels/panelSkills.py:653` sends the same giveskill command; after the loop it displays completion and forces a skills refresh (:644-656).
- RPC-02626/02627: `eve/client/script/ui/services/menusvc.py:890` sends `/unload me <itemID-or-typeID>` or `/heal <itemID>` from GM menu callbacks (:886-890).
- RPC-02647/02648: `eve/client/script/ui/shared/info/infosvc.py:3558` sends `/create <typeID> <quantity>` or, for GML, `/load me <typeID> <quantity>`; results are ignored while the progress window advances (:3540-3559).
- RPC-02661/02662: `eve/client/script/ui/shared/info/infosvc.py:3515` sends `/removeskill me <typeID>`, then posts a local message (:3513-3516).
- RPC-02669/02670: `eve/client/script/ui/services/menusvc.py:1241` sends `/skyhook state <id> <state>` and returns from the menu callback (:1238-1242).
- RPC-02684/02685: `eve/client/script/ui/structure/structuremenu.py:93` sends `/structure abandontimer <id> <seconds>`.
- RPC-02686/02687: `eve/client/script/ui/structure/structuremenu.py:88` sends `/structure deploytimer <id> <seconds>`.
- RPC-02688/02689: `eve/client/script/ui/structure/structuremenu.py:73` sends `/structure state <id> <state>`.
- RPC-02690: `eve/client/script/ui/structure/structuremenu.py:78` sends `/structure timer <id> <seconds>`; the adjacent unanchoring callback at :83 sends `/structure unanchor <id> <action>`.

A whole-tree Serena search for `SlashCmd` also found shared client helper callers in `eve/client/script/ui/services/menuSvcExtras/devFunctions.py:12-77`, slash command UI code in `chat/client/slash_command.py:16-25`, and numerous devtools and QA scripts. The result for the RPC is not inspected by the UI callsites listed above; command-side notifications and local refreshes carry their effect.

## EveJS comparison

- `server/src/services/admin/slashService.js:214-266` extracts the command, throws the command catalog for `/`, dispatches through `executeChatCommand`, and returns the handled message (or null); unknown and failed commands are surfaced through chat feedback and a message result.
- The matching generic dispatcher is `server/src/services/chat/chatCommands.js:1071-2403`. Structure GM command behavior is implemented by `server/src/services/structure/structureChatCommands.js:212-380`.
- eve.rs registers a real `SlashService` from `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:84-101`. It gates the method on staff role and returns a PyStr from command dispatch; its catalog path is a UserError. Existing `SlashCatalogTests` exercised the service directly, not the Host packet route.
- The new Host test registers the real `SlashService` in `ServiceRegistry`, sends the exact `slash.SlashCmd` packet with `machoVersion`, and asserts the PyStr response from the actual dispatcher for the `/heal <itemID>` call shape used at eve-logic `menusvc.py:890`.

## eve.rs state and tests

- MapService registers GetBeaconCount and GetIndustryCostModifier. The latter is connected through CharacterServicesComposition.ReadMapRpcDataAsync to industry job operations; IndustryComposition.cs:599-625 derives the cost map. The former still falls back to an empty PyDict; CharacterServicesComposition.DungeonRpc() installs that read hook (DungeonRpc.cs:43-52). Active cyno fields are persisted under space.deployed (SpaceContainers.Deployed.cs:12-13, 37-54), but this batch has not connected that owner to the map response.
- OverviewPresetMgrService is part of CoreServices.Create and writes through its existing GameStore callbacks. The existing DB persistence test exercises write/read/restart state through that store; the Host socket coverage exercises StoreLinkAndGetID/GetStoredPreset, including machoVersion, deduplication and a missing key.
- StructureRpcService registers structureHangarViewMgr.GetMyHangarViewState; Host composition wires PersistenceStructureProfilesRpcBackend.GetMyHangarViewStateAsync to the structures store. The Host socket test checks the Ex2 response and consumed state fields.

## Verification

- Added `Slash_command_is_dispatched_through_the_registered_host_with_macho_version` in `dotnet/tests/Eve.Host.Tests/SlashHostWireTests.cs`. It uses the live HostServer, strict RPC routing and the production SlashService class. The result is the existing `Select a character before using /heal.` PyStr branch.
- Initial Actions compile failures were fixed: added the `Eve.Services` import for `GameMasterRoles`, then used the serialized `Packet` envelope in the merged RPC coverage test.
- Selected Host test runs `40277112061` (`1e11b18d`), `40277857506` (`e59639fa`), `40277940730` (`563e94b2`), and `40278650613` (`19937d9a`) passed without failed tests or jobs.
- The local focused test rerun was canceled after prolonged shared build contention; it did not produce a test result.
- The push-triggered CI run `40278138665` for `b5c21d5` was still in progress when checked. No `ci.yml` run was found for `f82aae31` at that check.
- Command-specific mutations for `/create`, `/structure` and other command forms are outside this one shared-route wire assertion. The test establishes the route, transport kwargs, role gate and reply encoding for one listed caller shape; it does not verify command-specific persistence or notifications for every caller.

## Follow-up

Add the GetBeaconCount provider from the existing deployed-cyno-field owner to the map hook, then add a focused Host regression for live and absent field states. Do not represent a missing provider as active data.
