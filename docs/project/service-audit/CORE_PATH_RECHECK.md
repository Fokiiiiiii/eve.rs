# Core service path re-check

## 2026-10-26 checkpoint

This is a continuation record. The cross-service audit is not closed; only the findings below have been traced far enough to classify.

### Market: five M1 handlers confirmed present

- Client calls: V24.01 `marketsvc.py:253-295` routes through `marketProxy`. `BuyMulti` returns the `ordersCreated` sequence and immediately reads each row's `typeID` (`marketsvc.py:270-273`); `SellMulti` and `SellPlex` return their proxy values, while cancel/modify calls consume no result (`marketsvc.py:275-295`). Transaction rows are read by client code in `marketsvc.py:41-64`.
- Host handlers: `MarketProxyService.cs:560-645` implements `BuyMultipleItems`, `PlacePlexSellOrder`, `ModifyPlexCharOrder`, `CharGetTransactions`, and `CorpGetTransactions`. The Host method table and production registration checks are in `MarketHostTests.cs:28-34` and `MarketRpcHostWireTests.cs:90-105`; other market Host wire cases are in `MarketTradeHostWireTests.cs`.
- Reference: EveJS 261018.1 `server/src/services/market/marketProxyService.js` implements the corresponding proxy operations; its transaction entry builder is compared against the V24 fields consumed at `marketsvc.py:41-64`.
- Reconciliation: `docs/project/TODO.md:1084` is historical M1 completion context, not a current missing-handler claim. The methods are present and Host-registered. M1 tests and the listed live market walk cover earlier work; this re-check has not replayed all five through the current production Host and compared each persisted transition to EveJS.

### Mail: six client operations do not reach a handler

- Client calls in the whole V24.01 mail service include `mailMgr.EmptyTrash` (`mailSvc.py:458-476`), `DeleteMail` (`mailSvc.py:478-491`), `CreateLabel` (`mailSvc.py:727-737`), `EditLabel` (`mailSvc.py:711-725`), `DeleteLabel` (`mailSvc.py:739-754`), and `AssignLabels` (`mailSvc.py:756-772`). These methods update the client header/body caches; `CreateLabel` also consumes the returned label ID. Mailbox synchronization consumes `newMail`, `oldMail`, and `mailStatus`, including each status row's `messageID`, `statusMask`, and `labelMask` (`mailSvc.py:156-234`).
- Current Host surface: `MailService.HasMethod` in `dotnet/src/Eve.Services.Core/MailService.cs:44-50` omits all six names. `CallAsync` therefore does not route these calls to persistence. `MailboxMutation.Apply` in the same file handles status and label-mask changes only; it does not remove trashed/deleted statuses, add labels, or persist label definitions.
- Reference: EveJS 261018.1 `mailMgrService.js:429-518` routes these operations to `mailState.js` mutations (including returned label IDs and deletion/update notifications). This gap needs service callbacks, persisted mutations and Host wire assertions. It is not closed by the current `MailRpcHostWireTests.cs`, which exercises the currently supported mutations only.
- The chat-poke path is different: V24 calls it from `xmppchatsvc.py:1794-1795`; Host composition supplies `mailSendWriter.SendGmChatPokeAsync` in `MailRpc.cs:144-153`; EveJS composes the same titled mail in `mailState.js:2159-2176`.

### Other paths: inventory evidence gathered; full per-call review remains open

