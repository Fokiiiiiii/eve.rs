# コアプレイヤーパス再確認

確認対象: `origin/main` の `c73e7c1655ab`。ローカル PostgreSQL と実 GUI クライアントは使用していない。ライブ確認は GitHub Actions のサーバースタック＋headless client で実施。

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
