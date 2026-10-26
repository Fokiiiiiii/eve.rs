# Client verification — P0 implemented rows, batch 2

## Trace summary

The V24.01 client address book consumes `GetContactList` as `addressbook.addresses` rows and reads `contactID`, `inWatchlist`, `relationshipID`, and `labelMask` (`eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:207-246`). Contact edit/delete return values are ignored at the cited callsites; delete has two argument shapes at lines 819-842.

Owner-note consumers read row fields `note` and `label`, often using the first row (`eve/client/script/ui/shared/neocom/notepad.py:194-214,242-249,508-519`). Mutation results are ignored (`notepad.py:173,213,544,647`). Character-note reads return display text (`notepad.py:508-514`, `eve/client/script/ui/shared/info/panels/panelNotes.py:34-35`). Character creation date is memoized (`ccSvc.py:48-62`); settings are keyed mapping lookups and missing keys return None (`characterSettingsSvc.py:30-40)). Home station flows through the character sheet and callers read `stationID` (`charactersheet.py:58-62`; `quote.py:264`, `mapView.py:352,515`, `mapViewSolarSystem.py:540`, `mapFilterOreAnomalies.py:71`). The listed organization-info and cohorts callsites are `chat/client/local_chat/channel_controller.py:227` and `gatekeeper/client/service.py:41`.

These are client call/consumer traces, not a completed wire-shape comparison. Existing matching tests found through code search are service-level tests or generic dispatch fixtures. I did not confirm a focused test through real Host registration for any row below. EveJS 261018.1 and the owner-capture analysis have not yet been compared row by row, so these verdicts record missing wire-test evidence only.

| row_id | service.method | client call site | consumers | handler | verdict |
|---|---|---|---|---|---|
| RPC-01010 | charMgr.DeleteContacts | eve-logic `addressbookService.py:820` | mutation result ignored; addressbook refresh | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:599-608` | NO-TEST |
| RPC-01012 | charMgr.DeleteContacts | eve-logic `addressbookService.py:842` | mutation result ignored; addressbook refresh | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:599-608` | NO-TEST |
| RPC-01013 | charMgr.DeleteContacts | eve-logic `addressbookService.py:842` | nested contact ID list; result ignored | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:599-608` | NO-TEST |
| RPC-01014 | charMgr.EditContactsRelationshipID | eve-logic `addressbookService.py:688` | result ignored; relationship state refreshed | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:610-630` | NO-TEST |
| RPC-01015 | charMgr.EditContactsRelationshipID | eve-logic `addressbookService.py:688` | result ignored; relationship state refreshed | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:610-630` | NO-TEST |
| RPC-01016 | charMgr.EditOwnerNote | eve-logic `notepad.py:214` | writes owner note; later reads note and label | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01017 | charMgr.EditOwnerNote | eve-logic `notepad.py:545` | result ignored; notepad cache updated | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01018 | charMgr.EditOwnerNote | eve-logic `notepad.py:173` | result ignored; preference text assembled from returned notes | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST || RPC-01019 | charMgr.EditOwnerNote | eve-logic `notepad.py:173` | result ignored; preference text assembled from returned notes | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01020 | charMgr.EditOwnerNote | eve-logic `notepad.py:647` | result ignored; label updated locally | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01021 | charMgr.EditOwnerNote | eve-logic `notepad.py:647` | result ignored; label updated locally | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01022 | charMgr.GetCharacterCreationDate | eve-logic `ccSvc.py:54-55` | memoized at ccSvc.py:48-62; public-info startDateTime | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:188-190` | NO-TEST |
| RPC-01023 | charMgr.GetCharacterCreationDate | eve-logic `ccSvc.py:54-55` | memoized at ccSvc.py:48-62; public-info startDateTime | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:188-190` | NO-TEST |
| RPC-01024 | charMgr.GetCharacterDescription | eve-logic `bioPanel.py:28` | bio panel displays returned text | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:157-166` | NO-TEST |
| RPC-01025 | charMgr.GetCharacterDescription | eve-logic `bioPanel.py:28` | bio panel displays returned text | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:157-166` | NO-TEST |
| RPC-01026 | charMgr.GetCharacterSettings | eve-logic `characterSettingsSvc.py:32-33` | mapping lookup by setting key; missing key becomes None at lines 37-40 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:347-372` | NO-TEST |
| RPC-01027 | charMgr.GetCohortsForCharacter | eve-logic `gatekeeper/client/service.py:41` | client call located; whole-tree consumer trace incomplete | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:219-220` | NO-TEST |
| RPC-01028 | charMgr.GetContactList | eve-logic `addressbookService.py:207-208` | addressbook.addresses rows; fields contactID/inWatchlist/relationshipID/labelMask at lines 230-246 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:347-372` | NO-TEST |
| RPC-01029 | charMgr.GetContactList | eve-logic `addressbookService.py:217-218` | same addressbook cache and row fields | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:347-372` | NO-TEST |
| RPC-01030 | charMgr.GetHomeStationRow | eve-logic `charactersheet.py:59` | stationID consumed by quote.py:264, mapView.py:352,515, mapViewSolarSystem.py:540, mapFilterOreAnomalies.py:71 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:193-214` | NO-TEST |
| RPC-01031 | charMgr.GetHomeStationRow | eve-logic `charactersheet.py:59` | stationID consumed by quote.py:264, mapView.py:352,515, mapViewSolarSystem.py:540, mapFilterOreAnomalies.py:71 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:193-214` | NO-TEST |
| RPC-01032 | charMgr.GetNote | eve-logic `notepad.py:509` | returned string displayed in notepad | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:457-464` | NO-TEST |
| RPC-01033 | charMgr.GetNote | eve-logic `notepad.py:509` | returned string displayed in notepad | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:457-464` | NO-TEST |
| RPC-01034 | charMgr.GetNote | eve-logic `panelNotes.py:35` | panel displays returned text | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:457-464` | NO-TEST |
| RPC-01035 | charMgr.GetNote | eve-logic `panelNotes.py:35` | panel displays returned text | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:457-464` | NO-TEST |
| RPC-01036 | charMgr.GetOrganizationInfoForCharacters | eve-logic `channel_controller.py:227` | local chat org/character lookup; full field trace incomplete | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:493-538` | NO-TEST |
| RPC-01037 | charMgr.GetOwnerNote | eve-logic `notepad.py:195` | first row note/label fields joined at lines 194-214 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:438-455` | NO-TEST |
| RPC-01038 | charMgr.GetOwnerNote | eve-logic `notepad.py:247` | first row note/label fields read at lines 242-249 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:438-455` | NO-TEST || RPC-01039 | charMgr.GetOwnerNote | eve-logic `notepad.py:247` | first row note/label fields read at lines 242-249 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:438-455` | NO-TEST |

## Remaining work

Compare all 12 methods against EveJS 261018.1 and `reports/2026-10-24-core-breakage-from-owner-logs.md`; inspect each method's Host registration and reply encoding; add focused real-Host wire assertions and replace these provisional NO-TEST verdicts with MATCHES or MISMATCH. The status_evidence updater interface was not checked, so the CSV was left unchanged. No database was used.


# P0 implemented rows — batch 7

## Client contract and comparison

The client builds the config recordsets in `eve/common/script/sys/eveCfg.py`: alliance short names at :378-379, corporation tickers at :479-480, owners at :549-550, locations at :665-666, and positions at :396-397. `carbon/common/script/sys/cfg.py:537-604` shows the shared `Recordset._Prime` contract: it sends a list of pending keys, accepts a header/rows tuple or rowset, and indexes returned rows by the declared key column. The owner columns are `ownerID, ownerName, typeID, gender, ownerNameID` (`eveCfg.py:591-603)); the location columns include `locationID, locationName, solarSystemID, x, y, z, locationNameID`. The full client tree uses the owner recordset for names/types throughout UI and gameplay; an earlier whole-tree count found 768 `cfg.eveowners.Get` consumers in 275 files (service-audit/HANDOFF.md, P0 evidence).

The Host config calls return the same tuple-set headers and rows, and the single-item wrappers delegate to the tested multi-item reads. The batch Host wire test now also calls the single corporation ticker and alliance short-name routes using valid missing IDs and asserts their full empty headers. This covers the invalid-ID failures in the 2026-10-24 owner-log analysis: the existing owner batch request sends 501 long IDs and `machoVersion=1` through Host (`ConfigRecordsetHostWireTests.cs:13-108)); the dispatch strips transport metadata before the service validates the application list.

