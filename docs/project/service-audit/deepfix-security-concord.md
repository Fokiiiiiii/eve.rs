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
- Push gate: CI #1449 for SHA `f7c0e9e14ccd` completed green. SHA `95bd9c920736` exposed CS1501 at the Host build: `SpaceComposition.cs:396` passes five arguments to `NpcFactionStanding.ApplyAsync`, whose committed overload accepted four. That failure was reproduced locally after temporarily stashing and restoring only the pending implementation and test. Signed CI-fix commit `550a4ab4b5d7` adds the fifth argument and persisted reason field; local Release builds and its focused DB test passed. It was pushed to main. Selected test #1264 (`41570243071`) and push-triggered CI #1451 (`41570233121`) both completed successfully with no failed tests or jobs.

## マージ後のローカル確認

- Rust: `cargo +1.94.1 fmt --all -- --check`、`cargo +1.94.1 clippy --workspace --all-targets -- -D warnings`、`cargo +1.94.1 check -p eve-space -p eve-behaviors`、security-status 2件、Pochven sentry 1件、combat filter 69件を確認。
- .NET build: `Eve.Crimewatch.Tests`、`Eve.Services.Crimewatch.Tests`、`Eve.Services.Space.Tests`、`Eve.Host.Tests` は全て警告・エラーなし。
- eve_p5 の DB ロック経由で `AggressionTests` 23件、`RuntimeTests` 33件、Space 関連4クラス207件、Host 関連テスト24件が成功。
- Host の Abyssal 実 sim cleanup と saved-origin Rust scene guard の2件はテスト側の条件で skip。成功数に含めていない。
- 実クライアントGUI上の飛行・NPC反応と EveJS 実行比較は行っていない。EVE Online V24.01 クライアント呼出し・消費元の静的追跡と EveJS 261028.1 実装の照合は本文の file:line 根拠を参照。
- 現在の main 最終完了 CI #1446（`17a6e0e7`）は Windows Rust の docking wire test で失敗。task 変更外の失敗なので push を保留し、main CI #1447（`f06bfb23`）の完了を待つ。
## NPC security-status recovery: end-to-end check (2026-11-03)

### V24.01 client path

- Whole-client search found the service RPC/cached-value path at `eve/client/script/ui/services/crimewatchSvc.py:590-600`: `GetMySecurityStatus` calls the bound RPC only while the cached value is `None`, and `OnSecurityStatusUpdate` replaces that cache. Consumers found across the client are: agent warning `eve/client/script/ui/station/agents/agentDialogueUtil.py:175,424`; character overview `eve/client/script/ui/shared/neocom/charsheet/characterOverviewPanel/characterOverviewPanel.py:153-166` (one decimal place); dogma `eve/client/script/dogma/clientDogmaLocation.py:747-750`; fitting `eve/client/script/ui/shared/fittingScreen/fittingDogmaLocation.py:787-788`; security office slider and refresh `eve/client/script/ui/station/securityOfficeWindow.py:89-121,146-148,164-173,342-349`; and high-sec warning thresholds `eve/client/script/ui/services/crimewatchSvc.py:643-658`.
- Transaction history is a separate bound call at `crimewatchSvc.py:602-603`; `eve/client/script/ui/shared/neocom/charsheet/securityStatusPanel.py:26-74` displays it by `eventTypeID`. `eve/common/script/mgt/appLogConst.py:303-307` maps pirate-NPC gains to event ID 378. The read RPC has no client error-code translation; a missing cache triggers a fetch, while the transaction panel consumes returned rows. The Host wire test checks fractional status as `PyReal`.

### eve.rs and EveJS comparison

