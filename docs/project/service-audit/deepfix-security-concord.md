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
- Build-failure fix: `NpcFactionStanding.ApplyAsync` at `NpcFactionStanding.cs:18-47` now accepts `npcTypeId` and persists it as standing-reason `int_1`; `SpaceComposition.cs:396` supplies it, and `NpcFactionStandingTests.cs:18-76` checks the stored reason fields. Release builds passed for Host and Services.Space.Tests (0 warnings/errors); the `eve_p5` DB test class passed 1 test with 0 skips.
- Before/after evidence: recovery first reached 7.63671875 through per-kill accrual where the NPC cap is +5; after the tick fix the regression passes. Before the profile gate, a 1,000,000 ISK Rogue Drone beat the eligible 500,000 ISK pirate; after the gate the database test passes.
- Still to check: graphical V24.01 client behavior, exact per-profile recovery percentages, and this commit's push-triggered Actions. The rate-source gap remains open.
- Main CI #1448 failed before a later run #1449 for SHA `f7c0e9e14ccd` completed green, including Required CI gate. After #1449, `origin/main` was fetched and merged. Conflict resolution preserved the local security-status-loss queue and upstream target-lock recalculation. SHA `95bd9c920736` was pushed; Agent test #1263 and CI #1450 failed on the missing `ApplyAsync` overload. Signed fix `550a4ab4b5d7` was pushed; selected test #1264 and CI #1451 both passed with no failed tests or jobs. `ci-status.py failures 550a4ab4` confirmed the CI result.
## Follow-up: NPC security-status recovery uses the SDE rate (2026-11-03)

### Finding and evidence