- Mailing lists: client RPC calls are in `mailingListsSvc.py:42-191`. Consumers use `GetInfo().displayName`, `Join().id`, the member map keys, and welcome-mail return values (`mailingListsSvc.py:55-108,141-193`). Host wire tests exist in `MailRpcHostWireTests.cs` and `MailingListRpcWireTests.cs`; the full call/result matrix still needs comparison to EveJS.
- Fleet: the V24 service entry points and client transitions are in `fleetSvc.py:254-383`, with fleet-proxy and settings paths later in that file. Host wire coverage is split across `FleetObjectRpcWireTests.cs`, `FleetRows01559To01610HostWireTests.cs`, `FleetDamageSubscriptionHostWireTests.cs`, and `PersistenceFleetSettingsTests.cs`. This inventory does not yet certify every caller, consumed field, or EveJS transition.
- Chat and LSC: client `XmppChatMgr` calls are in `xmppchatsvc.py:283,311,349,996-1007,1082,1123-1134,1197-1204,1257,1484,1817-1818`; the channel window also calls `GrantChannelOwnership` in `xmppchannelsettings.py:124`. `XmppChatMgrServiceTests.cs` covers hostname, channel names, private channels and resync. XMPP room discovery and client-side member/message consumption are in `xmppchatsvc.py:461-630,909-945,1013-1172`. The separate LSC channel path and all XMPP consumer fields have not yet been reconciled against Host and EveJS.
- Contacts and notes: V24 loads personal, corporation and alliance contacts from the `GetContactList` result's `addresses` and `blocked` rows, consuming `contactID`, `inWatchlist`, `relationshipID`, `labelMask`, and `senderID` (`addressbookService.py:193-250`); notepad bootstrap consumes `noteID` and `label` (`notepad.py:86-140`). Host persistence/read-model coverage exists in `HostServerTests.cs:435-649`. A full call-by-call notification and restart comparison remains open.

## 2026-10-26: skills, clones, character sheet, corporation and alliance basics

### Method and scope

I traced the V24.01 callsites and downstream field reads in the whole `/home/user/eve-logic` tree, using the current `docs/project/service-audit/CLIENT_CALL_MATRIX.csv` and `EVE_RS_COVERAGE.csv` as indexes. The client evidence below identifies the calls and the data the caller uses. Host and EveJS comparisons are based on current source in this worktree and `/home/user/evejs-261018.1`.

This is a source audit, not a graphical-client run. This turn made no handler or test changes. Existing Host test source is reported as source coverage only; this turn did not execute those tests.

### Skills and training queue

- `eve/client/script/ui/services/skillsvc.py:130-142` binds `skillMgr2.GetMySkillHandler()`, then consumes `GetSkills()` and `GetAllSkills()` as maps keyed by skill type ID. The skill UI reads levels and SP from those records through `skillsvc.py:214-230,282-342,713-734`.
- `skillsvc.py:220-229` obtains `GetAttributes()` as an attribute-ID to value map, iterates every pair, and applies each value to the local dogma state. `skillsvc.py:360-364` caches the `GetSkillHistory(maxresults)` reply. `skillsvc.py:379-385` consumes `GetSkillChangesForISIS()` as `(typeID, pointChange)` pairs.
- `eve/client/script/ui/services/skillQueueSvc.py:105-127` unpacks `GetSkillQueueAndFreePoints()` into the queue and free-SP count. Positive free SP updates the skill service; queue entries are subsequently read through `trainingTypeID`, `trainingToLevel`, and `trainingEndTime` at `skillQueueSvc.py:124-135`. Queue edits are sent via `CharStartTrainingSkill` (`skillsvc.py:772`) and `AbortTraining` (`skillsvc.py:796`); the mutation replies are ignored.
- Other core reads/mutations and their response use are indexed at `CLIENT_CALL_MATRIX.csv:2321-2342`: injector/extractor methods, free SP, implant/booster lists, training attributes, queue, and the skill handler binder. In particular `GetImplants` and `GetBoosters` are cached by `skillsvc.py:962-968`; `GetDiminishedSpFromInjectors` is returned directly by `skillsvc.py:1000-1002`; the alpha-injection time is returned directly at `skillsvc.py:1004-1005`.
- Host coverage lists the P1 `skillHandler` methods from AbortTraining through SplitSkillInjector as `IMPLEMENTED-TESTED` against `SkillHandlerRpcHostTests.Skill_rpcs_accept_transport_macho_version_and_keep_the_client_wire_shapes_on_the_host`; `skillMgr2.GetMySkillHandler` is `IMPLEMENTED-TESTED` against `SkillMgr2HostWireTests.Skill_mgr2_returns_its_bound_skill_handler_moniker_over_the_host_route` (`EVE_RS_COVERAGE.csv:1663-1686`). The lower-priority development notification read and skill-purchase RPC have active routes but no matching Host wire-test source in this inventory.
- EveJS binds the same object in `server/src/services/skills/skillMgr2Service.js:10-21`; its queue and skill-map builders are in `skillMgrService.js:289-303,351-354,429-432`. The observed map/tuple/list consumption above agrees with the Host method families and their registered wire-test coverage. Alpha-injector scheduling uses persisted use state in EveJS `server/src/services/_other/alphaInjectorMgrService.js:15-20`; eve.rs tracks the corresponding state in `Eve.Services.Skills/AlphaInjectorMgrService.cs`, and source tests cover first-use, next downtime, and restart (`SkillServiceTests.cs:692-712`). The older fixed-value adapter is not the active method implementation.

