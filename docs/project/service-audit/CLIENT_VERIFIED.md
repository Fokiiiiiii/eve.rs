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


# Client verification — P0 implemented rows, batch 5

Whole-tree V24.01 caller searches show all consumers summarized below. The owner capture analysis (2026-10-24-core-breakage-from-owner-logs.md, findings 1–8) does not name any of these methods; that is not evidence that calls succeeded. EveJS 261018.1 uses the corresponding return intents at `charMgrService.js:626-628, 1104-10, 1112-21, 1123-40, 1142-52, 1199-1205, 1214-28`; creation/deletion at `charService.js:1089-1373,1869-79` and `characterDeletionRuntime.js:685-704,706-789`; Omega status at `charService.js:1526-29`.

| row_id | service.method | client call site | consumers | handler | verdict |
|---|---|---|---|---|---|
| RPC-01100 | charMgr.ListStations | `assetsWindow.py:922` | iterates station rows | `CharMgrService.cs:307-320`; registered Host rowset assertion `CharMgrRpcHostWireTests.cs:41-48` | MATCHES |
| RPC-01101 | charMgr.ListStations | `quote.py:364` | iterates station rows for market selector | `CharMgrService.cs:307-320`; `CharMgrRpcHostWireTests.cs:41-48` | MATCHES |
| RPC-01102 | charMgr.ListStations | `assetsinfo.py:148` | iterates station rows | `CharMgrService.cs:307-320`; `CharMgrRpcHostWireTests.cs:41-48` | MATCHES |
| RPC-01103 | charMgr.ListStations | `createContractWnd.py:1571` | iterates station rows | `CharMgrService.cs:307-320`; `CharMgrRpcHostWireTests.cs:41-48` | MATCHES |
| RPC-01104 | charMgr.ListStations | `corp_ui_accounts.py:752` | indexes rows by `stationID` | `CharMgrService.cs:307-320`; `CharMgrRpcHostWireTests.cs:41-48` | MATCHES |
| RPC-01105 | charMgr.LogSettings | `settingsSvc.py:153` | only called for nonempty stats; reply ignored | `CharMgrService.cs:1083-1099`; Host asserts `PyNone`, `CharMgrRpcHostWireTests.cs:67` | MATCHES |
| RPC-01106 | charMgr.LogSettings | `settingsSvc.py:153` | same call; reply ignored | `CharMgrService.cs:1083-1099`; `CharMgrRpcHostWireTests.cs:67` | MATCHES || RPC-01108 | charMgr.RemoveOwnerNote | `notepad.py:107` | parallel cleanup of invalid note IDs; reply ignored | `CharMgrService.cs:1102-1131`; Host asserts `PyNone`, `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01109 | charMgr.RemoveOwnerNote | `notepad.py:179` | parallel cleanup of stale note IDs; reply ignored | `CharMgrService.cs:1102-1131`; `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01110 | charMgr.RemoveOwnerNote | `notepad.py:703` | deletes note and reloads notepad; reply ignored | `CharMgrService.cs:1102-1131`; `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01111 | charMgr.RemoveOwnerNote | `notepad.py:703` | same delete flow; reply ignored | `CharMgrService.cs:1102-1131`; `CharMgrRpcHostWireTests.cs:68` | MATCHES |
| RPC-01112 | charMgr.SaveCharacterSetting | `characterSettingsSvc.py:48` | writes key/value then updates local cache; empty values use Delete; client caps values at 100 KB | `CharMgrService.cs:437-462`; registered Host persistence test `CharacterManagerMutationHostWireTests.cs:9-80` asserts `PyNone` and round-trips state | MATCHES |
| RPC-01113 | charMgr.SetActivityStatus | `gameui.py:338` | active status with FILETIME extraInfo; reply ignored | `CharMgrService.cs:1143-1157` | NO-TEST |
| RPC-01114 | charMgr.SetActivityStatus | `gameui.py:338` | same active transition | `CharMgrService.cs:1143-1157` | NO-TEST |
| RPC-01115 | charMgr.SetActivityStatus | `gameui.py:333` | AFK status with FILETIME extraInfo; reply ignored | `CharMgrService.cs:1143-1157` | NO-TEST |
| RPC-01116 | charMgr.SetActivityStatus | `gameui.py:333` | same AFK transition | `CharMgrService.cs:1143-1157` | NO-TEST |
| RPC-01117 | charMgr.SetCharacterDescription | `bioPanel.py:60` | autosaves text; on exception reloads it; reply ignored | `CharMgrService.cs:203-229` | NO-TEST |
| RPC-01118 | charMgr.SetCharacterDescription | `bioPanel.py:60` | same exception recovery | `CharMgrService.cs:203-229` | NO-TEST |
| RPC-01119 | charMgr.SetNote | `notepad.py:540` | asynchronous item note save; reply ignored | `CharMgrService.cs:1159-1185` | NO-TEST |
| RPC-01120 | charMgr.SetNote | `panelNotes.py:60` | saves changed text or empty string; reply ignored | `CharMgrService.cs:1159-1185` | NO-TEST |
| RPC-01121 | charMgr.SetNote | `panelNotes.py:60` | same note editor flow | `CharMgrService.cs:1159-1185` | NO-TEST |
| RPC-01122 | charMgr.UnblockOwners | `addressbookService.py:673` | local blocked cache update and `OnUnblockContacts` precede refresh; reply ignored | `CharMgrService.cs:1187-1195` | NO-TEST |
| RPC-01123 | charMgr.UnblockOwners | `addressbookService.py:673` | same unblock flow | `CharMgrService.cs:1187-1195` | NO-TEST |
| RPC-01125 | charUnboundMgr.CancelCharacterDeletePrepare | `characterSelection.py:870` | cancels deletion then refreshes slot; reply ignored | `CharUnboundMgrService.cs:449-462` | NO-TEST |
| RPC-01126 | charUnboundMgr.CancelCharacterDeletePrepare | `characterSelection.py:870` | same cancel and refresh | `CharUnboundMgrService.cs:449-462` | NO-TEST |
| RPC-01127 | charUnboundMgr.CreateCharacterWithDoll | `ccSvc.py:96` | reply ID clears selection cache; both creation UIs use ID for portrait insertion (`characterCreationContainer_new.py:1300-1304`, `characterCreationContainer.py:1839-1848`) | `CharUnboundMgrService.cs:261-305` | NO-TEST |
| RPC-01128 | charUnboundMgr.CreateCharacterWithDoll | `ccSvc.py:96` | same reply ID consumers | `CharUnboundMgrService.cs:261-305` | NO-TEST || RPC-01129 | charUnboundMgr.DeleteCharacter | `characterSelection.py:847` | expects falsey success; truthy error is displayed, otherwise selection refreshes (`characterSelection.py:851-859`) | `CharUnboundMgrService.cs:502-526` | NO-TEST |
| RPC-01130 | charUnboundMgr.GetCharOmegaDowngradeStatus | `characterSelection.py:300` | bool controls lapsed-subscription warning creation; errors are caught | `CharUnboundMgrService.cs:157-168`; registered Host bool test `CharUnboundOmegaHostTests.cs:9-46` | MATCHES |

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
