# Factional Warfare audit follow-up

確認対象は `/home/user/reports/90-audit-eve-factionwarfare-r14.md` の6件。V24.01 client source、現在のHost経路、PostgreSQL上の状態、EveJS v0.12.9を照合した。偽所見はなかった。5件を修正し、captureの1件は不足している配置根拠を特定したため未修正である。

## 修正した所見

### 未所属corporation statusのwire shape

確認: V24.01 `eve/client/script/ui/shared/factionalWarfare/enlistmentFlow/enlistCont.py:95-105` と `factionwarfare/client/enlistmentChecks.py:25-47` は、応答から `.status` と `.factionID` を直接読む。EveJS `server/src/services/faction/facWarMgrService.js:221-234` も3つのキーを持つKeyValを返す。従来のeve.rsは未所属時に`PyNone`だった。

修正: 未所属時も`status`, `factionID`, `startDate`を`None`にした`util.KeyVal`を返す。

検証: `Faction_warfare_reads_round_trip_from_persisted_state_through_host_registration`は修正前に`PyObject`期待対`PyNone`実値で失敗し、修正後にHost登録、RPC decode、全3属性を確認して通過した。

### Alliance enlistmentのcorporation向けread投影

確認: V24.01 `enlistmentUtil.py:110-124,142-170` はcorporation status readの後、sessionのallianceを見て退役/申請取消を選ぶ。Hostの既存実効faction解決にもalliance fallbackがあった一方、facWarMgr readはcorporation enlistmentだけを読んでいた。EveJSの同名readはdirect corporation stateのみで、alliance経路の動作根拠にはならない。

修正: direct corporation stateを優先し、なければ現在のalliance status/factionを使う。faction corporation/member集計は、当該factionに現在所属するallianceのcorporationを既存Affiliations providerから展開し、別factionへdirect enlistされたcorporationは重複所属させない。

検証: Host往復テストは修正前にalliance所属corporationが`GetFactionCorporations`から欠落して失敗した。修正後、Host RPCでcorp faction ID、active status、start date、一覧とFactionInfoのmember入力を確認した。

### 同一RPC再送によるLP donation二重加算

確認: V24.01 `eve/client/script/ui/services/facWarSvc.py:456-470` は2引数のdonation RPCを呼ぶ。EveJSにはこのdonation transactionの比較可能な実装がない。Hostの`ServiceCall`にはRPC `CallId`があるため、時刻ではなくsession/callを操作識別子にできる。

修正: Host session IDとRPC CallIdからoperation IDを作る。migration `0007_fw_donation_operations.sql`でoperation receipt、支払LP、pool-afterを記録し、wallet debit、pool更新、receiptを同一transactionに置いた。同一operationの再送は元のreceiptを返す。異なるCallIdなら同じmillisecondでも別donationとして処理する。

検証: Host testは修正前、CallId 2の同一再送でwallet残高は900のままpoolだけ17から34へ増えることを確認した。修正後、再送はwallet 900/pool 17/receipt 1行のまま、同じmillisecondのCallId 3はwallet 800/pool 34/receipt合計2行となった。

### Complexとbattlefield VPのcorporation/alliance統計欠落

確認: V24.01 `eve/client/script/ui/services/facWarSvc.py:353-381` はcorporation/alliance統計を読む。complexとbattlefield tickerはpilot daily rowだけを更新していた。

修正: complex pilot snapshotにtick時点のcorporation IDを含め、complex解決transactionでpilotとcorporationの日次VPを更新する。battlefield participant tableにcorporation IDを保持し、勝利transactionで同じpilot shareをcorporationにも記録する（migration `0008_fw_battlefield_pilot_corporations.sql`）。Host compositionはcharacters storeからcorporationと直接/企業/alliance factionを構成する。

検証: `Faction_warfare_reads_round_trip_from_persisted_state_through_host_registration`はDB tickerでcomplex 50 VPとbattlefield 2,000 VPを作成後、登録済みHostの`GetStats_Corp`と`GetStats_Alliance` wire結果に両方が反映されることを確認した。battlefield participantのcorp IDはtickのupsertで保持され、未知ID 0では既知値を消さない。

### Pending joinの上書きと初回insert race