### Character sheet and clones / implants

- `eve/client/script/ui/shared/neocom/charactersheet.py:48-54` reads `charMgr.GetHomeStationRow()` and consumes `stationID`; rank notifications are passed to the faction-warfare service at `:60-71`. Skills and attributes are obtained by the skill service described above.
- `eve/client/script/ui/services/clonejumpsvc.py:73-84` binds `jumpCloneSvc` at the current solar system or station, calls `GetCloneState()`, and consumes the KeyVal fields `clones`, `implants`, and `timeLastJump`. Ship and station clone lists are separately cached from `GetShipCloneState()` and `GetStationCloneState()` at `:87-96`. The UI also uses the integer result of `GetNumClonesInPilotsStructure()` (`:99`) and consumes validation error-label strings or (label, args) pairs from `ValidateInstallJumpClone()` (`:385-399`). Jumping is a session change through `CloneJump` (`:225-248`).
- EveJS builds the clone-state KeyVal in `server/src/services/station/jumpCloneRuntime.js:405-415` and serves it in `jumpCloneService.js:42-45`; the jump transition is in `:96-105`. The Host exposes persisted clone reads and mutations through `GameRpcServices.cs` and `HostJumpCloneStore`. Current Host source coverage marks the client-used clone count and validation methods as tested, citing `JumpCloneReadHostTests.Jump_clone_reads_return_count_and_install_validation_labels_through_host` (`EVE_RS_COVERAGE.csv:984-986`). The broader persisted clone jump/implant swap lifecycle is covered by existing Host and live test sources recorded in the character-phase plan, but was not run in this audit.

### Corporation members, titles and roles

- `eve/client/script/ui/services/corporation/bco_members.py:26-32` pages the result of `corp.GetMembersPaged(page)`; `:37-42` indexes `GetMembersByIds` rows by `characterID`; `:143-157` uses `GetMember` and preserves the returned member row. These are iterable member rows, not a scalar result.
- `eve/client/script/ui/services/corporation/bco_titles.py:52-86` consumes `GetTitles()` as a mapping keyed by `titleID`; each row uses `titleName` and the role/grantable-role masks (general, HQ, base and other location masks). Its write calls forward that exact mask set via `UpdateTitle`, `UpdateTitles`, and `DeleteTitle`. The members editor also forwards the same per-location masks and title ID through `UpdateMember` (`bco_members.py:243-270`).
- Host handlers are in `Eve.Services.Corporations/CorporationServices.cs` (`CorpRegistryService.GetTitles`: 953-955; `UpdateMember`: 1332-1343); persistent role/title state is in `Eve.Corporations/CorporationStore.Roles.cs`. EveJS implements matching methods in `server/src/services/corporation/corpRegistryRuntime.js:2669-2675,2929-2939`. Existing focused service tests assert title and role-mask behavior in `CorporationTests.Directors_and_titles_have_the_references_effective_and_grantable_masks` and protocol tests assert native packed member/title/login/bind shapes (`CorporationProtocolTests.Public_rows_packed_members_titles_login_and_bind_are_native_client_shapes`). This turn did not find or run a registered-Host wire test for these title/member replies, so Host source registration and service-level assertions must not be read as wire-test execution.

### Alliance basics

