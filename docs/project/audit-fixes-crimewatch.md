# Crimewatch r14 監査修正記録

監査レポート `/home/user/reports/90-audit-eve-crimewatch-r14.md` の3件を、実装・V24.01クライアント・EveJS v0.12.9で照合した。3件すべて実在し、誤検知として除外した項目はない。

## 確認済み

### 高: kill後に有効なkill-rightを消費しない

監査の指摘どおり、以前はストアの消費関数をShipLost/PodLost経路から呼んでいなかった。Hostが両方の損失通知を登録し、失われたship/capsule item ID、対象pilot、最終攻撃者で消費するようにした。DBトランザクションはdeath receiptをitem単位で保存し、最終攻撃者のactivationを優先してrightと全activationを一度にusedにする。同じdeathの再試行は保存済みactivationを返し、別deathとして二度消費しない。オンラインのowner、対象、activator、最終攻撃者には `OnKillRightUsed(killRightID, toID)` を送る。実装は [CrimewatchHostCallbacks.cs](../../dotnet/src/Eve.Host/Composition/CrimewatchHostCallbacks.cs)、[PgCrimewatchStore.cs](../../dotnet/src/Eve.Crimewatch/PgCrimewatchStore.cs) のdeath receipt migrationと `ConsumeKillRightAfterKillAsync`。

EveJSは [killmailTracker.js:715-742, 786-814, 859-892](/home/user/evejs-261028.1/server/src/space/combat/killmailTracker.js) でfinal attacker優先の選択、right消費、同じ4者へのオンライン通知を行う。shipとpodの双方のdeath経路がこの処理につながる。V24.01は [bountySvc.py:154-165](/home/user/eve-logic/eve/client/script/ui/shared/bountySvc.py) で `OnKillRightUsed(killRightID, toID)` を受け、target cacheとowner cacheを無効化する。

死亡後にtype 118のmail通知は新たに作成していない。EveJSのdeath pathはonline `OnKillRightUsed` のみを送信し、type 118は [killRightActivationEffects.js:87-95](/home/user/evejs-261028.1/server/src/services/bounty/killRightActivationEffects.js) の非owner activation/purchase経路で作られる。既存のHost側activation通知は維持しており、death pathにもmailを追加する監査案は実装挙動に合わせて採用しなかった。

回帰確認は `CrimewatchKillRightHostTests` のship/pod lossテストと、`RuntimeTests.Killmail_hook_selects_the_final_attackers_activation_and_consumes_the_right_once`。後者は同じlost itemへの並行呼び出し、store再生成後の再試行、別lost itemの消費も検証する。

### 高: security status重複抑止の対象と期間が違う

以前のDBキーはattacker/target character/offenseで、crime timerまたはlimited engagementの期限まで記憶を延長していた。今はattacker/target item/ship-or-capsule kindをキーにし、各キーを60秒だけ保持する。timerやlimited engagementによる延長は削除した。実装は [SecurityStatusPenalties.cs](../../dotnet/src/Eve.Services.Crimewatch/SecurityStatusPenalties.cs) と [PgCrimewatchStore.cs](../../dotnet/src/Eve.Crimewatch/PgCrimewatchStore.cs) の `0010_security_status_penalty_target_items` migration。

EveJSは [crimewatchState.js:68, 80, 1557, 2413-2458](/home/user/evejs-261028.1/server/src/services/security/crimewatchState.js) で60秒のweapon timerとtarget item/kind単位の記憶を使う。V24.01の [crimewatchSvc.py:590-600](/home/user/eve-logic/eve/client/script/ui/services/crimewatchSvc.py) は更新されたsecurity statusを受け取る。

`RuntimeTests` のDBテストは59,999ms時点で抑止し、60,000msで再適用すること、別shipとcapsuleの独立性、limited engagementが窓を延長しないこと、runtime再生成後の期限、別shipへの並行適用、同じshipへの並行適用を確認する。

### 中: 新規kill-rightの作成通知がHostに接続されていない