The station owner method returns a list of `util.KeyVal` rows with `ownerID`, `solarSystemID`, and `stationCount`, matching EveJS `configService.js:858-867`. The info service schedules this result but does not read its fields (`infosvc.py:1163-1170)); the map color and settled-corporation filter read `solarSystemID` (`mapViewColorHandler.py:199-204`, `mapFilterSettledByCorp.py:16-17`).

The other pairs also match their client use. Name validation returns the numeric validation code cached and consumed by the creation UI (`chooseNameSection.py:187-202)); EveJS returns that code (`charService.js:1913-1920)). Newbie-ship creation returns `None`, ignored by the caller; EveJS also returns null after its boarding operation (`station/base.py:609-620`, `dogmaService.js:4125-4152`). Both listed `invbroker.Add` callers ignore the result; their shared inventory wrapper forwards to the bound object (`invCache.py:998-1025)); EveJS returns the moved item ID (`invBrokerService.js:12463-13100)), and the Host test asserts the bound-call result and move. The item descriptor is cached as the row header for inventory CRowsets and dogma sub-location descriptors (`invCache.py:427-431,1140-1170`, `godma.py:122-149)); Host and EveJS both return the static descriptor (`invBrokerService.js:14030-14034`).

The style and multi-login calls return booleans consumed by visual branches and the character-entry gate (`characterSelectionUtils.py:71-75`; `characterSelection.py:620-628`; `characterSlots.py:1012-1018)); EveJS returns booleans (`kiringMgrService.js:151-154`, `multiLoginBlockerService.js:162-190`). Modified security systems are an empty indexable Rowset with header `solarSystemID`; the client checks membership before reading `security` and `modifiedSecurity`, so no row produces an empty label (`characterSlots.py:1012-1018`). EveJS returns the same empty rowset (`mapService.js:514-550)). Ping returns `None`, which the idle ping loop ignores (`connectionService.py:178-191)); EveJS also returns null (`pingService.js:17-19)). The current Host wire test passes `machoVersion=1`, addressing the Ping argument errors recorded in the owner-log analysis.

Every row maps to a client call or fetcher. `GetLocationsLocal` is the exception to the remote-call path: `eveCfg.py:665-666` passes it as `clientLocalFetcher`, and `cfg.py:552-576` invokes it directly only for local identity keys. That row is client-local, not a runtime RPC; its Host route mirrors the location-rowset contract and is covered by the Host test. Host tests cover real service registration, request decoding, reply encoding, and the fields consumed by the client. The map test uses the existing read hook to supply the matching empty read-side rowset and does not open a database connection.

| row_id | service.method | client call site | consumers | handler | verdict |
|---|---|---|---|---|---|
| RPC-01160 | charUnboundMgr.ValidateNameEx | eve-logic `chooseNameSection.py:188` | numeric name-validation code is cached and returned to the creation UI (`:188-202`) | `CharUnboundMgrService.cs:182-195`; `CharUnboundMgrValidationHostTests.cs:12-47` | MATCHES |
| RPC-01175 | config.GetAllianceShortNamesEx | `eveCfg.py:378-379` | keyed `(header, rows)` recordset; `allianceID`/shortName | `ConfigService.Recordsets.cs:17-19`; `ConfigRecordsetHostWireTests.cs:13,115-118` | MATCHES |
| RPC-01176 | config.GetCorpTickerNamesEx | `eveCfg.py:479-480` | keyed corporation ticker rows and optional logo/color columns | `ConfigService.Recordsets.cs:13-15`; `ConfigRecordsetHostWireTests.cs:13,113-116` | MATCHES |
| RPC-01177 | config.GetLocationsEx | `eveCfg.py:665-666` | `evelocations` rowset keyed by `locationID`; lazily loads local IDs then remote misses | `ConfigService.Recordsets.cs:21-23`; `ConfigRecordsetHostWireTests.cs:91-95` | MATCHES |
| RPC-01178 | config.GetLocationsLocal | `eveCfg.py:665-666` | client-local identity fetch in `cfg.py:552-576`; tuple/columns rows populate `evelocations`, so this route is not called by the client | `ConfigService.Recordsets.cs:25-26`; `ConfigRecordsetHostWireTests.cs:94-95` | MATCHES (client-local path) |
| RPC-01179 | config.GetMultiAllianceShortNamesEx | `eveCfg.py:378-379` | batch alliance rows are indexed by `allianceID` in `cfg.py:576-604` | `ConfigService.cs:190-210`; `ConfigRecordsetHostWireTests.cs:37,84-86` | MATCHES |
| RPC-01180 | config.GetMultiCorpTickerNamesEx | `eveCfg.py:479-480` | batch corporation rows are indexed by `corporationID` | `ConfigService.cs:159-188`; `ConfigRecordsetHostWireTests.cs:37,81-83` | MATCHES |
| RPC-01181 | config.GetMultiLocationsEx | `eveCfg.py:665-666` | batch location rows populate the same `evelocations` cache | `ConfigService.cs:213-256`; `ConfigRecordsetHostWireTests.cs:91-95` | MATCHES |
| RPC-01182 | config.GetMultiOwnersEx | `eveCfg.py:549-550` | owner rows indexed by `ownerID`; whole-tree `eveowners` consumers read name/type and related columns | `ConfigService.cs:96-154`; `ConfigRecordsetHostWireTests.cs:76-80` | MATCHES |
| RPC-01183 | config.GetOwnersEx | `eveCfg.py:549-550` | single owner row uses the same owner columns and key | `ConfigService.Recordsets.cs:9-11`; `ConfigRecordsetHostWireTests.cs:70-75` | MATCHES |
| RPC-01184 | config.GetPositionEx | `eveCfg.py:396-397` | keyed position row reads itemID, coordinates, and rotation values | `ConfigService.Recordsets.cs:28-30`; `ConfigRecordsetHostWireTests.cs:97-104` | MATCHES |
| RPC-01185 | config.GetPositionsEx | `eveCfg.py:396-397` | batch position rows use the same itemID key and seven columns | `ConfigService.Recordsets.cs:32-39`; `ConfigRecordsetHostWireTests.cs:105-108` | MATCHES |
| RPC-01186 | config.GetStationSolarSystemsByOwner | `infosvc.py:1163-1164` | parallel result is assigned to `systems`; no fields are read there | `ConfigService.cs:38-57`; `ConfigRecordsetHostWireTests.cs:110-113` | MATCHES |
| RPC-01187 | config.GetStationSolarSystemsByOwner | `mapViewColorHandler.py:199` | iterates rows and reads `solarSystemID` at :202 | `ConfigService.cs:38-57`; `ConfigRecordsetHostWireTests.cs:110-113` | MATCHES |
| RPC-01188 | config.GetStationSolarSystemsByOwner | `mapViewColorHandler.py:199` | same map color path; reads each row's `solarSystemID` | `ConfigService.cs:38-57`; `ConfigRecordsetHostWireTests.cs:110-113` | MATCHES |
| RPC-01189 | config.GetStationSolarSystemsByOwner | `mapFilterSettledByCorp.py:16` | builds solar-system keyed filter data from `solarSystemID` at :17 | `ConfigService.cs:38-57`; `ConfigRecordsetHostWireTests.cs:110-113` | MATCHES |
| RPC-01190 | config.GetStationSolarSystemsByOwner | `mapFilterSettledByCorp.py:16` | same filter path; reads `solarSystemID` | `ConfigService.cs:38-57`; `ConfigRecordsetHostWireTests.cs:110-113` | MATCHES |
| RPC-01355 | dogmaIM.CreateNewbieShip | `station/base.py:613` | return ignored; UserError is re-raised, other failures map to station error | `DogmaIMService.cs:184-220`; `DogmaRpcHostTests.cs:11-65` | MATCHES |
| RPC-01356 | dogmaIM.CreateNewbieShip | `station/base.py:613` | same call; client relies on server boarding side effects, not reply data | `DogmaIMService.cs:184-220`; `DogmaRpcHostTests.cs:11-65` | MATCHES |
| RPC-00994 | invbroker.Add | `fittingSlotController.py:237-242` | return ignored; module move drives inventory/dogma updates | `InvBrokerService.cs:1255-1300`; `ServiceAuditP0HostWireTests.cs:99-147` | MATCHES |
| RPC-00995 | invbroker.Add | `nodegraph/client/actions/module.py:73-77` | return ignored; module move is executed through the same inventory wrapper | `InvBrokerService.cs:1255-1300`; `ServiceAuditP0HostWireTests.cs:99-147` | MATCHES |
| RPC-01708 | invbroker.GetItemDescriptor | `invCache.py:429-431` | cached descriptor defines inventory rowsets and dogma sub-location fields | `InvBrokerService.cs:373-374`; `InvBrokerDescriptorHostTests.cs:12-37` | MATCHES |
| RPC-01709 | invbroker.GetItemDescriptor | `invCache.py:429-431` | same cached descriptor and all inventory rowset consumers | `InvBrokerService.cs:373-374`; `InvBrokerDescriptorHostTests.cs:12-37` | MATCHES |
| RPC-01750 | kiringMgr.IsMonochromeStyleEnabled | `characterSelectionUtils.py:71-75` | bool gates logo, portrait, style and security-status color branches | `CoreCompatibilityServiceAdapters.cs:338-340`; `ImplementedClientRowsHostWireTests.cs:13-25` | MATCHES |
| RPC-01751 | kiringMgr.IsMonochromeStyleEnabled | `characterSelectionUtils.py:71-75` | same memoized bool, consumed by character selection and slots | `CoreCompatibilityServiceAdapters.cs:338-340`; `ImplementedClientRowsHostWireTests.cs:13-25` | MATCHES |
| RPC-01962 | map.GetSecurityModifiedSystems | `characterSlots.py:1013-1014` | Indexes by `solarSystemID`; reads security fields only for a present row; otherwise returns empty text | `StationService.cs:293-300`; `ImplementedClientRowsHostWireTests.cs:28-55` | MATCHES |
| RPC-02066 | multiLoginBlocker.Login | `characterSelection.py:623-624` | false opens the blocked window and stops login; true continues | `MultiLoginBlockerService.cs:25-50`; `ImplementedClientRowsHostWireTests.cs:59-86` | MATCHES |
| RPC-02067 | multiLoginBlocker.Login | `characterSelection.py:623-624` | same bool gate when entering a selected character | `MultiLoginBlockerService.cs:25-50`; `ImplementedClientRowsHostWireTests.cs:59-86` | MATCHES |
| RPC-02247 | pingService.Ping | `connectionService.py:189` | periodic idle ping; reply ignored | `PingService.cs:10-16`; `LoginCompatibilityRpcHostWireTests.cs:57` | MATCHES |

