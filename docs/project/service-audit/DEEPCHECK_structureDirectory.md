# Deep check: `structureDirectory` (V24.01)

Checked against the whole V24.01 client source tree for build 3396210, the eve.rs Host route, and EveJS 261018.1 at `/home/user/evejs-261018.1/src`.

## Service identity, binding, and argument transport

- The client defines the remote service as `structureDirectory` at `eve/client/script/ui/services/structure/structureDirectory.py:10`. The client invokes it through `sm.RemoteSvc`; whole-tree searches found no bound-object or moniker call using this service name. The nearby jump-destination helper is a separate `structureJumpBridgeMgr` call at `structureDirectory.py:174-176`.
- Production Host composition constructs `StructureDirectoryService` at `dotnet/src/Eve.Host/Rpc/StructureRpc.cs:58-60`, includes it in `CharacterServiceList` at `:65`, and registers the character service list through the real registry at `dotnet/src/Eve.Host/Hosting/HostComposition.cs:263-265`. A distinct `StructureMonikerService` is registered at `StructureRpc.cs:61`; it is not a directory alias.
- `StructureDirectoryService.HasMethod` names all 14 client methods at `dotnet/src/Eve.Services.Core/StructureDirectoryService.cs:123-137`; `CallAsync` dispatches them at `:139-251`. The Host socket regression `HostServerTests.Structure_directory_all_client_methods_keep_wire_shapes_and_errors` sends each method to the registered `structureDirectory` endpoint with `machoVersion=1` in transport kwargs (`dotnet/tests/Eve.Host.Tests/StructureRpcHostCoverageTests.cs:286-564`).
- All application arguments observed in the whole client tree are positional. `ServiceCall.Arg` and `Kwarg` preserve the positional sequence and keyword dictionary, and missing positional arguments read as `None` (`dotnet/src/Eve.Services/ServiceCall.cs:15-43`). The client packet builders add `machoVersion` transport metadata (`carbon/common/script/net/ServiceCallGPCS.py:221-225`, `ObjectCallGPCS.py:403-407`). The wire test covers `PyInt`, `PyLong`, optional `None`, zero-argument methods, and the observed list parameter form.

## Method-by-method results

### 1. `GetStructureInfo`

- **Client calls and use:** The RPC wrapper is `structureDirectory.py:32-38`; local `IsStructure` and `CanContractFrom` use its presence result at `:41,90`. All other whole-tree consumers are listed in Appendix A.
- **Arguments and reply:** One structure ID is positional. The client first filters IDs with `IsPlayerItem` (`:33-34`). Host accepts Python int/long IDs, returns `None` for `None`, nonpositive, or missing/nondeployed rows, and returns a `util.KeyVal` for a persisted structure. Nonowners receive the eight public fields `typeID, structureID, upkeepState, wars, ownerID, solarSystemID, itemName, inSpace`; owning-corporation members receive the full directory fields, including SDE group/category IDs, profile ID, service map, timers, fuel and war data. The UI reads both basic identity/location fields and owner-only details; examples include `eve/client/script/ui/shared/market/quote.py:326-331`, `eve/client/script/ui/structure/structureBrowser/reinforcementTimeWnd.py:62-64,162-165`, and `sensorsuite/overlay/structures.py:83`.
- **State, errors, events:** Read-only persistent-state lookup; no RPC-triggered notification is expected. Client cache invalidation is handled separately by session, slim-item, and war events (`structureDirectory.py:96-159`).
- **EveJS and check:** EveJS `structureDirectoryService.js:362-376` and `structurePayloads.js:262-282,4627-4662` use the same public/owner split. The Host wire test checks long, int and None inputs, the full-row keys, and the exact eight-key public projection.

### 2. `GetStructureInfo_`

- **Client call and use:** The whole-tree search found this alias at `eve/common/script/net/eveMachoNet.py:369`. It is the fallback when the location cache has no structure row and consumes `solarSystemID`.
- **Arguments and reply:** One positional structure ID. It returns the same public/owner `util.KeyVal` or None as `GetStructureInfo`; the caller reads `solarSystemID`.
- **State, errors, events:** Same persistent read and no-notification behavior as `GetStructureInfo`.
- **EveJS and check:** EveJS routes `Handle_GetStructureInfo_` to `Handle_GetStructureInfo` (`server/src/services/structure/structureDirectoryService.js:374-376`). Host registers and dispatches this alias to the same implementation. The real-Host wire test calls it and checks `solarSystemID`.

### 3. `GetMyCharacterStructures`

