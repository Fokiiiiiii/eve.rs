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
| RPC-01106 | charMgr.LogSettings | `settingsSvc.py:154` | same call; reply ignored | `CharMgrService.cs:1084-1100`; `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01108 | charMgr.RemoveOwnerNote | `notepad.py:108` | parallel cleanup of invalid note IDs; reply ignored | `CharMgrService.cs:1103-1132`; Host asserts `PyNone`, `CharMgrRpcHostWireTests.cs:69` | MATCHES |
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
| RPC-01128 | charUnboundMgr.CreateCharacterWithDoll | `ccSvc.py:97` | same reply ID consumers | `CharUnboundMgrService.cs:262-306` | NO-TEST |
| RPC-01129 | charUnboundMgr.DeleteCharacter | `characterSelection.py:848` | expects falsey success; truthy error is displayed, otherwise selection refreshes (`characterSelection.py:852-860`) | `CharUnboundMgrService.cs:503-527` | NO-TEST |
| RPC-01130 | charUnboundMgr.GetCharOmegaDowngradeStatus | `characterSelection.py:301` | bool controls lapsed-subscription warning creation; errors are caught | `CharUnboundMgrService.cs:158-169`; registered Host bool test `CharUnboundOmegaHostTests.cs:10-47` | MATCHES |

## Batch 5 remaining work

The inspected registered-Host tests verify ListStations, LogSettings, RemoveOwnerNote, SaveCharacterSetting, and GetCharOmegaDowngradeStatus. Seven distinct methods remain NO-TEST: SetActivityStatus, SetCharacterDescription, SetNote, UnblockOwners, CancelCharacterDeletePrepare, CreateCharacterWithDoll, and DeleteCharacter. Existing service-level or persistence tests do not replace their missing client-shaped Host assertions. `run_service_audit_current.py --help` exposes only client/repository/queue roots, and its current status override has no batch-5 evidence entries; EVE_RS_COVERAGE.csv was left unchanged. No database, graphical client, or focused test was used for this verification.