The recovery calculation previously treated bounty as the rate and capped it at `0.0025`. The subscriber also retained the greatest bounty in its durable five-minute window. This can select the wrong rat and produce the wrong amount: pinned SDE `dogmaAttributes` identifies `entitySecurityStatusKillBonus` (attribute ID 252) as the percentage movement for killing that type, and the queried SDE rows show different values for NPC types with the same bounty. EVE University documents that each rat has its own security percentage and the highest-value outlaw NPC in a five-minute period determines the gain; Fast Talk adds 5% per trained level ([Repairing security status](https://wiki.eveuniversity.org/Repairing_security_status), lines 133-168).

The server kill event already carries both the NPC `typeId` and catalog profile: `SpaceSessions.FinishNpcDeathAsync` builds and announces it (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:289-326`), and the sim regression test asserts those fields (`dotnet/tests/Eve.Services.Space.Tests/SpaceEventsTests.cs:209-228`). `NpcProfile.ShipTypeId` is the static type key (`dotnet/src/Eve.Npc/NpcAuthority.cs:8-10`). Host now resolves `entitySecurityStatusKillBonus` by SDE name, loads those profile types, and reads the value from `DogmaStatic.Type(...).Attributes` before creating each recovery profile (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:116-138`; `dotnet/src/Eve.Dogma/DogmaStatic.cs:109,129,139-187,311-391`).

`NpcSecurityStatusRecovery.TryNext` now consumes that percentage directly, applies Fast Talk, preserves the reference formula and the existing five-status ceiling, and has no bounty-derived cap (`dotnet/src/Eve.Npc/NpcSecurityStatusRecovery.cs:25-45`). The durable window stores the percentage and replaces its candidate only when a higher percentage arrives; migration `0007` adds the rate and allows a zero-bounty NPC with a positive SDE rate (`dotnet/src/Eve.Crimewatch/PgCrimewatchStore.cs:29-41,195-213,220-290`). A legacy active window with the migration default of zero falls back to the current static profile rate during settlement (`dotnet/src/Eve.Services.Crimewatch/NpcSecurityStatusRecoverySubscriber.cs:109-181`).

This intentionally diverges from EveJS 261028.1's fallback that derives a capped modifier from the bounty (`/home/user/evejs-261028.1/src/server/src/services/bounty/bountyRuntime.js:257-287,362-446`). The V24.01 client path stays scalar and unchanged: `GetMySecurityStatus` lazily calls the crimewatch bound RPC and `OnSecurityStatusUpdate` stores its value (`/home/user/eve-logic/eve/client/script/ui/services/crimewatchSvc.py:590-600`); the security-office listener refreshes the tag bar (`/home/user/eve-logic/eve/client/script/ui/station/securityOfficeWindow.py:137-148`). Whole-client search found the other `GetMySecurityStatus` readers in character overview (`characterOverviewPanel.py:152-166`), security warnings (`agentDialogueUtil.py:164-202,422-446`), crimewatch safety (`crimewatchSvc.py:643-658`), and fitting (`fittingDogmaLocation.py:787-788`). The client also exposes `GetCharacterSecurityStatus` through `crimewatchSvc.py:595-596` and dogma location callsites at `clientDogmaLocation.py:747-750` and `fittingDogmaLocation.py:787-788`.

### Verification

- Before the fix, the database-backed restart/window regression failed: expected `0.03`, got `0.03125` from the bounty cap.
- After the fix, the focused recovery subscriber test passed (1); the full `Eve.Services.Crimewatch.Tests` project passed (52), `Eve.Crimewatch.Tests` passed (60), `NpcSecurityStatusRecoveryTests` passed (20), Host Crimewatch wire tests passed (6), and `SpaceEventsTests` passed (13). These runs had no skipped tests.
- Release builds passed for `Eve.Host`, `Eve.Crimewatch`, `Eve.Npc.Tests`, `Eve.Services.Crimewatch.Tests`, `Eve.Host.Tests`, and `Eve.Crimewatch.Tests`.
- The newest completed main CI before this change was run 1452 on `3d9ffdd28db3`, green across all jobs. This evidence does not include a run for the current recovery-rate patch.
- The focused and neighboring tests were database-backed on `eve_p5`, and the sim test was headless. No graphical V24.01 client session was used for this change.
### Verification after merge from `origin/main`

The `d9fef52c` fast-forward was followed by fresh Release builds and `eve_p5` checks:

- Builds passed for `Eve.Host`, `Eve.Npc.Tests`, `Eve.Services.Crimewatch.Tests`, `Eve.Host.Tests`, `Eve.Crimewatch.Tests`, and `Eve.Services.Space.Tests`, each with zero warnings and errors.
- Database-locked checks passed with no skips: `NpcSecurityStatusRecoveryTests` (20), `Eve.Services.Crimewatch.Tests` (52), `Eve.Crimewatch.Tests` (60), Host `Crimewatch_` wire tests (6), and `SpaceEventsTests` (13).
- The graphical V24.01 client was not used; behavior was checked against the client source callsites cited above.

### Push and Actions handoff

- Code commit `31dac370` was pushed to `main` after a green local build/test pass and green latest completed main CI.
- Selected Actions run `41579007784` passed for `Eve.Host.Tests`, `Eve.Services.Crimewatch.Tests`, `Eve.Crimewatch.Tests`, `Eve.Npc.Tests`, and `Eve.Services.Space.Tests` with no failed tests or skipped jobs.
- Push-triggered CI run `41578995207` (CI #1454) completed successfully with no failed tests or jobs, including the headless end-to-end and Windows jobs.

## Security-status penalty memory correction (2026-11-03)

### Finding

Repeated low-sec attacks against the same pilot were only suppressed for the 60-second weapon timer. EveJS 261028.1 uses `SECURITY_STATUS_DUPLICATE_WINDOW_MS = WEAPON_TIMER_DURATION_MS` (`/home/user/evejs-261028.1/src/server/src/services/security/crimewatchState.js:68,80,1557-1587,2315-2496`) and keys the target by entity item ID plus ship/capsule. A player could wait out the weapon timer, change to another ship, and incur another penalty while the same suspect/criminal timer or limited engagement remained active.

EVE University gives the ship/capsule penalty formula (`[Security status](https://wiki.eveuniversity.org/Security_status):169-183`) and says sentry guns engage on the grid while the security-loss event continues to recur after new offenses (`:196-200`). CCP's Crimewatch design explanation says the offense memory is retained while a suspect/criminal timer or any limited engagement remains and keys the victim plus ship/pod offense level ([archived CCP design discussion](https://eve-search.com/thread/160005-1/author/Gizznitt%20Malikite)). The weapon timer is therefore too short to own this memory.

### Change and evidence

- `SecurityStatusPenalties.OnAggressionAsync` now extends memory through the latest active criminal/suspect flag or limited engagement, and keys by target character plus ship/capsule (`dotnet/src/Eve.Services.Crimewatch/SecurityStatusPenalties.cs:49-119`). A fresh victim or offense level gets its own hit; non-attack Crimewatch actions only extend an existing memory.
- Migration `0008` stores `(attacker_character_id,target_character_id,offense_level,expires_at_ms)`. The store serializes an attacker's reservation, expires old rows, and commits the reservation together with the status history write (`dotnet/src/Eve.Crimewatch/PgCrimewatchStore.cs:213-223,335-410`; Host callback `dotnet/src/Eve.Host/Composition/SpaceComposition.cs:455-480`). A persistence failure rolls the reservation back.
- The V24.01 client contract remains scalar and notification-compatible: the full client reads `GetMySecurityStatus` and updates its cache from `OnSecurityStatusUpdate` (`eve/client/script/ui/services/crimewatchSvc.py:590-600`); `securityOfficeWindow.py:146-148` refreshes the visible status. Other readers and consumers remain listed above under “V24.01 client path.”
- The CONCORD path remains the existing verified flow: `CrimewatchRuntime.RecordAggressionAsync` records criminal consequences before aggression handlers and synchronizes state in `finally` (`dotnet/src/Eve.Services.Crimewatch/CrimewatchRuntime.cs:196-256`); its response scheduling, restart resume, Host hookup, ship selection and ship loss are cited above. The crimewatch and Host wire regression classes passed in this run.

### Verification for this change

- Before/after: temporarily restored the former `now + 60_000` expiry. The Host sim regression failed because the second shot produced a second status-history write. Restored the Crimewatch timer expiry; the same test passed.
- `Eve.Services.Crimewatch.Tests`: 55 passed, 0 skipped. `Eve.Crimewatch.Tests`: 60 passed, 0 skipped with `eve_p5` (the initial run without a DB URL skipped four database cases; the locked rerun included all of them). `WeaponHostTests`: 36 passed, 0 skipped. Host Crimewatch wire filter: 6 passed, 0 skipped. The focused Host sim test: 1 passed, 0 skipped.
- Builds passed with 0 warnings/errors for `Eve.Crimewatch.Tests`, `Eve.Services.Crimewatch.Tests`, `Eve.Services.Space.Tests`, and `Eve.Host.Tests`. DB-backed tests ran against `eve_p5` via `tools/dev/db-lock.sh`.
- The whole-client static path and EveJS implementation were compared, but no graphical V24.01 client session or EveJS process execution was performed.

### Open sentry scope

EVE University says any sentry gun on grid engages when a suspect/criminal act reduces security status, then stops after the pilot leaves that grid (`Security status:196-200`; [Timers](https://wiki.eveuniversity.org/Timers):190-205). V24.01 consumes `MESSAGE_SEC_STATUS_LOST_EVENT` through `behaviors/monitors/combat.py:368-406`; eve.rs queues and broadcasts the `(ownerID,shipID)` event and the Rust monitor applies the matching bubble/range checks (`crates/eve-space/src/scene/session.rs:606-727`, `brains/world.rs:164-174`, `eve-behaviors/src/monitors/combat.rs:474-512`).

The normal Empire gate/station sentry population and its combat response are not complete in this checkout: the Rust sentry population found here is Pochven-only (`crates/eve-space/src/pochven_population.rs:475-498`), and `Scene::new` starts with empty NPC maps (`crates/eve-space/src/scene/mod.rs:867-917`). EveJS has Empire gate/station sentry scene entities and a native sentry policy (`/home/user/evejs-261028.1/src/server/src/space/defaultEmpireScenery.js:272-314,357-399`, `npc/npcSentryPolicy.js:75-124`). Do not treat the end-to-end standard Empire sentry flow as complete; this correction only fixes Crimewatch offense memory and its persistence.
