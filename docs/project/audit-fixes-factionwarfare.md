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

## 確認済みだが未修正の所見

### Vulnerable system captureとControl Bunker

確認: `FwService.CaptureAsync`は存在するが通常Host/space eventから呼ばれず、`FacWarMgrService.GetControlBunkerIDInSystem`は`None`を返す。V24.01 `eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60` はそのIDをballparkのinventory itemとballから引く。V24.01 `fwVictoryPointSvc.py:50-67` と`fwWarzoneSvc.py:78-95` にはVP/vulnerability/occupancy更新のclient通知callsiteがある。EveJS v0.12.9には同等のoccupancy/capture runtimeがない。

未修正理由: ピン留めSDEにはControl Bunkerのtype/faction/health情報はあるが、FW systemごとのlive bunker item IDとspace positionがない。V24.01 client callsiteもIDを使うだけでplacement規則を示さない。位置とballを推測して配置すると、正規の攻撃対象/Agency表示ではない偽objectを作ることになるため、Host破壊event・capture transaction・通知を接続する根拠が足りない。これは偽所見として除外したのではなく、confirmed P1 gapのまま残した。実クライアントtraceか権威ある配置データが必要。

この状態ではcaptureを完了扱いにしていない。`GetControlBunkerIDInSystem`、bunker spawn/damage、capture commit後のoccupation/VP notification sequence、downtime transitionは未実装・未検証である。

## 検証範囲と未確認事項

- 専用`eve_p4`を使い、`Eve.FactionWarfare`, `Eve.Services.Core`, `Eve.Host`, `Eve.FactionWarfare.Tests`, `Eve.Host.Tests`をReleaseでbuildした。すべて0 warning / 0 error。
- Release focused tests: `FacWarMgrRpcTests`, `FwDatabaseTests`, `FwComplexTickerTests`, `FwBattlefieldTickerTests`, `FwOwnerWindowTests`は30 passed / 0 failed / 0 skipped。HostのFW read/donation、登録済みFacWarMgr、affiliation writer、warzone、facility/fighter関連10 testsは10 passed / 0 failed / 0 skipped。すべて`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`を設定した。
- 各修正では先に追加した回帰testを旧実装でfailさせ、その後同じassertionを修正後passさせた。別途Join初回owner rowの並行testも、row作成を一時的に外すとAcceptedが2件になり、復旧後はAcceptedとAlreadyEnlistedが各1件になった。
- graphical V24.01 clientは起動していない。Host wire testとticker testはreal graphical-client verificationではない。
- donation retryは同じCallIdの連続再送を検証した。プロセス再起動後に同じCallIdを再送するclient scenarioは実際のclient traceがなく未確認。
- migration後に継続中の旧battlefield参加者はcorporation IDが0で移行される。移行前の参加時corporationを復元する証拠がないため、その既存participantへの遡及creditは未確認。
- 検証はfocusedであり、full .NET suite、live headless suite、graphical clientは実行していない。GitHub Actionsでは最新mainのCI run 1617が`tests/test_structures.py`の`OnDockingFinished` timeoutでredだったため、このbranchのCI結果は未確認でpushしていない。
- 当初から存在するSpace movement/safe-logoff関連の作業tree変更はこの作業に含めていない。
