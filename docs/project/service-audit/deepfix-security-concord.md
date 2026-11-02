# Security / CONCORD / sentry correctness handoff

更新日: 2026-11-02。対象はV24.01/build 3396210のCrimewatch、security status、CONCORD応答、sentry status-loss通知。

## V24.01クライアント契約

- `eve/client/script/ui/services/crimewatchSvc.py:94-114` はchar/location変更時に `GetClientStates()` を呼び、5種のタイマー、engagement、criminal/suspect一覧を復元する。`:116-123` はsystem/ship変更時に同じ4要素タプルの最終値をsafety levelとして読む。`:195-210,252-255` がflag一覧をUI状態へ反映する。
- Security statusは `crimewatchSvc.py:590-600` で `None` を未取得キャッシュ値としてmonikerへ読み取り、更新通知でキャッシュを更新する。`clientDogmaLocation.py:747-750` は現char以外には0.0を返す。`securityOfficeWindow.py:146-148` が同通知でtag barを更新する。
- `behaviors/monitors/combat.py:368-382` はsystem itemをsubjectに `MESSAGE_SEC_STATUS_LOST_EVENT` を購読する。`:385-406` はownerID/shipIDを受け、ship存在・同一bubble・maxDistanceを確認してからtargetにする。Client側cleanupは`:378-382` で異なるevent名を解除する実装だが、この作業ではRust側monitorが自身の購読eventを解除するよう修正した。
- Safety拒否は `crimewatchSvc.py:507-518` の `CheckUnsafe` / `SafetyActivated` 経由でUI eventになる。本変更はこれらのerror/UI契約を変えていない。

## 実装経路と修正

- `CombatState.Flagged` (`dotnet/src/Eve.Crimewatch/CombatState.cs:88-94`) は、active criminalへsuspect offenseが続いてもcriminal flagを保ち、期限を更新する。回帰assertionは `dotnet/tests/Eve.Crimewatch.Tests/AggressionTests.cs:96-111`。
- `CrimewatchRuntime.RecordAggressionAsync` (`dotnet/src/Eve.Services.Crimewatch/CrimewatchRuntime.cs:195-255`) がauthoritative storeを更新し、criminal consequence・handlers・client state同期へ進む。High-sec responseの初回scheduleは `:362-374`、重複抑止と最小5msのdelayは `:510-521`、dispatch後の解放は `:523-548`。sec-status帯ごとのdelayは `:550-559`。
- 再起動時は `CrimewatchRuntime.cs:639-657,450-470` が永続criminal timerを読み、現在systemのsecurityに応じCONCORD responseを再予約する。既に期限を過ぎた応答は最小5msでdispatchする。`RuntimeTests.cs:654-712` の2ケースは、20秒経過後に10秒応答を復旧する期待値を5msへ直した。`RuntimeTests.cs:332-358` は完了済み履歴timerでなく、唯一のpending timerをassertする。
- Hostは `dotnet/src/Eve.Host/Composition/SpaceComposition.cs:429-434` で `ConcordResponseDue` を `RespondToCriminalAsync` に接続する。system entryは `SpaceSessions.Modules.cs:518-530` でsystem securityをruntimeへ渡す。実際の船選択・spawn・15秒後のbattleshipによるship lossは `SpaceSessions.Npc.cs:665-746`。
- CONCORD compositionはEVEU記述に合わせて2 police cruiserの後にbattleshipを1隻投入する。type 1904/1912のプロフィール解決とspawn/lossの回帰は `dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2550-2621`。EveJS 261028.1は `crimewatchState.js:1282-1291` で5/6/7隻を選ぶため、意図的差分を `docs/reference/compat/DEVIATIONS.md:3-4` に記録した。
- Security penaltyは `SecurityPenalty.cs:31-36` の現status、target status、system pseudo-security、ship/capsule modifierを使う。`SecurityStatusPenalties.cs:50-113` はcriminal/suspect attackに限定し、status/historyを保存してからsim更新と通知を行う。`:121-132` はshipの `OnModuleAttributeChanges` の後に `OnSecurityStatusUpdate` を送る。`SpaceSessions.cs:1045-1053` は保存statusをsimへ `ShipChanged` として渡す。
- Rustの `Scene.respec` (`crates/eve-space/src/scene/session.rs:606-727`) はプレイヤーのstatusが実際に下がった時だけ(ownerID, shipID)を一回queueし、新slim itemも通知する。`brains/world.rs:164-174` はqueueをsystem item subjectへ `[ownerID, shipID]` として一度だけbroadcastする。`eve-behaviors/src/monitors/combat.rs:474-512` はeventを購読・解除し、clientと同じbubble/range条件でtarget化する。回帰は `scene/tests.rs:1408-1424`, `world_tests.rs:181-196`, `monitors/combat.rs:688-724`。

