# スキル機能監査 r14 の修正記録

対象監査: `/home/user/reports/90-audit-eve-skills-r14.md`（2026-11-07）
比較対象: V24.01 eve-logic (`/home/user/eve-logic`) と EveJS (`/home/user/evejs-261028.1`)

## 結果

監査の6件をコード、クライアント呼び出し、EveJS の対応フローと照合しました。6件すべて実在し、修正しました。誤検知として除外した所見はありません。

## 修正した所見

1. **スキル本注入と直接購入の排他** — `SkillHandlerService.Injection.cs` の注入経路を購入・キュー保存と同じ `skills:{character}` advisory lock に統一しました。ロック後にスキルの有無を再読込してから、本の消費とスキル付与を同じ transaction で行います。Host 回帰テストは購入 RPC と `dogmaIM.InjectSkillIntoBrain` を同時実行し、成功は片方だけであること、付与行が1件であること、ISK と本のどちらか片方だけが消費されることを確認します。クライアント経路は `skillsvc.py:775-792`、EveJS の購入・スキル本処理は `skillbookRuntime.js:206-261,323-355,489-523` です。

2. **Expert Systems 無効化** — `ClusterOptions` を Host の character composition に渡し、無効時は `GetSkills` の grant projection に Expert Systems を渡さず、`ConsumeExpertSystem` は有効な Expert System アイテムを解決した後に設定エラーで拒否します。Host テストは既存 grant がスキル一覧に現れないこと、global config が 0 であること、直接 activation が拒否されアイテム・grant・通知状態が変わらないことを確認します。V24.01 は `expertSystems/feature_flag.py:9-11` で設定を読み、`characterPanel.py:44-45` でタブを隠しますが、`expertSystems/client/service.py:55-56` の activation RPC にはクライアント側の設定拒否がありません。EveJS は `expertSystemProjection.js:27-30,125-130` で grant を投影せず、`expertSystemRuntime.js:132-147` で通常 activation を拒否します。

3. **Injector の inventory access 検証** — preview と mutation で、所有者に加えて「現在ドック中の station の hangar」または「session の active ship の cargo hold」を検査します。mutation は item row lock 後にも同じ条件を再確認します。Host テストで別 station と誤った flag を preview・mutation の両方から拒否し、session の active capsule cargo item は利用できることを確認します。クライアント呼び出しは `skillsvc.py:976-984`、画面の検査と実行は `skillInjectorWindow.py:141-147,162-170` です。EveJS の同等の所有・場所・flag 検査は `skillTradingRuntime.js:166-210,699-705` です。

4. **直接 Skill Purchase 無効化** — `SkillPurchaseEnabled` を `SkillHandlerService` に渡し、`PurchaseSkills` の wallet 操作・入力処理より前に `SkillPurchaseDisabled` を返します。Host テストは購入可能な SDE スキルと十分な残高を用意し、global config、拒否コード、wallet balance・journal、スキル行、通知の不変を検証します。V24.01 は `skillsvc.py:1076-1078` から購入処理へ進み、共通購入検証が `characterskills/common/purchase.py:9-16` で無効時に拒否します。EveJS は `skillbookRuntime.js:206-209` で購入検証の先頭に同じ拒否を行います。

5. **抽出と queue 保存の競合** — 抽出処理は完了済み training を settle した後、共通 `skills:{character}` lock を transaction 内で取得し、最新の queue をその transaction から読んで queue item と prerequisite を検査します。Host 回帰テストでは queue insert を遅延させ、queue save と extraction RPC を競合させ、最新 queue に載ったスキルの抽出を拒否し、extractor・skill points・queue が保持されることを検証します。V24.01 の抽出呼び出しは `skillsvc.py:970-974` と `skillExtractorController.py:584-586`、EveJS は同期処理内で snapshot を検証してスキル状態を置換します (`skillTradingRuntime.js:857-885`)。

6. **Expert System 失効通知の永続化** — lapse notice を削除 transaction の commit より前に書き込み、削除 commit に失敗すれば expiry row を残して再試行可能にしました。通知の idempotency key は `expert system type ID` と `expires_at_ms` から作るため再試行で重複しません。Host テストは deferred constraint trigger で delete commit を失敗させ、通知が残り expiry row が再試行対象として残ること、再試行で row が削除されても永続通知が1件であることを確認します。V24.01 の通知 type は `notificationconst.py:230`、formatter は `notificationUtil.py:1429-1430` です。EveJS は offline の pilot にも type 253 / misc group の通知を保存します (`expertSystemNotifications.js:78-97`)。

## テスト先行と検証結果

各 Host 回帰ケースを先に追加して修正前に実行し、6件とも失敗することを確認しました。再現内容は、二つの grant RPC の両方が成功、disabled Expert System の grant projection と activation が通る、remote/wrong-flag injector が通る、購入無効時に拒否されない、競合 queue item の抽出が通る、delete commit failure 後に通知がない、でした。購入テストでは初回に残高不足で早期拒否される設定ミスがあり、十分な残高を保証して再実行し、目的の `SkillPurchaseDisabled` が出ないことを確認してから修正しました。queue テストも初回の抽出選択が不正だったため、SDE から有効な500,000 SP 以上の選択を作り、queue 拒否の通知内容と状態保持を確認する形に整えてから赤を確認しました。

修正後、次を実行しました（DB-backed run は指定 database を明示）：

- `Eve.Skills`、`Eve.Services.Skills`、`Eve.Host` と対応する3つの test project を Release build。すべて 0 warnings / 0 errors。
- Host の6件の新規回帰テスト: `eve_p4` で 6 passed、`eve_p3` で 6 passed、いずれも skipped 0。
- origin/main 取り込み後の `Eve.Services.Skills.Tests` 全体: `eve_p4` で 85 passed、skipped 0。
- origin/main 取り込み後の `Eve.Skills.Tests` の CharacterTraining / Learning / ExpertSystem / ExpertSystemStore / TrainingQueue 対象: `eve_p4` で 58 passed、skipped 0。
- origin/main 取り込み後の Skills Host / wire route 対象: `eve_p4` で 10 passed、skipped 0。
- origin/main 取り込み後の `CharacterHostTests` 全体: `eve_p4` で 68 passed、1 skipped、0 failed（15分10秒）。skip は `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` で、pass には数えていません。

## 照合範囲と未確認事項

- V24.01 全体を `rg` で検索し、Skill Purchase、injector preview/mutation、extraction、Expert System consume、feature flag、expiry notification の呼び出し元・RPC callsite・通知定義を追いました。上記に挙げた file:line は各 client-visible flow の根拠です。
- EveJS は該当 runtime / projection / notification source を読み、対応フローを照合しました。実行可能な EveJS test script / test suite は確認できていないため、EveJS との実行比較は未実施です。
- V24.01 の graphical client を実際に接続する検証はしていません。
- 作業前に確認した main の最新 full CI run 1653（commit `7f9b36e9dc23`）は green で、全 jobs が成功でした。直前の run 1646 は `Undocking_tells_the_client_in_the_reference_order_and_answers_none` で赤でしたが、root cause は未登録 OID の解放時に session の OID bookkeeping を消さなかった点で、`5f948819f` の `ServiceRegistry` 修正が回帰テストを追加しています。修正後の run 1653 は green です。root-cause 記録は `docs/project/audit-fixes-services-core-hub.md` にあります。今回はまだ push していないため、この Skills change の CI run は未確認です。