- `eve/client/script/ui/services/alliances/all_cso_alliance.py:27-31` calls `GetRankedAlliances(maxLen)` and indexes each reply row by `allianceID`; `:49-58` calls `GetAlliance(allianceID)` and indexes the returned rowset by `allianceID`.
- `eve/client/script/ui/shared/info/infosvc.py:2894-2904` consumes `GetAllianceMembers(itemID)` as rows with `corporationID`, then primes the owner cache from those IDs. `corpDonateEMPanel.py:41-52` consumes `GetDaysInAlliance(allianceID, corporationID)` as a number and `GetAllianceMembersOlderThan(...)` as corporation IDs.
- Host handlers construct alliance headers/rows and member rows in `Eve.Services.Corporations/AllianceRegistryService.cs:27-58,104-133,177-185`. EveJS implements ranking and alliance member reads in `server/src/services/corporation/allianceRegistryRuntime.js:537-551`. The current service-audit inventory finds active Host routes for these P1 calls but no matching Host wire-test source (`EVE_RS_COVERAGE.csv:208-227`). No input-rejection defect was demonstrated in this source pass; the missing wire-test evidence remains a follow-up.

### Findings and next checks

No missing or hollow P1 skill-handler route, no mismatch in the client-consumed skill or clone shape listed above, and no confirmed over-rejection was found in the reviewed source paths. The alliance basic routes are present but lack focused Host wire-test evidence in the current inventory. Corporation title/member service tests verify the described shapes and persistence at service level; a Host-dispatch test was not located during this pass. The untested lower-priority skill notification/purchase paths and the real graphical client remain outside the verification performed here.

No source code changed and no tests were run. No PostgreSQL database was used.

## Core player path recheck — 2026-10-26

確認対象: 現在の `main` は `2401d299`。ソース確認 baseline は `c73e7c1655ab`（以後の関連 merge は監査範囲の server 実装を変更していない）。ローカル PostgreSQL と実 GUI クライアントは使用していない。ライブ確認は GitHub Actions のサーバースタック＋headless client で実施。

## パスごとの結果

| ステップ | V24.01 クライアント根拠 | eve.rs 根拠 | 結果 |
|---|---|---|---|
| ログイン、キャラクター選択、ステーション／ハンガー | `carbon/common/script/sys/sessions.py:418-500` の `PerformSessionChange` と `eve/client/script/parklife/gameui.py:430-472,604-637` が `charid`、`stationid`、`structureid`、`solarsystemid`、`shipid` の変更を処理する。 | `tools/headless-client/tests/test_login_to_character.py` がログインから選択までを確認。ジャンプwalkは `test_jump.py:106-132` から同じ状態で続く。 | fast live CI 成功。実 GUI の station panel 表示までは確認していない。 |
| アンドック | `test_jump.py` の呼び出し契約は station ship bind 後の `Undock`。クライアントの session 更新消費点は `gameui.py:430-472,604-637`。 | `test_jump.py:125-137` が bind、RPC、通知を確認。 | fast live CI のE2E内で成功。 |
| ゲートへのワープ | `eve/client/script/stargate/client/gateJumpSvc.py:98-127` が stargate ID を使って warp を開始。Michelle の destiny update 消費は `remote/michelle.py:482-512,1098-1104`。 | `test_jump.py:139-188` が warp updates、dropout、停止、ゲート到着時の ballpark state を確認。 | slow jump walk は jump RPC まで到達。 |
| Stargate jump と次システム到着 | `gateJumpSvc.py:118-124` は `CmdStargateJump(stargateID, beaconID, session.shipid)` の戻り値を読まない。`gameui.py:430-472` と `sceneManager.py:211-218,527-530,638-657` は session location/system を使う。 | `BeyonceService.cs:633-645` は `JumpAsync` の戻り値を捨て `None` を返す。`SpaceSessions.Movement.cs:314-360` は warp 状態を外し、queue 更新を送り、非同期到着を始める。 | session change、destination bind、state 取得まで進む。IN_WARP 拒否は再現していない。slow test のみ古い fixture の戻り値 assertion で失敗: fixture tuple を期待し実際は `None`。この assertion 修正は未実施。 |
| 到着後のドック | client の docking 呼び出し形は `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:503-518` は `sessionMgr.PerformSessionChange('dock', bp.CmdDock, itemID, session.shipid)` を呼ぶ。独立 walk の証拠は `tools/headless-client/tests/test_undock.py:244-263`。 | `test_jump.py` は destination の `UpdateStateRequest` の後に終了する。 | jump 後 destination station への warp／dock を一連で確認する E2E assertion がない。 |