- **Client calls and use:** The client calls it for `GetStructures` at `structureDirectory.py:44-45` and for regional market locations at `:64-70`; the market helper reads `services`. Structure list consumers read the structure ID, owner, type, system and services, including `eve/client/script/ui/structure/structureBrowser/controllers/structureBrowserController.py:73-83`.
- **Arguments and reply:** No method arguments. Host returns a dictionary keyed by numeric structure/station ID. Structure values are six-field KeyVals: `typeID, structureID, wars, services, ownerID, solarSystemID`. The client derives market-dockable IDs from the service map.
- **State, errors, events:** Host reads all persistently deployed structures and returns any for which the authenticated character can dock, including public third-party structures; it also appends NPC stations from the session region. It uses session `charid` and `regionid` at `StructureDirectoryService.cs:233-239`. NPC service IDs come from the pinned SDE station-operation services plus the V24 station service mapping (`Eve.Universe/StationCatalog.cs:78-116`); no write or RPC-triggered notification.
- **EveJS and check:** EveJS `structureDirectoryService.js:383-388` calls `listDockableStructuresForCharacter`; `structureState.js:4709-4726` filters by docking ACL without a corporation-owner restriction, and `structurePayloads.js:303-323` builds the six-field structure rows. NPC rows come from `npcStationDirectory.js:13-36,70-82,118-150`. The Host test checks own and public third-party structures, exact six-field values, and station service IDs.

### 4. `GetMyCorporationStructures`

- **Client calls and use:** Remote wrapper `structureDirectory.py:47-49`; browser and corporation structure entries consume the keyed map and service/state fields (`eve/client/script/ui/structure/structureBrowser/controllers/structureBrowserController.py:48-83`, `eve/client/script/ui/structure/structureBrowser/controllers/structureEntryController.py:45,93-115,137-155,185-196`).
- **Arguments and reply:** No method arguments. Reply is a structure-ID-keyed dictionary of full KeyVal rows with service state values, which the corporation UI uses to render online/offline status.
- **State, errors, events:** Read-only query over persisted corporation-owned structures. Current membership is rechecked; the directory requires Station Manager or Brand Manager. Denial is `CrpAccessDenied(reason=[101, "UI/Corporations/AccessRestrictions/InsufficientRoles"])`, not a generic error (`PersistenceStructureDirectoryReadModel.cs:344-366`). No notification is sent.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:390-403`. The real-Host test checks returned service data and parses the complete UserError envelope and reason fields on denial.

### 5. `GetMyDockableStructures`

- **Client calls and use:** `structureDirectory.py:52-55` sends the current system ID. `structureProximityTracker.py:104-110` iterates the returned IDs and tracks nonstation structures.
- **Arguments and reply:** One positional solar-system ID; Host accepts int/long and falls back to session system when it is absent/None (`StructureDirectoryService.cs:214-229`). Reply is a list of structure IDs that pass the current docking ACL.
- **State, errors, events:** Read-only persisted structure and access-group state. Empty/no-system requests return an empty list. No notification.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:409-419`. Host wire test checks a long system ID and confirms the accessible structure ID.

### 6. `GetStructureMapData`

- **Client calls and use:** Positional system ID from `structureDirectory.py:73-75`; `mapSvc.py:135-139` inserts returned rows into the solar-system map and `mapSvc.py:534-535` finds a row by `itemID`.
- **Arguments and reply:** One system ID; Host accepts int/long and uses the session solar system if the argument is None (`StructureDirectoryService.cs:148-156`). With SDE loaded it returns a `carbon.common.script.sys.crowset.CRowset` with the client header fields `groupID,typeID,itemID,itemName,locationID,orbitID,connector,x,y,z,celestialIndex,orbitIndex` (`PersistenceStructureDirectoryReadModel.cs:413-495).
- **State, errors, events:** Read-only persisted rows enriched with type groups from SDE. No notification.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:441-457`. The Host socket test verifies route and empty-list fallback. Its read model uses `sde=null`, so it does not exercise a populated CRowset or SDE-derived row values; those are source-reviewed only.

### 7. `GetMyAccessibleOnlineCynoBeaconStructures`

- **Client calls and use:** Wrapper `structureDirectory.py:77-79`. Capital navigation unpacks each six-item row `structureID,typeID,ownerID,solarSystemID,structureState,name` at `inflight/capitalnavigation.py:128-133`; the menu path primes locations from the solar-system slot at `menusvc.py:2171-2175`.
- **Arguments and reply:** No method arguments. Host returns a list of six-item lists with integer IDs/state and a text name. The real-Host test asserts the six positions and populated Upwell beacon values.
- **State, errors, events:** Read-only persisted Upwell Pharolux structures, online service 16, valid profile access, and unjammed systems. No notification.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:505-528`; its beacon source also includes POS cyno generators through `starbaseTravelRuntime.js:78-81`. eve.rs currently has no authoritative POS cyno-generator/service-module read model, so POS beacons are absent from this result.

### 8. `GetSolarSystemsWithBeacons`

- **Client calls and use:** `structureDirectory.py:81-83`; star map, star mode and cynosural map filter consume system IDs for membership/coloring (`starModeHandler.py:630-640`, `mapViewColorHandler.py:665-684`, `mapFilterCynosuralFields.py:23-43).
- **Arguments and reply:** No arguments; returns a sorted list of solar-system IDs containing an online accessible-to-scan Upwell cyno beacon. Wire test checks the beacon system ID.
- **State, errors, events:** Read-only. No notification. Same POS coverage gap as method 6.
- **EveJS and check:** EveJS `structureDirectoryService.js:530-549` also accounts for POS sources. eve.rs derives the tested Upwell system from persisted structures and service state.