以前はkill-rightの保存だけで、`OnKillRightCreated` とtype 117の earned notificationを送る本番購読者がなかった。作成と `crimewatch.kill_right.created` outbox eventを同じDB transactionに入れた。購読者はowner/victim宛に `KillRightEarned` (type 117, MISC group, sender=`targetID`, data=`{charID: targetID}`) を永続化し、ownerとtargetのonline sessionへ `OnKillRightCreated(killRightID, fromID, toID, expiryTime)` を送る。idempotency keyはkillRight IDに固定した。実装は [CrimewatchKillRightEvents.cs](../../dotnet/src/Eve.Host/CrimewatchKillRightEvents.cs)、[CrimewatchComposition.cs](../../dotnet/src/Eve.Host/Composition/CrimewatchComposition.cs)、[MailComposition.cs](../../dotnet/src/Eve.Host/Composition/MailComposition.cs)、[PersistenceMailSendHostAdapter.cs](../../dotnet/src/Eve.Host/Persistence/Mail/PersistenceMailSendHostAdapter.cs)。犯罪のgrantではownerが被害者、targetがaggressorである。

EveJSは [crimewatchState.js:2699-2748, 2751-2812](/home/user/evejs-261028.1/server/src/services/security/crimewatchState.js) で作成通知を送り、[killRightNotifications.js:23-42](/home/user/evejs-261028.1/server/src/services/bounty/killRightNotifications.js) でvictim向けtype 117を構成する。V24.01の [bountySvc.py:154-156](/home/user/eve-logic/eve/client/script/ui/shared/bountySvc.py) は作成通知を受けると自分のkill-right cacheを無効化する。

`CrimewatchKillRightHostTests.A_new_kill_right_publishes_one_atomic_creation_event_then_persists_the_earned_notification` はDB保存、outbox、通知type/group/sender/recipient/data、オンラインwire引数、配信順、重複createと、通知が保存された後にlive sendが失敗した場合のretry・通知ID再利用を検証する。

## CI追補

push後のActions run 1614では、既存の `WeaponHostTests` が60秒を超えた攻撃でもcriminal timer終了までsecurity status penaltyを抑止すると期待していた。EveJSの [crimewatchState.js:1539-1573, 2384-2458](/home/user/evejs-261028.1/server/src/services/security/crimewatchState.js) は重複攻撃時に期限を延長せず、元の60秒期限後に同じtarget itemへ再適用する。Hostテストを59,999msでは抑止し、60,000ms境界とその次の1分で再適用する期待へ直し、criminal flagが継続中であることも確認する。

同runの `HackingMgrServiceTests.Closing_the_session_retries_a_failed_settlement_until_the_target_is_released` は、fake siteが2回目のsettlement開始時に通知した直後、serviceがHackingWindowを削除する前にassertしていた。テストは実装処理の完了まで待ち、window削除とtarget reservation解放を確認するようにした。service実装自体は変更していない。

run 1614は上記2件がfailとなり、Required CI gateもfailureで完了した。原因は、Host testがCrimewatchの60秒境界を古い15分期待で固定していたことと、settlement retry testの完了通知位置が早すぎたこと。2件ともassertionを弱めず、期待仕様と処理完了条件に合わせて修正した。この追補後のローカル確認はRelease build 0 warning、`HackingMgrServiceTests` 16件、`WeaponHostTests` 36件がすべてpass、skipなし。いずれも `eve_p4` を使用した。修正を含む次のmain CI runはまだ実施していない。

## 未確認

- V24.01のグラフィカルクライアントへの実接続は行っていない。wireと遷移の確認はDB-backed Hostテストとソース照合による。
- EveJSのサーバーフローはソースを追跡したが、EveJS実行環境でのシナリオ再生は行っていない。
- `codebase-memory-mcp` / Serenaはこの作業環境で使用できなかったため、全体検索と行番号付きのソース読み取りで照合した。
- V24.01の実クライアント接続、EveJS実行、修正後のActions main CI結果は未確認。