## 参照比較

- EVE University [CONCORD](https://wiki.eveuniversity.org/CONCORD) はdelayがsecurityに応じること、2 cruiserからbattleshipへ増援することを記載。eve.rsのdelay帯はEveJS `crimewatchState.js:1251-1269` と一致する。
- EVE University [Security status](https://wiki.eveuniversity.org/Security_status) の計算と `SecurityPenalty.Next` を照合し、係数式の差は見つからなかった。EveJS側の計算は `crimewatchState.js:2315-2496`。
- EveJSのcriminal aggression経路は `crimewatchState.js:3880-4204`、restoreは `:4491-4559`、tick時の呼び出しは `:4561-4708`。restoreでは期限切れresponseを現在時刻へ丸めるため、eve.rsの5ms再開と整合する。
- EveJSには標準Empire gate/station sentry visual entityを作る `defaultEmpireScenery.js:272-314,357-399` とstationary NPC sentry policy (`npcSentryPolicy.js:75-124`) がある。Rust側ではstatus-loss eventのdeliveryとlistener lifecycleを検証したが、通常のlow-sec gate/station配置からgraphical client combatまでの一連動作はこの作業では確認していない。Low-sec mechanicsの基準はEVE University [Safety Tips for Operating in Low Sec](https://wiki.eveuniversity.org/Safety_Tips_for_Operating_in_Low_Sec)。

## ローカル検証

- `Eve.Crimewatch.Tests` の `AggressionTests`: 23 passed、0 skipped。
- `Eve.Services.Crimewatch.Tests` の `RuntimeTests`: 修正前は3件失敗。2件は過去期限のresponseを10,005ms待つ誤期待、1件は完了済みtimerも数えるassertionだった。修正後は33 passed、0 skipped。
- `Eve.Services.Space.Tests` のSpaceService/SpaceEvents/PlayerDeath/AbyssalSpaceSessions選定: 206 passed、0 skipped。
- `Eve.Host.Tests` のCrimewatch wire + AbyssalHostFlow選定: 24 passed、2 skipped。AbyssalのDB test guardが `eve_p2`〜`eve_p4` だけ許可していたため `AbyssalHostFlowTests.cs:935` に `eve_p5` を追加し、同DBで再実行した。スキップは実sim依存の2件でpassに数えていない。
- 全DBテストは自分の `eve_p5` と `tools/dev/db-lock.sh` 経由。C#のCrimewatch/Services.Crimewatch/Services.Space/Host test projectsはbuild成功、0 warning/error。
- Rustでは `cargo +1.94.1 test -p eve-behaviors a_target_that_cloaks_clears_the_value_and_a_ship_that_lost_security_status_in_my_bubble_is_a_target`、`cargo +1.94.1 test -p eve-space security_status` (2 passed)、`cargo +1.94.1 check -p eve-space -p eve-behaviors`、`cargo +1.94.1 fmt --all -- --check`、`cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` が成功。Cargo targetは `target-cx`。
- EveJSの該当restore/CONCORDに対する専用test fileはCBM検索で見つからず、EveJSの実行テストは行っていない。V24.01 graphical clientでも未確認。

## 次の担当への状態

- Sentryのstatus-loss通知契約とRust monitor lifecycleはunit/headless test済み。通常low-sec gate/station sentry flowのend-to-end確認は残るため、この領域全体を完了扱いにしないこと。
- Push前に `python3 tools/dev/ci-status.py status` でmain最終CIがgreenであることを確認し、merge後に上記の選定DBテストを再実行する。