確認: V24.01 `enlistCont.py:120-132` はpending join中に取消導線を示す。旧JoinAsyncは`FactionId == 0`のみを確認していたため、pending joinを別factionで置換し、同じowner rowがまだない並行要求も直列化できなかった。

修正: pending joinへの同faction retryは変更時刻/effective timeを保存し直さずAccepted、異なるfactionはAlreadyEnlisted。新規ownerは`INSERT ... ON CONFLICT DO NOTHING`で先にrowを作り、後続の`SELECT ... FOR UPDATE`でkeyを直列化する。

検証: Host RPCテストは修正前、同faction retryでstartDateが30秒進むことで失敗した。修正後はstartDateが保持され、別faction要求は`AlreadyInMilitia` user errorとなり、DB rowも変わらない。別のPostgreSQL concurrency testは修正前に2件ともAcceptedとなる状態をrelation-table lockで再現し、修正後はAccepted/AlreadyEnlistedが各1件となることを確認した。

## 部分実装後も残る所見

### Vulnerable system captureとControl Bunker

確認: V24.01 `eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60` は`GetControlBunkerIDInSystem`のIDからballparkのinventory itemとballを読むだけで、placement規則を示さない。`fwVictoryPointSvc.py:50-69` はVP state更新とbunker vulnerable/non-vulnerable通知を消費する。EveJS 261108.1のFaction Warfare service passでは同等のplayer-kill / Control Bunker lifecycle producerを見つけなかった。

実装: migration `0009_fw_control_bunkers.sql`がreserved Control Bunker IDと次回downtime capture stateを保持する。登録済みHostの`FactionWarfareControlBunkers`はgroup-925 structure hull depletionを durable event/jobへ結び、vulnerable stateでだけ次回downtimeの captureを予約する。due jobは同一transactionでoccupier、attacker points、LP poolを更新し、commit後にVP/bunker通知を送る。V24のreserved ID範囲とピン留めSDEのgroup/type/faction/dogma healthを使う。scene-entry spawnとcapture後の再spawnは、ピン留め`mapStars`のSun半径からV24 `GetSunWarpInPoint`を計算し、EveJS Sovereignty HubのY offsetを適用する（実装推定。正確なFW座標規則とは断定しない）。`GetControlBunkerIDInSystem`はsim内にlive group-925 ballがある場合だけ返す。

残るgap: 公開一次資料とV24 callsiteはFW Hubのexact座標を示さず、EveJSにもFW Hub lifecycle producerがない。このためSun warp-in anchorは根拠を明記した実装推定であり、live V24配置との一致は未確認。Host PostgreSQL lifecycle testは本番と同じSDE-derived anchor、type/health、hull depletion、next-downtime capture、state reset、scene replacementを確認する。headless FW capture walkは到着後のAgency RPCがlive bunker IDを返すことを確認する。graphical Agency presentationと船キルLP payoutは引き続き未確認。

## 検証範囲と未確認事項

- このjobの再検証は専用DB`eve_p3`で実施。Release focused builds/testsの最新結果は[`space-world-gaps-implementation.md`](space-world-gaps-implementation.md)を参照。
- 各修正では先に追加した回帰testを旧実装でfailさせ、その後同じassertionを修正後passさせた。別途Join初回owner rowの並行testも、row作成を一時的に外すとAcceptedが2件になり、復旧後はAcceptedとAlreadyEnlistedが各1件になった。
- graphical V24.01 clientは起動していない。Host wire testとticker testはreal graphical-client verificationではない。
- donation retryは同じCallIdの連続再送を検証した。プロセス再起動後に同じCallIdを再送するclient scenarioは実際のclient traceがなく未確認。
- migration後に継続中の旧battlefield参加者はcorporation IDが0で移行される。移行前の参加時corporationを復元する証拠がないため、その既存participantへの遡及creditは未確認。
- 検証はfocusedであり、full .NET suite、live headless suite、graphical clientは実行していない。GitHub Actionsでは最新mainのCI run 1617が`tests/test_structures.py`の`OnDockingFinished` timeoutでredだったため、このbranchのCI結果は未確認でpushしていない。
- 当初から存在するSpace movement/safe-logoff関連の作業tree変更はこの作業に含めていない。