## RPC と client 消費点

- Jump reply: `gateJumpSvc.py:118-124` は reply を保持しない。EveJS 261018.1 `server/src/services/ship/beyonceService.js:4305-4346` は実 client trace の reply が `None` と記録。eve.rs `BeyonceService.cs:633-645` も `None`。Actions `40352531935` の失敗は server jump ではなく、古い fixture と assertion の不一致。
- Station UI: `eve/client/script/ui/station/base.py:98-108` は `station.GetGuests()` の行を `charID, corpID, allianceID, warFactionID` と読む。`base.py:572-584` は `GetStationItemBits()` を `ownerID,itemID,operationID,stationTypeID` の順で Row 化。この GUI 初期化は headless jump walk の対象外。
- Dogma / ship: `station/base.py:611-614` が `dogmaIM.CreateNewbieShip(shipID, locationID)` を呼ぶ。`environment/invCache.py:1267-1282` と `environment/godma.py:338-345` は `GetCapacityForItem(itemID, attributeID)` の数値または `None` を扱い、`None` では SDE 属性へ fallback。船の bind は `test_jump.py:128-130`。
- Inventory: `environment/invCache.py:428-431` が `invbroker.GetItemDescriptor()` を item header として cache する。header全フィールドと全 inventory bind burst は今回の walk では検査していない。
- Beyonce / Michelle: `remote/michelle.py:322-324` は `beyonce.GetFormations()` の返値を `LoadFormations` に渡す。`DoDestinyUpdate` は `remote/michelle.py:1098-1104` で受け、`FlushState` が処理する。空 action list は `remote/michelle.py:482-512` で無視。
- Search: `eve/client/script/ui/util/searchUtil.py:275-280` は `search.Query` を group ID ごとの item ID list として使う。`SearchQueryService.cs:4-42` を `CoreRpc.cs:103-105` が登録。
- Event log: `eve/client/script/sys/clientStatsSvc.py:310-313,892` は `LogClientStats` と `LogPlayerRequestedDisconnect` を呼ぶ。`EventLogRpcService.cs:12-52` に実装。
- XMPP: `eve/client/script/ui/shared/neocom/help.py:123-126` は `GetHelpChannel` を使う。`XmppChatMgrService.cs:10-11` の `Hostname` は設定 domain。
- `machoVersion`: `carbon/common/script/net/ServiceCallGPCS.py:221-225` で client 呼び出し層が kwargs に付加する。
- View/map UI: `viewStateSvc.py:242-362,487-502,635-658` はローカル view/overlay を操作する。`mapViewPanel.py:69-72` は close 後 `mapView` を `None` にする。これらの箇所に RPC reply payload の直接消費はない。headless run では実 UI の children 例外は確認できない。
- SessionRpcBudget: `PacketDispatcher.cs:178-206` が高コスト RPC の予算を station、inventory、dogma、contract、corporation 等に分ける。`test_command_burst_live.py` run `40354399443` は成功。NPC spawn walk `test_npc.py` run `40354804793` も成功し、NO_SCENE は再現しなかった。
- ServiceNotFound と ARGUMENTS_INVALID は今回のプレイヤーパスの headless runs では発生しなかった。real UI の viewState/map例外と station/dogma/inventory response全項目は未検査。

## 実行記録

- GitHub Actions `CI` / “End to end (server stack + headless client)” run `40352549613`: 成功。fast live test 群。
- GitHub Actions `Live (slow)` / `test_jump.py` run `40352531935`: failed。warp、jump後 session、destination bind/state まで進み、旧戻り値 assertion のみ失敗。
- GitHub Actions `Live (slow)` / `test_command_burst_live.py` run `40354399443`: 成功。SessionRpcBudget burst check。
- GitHub Actions `Live (slow)` / `test_npc.py` run `40354804793`: 成功。NPC spawn check。
- `test_jump.py` の到着後 dock assertion は未追加。到着後の station location/source を決める client-compatible fixture がなく、live でその遷移を確認していない。
