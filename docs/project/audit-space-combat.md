# Space / combat gameplay audit

対象は V24.01 client build 3396210、EveJS v0.12.9、eve.rs の sim / Host / 永続化経路。修正済み所見は回帰させないため再実装せず、station docking、wormhole、NPC loot、Crimewatch module-end timer の既存記録は [audit-fixes-crate-space.md](audit-fixes-crate-space.md)、[audit-fixes-wormholes.md](audit-fixes-wormholes.md)、[audit-fixes-npc.md](audit-fixes-npc.md)、[audit-fix-corp-contract.md](audit-fix-corp-contract.md) を参照した。

## 確認済み、修正

### 一時的なスポーン拒否で CONCORD 応答と船の処罰が失われる

V24.01 client の `eve/client/script/ui/services/crimewatchSvc.py:96-103` は session change 時に `GetClientStates` から犯罪タイマーを読み、`:240-253` は `OnCriminalTimerUpdate` と `OnSystemCriminalFlagUpdates` を表示状態へ反映する。CONCORD 出現は server 側の応答であり、client の timer notification は応答完了を意味しない。EveJS `server/src/services/security/crimewatchState.js:3266-3342` は応答 record を保ち、`:5169-5185` で responder が0隻なら `dueAtMs = now + 1_000` として再試行する。

旧 eve.rs は `CrimewatchRuntime.DispatchConcordResponseAsync` が一度だけ Host callback を呼び、完了・例外どちらでも dedup key を消していた。Host の `SpaceComposition.cs` は `RespondToCriminalAsync` の結果を破棄していたため、pilot / profile / position が一時的に見つからないか sim が spawn を拒否した場合、犯罪 timer が続いていても responder の再試行は登録されなかった。また最初の cruiser spawn 後に15秒で行う battleship spawn が拒否された場合も `composition.Count == 0` のまま終了し、CONCORD による船の破壊まで進まなかった。修正前に追加した Runtime と Space の DB-backed regression test はどちらも再試行 timer が無いことで失敗した。

Host callback が受理結果 (`bool`) を返すようにし、明示的な拒否の場合だけ Crimewatch が1秒後の再試行を登録する。再試行前に DB state が同じ system の有効な criminal flag であることを確認し、期限切れや system 移動後は止める。結果が曖昧な例外は二重 spawn を避けるため自動再試行しない。15秒後の battleship spawn も position 不在または明示的拒否のとき、同じ flag / system 条件を見て1秒後に再試行し、受理後に船を破壊する。

`RuntimeTests.A_failed_concord_response_is_retried_while_the_criminal_timer_is_active` は初回拒否後に callback が再度呼ばれること、`SpaceServiceTests.Concord_punishment_retries_after_a_temporary_spawn_refusal` は拒否した battleship spawn の再受理後に offender の `SimDestroy` が送られることを検証する。`ConcordResponseHostTests.Host_wiring_reports_a_rejected_concord_spawn_so_crimewatch_retries_it` は production Host callback 登録から DB-backed Crimewatch timer までを確認する。テスト DB は `eve_p5`。

### 命中率1%未満で命中率より大きいrollがwrecking hitになる

