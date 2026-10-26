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
| RPC-01018 | charMgr.EditOwnerNote | eve-logic `notepad.py:173` | result ignored; preference text assembled from returned notes | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
| RPC-01019 | charMgr.EditOwnerNote | eve-logic `notepad.py:173` | result ignored; preference text assembled from returned notes | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:632-673` | NO-TEST |
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
| RPC-01038 | charMgr.GetOwnerNote | eve-logic `notepad.py:247` | first row note/label fields read at lines 242-249 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:438-455` | NO-TEST |
| RPC-01039 | charMgr.GetOwnerNote | eve-logic `notepad.py:247` | first row note/label fields read at lines 242-249 | `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs:438-455` | NO-TEST |

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