## Checks and remaining limits

The status-evidence generator was refreshed without a queue directory, so the 03-allsvc-batch queue files were left untouched. The new tests and existing real-Host wire tests are source checks; Actions results are recorded after the selected run completes. No PostgreSQL database was used. The graphical V24.01 client was not exercised.
# Client verification — P0 implemented rows, batch 5

Whole-tree V24.01 caller searches show all consumers summarized below. The owner capture analysis (2026-10-24-core-breakage-from-owner-logs.md, findings 1–8) does not name any of these methods; that is not evidence that calls succeeded. EveJS 261018.1 uses the corresponding return intents at `charMgrService.js:626-628, 1104-10, 1112-21, 1123-40, 1142-52, 1199-1205, 1214-28`; creation/deletion at `charService.js:1089-1373,1869-79` and `characterDeletionRuntime.js:685-704,706-789`; Omega status at `charService.js:1526-29`.

| row_id | service.method | client call site | consumers | handler | verdict |
|---|---|---|---|---|---|
| RPC-01100 | charMgr.ListStations | `assetsWindow.py:923` | iterates station rows | `CharMgrService.cs:308-321`; registered Host rowset assertion `CharMgrRpcHostWireTests.cs:53-58` | MATCHES |
| RPC-01101 | charMgr.ListStations | `quote.py:365` | iterates station rows for market selector | `CharMgrService.cs:308-321`; `CharMgrRpcHostWireTests.cs:53-58` | MATCHES |
| RPC-01102 | charMgr.ListStations | `assetsinfo.py:149` | iterates station rows | `CharMgrService.cs:308-321`; `CharMgrRpcHostWireTests.cs:53-58` | MATCHES |
| RPC-01103 | charMgr.ListStations | `createContractWnd.py:1572` | iterates station rows | `CharMgrService.cs:308-321`; `CharMgrRpcHostWireTests.cs:53-58` | MATCHES |
| RPC-01104 | charMgr.ListStations | `corp_ui_accounts.py:753` | indexes rows by `stationID` | `CharMgrService.cs:308-321`; `CharMgrRpcHostWireTests.cs:53-58` | MATCHES |
| RPC-01105 | charMgr.LogSettings | `settingsSvc.py:154` | only called for nonempty stats; reply ignored | `CharMgrService.cs:1084-1100`; Host asserts `PyNone`, `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01106 | charMgr.LogSettings | `settingsSvc.py:154` | same call; reply ignored | `CharMgrService.cs:1084-1100`; `CharMgrRpcHostWireTests.cs:68` | MATCHES || RPC-01108 | charMgr.RemoveOwnerNote | `notepad.py:108` | parallel cleanup of invalid note IDs; reply ignored | `CharMgrService.cs:1103-1132`; Host asserts `PyNone`, `CharMgrRpcHostWireTests.cs:69` | MATCHES |
| RPC-01109 | charMgr.RemoveOwnerNote | `notepad.py:180` | parallel cleanup of stale note IDs; reply ignored | `CharMgrService.cs:1103-1132`; `CharMgrRpcHostWireTests.cs:69` | MATCHES |
| RPC-01110 | charMgr.RemoveOwnerNote | `notepad.py:704` | deletes note and reloads notepad; reply ignored | `CharMgrService.cs:1103-1132`; `CharMgrRpcHostWireTests.cs:69` | MATCHES |
| RPC-01111 | charMgr.RemoveOwnerNote | `notepad.py:704` | same delete flow; reply ignored | `CharMgrService.cs:1103-1132`; `CharMgrRpcHostWireTests.cs:69` | MATCHES |
| RPC-01112 | charMgr.SaveCharacterSetting | `characterSettingsSvc.py:49` | writes key/value then updates local cache; empty values use Delete; client caps values at 100 KB | `CharMgrService.cs:437-462`; registered Host persistence test `CharacterManagerMutationHostWireTests.cs:10-81` asserts `PyNone` and round-trips state | MATCHES |
| RPC-01113 | charMgr.SetActivityStatus | `gameui.py:339` | active status with FILETIME extraInfo; reply ignored | `CharMgrService.cs:1144-1158` | NO-TEST |
| RPC-01114 | charMgr.SetActivityStatus | `gameui.py:339` | same active transition | `CharMgrService.cs:1144-1158` | NO-TEST |
| RPC-01115 | charMgr.SetActivityStatus | `gameui.py:334` | AFK status with FILETIME extraInfo; reply ignored | `CharMgrService.cs:1144-1158` | NO-TEST |
| RPC-01116 | charMgr.SetActivityStatus | `gameui.py:334` | same AFK transition | `CharMgrService.cs:1144-1158` | NO-TEST |
| RPC-01117 | charMgr.SetCharacterDescription | `bioPanel.py:61` | autosaves text; on exception reloads it; reply ignored | `CharMgrService.cs:204-230` | NO-TEST |
| RPC-01118 | charMgr.SetCharacterDescription | `bioPanel.py:61` | same exception recovery | `CharMgrService.cs:204-230` | NO-TEST |
| RPC-01119 | charMgr.SetNote | `notepad.py:541` | asynchronous item note save; reply ignored | `CharMgrService.cs:1160-1186` | NO-TEST |
| RPC-01120 | charMgr.SetNote | `panelNotes.py:61` | saves changed text or empty string; reply ignored | `CharMgrService.cs:1160-1186` | NO-TEST |
| RPC-01121 | charMgr.SetNote | `panelNotes.py:61` | same note editor flow | `CharMgrService.cs:1160-1186` | NO-TEST |
| RPC-01122 | charMgr.UnblockOwners | `addressbookService.py:674` | local blocked cache update and `OnUnblockContacts` precede refresh; reply ignored | `CharMgrService.cs:1188-1196` | NO-TEST |
| RPC-01123 | charMgr.UnblockOwners | `addressbookService.py:674` | same unblock flow | `CharMgrService.cs:1188-1196` | NO-TEST |
| RPC-01125 | charUnboundMgr.CancelCharacterDeletePrepare | `characterSelection.py:871` | cancels deletion then refreshes slot; reply ignored | `CharUnboundMgrService.cs:450-463` | NO-TEST |
| RPC-01126 | charUnboundMgr.CancelCharacterDeletePrepare | `characterSelection.py:871` | same cancel and refresh | `CharUnboundMgrService.cs:450-463` | NO-TEST |
| RPC-01127 | charUnboundMgr.CreateCharacterWithDoll | `ccSvc.py:97` | reply ID clears selection cache; both creation UIs use ID for portrait insertion (`characterCreationContainer_new.py:1301-1305`, `characterCreationContainer.py:1840-1849`) | `CharUnboundMgrService.cs:262-306` | NO-TEST |
| RPC-01128 | charUnboundMgr.CreateCharacterWithDoll | `ccSvc.py:97` | same reply ID consumers | `CharUnboundMgrService.cs:262-306` | NO-TEST || RPC-01129 | charUnboundMgr.DeleteCharacter | `characterSelection.py:848` | expects falsey success; truthy error is displayed, otherwise selection refreshes (`characterSelection.py:852-860`) | `CharUnboundMgrService.cs:503-527` | NO-TEST |
| RPC-01130 | charUnboundMgr.GetCharOmegaDowngradeStatus | `characterSelection.py:301` | bool controls lapsed-subscription warning creation; errors are caught | `CharUnboundMgrService.cs:158-169`; registered Host bool test `CharUnboundOmegaHostTests.cs:10-47` | MATCHES |

## Batch 5 remaining work

The inspected registered-Host tests verify ListStations, LogSettings, RemoveOwnerNote, SaveCharacterSetting, and GetCharOmegaDowngradeStatus. Seven distinct methods remain NO-TEST: SetActivityStatus, SetCharacterDescription, SetNote, UnblockOwners, CancelCharacterDeletePrepare, CreateCharacterWithDoll, and DeleteCharacter. Existing service-level or persistence tests do not replace their missing client-shaped Host assertions. `run_service_audit_current.py --help` exposes only client/repository/queue roots, and its current status override has no batch-5 evidence entries; EVE_RS_COVERAGE.csv was left unchanged. No database, graphical client, or focused test was used for this verification.


# Client verification — P0 implemented rows, batch 1 (2026-10-26)

All 29 rows were checked; duplicate IDs share the same V24.01 caller and contract. Client paths are relative to `/home/user/eve-logic`. Whole-tree searches confirmed the consumers listed below.

| row_id | service.method | client call site | consumers and contract | Host handler / test | verdict |
|---|---|---|---|---|---|
| RPC-00866 | air_npe.get_air_npe_state | `storylines/client/airnpe.py:78-83` | Caches integer state; compares ACTIVE/COMPLETED/SKIPPED (:86-96); state notification updates cache/UI (:56-64). | `AirNpeService.cs:34-42`; `LoginCompatibilityRpcHostWireTests.cs:49`, `LoginOnboardingSubscriptionRpcHostWireTests.cs:58-61` (int, post-skip state/notice). | MATCHES |
| RPC-00867 | air_npe.is_air_npe_enabled | `storylines/client/airnpe.py:58-61` | Cached bool used by both creation flows (`characterCreationContainer_new.py:741-742`, `characterCreationContainer.py:893-894`); config change rechecks and may skip (:71-74). | `AirNpeService.cs:24-32`; `LoginCompatibilityRpcHostWireTests.cs:50`, `TransportMetadataHostWireTests.cs:35` (PyBool). | MATCHES |
| RPC-00868 | air_npe.is_air_npe_enabled | `storylines/client/airnpe.py:58-61` | Same boolean consumer as RPC-00867. | `AirNpeService.cs:24-32`; `LoginCompatibilityRpcHostWireTests.cs:50`. | MATCHES |
| RPC-00869 | air_npe.skip_air_npe | `storylines/client/airnpe.py:101-111` | Ignores reply; state notification updates cache/UI (:56-64). | `AirNpeService.cs:44-52`; `LoginOnboardingSubscriptionRpcHostWireTests.cs:58-61` asserts None, state change and notification. | MATCHES |
| RPC-00899 | authentication.AccruedTime | `eve/client/script/ui/login/antiaddiction.py:211-212` | Zero arguments; `LoginHistory` maps returned session rows with `Session.PreLoad` (:117-127). | `LoginSupportRpcServices.cs:18-21`; `LoginCompatibilityRpcHostWireTests.cs:51`, `LoginOnboardingSubscriptionRpcHostWireTests.cs:63-64` assert PyList; `LoginSupportRpcTests.cs:37-64` checks provider value preservation. | MATCHES |
| RPC-00900 | authentication.AccruedTime | `antiaddiction.py:211-212` | Same session-row list contract as RPC-00899. | `LoginSupportRpcServices.cs:18-21`; same Host/provider tests as RPC-00899. | MATCHES || RPC-00901 | authentication.AmUnderage | `antiaddiction.py:206-208` | Sends login JWT; boolean controls optic anti-addiction branch at :17; helper memoized. | `LoginSupportRpcServices.cs:23-31`; `LoginCompatibilityRpcHostWireTests.cs:52`, `LoginOnboardingSubscriptionRpcHostWireTests.cs:65-66` assert PyBool and invalid-token error. | MATCHES |
| RPC-00902 | authentication.AmUnderage | `antiaddiction.py:206-208` | Same JWT and boolean consumer as RPC-00901. | `LoginSupportRpcServices.cs:23-31`; same Host tests. | MATCHES |
| RPC-00917 | beyonce.AdminGetBubbleID | `eve/client/script/remote/michelle.py:763-764` | Sole outside caller `menuSvcExtras/modelDebugFunctions.py:87-90` stringifies ID to clipboard. | `BeyonceService.cs:187-204`; `BeyonceRpcHostTests.cs:48-49` asserts numeric result. | MATCHES |
| RPC-00918 | beyonce.AdminGetBubbleID | `michelle.py:763-764` | Same debug clipboard consumer as RPC-00917. | `BeyonceService.cs:187-204`; `BeyonceRpcHostTests.cs:48-49`. | MATCHES |
| RPC-00919 | beyonce.BookmarkScanResult | `bookmarkLocationWindow.py:225`; remote fallback `bookmarkSvc.py:622-630` | Unpacks exact eight fields (IDs, xyz, location, expiry), caches/indexes, refreshes UI and emits `OnBookmarkCreated` (:633-638), consumed at `eventHandler.py:357`. | `BeyonceService.cs:112-146`; `ServiceAuditP0HostWireTests.cs:17-92` asserts tuple types/values and storage callback through registered Host. | MATCHES |
| RPC-00920 | beyonce.CmdStargateJump | `autopilot.py:358-360` | Passed to `PerformSessionChange`; no direct reply unpack; session flow at `carbon/common/script/sys/sessions.py:417-500`, then jump queue notice. | `BeyonceService.cs:633-640`; `StargateJumpWireTests.cs:23-89` asserts bound-object/FILETIME tuple and notice order; `SpaceServiceTests.cs:2490-2505` exercises transition. | MATCHES |
| RPC-00922 | beyonce.CmdWarpToStuffAutopilot | `autopilot.py:462-486` | Sends one destination ID after local `WarpDestination`; ignores reply and handles warp errors. | `BeyonceService.cs:621-630`; `StargateJumpWireTests.cs:48-57` asserts None and 10,000 range; `SpaceServiceTests.cs:1923-1951` covers success/refusal/binding. | MATCHES |
| RPC-00923 | beyonce.GetCynoJammerState | `sovDashboard/cynoJammerIcon.py:45-47` | PyLong FILETIME schedules refresh; None clears timer and schedules none (:49-59). | `BeyonceService.cs:210-220`; `BeyonceRpcHostTests.cs:53-55` asserts PyLong; `SpaceServiceTests.cs:52-78` covers FILETIME/None. | MATCHES |
| RPC-00924 | beyonce.GetCynoJammerState | `cynoJammerIcon.py:45-47` | Same nullable FILETIME consumer. | `BeyonceService.cs:210-220`; same Host/behavior tests. | MATCHES |
| RPC-00927 | beyonce.GetFormations | `eve/client/script/remote/michelle.py:324` | Passes value to native `Ballpark.LoadFormations`, held for ballpark session. | `BeyonceService.cs:223-225`; `BeyonceRpcHostTests.cs:59-68` asserts cache wrapper and decoded formation tuple names/shapes. | MATCHES |
| RPC-00928 | beyonce.GetFormations | `michelle.py:324` | Same native loader consumer. | `BeyonceService.cs:223-225`; `BeyonceRpcHostTests.cs:59-68`. | MATCHES |
| RPC-00996 | charMgr.AddContact | `addressbookService.py:732-733` | Agent path sends one ID and ignores reply. Personal path at :788-807 sends relation/watchlist/notification/message, updates cache and emits `OnContactChange`. | `CharMgrService.cs:926-937`; `CharacterManagerMutationHostWireTests.cs:25-32` asserts None and subsequent rowset through Host. | MATCHES |
| RPC-00997 | charMgr.AddContact | `addressbookService.py:732-733` | Same agent/personal-contact consumers. | `CharMgrService.cs:926-937`; same Host test. | MATCHES |
| RPC-01000 | charMgr.AddOwnerNote | `notepad.py:206-207` | Returned ID seeds note/folder cache; later edits use it (:208-214). | `CharMgrService.cs:964-985`; `CharacterManagerMutationHostWireTests.cs:52-64` asserts positive PyInt and reads note back. | MATCHES |
| RPC-01001 | charMgr.AddOwnerNote | `notepad.py:206-207` | Same returned note-ID consumer. | `CharMgrService.cs:964-985`; same Host test. | MATCHES |
| RPC-01002 | charMgr.AddOwnerNote | `notepad.py:622-623` | ID is cache key; note is shown and later edited (:624-633). | `CharMgrService.cs:964-985`; `CharacterManagerMutationHostWireTests.cs:52-64`. | MATCHES |
| RPC-01003 | charMgr.AddOwnerNote | `notepad.py:622-623` | Same note-ID/cache consumer. | `CharMgrService.cs:964-985`; same Host test. | MATCHES |
| RPC-01004 | charMgr.AddOwnerNote | `notepad.py:174-175` | Chunked settings retain returned IDs; reads GetOwnerNote and joins row `note` (:181-202). | `CharMgrService.cs:964-985`; `CharacterManagerMutationHostWireTests.cs:52-64`; persistence/read-back `CharacterContactNotepadHostTests.cs:50-63`. | MATCHES |
| RPC-01005 | charMgr.AddOwnerNote | `notepad.py:174-175` | Same chunked setting ID/note-row consumer. | `CharMgrService.cs:964-985`; same Host tests. | MATCHES |
| RPC-01006 | charMgr.BlockOwners | `addressbookService.py:657-658` | Sends owner list, ignores None; updates blocked cache, emits `OnBlockContacts`, refreshes (:659-663). | `CharMgrService.cs:992-1001`; `CharacterManagerMutationHostWireTests.cs:34-36` asserts None and reads 500 rows. | MATCHES || RPC-01007 | charMgr.BlockOwners | `addressbookService.py:657-658` | Same list and ignored-reply consumer. | `CharMgrService.cs:992-1001`; same Host test. | MATCHES |
| RPC-01008 | charMgr.DeleteCharacterSetting | `characterSettingsSvc.py:53-60` | Only called for existing key; ignores reply then removes local key. | `CharMgrService.cs:464-478`; `CharacterManagerMutationHostWireTests.cs:67-70` asserts None and reads later state. | MATCHES |
| RPC-01009 | charMgr.DeleteContacts | `addressbookService.py:817-820,841-855` | Agent/personal-contact delete; ignores reply, updates contact/online caches and emits contact-change events. | `CharMgrService.cs:1003-1012`; `CharacterManagerMutationHostWireTests.cs:47-50` asserts None and empty subsequent rows. | MATCHES |

### EveJS and capture cross-check

EveJS 261018.1 agrees on AIR state/boolean/skip (`services/npe/airNpeService.js:24-56`), accrued list/underage boolean (`services/login/authenticationService.js:234-248`), bookmark tuple (`services/ship/beyonceService.js:4374-4402`, `services/bookmark/bookmarkPayloads.js:148-164`), jump tuple/notification and warp None (`services/ship/beyonceService.js:4206-4224,4304-4338`), nullable jammer/formation data (`services/ship/beyonceService.js:2480-2500,2657-2659`), and charMgr mutations (`services/character/charMgrService.js:1024-1100,1180-1204,1229-1233`). The cited client consumer shapes agree with the Host implementations and tests.

The audit generator has no `status_evidence` support; the CSV remains unchanged. Actions run 40256449264 passed the selected registered-Host contract tests; run 40256464803 passed the selected movement wire/flow tests. No PostgreSQL database or graphical client was used.

# P0 client-side verification — batch 3 (RPC-01040–RPC-01068)

Client references below are from the V24.01 `home-user-eve-logic` tree. The rows with the same method and call site represent separate audit observations but share one client contract and one Host implementation.

## Wire contracts and whole-tree consumer scan

- `GetOwnerNote`: `eve/client/script/ui/shared/neocom/notepad.py:518-521` indexes the first returned record and reads `.note.strip()` and `.label[2:]`. The neighboring `GetNoteText` path at `:247-251` consumes the same shape. EveJS 261018.1 `server/src/services/character/charMgrService.js:1168-1179` returns a one-element list of KeyVal records; eve.rs returns a one-element list of KeyVal at `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:819-837`. The registered Host wire regression checks note fields in `dotnet/tests/Eve.Host.Tests/CharacterManagerMutationHostWireTests.cs:48-64`.
- `GetOwnerNoteLabels`: `notepad.py:87-105` iterates returned rows and reads integer `noteID` and string `label`; it groups `I:`, `N:`, and `S:` labels and removes malformed labels. EveJS 261018.1 `charMgrService.js:1154-1166` and eve.rs `CharMgrService.cs:798-817` both return a `Rowset` with `noteID,label` columns and `util.Row` rows.
- `GetPaperdollState`: `eve/client/script/ui/station/base.py:195-198` calls with no arguments, caches the scalar reply, and returns it. The character-selection accessor at `loginCharacterselection/charselData.py:170-171` reads a local `charDetails.paperdollState`; it makes no service call. EveJS 261018.1 `charMgrService.js:833-838` returns the scalar resolved state; eve.rs returns a `PyInt` at `CharMgrService.cs:327-336`.
- `GetPrivateInfo`: `corp_dlg_edit_member.py:148` stores the result in `self.memberinfo`; `:274-284` reads `characterID`, `gender`, `raceID`, and `createDateTime` as object attributes. Whole-file CBM search found these as the result consumers. EveJS 261018.1 `server/src/services/character/charMgrService.js:708-733` returns one `util.Row` (`header` and `line`). The prior eve.rs reply was a `Rowset`, which did not provide the direct row object the client reads; this batch changes it to `util.Row`.- `GetPrivateInfoOnCorpChange`: `personalDonateEMPanel.py:68-75` returns false only when the entire reply is None; otherwise reads `corporationDateTime`, subtracts it from wall clock, and checks the player-corporation predicate. EveJS 261018.1 `charMgrService.js:735-748` and eve.rs `CharMgrService.cs:847-867` return a KeyVal with corporation ID and date. Registered Host wire coverage is `CharMgrRpcHostWireTests.cs:38-52`.
- Whole-tree `GetPublicInfo` scan (excluding distinct `corpmgr.GetPublicInfo` callers): `avatardisplay/avatardisplay.py:1259-1261,1359-1361` reads bloodline; `ccSvc.py:179-183` reads school; `crimewatchSvc.py:179-187` reads corporation, war faction, and alliance; `scenePortraitQueue.py:125-127` stores the record and maps UserError to None; `scenePortraitRenderer.py:188-204` reads gender and bloodline; `operationDestinationController.py:12-14` reads school; `panelStandings.py:120-126` reads corporation and alliance; `preview.py:851-861` reads gender and passes the record into scene setup; `characterOverviewPanel.py:46-93,141` reads race, bloodline, ancestry, school, and creation time; `agents.py:281-285,519-522` reads school. Nodegraph, homestation, and safespace consumers at `safe_schools_client.py:12-20`, `getters/location.py:31-33`, `homestation/client/service.py:116-119`, and `safespace/client/util.py:8-20` treat a missing record or school as false/no station. EveJS 261018.1 `charMgrService.js:646-650` returns its public KeyVal; eve.rs does so at `CharMgrService.cs:179-183`. The registered Host test asserts the KeyVal fields, scalar/null values, multiple characters, and missing-character result at `CharMgrPublicInfoHostTests.cs:12-107`.

## Row verdicts

| row_id | service.method | client call site | consumers | handler | verdict |
|---|---|---|---|---|---|
| RPC-01040 | charMgr.GetOwnerNote | `notepad.py:518` | `ShowNote:518-521`, first record's `note` and `label` | `CharMgrService.cs:819-837`; `CharacterManagerMutationHostWireTests.cs:48-64` | MATCHES |
| RPC-01041 | charMgr.GetOwnerNote | `notepad.py:518` | `ShowNote:518-521`, first record's `note` and `label` | `CharMgrService.cs:819-837`; `CharacterManagerMutationHostWireTests.cs:48-64` | MATCHES |
| RPC-01042 | charMgr.GetOwnerNoteLabels | `notepad.py:87` | `LoadNotesData:87-105`, iterable rows with `noteID` and `label` | `CharMgrService.cs:798-817`; `CharMgrClientReadHostWireTests.cs:12-81` | NO-TEST (wire test added; first Actions build failed before tests on missing Eve.Characters import (fixed); rerun pending) |
| RPC-01043 | charMgr.GetOwnerNoteLabels | `notepad.py:87` | `LoadNotesData:87-105`, iterable rows with `noteID` and `label` | `CharMgrService.cs:798-817`; `CharMgrClientReadHostWireTests.cs:12-81` | NO-TEST (wire test added; first Actions build failed before tests on missing Eve.Characters import (fixed); rerun pending) |
| RPC-01044 | charMgr.GetPaperdollState | `station/base.py:197` | `GetPaperdollStateCache:195-198`, caches and returns scalar | `CharMgrService.cs:327-336`; `CharMgrClientReadHostWireTests.cs:12-81` | NO-TEST (wire test added; first Actions build failed before tests on missing Eve.Characters import (fixed); rerun pending) |
| RPC-01045 | charMgr.GetPaperdollState | `station/base.py:197` | `GetPaperdollStateCache:195-198`, caches and returns scalar | `CharMgrService.cs:327-336`; `CharMgrClientReadHostWireTests.cs:12-81` | NO-TEST (wire test added; first Actions build failed before tests on missing Eve.Characters import (fixed); rerun pending) |
| RPC-01046 | charMgr.GetPrivateInfo | `corp_dlg_edit_member.py:148` | `LoadChar:148`, then `OnTabGeneral:274-284` reads four attributes | `CharMgrService.cs:234-250`; `CharMgrClientReadHostWireTests.cs:12-81` | MISMATCH (Rowset corrected to util.Row; first Actions build failed before tests on missing Eve.Characters import (fixed); rerun pending) |
| RPC-01047 | charMgr.GetPrivateInfoOnCorpChange | `personalDonateEMPanel.py:69` | `_is_corp_membership_valid:69-75`, None guard then `corporationDateTime` | `CharMgrService.cs:847-867`; `CharMgrRpcHostWireTests.cs:38-52` | MATCHES |
| RPC-01048 | charMgr.GetPrivateInfoOnCorpChange | `personalDonateEMPanel.py:69` | `_is_corp_membership_valid:69-75`, None guard then `corporationDateTime` | `CharMgrService.cs:847-867`; `CharMgrRpcHostWireTests.cs:38-52` | MATCHES |
| RPC-01049 | charMgr.GetPublicInfo | `avatardisplay/avatardisplay.py:1360` | `GetBloodlineFromDNA:1360-1362` reads `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01050 | charMgr.GetPublicInfo | `avatardisplay/avatardisplay.py:1360` | `GetBloodlineFromDNA:1360-1362` reads `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01051 | charMgr.GetPublicInfo | `ccSvc.py:180` | `GetSchoolAndSchoolCorpInfoForChar:180-183` reads `schoolID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01052 | charMgr.GetPublicInfo | `ccSvc.py:180` | `GetSchoolAndSchoolCorpInfoForChar:180-183` reads `schoolID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES || RPC-01053 | charMgr.GetPublicInfo | `crimewatchSvc.py:180` | `GetSlimItemDataForCharID:180-187` reads corporation, war-faction, and alliance IDs | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01054 | charMgr.GetPublicInfo | `crimewatchSvc.py:180` | `GetSlimItemDataForCharID:180-187` reads corporation, war-faction, and alliance IDs | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01055 | charMgr.GetPublicInfo | `scenePortraitQueue.py:126` | `_FetchBatchData:125-127` stores KeyVal, maps UserError to None | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01056 | charMgr.GetPublicInfo | `scenePortraitQueue.py:126` | `_FetchBatchData:125-127` stores KeyVal, maps UserError to None | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01057 | charMgr.GetPublicInfo | `scenePortraitRenderer.py:189` | `Render:188-204` reads `gender` and `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01058 | charMgr.GetPublicInfo | `scenePortraitRenderer.py:189` | `Render:188-204` reads `gender` and `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01059 | charMgr.GetPublicInfo | `operationDestinationController.py:13` | `GetSchoolID:12-14` reads `schoolID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01060 | charMgr.GetPublicInfo | `panelStandings.py:121` | `GetToIDsForPlayerOwner:120-126` reads corporation and alliance IDs | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01061 | charMgr.GetPublicInfo | `panelStandings.py:121` | `GetToIDsForPlayerOwner:120-126` reads corporation and alliance IDs | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01062 | charMgr.GetPublicInfo | `avatardisplay/avatardisplay.py:1260` | `LoadScene:1259-1261` reads `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01063 | charMgr.GetPublicInfo | `avatardisplay/avatardisplay.py:1260` | `LoadScene:1259-1261` reads `bloodlineID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01064 | charMgr.GetPublicInfo | `preview.py:852` | `LoadScene:851-861` reads `gender` and passes the KeyVal into scene setup | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01065 | charMgr.GetPublicInfo | `preview.py:852` | `LoadScene:851-861` reads `gender` and passes the KeyVal into scene setup | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01066 | charMgr.GetPublicInfo | `characterOverviewPanel.py:47` | `ApplyAttributes:47,90-93,141` reads race, bloodline, ancestry, school, and creation time | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01067 | charMgr.GetPublicInfo | `agents.py:282` | `_GetMySchoolAgents:281-285` reads `schoolID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |
| RPC-01068 | charMgr.GetPublicInfo | `agents.py:282` | `_GetMySchoolAgents:281-285` reads `schoolID` | `CharMgrService.cs:179-183`; `CharMgrPublicInfoHostTests.cs:12-107` | MATCHES |

The `run_service_audit_current.py` entry point exposes no per-row evidence input; it delegates to the audit generator and applies only fixed in-script compatibility overrides. The generated CSV was left unchanged, and the queue files were not regenerated.

## Verification state

- The initial local focused `dotnet test` compiled `Eve.Services.CharacterInfo`, then stalled during `Eve.Host` compilation and was cancelled; no test executed. A subsequent no-restore Host test-project build was cancelled during dependency compilation on this shared machine.
- Actions run 40256730678 failed before tests: the new Host test omitted `using Eve.Characters`, producing CS0246 for `Character`. That import is fixed in the source. Push CI run 40256722236 failed on the same compiler error.
- Focused GitHub Actions rerun is pending. No PostgreSQL database or graphical client was used.

- Local focused command: `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter FullyQualifiedName~Char_mgr_client_read_shapes_match_the_registered_host_wire_contract`. The build compiled `Eve.Services.CharacterInfo`, then stalled while compiling `Eve.Host`; it was cancelled after no output for about two minutes. No test executed.
- No PostgreSQL database or graphical client was used.
- Focused GitHub Actions verification is pending.


# Client verification — P0 implemented rows, batch 4 (RPC-01069–RPC-01099)

Whole-tree client searches confirmed the consumers below. Client paths are relative to `/home/user/eve-logic`. EveJS comparison uses `/home/user/evejs-261018.1/src/server/src/services/character/charMgrService.js:645-698,851-854` and `charMgrGlobalAssets.js:544-584`. The owner capture report `/home/user/reports/2026-10-24-core-breakage-from-owner-logs.md` names none of these methods in its failure findings.

| row_id | service.method | client call site | consumers and contract | handler / Host wire evidence | verdict |
|---|---|---|---|---|---|
| RPC-01069 | charMgr.GetPublicInfo | `agents.py:520` | Public KeyVal; whole-tree callers additionally read schoolID (safe-school/safespace return false for absent/None), corporationID, allianceID, factionID, bloodlineID, gender, security and portrait fields. | `CharMgrService.cs:132-145`; `CharMgrPublicInfoHostTests.cs:44-98` | MATCHES |
| RPC-01070 | charMgr.GetPublicInfo | `agents.py:520` | Same KeyVal and field/null contract; duplicate audit observation. | same | MATCHES |
| RPC-01071 | charMgr.GetPublicInfo | `nodegraph/client/conditions/safe_schools_client.py:13` | Reads schoolID; absent reply/field or None -> false (`:12-22`). | same | MATCHES |
| RPC-01072 | charMgr.GetPublicInfo | `safe_schools_client.py:13` | Same schoolID and None handling. | same | MATCHES |
| RPC-01073 | charMgr.GetPublicInfo | `nodegraph/client/getters/location.py:32` | Public record used for character location derivation. | same | MATCHES |
| RPC-01074 | charMgr.GetPublicInfo | `getters/location.py:32` | Same location consumer. | same | MATCHES |
| RPC-01075 | charMgr.GetPublicInfo | `safespace/client/util.py:9` | Reads schoolID; absent reply/field or None -> false (`:8-18`). | same | MATCHES |
| RPC-01076 | charMgr.GetPublicInfo | `safespace/client/util.py:9` | Same schoolID and None handling. | same | MATCHES |
| RPC-01077 | charMgr.GetPublicInfo3 | `eve/client/script/ui/shared/dockedUI/inControlCont.py:41` | Reads first character record and named fields. | `CharMgrService.cs:148-155`; `CharMgrPublicInfoHostTests.cs:91-98` | MATCHES |
| RPC-01078 | charMgr.GetPublicInfo3 | `eve/client/script/ui/shared/info/characterInfoWindow.py:64` | Character window consumes first record; another path reads bloodlineID at :193-198. | same | MATCHES |
| RPC-01079 | charMgr.GetPublicInfo3 | `eve/client/script/ui/shared/info/infoContentBase.py:410` | Info content consumes first record and fields. | same | MATCHES |
| RPC-01080 | charMgr.GetPublicInfo3 | `nodegraph/client/getters/player.py:24` | Reads info[0].corporationID; falsey reply -> no value (:23-27). | same | MATCHES |
| RPC-01081 | charMgr.GetPublicInfo3 | `getters/player.py:24` | Same corporationID contract. | same | MATCHES |
| RPC-01082 | charMgr.GetPublicInfo3 | `eve/client/script/ui/shared/info/characterInfoWindow.py:194` | Reads info[0].bloodlineID to form description (:193-198). | same | MATCHES |
| RPC-01083 | charMgr.GetPublicInfo3 | `characterInfoWindow.py:194` | Same bloodlineID contract. | same | MATCHES |
| RPC-01084 | charMgr.GetRecentShipKillsAndLosses | `eve/client/script/ui/shared/info/infosvc.py:2648` | Iterates rows and filters finalCharacterID == session.charid (:2646-2648). | `CharMgrService.cs:1198-1221`; service paging test `CharacterInfoTests.cs:528-568`; Host test at `CharMgrRpcHostWireTests.cs:50` covers only unbound None. | NO-TEST |
| RPC-01085 | charMgr.GetRecentShipKillsAndLosses | `infosvc.py:2648` | Same finalCharacterID consumer. | same | NO-TEST |
| RPC-01086 | charMgr.GetRecentShipKillsAndLosses | `infosvc.py:2652` | Iterates rows and filters victimCharacterID == session.charid (:2650-2652). | same | NO-TEST |
| RPC-01087 | charMgr.GetRecentShipKillsAndLosses | `infosvc.py:2652` | Same victimCharacterID consumer. | same | NO-TEST |
| RPC-01088 | charMgr.GetSettingsInfo | `carbonui/services/settingsSvc.py:148` | Unpacks (code, verified), verifies, unmarshals and executes code, then logs nonempty stats (:147-158). | `CharMgrService.cs:344-345`; Host `CharMgrRpcHostWireTests.cs:41-43`; dispatcher `CharMgrSettingsWireTests.cs:10-30`. | MATCHES |
| RPC-01089 | charMgr.GetSettingsInfo | `settingsSvc.py:148` | Same tuple/Buffer/int and follow-up order. | same | MATCHES |
| RPC-01091 | charMgr.List | `eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:312` | Iterates inventory records with named item fields. | `CharMgrService.cs:313-314`; registered Host test `CharMgrClientItemListHostTests.cs:10-48`. | MATCHES |
| RPC-01092 | charMgr.List | `eve/client/script/environment/fittingSvc.py:583` | Iterates named inventory records. | same | MATCHES |
| RPC-01093 | charMgr.List | `eve/client/script/ui/station/assembleModularShip.py:234` | Iterates named inventory records. | same | MATCHES |
| RPC-01095 | charMgr.ListIncludingContainers | `nodegraph/client/getters/inventory.py:217` | Applies predicate to item record fields (:216-222); assets window also wraps rows (:414-416). | `CharMgrService.cs:316-317`; `CharMgrClientItemListHostTests.cs:10-48`. | MATCHES |
| RPC-01096 | charMgr.ListStationItems | `eve/client/script/ui/shared/assetsWindow.py:745` | Iterates station inventory item records; other consumers are market quote and safety. | `CharMgrService.cs:310-311`; `CharMgrClientItemListHostTests.cs:10-48`. | MATCHES |
| RPC-01097 | charMgr.ListStationItems | `eve/client/script/ui/shared/market/quote.py:377` | Filters candidate item records (:376-381). | same | MATCHES |
| RPC-01098 | charMgr.ListStationItems | `eve/client/script/ui/shared/assets/assetSafetySvc.py:36` | Reads itemID to exclude current ship (:35-36). | same | MATCHES |
| RPC-01099 | charMgr.ListStations | `eve/client/script/ui/shared/assetsWindow.py:441` | Indexes by stationID; reads stationID, solarSystemID and typeID. Other callers: assetsWindow.py:922, assetsinfo.py:148-153, createContractWnd.py:1571-1575, quote.py:364-369. | `CharMgrService.cs:307-308`; Host CRowset assertion `CharMgrRpcHostWireTests.cs:52-57`. | MATCHES |

EveJS returns GetPublicInfo as KeyVal, GetPublicInfo3 as a one-row CRowset, killmail rows as a list, settings as [Buffer, 0], and global assets as packed rows/station CRowset. eve.rs GetPublicInfo3 returns one PyList record; the listed client consumers index row zero and read named attributes, verified through the Host test. The original global-list Host fixture used tuple entries; this batch adds a Host route assertion for named itemID/typeID/quantity fields. Recent-kill paging and fields are verified in the service test, but valid-session Host transport remains without a focused assertion. No graphical client was used. The audit generator has no status_evidence input, so EVE_RS_COVERAGE.csv remains unchanged.


## Batch 4 verification update (2026-10-26)

- Focused registered-Host check `Char_mgr_item_list_routes_return_item_records_with_client_accessed_fields` passed in Actions run 40258181572 for commit 655f60ce; no failed tests or jobs. The run's headless-client step was skipped because `--live ''` was requested; this is not graphical-client verification.
- The preceding Actions run 40257537139 found test compile errors (Host call ID type and missing Eve.Characters import). Both were corrected in 655f60ce; the focused rerun passed.
- The valid-session Host wire shape for `GetRecentShipKillsAndLosses` remains without a focused assertion; service paging/row behavior is covered by the database-backed service test. No database or graphical EVE client was used locally.
- Push-triggered ci.yml run 40258172650 for 655f60ce was in progress at this update.

# Client verification: P0 implemented rows, batch 8

Checked the V24.01 client sources and traced the consumers in the full `/home/user/eve-logic` tree. The same handler applies to duplicated audit rows for a given method; each row is listed separately below.

The 2026-10-24 owner log analysis reports `search.Query` as `ServiceNotFound` in that capture. The current host registers `search`; this does not establish that the captured build had the current route. The report contains no breakage finding for the other methods in this batch.

EveJS comparison: search returns a group-ID keyed dictionary of lists for `Query` and a flat ID list for `QuickQuery` (`/home/user/evejs-261018.1/src/server/src/services/_other/searchService.js:463-500)); station returns a cached KeyVal station record, list-of-four guest rows, tuple bits, and KeyVal station rows (`.../services/station/stationService.js:68-190)); subscription returns clone grade 0/1 and FILETIME long or null (`.../services/subscription/subscriptionMgrService.js:67-81)); population-cap load slots are system-ID-to-token mappings used by the following move call (`.../services/populationCap/populationCapService.js:178-190`). These match the client contracts below. No intentional EveJS difference was identified.