- `SpaceSessions.Npc.cs:322` emits `npc.killed` for an NPC ship death. `NpcSecurityStatusRecoverySubscriber.cs:64-119` filters the profile and persists the highest eligible bounty in the character's anchored five-minute window; settlement writes the relation history and `npc.security_status.recovered` in one transaction. `SpaceComposition.cs:116-141` registers recovery and notification delivery outside the simulator-only block. The notification bridge sends `OnSecurityStatusUpdate` with subject `charid` and a numeric status, matching the service cache update and security-office refresh.
- EveJS 261028.1 immediately applies recovery for every fresh bounty kill (`/home/user/evejs-261028.1/src/server/src/services/bounty/bountyRuntime.js:362-446`). CCP documents a character-wide five-minute interval starting with the first NPC kill and Fast Talk at 5% per level ([CCP dev blog](https://www.eveonline.com/news/view/wanttotrade-tags-for-security-status), lines 61-74); EVE University describes the highest outlaw NPC per interval and a +5 NPC recovery ceiling ([Repairing security status](https://wiki.eveuniversity.org/Repairing_security_status), sections "Security gain mechanics" and "Notes"). These sources justify the durable aggregation and +5 cap.
- The profile gate allows Guristas, Angel Cartel, Blood Raiders, Sansha's Nation, and Serpentis. EVE University excludes Amarr/Gallente faction rats, sentry guns and typically named targets. The current kill producer supplies an NPC ship profile, so sentry structures are outside this event path; generic mission profiles, Empire factions and Rogue Drones with positive bounties are rejected. `NpcSecurityStatusRecoveryTests.cs` covers the five allowed factions and these excluded profile classes.
- Exact per-rat rates remain a known gap: the profile model exposes bounty, faction and the NPC's own security status (`dotnet/src/Eve.Npc/NpcAuthority.cs:7-12`) but no recovery modifier. The EVE University examples list 0.1920% at 500,000 ISK and 0.0762% at 74,063 ISK with max Fast Talk. EveJS's fallback in `bountyRuntime.js:273-287` yields 0.3125% and about 0.0926%; eve.rs retains that fallback without claiming exact rate parity. A supported per-profile rate source or client trace is still needed.

### Verification

- Post-merge builds passed with 0 warnings/errors for Eve.Host and Host.Tests, Eve.Npc.Tests, Eve.Services.Crimewatch.Tests, Eve.Crimewatch.Tests, Eve.Events.Tests, and Eve.Services.Dogma.Tests. `eve_p5` tests via `tools/dev/db-lock.sh` passed: Services.Crimewatch 36, Crimewatch aggression/safety 44, NPC recovery 20, EventBus 17, Host 26, and Dogma kill-right 2. Host also skipped `Real_sim_cleanup_confirms_live_npc_environment_and_reward_scene_removal_with_another_run_intact` and `Saved_origin_gate_open_update_passes_the_real_rust_scene_guard`; neither is counted as a pass.
- Post-merge Rust checks passed: `cargo +1.94.1 fmt --all -- --check`, workspace clippy, `cargo check -p eve-space`, and five focused tests (security-status slim update plus four target-lock recalculation/limit cases).
- Build-failure fix: `NpcFactionStanding.ApplyAsync` accepts the NPC corporation and type from `SpaceComposition.cs`; the standing history stores the owner in `int_1`, solar system in `int_2`, and type in `int_3`, as consumed by `eve-logic/eve/common/script/util/eveFormat.py:101-105`. The earlier main-only fix used an aggression event and placed the type in `int_1`; the client and the [EVE University Agent guide](https://wiki.eveuniversity.org/Agent) support a ship-kill event for NPC ship destruction. `NpcFactionStandingTests` checks the stored fields.
- Before/after evidence: recovery first reached 7.63671875 through per-kill accrual where the NPC cap is +5; after the tick fix the regression passes. Before the profile gate, a 1,000,000 ISK Rogue Drone beat the eligible 500,000 ISK pirate; after the gate the database test passes.
- Still to check: graphical V24.01 client behavior, exact per-profile recovery percentages, and this commit's push-triggered Actions. The rate-source gap remains open.
- Main CI #1448 failed before a later run #1449 for SHA `f7c0e9e14ccd` completed green, including Required CI gate. After #1449, `origin/main` was fetched and merged. Conflict resolution preserved the local security-status-loss queue and upstream target-lock recalculation. SHA `95bd9c920736` was pushed; Agent test #1263 and CI #1450 failed on the missing `ApplyAsync` overload. Signed fix `550a4ab4b5d7` was pushed; selected test #1264 and CI #1451 both passed with no failed tests or jobs. `ci-status.py failures 550a4ab4` confirmed the CI result.
