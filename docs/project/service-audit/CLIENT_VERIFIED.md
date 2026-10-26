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