### 9. `CheckMyDockingAccessToStructures`

- **Client calls and use:** `structureDirectory.py:85-87`; the location parameter and freelance project selector pass structure-ID lists and catch `GenericStopSpamming` (`parameter_types/controllers/matchertypes/parameter_location.py:136,147,169-173`, `jobboard/client/features/freelance_projects/job.py:602,634-640).
- **Arguments and reply:** One positional list of IDs; Host accepts Python int/long members, removes invalid/nonpositive/duplicate IDs, and returns the allowed IDs in request order. None/empty returns an empty list. The Host wire regression covers normal IDs, the 500-item boundary, None, and 501-item refusal.
- **State, errors, events:** Access is calculated against persisted docking profiles and access groups. More than 500 raw list entries returns the exact client-handled `GenericStopSpamming` UserError before snapshot reads (`StructureDirectoryService.cs:192-206); no state changes or notification.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:496-503`. The focused service test `StructureDirectoryServiceTests.Docking_access_refuses_more_than_500_structure_ids_before_the_callback` verifies refusal before callback work.

### 10. `GetValidWarHQs`

- **Client calls and use:** The wrapper selects `session.allianceid or session.corpid` and sends it as the positional owner ID (`structureDirectory.py:179-182). War landing and declaration paths call the local wrapper at `warLandingPage.py:149-152` and `warDeclare.py:478-493`; the declaration UI reads `structureID,itemName,typeID`.
- **Arguments and reply:** One int/long owner ID. Host returns a list of KeyVals with those fields plus `upkeepState,wars,ownerID,solarSystemID,inSpace`; it returns an empty list for an owner ID outside the current corporation/alliance (`StructureDirectoryService.cs:164-171`). Candidate validation uses SDE category 65, dockable type, and system security at least 0.45 (`PersistenceStructureDirectoryReadModel.cs:607-680).
- **State, errors, events:** Read-only persisted structure/SDE query; no notification. An empty candidate set returns empty without issuing SDE reads (`:635-636`).
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:551-569`. The wire test sends the client-selected alliance ID as PyLong and gets an empty list because the fixture contains no eligible structure. Populated SDE-backed war-HQ rows are not exercised locally.

### 11. `GetJumpBridgesWithMyAccess`

- **Client calls and use:** Direct remote call from `mapSvc.py:461-462`; pathfinder, star map and map view unpack its three components at `clientPathfinderService.py:220-222`, `starMapSvc.py:1692-1693`, and `mapView.py:1455-1456`.
- **Arguments and reply:** No arguments. Returns a three-part tuple of jump-bridge pairs, structures accessible to the character, and structures without access. The Host test checks the tuple, a reciprocal pair, and accessible/offline lists.
- **State, errors, events:** Read-only persisted bridge/profile/access-group state; no notification.
- **EveJS and check:** EveJS `structureDirectoryService.js:571-574` delegates to the same state calculation. The Host wire test also confirms the offline bridge is excluded from accessible routes while remaining in the separate bridge directory result.

### 12. `GetStructureDescription`

- **Client calls and use:** Direct RPC calls at `panelDescription.py:31-33` and `structureEntryMyCorp.py:408-410`.
- **Arguments and reply:** One positional structure ID (int/long). The reply is a Python 2 string (ASCII wire values decode as PyBytes in the Host test). Missing/invalid structure IDs return an empty string. The description editor consumes the current text.
- **State, errors, events:** Reads the persisted description, capped at 1,000 characters; no mutation or notification.
- **EveJS and check:** EveJS `structureDirectoryService.js:459-467`. Host test reads a long ID before the write and an int ID after it.

### 13. `SetStructureDescription`

- **Client calls and use:** Direct RPC at `structureEntryMyCorp.py:598`; the editor closes immediately at `:599` and ignores the success reply.
- **Arguments and reply:** Positional structure ID and description string; int/long IDs are accepted, text is capped at 1,000 characters. Success is None. The client exposes the edit action to Station Managers at `:340-341`.
- **State, errors, events:** Host rechecks persisted corporation membership, ownership, and Station Manager role. Denial is `CrpAccessDenied(reason=[101, "UI/Corporations/AccessRestrictions/InsufficientRoles"])`, matching the client-visible UserError. Success writes through the `StructureStore.SetDescriptionAsync` transaction callback (`StructureComposition.cs:60-62`) and a later directory read returns the saved value. No notification is expected or emitted; EveJS returns None after its structure update.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:469-494`, with the same role requirement at `:118-131`. Host wire test checks exact error code and reason, success None, and read-after-write visibility. The regression uses the in-memory Host read/write provider; database restart durability is source-reviewed but not exercised.

### 14. `GetNearbyJumpBridges`

- **Client calls and use:** The deployment helper makes the no-argument RPC at `structureDeployment.py:250-256`, then indexes rows by solarSystemID; the deployment UI reads `alignedToCurrentSystem` at `stargateDeploymentCont.py:42-43,57-74`.
- **Arguments and reply:** No arguments; current system comes from the session. Returns a list of KeyVals with structure/type/system/owner/name/destination/alignment fields. Host test checks a foreign offline bridge and current-system alignment.
- **State, errors, events:** Read-only list of nondestroyed persisted Ansiblex bridges. No notification. The missing-name fallback is the same `Ansiblex Jump Bridge` string as EveJS.
- **EveJS and check:** EveJS handler is `structureDirectoryService.js:576-601`. Host wire test covers reciprocal, offline, and third-party bridge rows.

## State, notification, and compatibility summary

Only `SetStructureDescription` mutates state. Production Host wiring writes through the structure-store transaction, and the wire test reads the new description on the next call. Directory read methods do not emit notifications; the client invalidates local caches from its own session/slim-item/war events in `structureDirectory.py:96-159`. No server notification is expected after the description edit: the client closes the editor after the RPC.

The fixed Host gap was `GetMyCharacterStructures` filtering by owning corporation. EveJS lists all structures the character can dock at, including public third-party structures; Host now ACL-filters all persisted structures. The regression includes an accessible other-corporation bridge and confirms its six-field safe projection. Station entries use service lists loaded from the pinned SDE.

A POS cyno-beacon source is still absent from both beacon methods because the Host has no authoritative POS module-state read model. No POS payload is fabricated. The populated `GetStructureMapData` CRowset and nonempty SDE-filtered `GetValidWarHQs` row were source-reviewed; the local wire fixture intentionally uses `sde=null` and exercises only their valid call routing and empty replies. No PostgreSQL database or graphical client was used. The description persistence test proves immediate read-after-write through the Host but not database restart behavior.

## Local checks

- Passed: `Eve.Host.Tests.HostServerTests.Structure_directory_all_client_methods_keep_wire_shapes_and_errors` (1 passed, 0 failed, 0 skipped).
- Passed: `Eve.Services.Core.Tests.StructureDirectoryServiceTests.Docking_access_refuses_more_than_500_structure_ids_before_the_callback` (1 passed, 0 failed, 0 skipped).
- Full `Eve.Host.Tests` project shard: 529 passed, 100 failed, 227 skipped, without a database URL. The failing set includes database-dependent tests; the skipped count is reported separately. The full shard is not a passing result.
- The first push-triggered `ci.yml` run failed on the registry inventory total and the mail adapter regression test. The inventory now includes the added directory alias; the mail assertions now follow the client’s null recipient and sender read-bit semantics (`eve/client/script/ui/services/mail/mailSvc.py:1128-1134`). Both focused tests pass locally; a CI rerun is pending.
- No real graphical client run was performed.

## Appendix A: whole-client `GetStructureInfo` callers

The full V24.01 source search found the following direct calls and wrappers. `GetStructureInfo_` is the distinct alias used by `eveMachoNet.py`.

- Service wrapper and local helpers: `eve/client/script/ui/services/structure/structureDirectory.py:32-38,41,90`; alias call `eve/common/script/net/eveMachoNet.py:369`.
- `eveAssets/assetSearchUtil.py:518`; `parameter_types/controllers/matchertypes/parameter_location.py:136,147`; `moonmining/autoMoonMining/client/auto_moon_miner_window.py:38`; `explorationscanner/signalTracker.py:139,239,305` (local helper and its callers).
- `menucheckers/characterCheckers.py:231`; `menucheckers/itemCheckers.py:968`; `menucheckers/bookmarkChecker.py:170`; `menucheckers/celestialCheckers.py:58,1986,2031,2275`; `eve/common/script/util/industryCommon.py:443`.
- `eve/client/script/util/clientPathfinderService.py:29`; `eve/client/script/dogma/clientDogmaLocation.py:145`; `eve/client/script/ui/eveCommands.py:2705`; `eve/client/script/ui/shared/autopilotSettings.py:176`; `eve/client/script/ui/shared/neocom/corporation/corp_ui_accounts.py:551,763`.
- `eve/client/script/ui/shared/neocom/corporation/war/warReport.py:268`; `eve/client/script/ui/shared/neocom/corporation/war/warDeclare.py:460,606`; `eve/client/script/ui/shared/neocom/wallet/panels/marketTransactionsPanel.py:262`; `eve/client/script/ui/shared/neocom/wallet/panels/personal/lpPanel.py:130,209`; `eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:384`.
- `eve/client/script/ui/shared/neocom/contracts/contracts.py:705,914`; `eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:565,581,761,1140`; `eve/client/script/ui/shared/neocom/contracts/contractentry.py:161`; `eve/client/script/ui/shared/neocom/contracts/contractsDetailsWnd.py:299,921`; `eve/client/script/ui/shared/fittingScreen/tryFit.py:283`.
- `eve/client/script/ui/shared/infoPanels/infoPanelSvc.py:443`; `eve/client/script/ui/shared/infoPanels/infoPanelRoute.py:316,552,598,615`; `eve/client/script/ui/shared/infoPanels/infoPanelLocationInfo.py:283`; `eve/client/script/ui/shared/maps/starMapSvc.py:2318`; `eve/client/script/ui/shared/maps/maputils.py:391`; `eve/client/script/ui/shared/maps/mapSvc.py:194,260`.
- `eve/client/script/ui/shared/info/infoContentBase.py:1114,1490`; `eve/client/script/ui/shared/info/infosvc.py:788,824`; `eve/client/script/ui/shared/market/marketHistory.py:245,300`; `eve/client/script/ui/shared/market/quote.py:326`; `eve/client/script/ui/shared/market/entries.py:118`; `eve/client/script/ui/shared/market/sellMulti.py:222`; `eve/client/script/ui/shared/market/buyThisTypeWindow.py:635`; `eve/client/script/ui/shared/market/orders.py:288`.
- `eve/client/script/ui/shared/fitting/fittingSlotController.py:183`; `eve/client/script/ui/structure/structureHackingResult.py:192`; `eve/client/script/ui/structure/structureBrowser/reinforcementTimeWnd.py:62,162`; `eve/client/script/ui/structure/dropbox/dropboxController.py:21`; `eve/client/script/ui/util/searchUtil.py:209`.
- `eve/client/script/ui/moonmining/schedulingSvc.py:26`; `eve/client/script/ui/moonmining/scheduling.py:203`; `eve/client/script/ui/services/marketsvc.py:178,487`; `eve/client/script/ui/services/menusvc.py:167,1613`; `eve/client/script/ui/services/structure/structureControl.py:41`; `eve/client/script/ui/services/structure/structureDeployment.py:58`.
- `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:351`; `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:463`; `eve/client/script/ui/station/undockQuestions.py:132`; `eve/client/script/ui/control/statefulButtonLocationController.py:104,197`; `eve/client/script/ui/control/eveEditPlainText.py:328`.
- `eve/client/script/environment/invCache.py:688`; `eve/client/script/parklife/gameui.py:926`; `eve/client/script/parklife/autopilot.py:213,664`; `objectives/client/objective_task/location.py:55`; `carbonui/control/forms/formValidators.py:152`; `jobboard/client/features/freelance_projects/job.py:602`; `localization/propertyHandlers/locationPropertyHandler.py:61`; `contracts/createContractHelpers.py:90`; `campaigns/client/parameter_types/controllers/parameter_location.py:113`.