V24.01 client の `eve/client/script/environment/godma.py:502-533` は server の `OnDamageMessage` の `hitQuality` を turret / damage 表示へ渡し、`eve/client/script/ui/shared/uilog.py:210-220,278-305` は同じ値を combat log 文言にする。client は命中判定を再計算しないため、server の hit / miss と quality が authoritative である。EveJS `server/src/space/combat/laserTurrets.js:138-175` はまず `roll >= hitChance` を miss にし、命中した roll のみ `roll < 0.01` なら wrecking quality にする。二次資料の [EVE University turret mechanics](https://wiki.eveuniversity.org/Turret_mechanics) も hit には random roll が hit chance を下回る必要があると記している。

旧 Rust `weapons::math::resolve_shot` は `chance < 0.01` でも `roll < 0.01` を先に wrecking と判定し、`roll >= chance` を通していた。修正前に境界テストを実行すると、chance `0.002122`、roll `0.005` で `hit: true, wrecking: true` となり失敗した。miss の判定を先にし、残った hit のみ wrecking quality を付けるようにした。回帰テストはこのmissと、同じ低命中率でも chance を下回るrollがwreckすることの両方を検査する。

### 遠方の構造物を攻撃するドローンが接近しない

V24.01 の `eveCommands.py:1252-1261` は `CmdDronesEngage` から `PerformPrimaryAction` を呼び、`droneFunctions.py:59-98` は安全判定後に entity の `CmdEngage(droneIDs, targetID)` を送る。Host は `EntityService.cs:50-53` で bound RPC を登録し、`SpaceDrones.cs:413-428` から制御距離込みで simulator に渡す。したがって遠方の対象でも、受理後にドローンが対象へ移動して攻撃距離へ入る必要がある。EveJS `droneRuntime.js:5312-5387` は対象 / 制御距離 / 攻撃可能性を検査し、`:4221-4268` は pursuit / approaching / combat の状態を設定、`:3668-3725` は pursuit の移動を開始する。

eve.rs は構造物対象の `engage_structure` で攻撃タスクと activity だけを設定し、移動 course を作らなかった。10 km 離れた構造物へ命令しても、結果は成功なのにドローンはその場に留まり、武器が届かなかった。テスト `drones::tests::a_drone_sent_at_a_distant_structure_approaches_until_its_weapon_can_fire` を修正前の HEAD で先に実行して再現した。

`engage_structure` に target への orbit course を追加し、表面距離から pursuit / approaching / combat を選ぶようにした。回帰テストは command の成功、`Orbit` / `SetSpeedFraction` の Destiny payload（drone ID は Long、structure ID は Int）、course、初期 PURSUIT、接近後の `StructureDamaged` を検査する。近距離で既に射程内の既存攻撃テストも保持する。

### Cyno 到着位置と向きが SDE / EveJS と異なる

V24.01 の `cynoTravelSvc.py:30-55` は fleet module / deployable / structure cyno を session-change 経由で要求する。Host `SpaceSessions.CynoJumpAsync:247-299` は pilot と sim の応答を確認し、受理後に jump queue update を送る。EveJS `cynoArrivalSpawnState.js:6-36` は設定半径（未設定時 5,000 m）の球体内に一様配置し、向きに同じ外向き spawn vector を使う。`beyonceService.js:2660-2664` が spawn override を渡し、`transitions.js:4761-4783` が位置と向きを到着 state に設定する。

公式 SDE build 3396210 (`/home/user/.cache/sde/eve-online-static-data-3396210-jsonl/_sde.jsonl:1`) の attribute 2455 は `cynosuralFieldSpawnRadius` (`dogmaAttributes.jsonl:2124`) で、normal 21096 / covert 28646 / industrial 52694 の各 generator に 5,000 m が設定されている (`typeDogma.jsonl:8708,13749,20703`)。旧 eve.rs は 2,455 m 固定半径の輪に置き、さらに方向を field 中心向きにしていた。位置テストは旧実装が常に 2,455 m になることを修正前に検出し、向きの assertion も内向き vector を検出した。

到着を SDE の共通半径 5,000 m の球内に一様配置し、方向を外向き vector に合わせた。`scene::tests::a_cyno_jump_leaves_when_its_spool_is_over_and_appears_within_the_field_spawn_radius` は spool 完了、半径上限、中心寄りの配置、方向、停止状態を検査する。EveJS helper を固定乱数で直接実行し、距離 3,968.50 m と外向き方向を確認した。EveJS の全 server flow は実行していない。

### 発射済み弾薬の残数が DB 障害時に失われる、または古い残数で再開する

V24.01 の `clientDogmaLocation.py:949-951,980-996,1106-1140` は fitting slot の charge stack を Dogma の load / unload RPC へ渡し、残数は船の fitted inventory として扱う。eve.rs では sim の `SimChargeSpent` を `SpaceSessions.cs:1214-1232` が `ChargeSpentQueue` に渡し、`CharacterShips.Ammo.cs:923-950` が fitted stack を更新または削除する。dock、death、relocation、disconnect はそれぞれ `SpaceSessions.Movement.cs:645-649`、`SpaceSessions.Death.cs:447-449`、`SpaceSessions.Relocation.cs:258-263`、`SpaceSessions.cs:1536-1545` で queue を閉じ、module activation は `SpaceSessions.Modules.cs:135-137` で flush してから fit を読む。

旧 `WriteAsync` は DB 例外を4回受けると最後の失敗を記録して return し、flush / close は未保存でも成功扱いだった。新しい queue Host テストを先に実行すると、期待した残数1件に対して保存件数0件になった。PostgreSQL Host test も切断時に期待70発、実際80発を確認した。さらに旧15秒 timeout は pending write を抱えたまま close を完了させ、また activation が古い80発を sim に再送することを、それぞれ実 Host / `eve_p5` で修正前に再現した。

retry を最大10秒の指数 backoff で成功まで続ける。flush / close の待機は15秒ごとに待機ログを出すが、永続化前に成功を返さない。これにより dock / death / session close / module start は、古い stack を使った次状態へ進まない。回帰テストは一時的な4回失敗からの復旧、15秒を越える切断待ち、15秒を越える module activation の順序と sim へ渡す残数を検証する。

## 誤検出

### 切断で15秒後の CONCORD 処罰が失われる

この疑いは、ログアウトした船に対する処罰がそのまま失われるという範囲では誤検出。eve.rs は通常の切断で `OnSessionClosedAsync` から保存後に `LeaveWhy.LoggedOff` を sim へ送り、15秒 callback 時に `_pilots` から対象が消えていれば何もせず戻る (`SpaceSessions.cs:1511-1512,1547-1578`; `SpaceSessions.Npc.cs:863-878`)。ただし reconnect 後の宇宙 entry は `SpaceSessions.cs:814-817` から `NotifyPilotEnteredSystemAsync` を通り、`CrimewatchRuntime.OnSystemEnteredAsync` が有効な criminal timer と high-sec entry を確認して新しい response を予約する (`SpaceSessions.Modules.cs:608-621`; `CrimewatchRuntime.cs:829-864`)。

EveJS も切断時の通常 detach では ship entity を grid から外す (`server/src/space/runtime/scene/sessions.js:1423-1429`)。一方、Crimewatch は response record を保持し、offender 不在時は responder を止めて次回の retask を予約し、active criminal の response を回復する (`server/src/services/security/crimewatchState.js:4996-5055,5092-5166`)。したがって通常切断中の ship は空間上に残らず、criminal timer 中の再 entry で eve.rs は response を再予約する。ログアウトした pilot の ship をその場で破壊する必要があるという根拠はなく、修正は行わなかった。実 reconnect GUI シナリオは未実行。

### Abyssal 転送時に旧 session の pending Destiny 通知を新 session へ付け替える必要がある

この疑いは誤検出。V24.01 `michelle.py:482-485` は ballpark が無い間の `DoDestinyUpdate` を無視し、`:968-987` の `SetState` は `DoBallClear` の後に全 state を読み直してから有効化する。EveJS `runtime/scene/presentation.js:772-815,928-956` も新 session 向けの `SetState` と bootstrap updates を作り、その後に one-shot updates を送る。Host `SpaceSessions.cs:1104-1113` は通知の `SessionId` に一致する現在の pilot にだけ送るため、旧 session の通知を新 session に再割当てしない。

再割当てを要求するテストは、旧 session の `DoDestinyUpdate` が新 session の `SetState` 前に挿入される順序を作り、bootstrap contract を壊した。変更を戻し、現行 session ID による配送を維持した。実 GUI client は未起動。

## 追加所見なしとして確認した経路

- Stargate / cyno jump は scene 側で in-warp、disruption、capacitor、距離、進行中 jump を確認する。gate transit の受理後だけ停止・module/target cleanup に進む。wormhole mass は Host `SpaceWormholes.cs:174-250` が simulation の受理後に記録する。以前の wormhole cancel / state 所見は上記既存記録を参照。
- death は `FinishDeathAsync` が charge queue を閉じてから `DestroyShipAsync` を行い、killmail / bounty は ship loss transaction に含め、wreck・survivor 通知は commit 後に送る。`_dying` と death waiter は `finally` で解放する (`SpaceSessions.Death.cs:430-612`)。この範囲では追加の item / ISK loss を確認しなかった。
- limited engagement expiry は expected expiry を確認してから expire し、まだ有効な期限なら再スケジュールする (`CrimewatchRuntime.cs:193-240`)。client timer 表示は V24.01 `crimewatchTimers.py:360-392`。既存の module combat activity expiry 所見は再度変更せず、上記 audit record を参照。
- NPC loot と wreck item kind の既修正所見は `audit-fixes-npc.md` に記録済みのため再実装していない。

## 検証と未確認事項

- 修正前の失敗: drone 遠距離攻撃、cyno 半径と向き、queue 4回失敗、Host 切断後残数、15秒超の切断、15秒超の gun activation。
- 修正後の .NET Release focused classes: `ChargeSpentQueueTests` と PostgreSQL-backed `AmmoHostTests` の合計22 passed、0 failed、0 skipped。DB は `eve_p5`。
- Cyno jump の DB-backed Host tests は40 passed、0 failed、0 skipped。先に実行した drone Host tests は29 passed、0 failed、0 skipped。
- Cyno EveJS helper の決定的な直接実行は行った。EveJS の drone / ammo end-to-end server flow と graphical V24.01 client は実行していない。SDE は build 3396210 のローカル JSONL を読み、数値を確認した。
- .NET `Eve.Services.Space` Release build は 0 warnings / 0 errors。`ChargeSpentQueueTests` と PostgreSQL-backed `AmmoHostTests` は `eve_p5` で合計22 passed、0 failed、0 skipped。追加前に実行した `DroneHostTests` は29 passed、0 failed、0 skipped。
- Rust `cargo fmt --package eve-space -- --check`、`cargo check -p eve-space`、`cargo clippy -p eve-space --all-targets -- -D warnings` は成功。focused drone tests は4 passed、cyno arrival test は1 passed。
- 全 suite、実 client、再起動をまたぐ未flush queue の crash recovery は未確認。EveJS の全 end-to-end flow も実行していない。
- 今回の Crimewatch Runtime class は Release で42 passed、SpaceServiceTests class は172 passed、Host 配線 test は1 passed。すべて `eve_p5` のDB付き、0 failed / 0 skipped。
- 今回の Rust weapon slice は91 passed、0 failed。`cargo fmt --package eve-space -- --check`、`cargo check -p eve-space`、`cargo clippy -p eve-space --all-targets -- -D warnings` はすべて成功。
- current `origin/main` の CI run 1807 (`467fdde6`) は Host の Release build で `OmegaLicenses.TrainingHistory` 不在により失敗。これは今回の space 変更ではない。red gate のため push は保留し、実 V24.01 GUI と EveJS の全 server flow は今回実行していない。