| row_id | service.method | client call site | consumers in eve-logic | handler | verdict |
|---|---|---|---|---|---|
| RPC-02258 | populationCap.GetCharacterLoadSlot | `characterSelection.py:750` | `TryEnterGame:750-757` uses a dict: `len`, `keys`, and system-ID lookup for the slot token; one-system answers proceed directly to entry. | `PopulationCapService.cs:55-75`; host wire shape test `PopulationCapHostWireTests.cs:9-31` | MATCHES |
| RPC-02259 | populationCap.MoveCharacterToNewSystem | `characterSelection.py:758` | `TryEnterGame:751-761` sends the selected system ID and its token, then enters the character; reply is not read. | `PopulationCapService.cs:78-83`; no successful Host transition assertion | NO-TEST |
| RPC-02354 | search.Query | `searchUtil.py:278` | `searchUtil.py:277-281` treats the reply as a mapping from integer result type to lists of integer IDs, primes owner/type caches and sorts selected result lists; callers include search panels, contracts, maps, kill rights and bounty search. | `SearchQueryService.cs:14-42`; `SearchQueryHostWireTests.cs:10-34` | MATCHES |
| RPC-02355 | search.QuickQuery | `searchUtil.py:301` | `GetResultsList` returns the reply to UI callers as a flat list of integer IDs; caller families include contracts, maps, petitions, kill rights, bounties and item lookup. | `SearchQueryService.cs:45-77`; `SearchQuickQueryHostWireTests.cs:12-49` | MATCHES |
| RPC-02724 | station.GetGuests | `station/base.py:102-108` | Each returned row is unpacked as `(charID, corpID, allianceID, warFactionID)`; client caches `charID -> (corpID, allianceID, warFactionID)`. Docked guest UI reads that map at `dockedUI/guests.py:94-95`. | `StationService.cs:103-121`; `StationStructureRpcHostWireTests.cs:15-45` | MATCHES |
| RPC-02725 | station.GetGuests | `station/base.py:102-108` | Same RPC and consumer as RPC-02724; the response is a list of four-field guest tuples. | `StationService.cs:103-121`; `StationStructureRpcHostWireTests.cs:15-45` | MATCHES |
| RPC-02726 | stationSvc.GetAllianceSystems | `mapViewData.py:99-106` | Iterates row objects, reads integer `solarSystemID` and `allianceID`, then builds a system-to-alliance dictionary; faction-warfare occupation data overwrites that map afterwards. | `StationService.cs:150-158`; `StationStructureRpcHostWireTests.cs:47-51` | MATCHES |
| RPC-02727 | stationSvc.GetAllianceSystems | `mapViewData.py:99-106` | Same map consumer as RPC-02726. | `StationService.cs:150-158`; `StationStructureRpcHostWireTests.cs:47-51` | MATCHES |
| RPC-02728 | stationSvc.GetAllianceSystems | `starMapSvc.py:1607-1614` | Iterates row objects and reads integer `solarSystemID` and `allianceID`; later faction-warfare occupation values overwrite the alliance map. | `StationService.cs:150-158`; `StationStructureRpcHostWireTests.cs:47-51` | MATCHES |
| RPC-02729 | stationSvc.GetAllianceSystems | `starMapSvc.py:1607-1614` | Same star-map consumer as RPC-02728. | `StationService.cs:150-158`; `StationStructureRpcHostWireTests.cs:47-51` | MATCHES |
| RPC-02732 | stationSvc.GetStation | `notificationUtil.py:67-70` | Reads `stationTypeID` to create a show-info link. Other whole-tree callers include `mapSvc.py:528-531`, map/info UI and location helpers; map caller reads `stationName` when a local identity overrides it. | `StationService.cs:39-67`; `StationStructureRpcHostWireTests.cs:72-81` | MATCHES |
| RPC-02733 | stationSvc.GetStation | `notificationUtil.py:67-70` | Same notification utility caller and consumers as RPC-02732. | `StationService.cs:39-67`; `StationStructureRpcHostWireTests.cs:72-81` | MATCHES |
| RPC-02734 | stationSvc.GetStation | `mapSvc.py:528-532` | Reads the returned cached station object; for local IDs it replaces `stationName` from the location cache. Downstream map, information, market and docking UI read station fields. | `StationService.cs:39-67`; `StationStructureRpcHostWireTests.cs:72-81` | MATCHES |
| RPC-02735 | stationSvc.GetStation | `mapSvc.py:528-532` | Same map caller and consumers as RPC-02734. | `StationService.cs:39-67`; `StationStructureRpcHostWireTests.cs:72-81` | MATCHES |
| RPC-02736 | stationSvc.GetStationItemBits | `station/base.py:574-576` | Passes the four-value tuple to `SetStationItemBits`, which creates a Row with fields `ownerID, itemID, operationID, stationTypeID` (`station/base.py:581-584`). | `StationService.cs:96-100`; `StationStructureRpcHostWireTests.cs:58-60` | MATCHES |
| RPC-02737 | stationSvc.GetStationItemBits | `station/base.py:574-576` | Same tuple-to-Row consumer as RPC-02736. | `StationService.cs:96-100`; `StationStructureRpcHostWireTests.cs:58-60` | MATCHES |
| RPC-02738 | stationSvc.GetStationsForOwner | `lpStoreSvc.py:534-535` | Iterates station KeyVals and groups by `solarSystemID`, appending each `stationID` to find the nearest dockable LP station (`lpStoreSvc.py:536-559`). | `StationService.cs:70-93`; `StationStructureRpcHostWireTests.cs:62-67` | MATCHES |
| RPC-02739 | stationSvc.GetStationsForOwner | `lpStoreSvc.py:534-535` | Same LP-store consumer as RPC-02738. | `StationService.cs:70-93`; `StationStructureRpcHostWireTests.cs:62-67` | MATCHES |
| RPC-02740 | stationSvc.GetSystemsForAlliance | `all_ui_systems.py:31` | Receives the requested alliance's row list and uses system IDs in the alliance systems UI; the call passes `session.allianceid`. | `StationService.cs:160-168`; `StationStructureRpcHostWireTests.cs:52-56` | MATCHES |
| RPC-02741 | stationSvc.GetSystemsForAlliance | `all_ui_systems.py:31` | Same alliance systems UI consumer as RPC-02740. | `StationService.cs:160-168`; `StationStructureRpcHostWireTests.cs:52-56` | MATCHES |
| RPC-02845 | subscriptionMgr.GetCloneGrade | `omega/client/clone_grade_svc.py:125` | Caches an integer clone grade and compares it to Alpha/Omega constants (`clone_grade_svc.py:136-147`); other Omega checks use the same cache. | `SubscriptionMgrService.cs:169-170`; `LoginCompatibilityRpcHostWireTests.cs:58-60` | MATCHES |
| RPC-02846 | subscriptionMgr.GetCloneGrade | `omega/client/clone_grade_svc.py:125` | Same integer 0/1 consumer as RPC-02845. | `SubscriptionMgrService.cs:169-170`; `LoginCompatibilityRpcHostWireTests.cs:58-60` | MATCHES |
| RPC-02847 | subscriptionMgr.GetSubscriptionTime | `plexPanel.py:145-146` | Panel treats a truthy FILETIME as an Omega expiry and formats it; `None` selects the Alpha display branch (`plexPanel.py:147-154`). | `SubscriptionMgrService.cs:164-167`; `LoginCompatibilityRpcHostWireTests.cs:59-60` | MATCHES |
| RPC-02848 | subscriptionMgr.GetSubscriptionTime | `plexPanel.py:145-146` | Same FILETIME-long-or-None consumer as RPC-02847. | `SubscriptionMgrService.cs:164-167`; `LoginCompatibilityRpcHostWireTests.cs:59-60` | MATCHES |

## Scope and evidence

- The Host wire tests use the production service handlers and network dispatch. They assert client-used wire types and fields: station cache wrapper/KeyVal, tuple and list rows, station owner fields, alliance rows, integer clone grade, and FILETIME long.
- The new Query test sends the client's group-ID list and `exact` keyword with the Macho `machoVersion` transport keyword, then checks the dictionary and list value shapes.
- The new population test exercises the registered service through Host dispatch for an unauthenticated session and verifies the mapping wire type. A successful character move needs owned character persistence and is not exercised by this database-free batch; RPC-02259 remains NO-TEST.
- The audit-current generator has no status-evidence update option; the CSV was left unchanged as instructed.
- Graph/source comparison checked current handlers against EveJS v0.12.9-compatible behavior. The full graphical client was not launched for this batch.
