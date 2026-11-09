# CharacterSelect サービス監査の修正記録

監査報告 `/home/user/reports/90-audit-services-characterselect-r15.md` の4件を、実装・V24.01 クライアント・EveJS v0.12.9 の該当コードで照合した。4件とも再現可能な不具合で、偽陽性として見送った項目はない。

調査時点で `CLAUDE.md` 指定の codebase-memory-mcp と Serena MCP は利用できなかったため、読み取りには `rg`、`nl` と直接のソース確認を用いた。EveJS checkout の `package.json` に test script がなく、該当する実行可能なテストハーネスも見つからなかったため、EveJS との比較はソース上で行った。

## P1: 失敗した entry の通知がクライアントに残る

**確認根拠:** V24.01 は `eve/client/script/ui/login/charSelection/characterSelection.py:705-725` で `SelectCharacterID` を `PerformSessionChange('charsel', ...)` として呼ぶ。entry hook の失敗後に選択先への session change と復元の両方を通知する旧動作は、この遷移と矛盾していた。EveJS は `server/src/services/character/charService.js:1704-1729,1816-1820` で通知を抑えた状態で session を適用し、成功後に通知計画を flush する。

**修正:** `ClientSession` に通知・session change の batch を追加し、entry hook とオンライン登録が完了した場合だけ `CharUnboundMgrService.SelectCharacterID` から commit する。失敗時は session/runtime を戻した後、未commit の通知を破棄する。成功時の既存通知順は batch 内に維持する。

**テスト:** `CharUnboundMgrTests.Failed_character_entry_releases_new_reservation_and_restores_previous_session` は hook 通知を出してから例外にし、packet 数が増えず旧 session が残ることを検査する。同テスト内で再試行が成功し、切替用の `OnCharacterSelected` と session change の2 packet が届くことも検査する。修正前の再現では packet 数が `2` から `6` に増えた。

## P2: 選択と削除予約の競合

**確認根拠:** 選択は `CharUnboundMgrService.cs` の character 読み取りと `IsQueuedForDeletion` 判定の後でオンライン登録を行い、削除予約はオフライン確認の後に `SetDeletePrepareAsync` を実行していた。両者を同じ character の状態遷移として直列化していなかった。V24.01 の削除 UI は `characterSelection.py:861-873` から予約・取消を呼び、EveJS は `server/src/services/character/characterDeletionRuntime.js:672-692` で offline 状態を確認してから予約する。EveJS のこの同期的な reference 実装に合わせ、eve.rs は entry と予約の共有 lifecycle lock を持つ。

**修正:** `OnlineCharacters` に character ごとの lifecycle lock を追加。`SelectCharacterID` は最新の永続状態の読み取りから entry 完了まで保持し、`PrepareCharacterForDelete` は offline 確認と queue 更新を同じ lock 内で行う。

**テスト:** `CharUnboundMgrTests.Selection_and_delete_reservation_are_serialized_for_the_same_character` で選択の読取を barrier で止め、その間に予約が更新を完了できないこと、選択後には予約が online error になることを検査する。`A_completed_delete_reservation_prevents_a_later_character_entry` は逆順で予約確定後の選択が queue error となることを検査する。lock 導入前は選択を止めた状態で削除予約が完了した。

## P2: 同一セッションの切替で人口枠を二重計上

**確認根拠:** V24.01 は `characterSelection.py:742-762` で population cap を照会してから同じ session で `SelectCharacterID` を行う。既存実装は切替中も旧 character を online として保持するため、旧 character と新 character が同じ system にいる際に旧 session と新しい予約を別々に数え得た。EveJS の `server/src/services/populationCap/populationCapService.js:166-189` は現在の system の単一 slot を返し、上限到達モデルを実装していないため、同じ上限動作の差分比較はできない。eve.rs 固有の上限内では一 session 一占有枠にする。

**修正:** `SystemPopulation` の判定・予約 API に切替元 session ID を渡す。集計ではその session の旧占有を外し、entry reservation を新しい1枠として数える。entry が失敗すれば予約を破棄し、成功すれば session が占有を引き継ぐ。

**テスト:** `PopulationCapTests.Selecting_another_character_in_the_same_session_reuses_its_population_slot` は上限1の system で切替成功を検査する。`A_failed_character_switch_releases_its_reserved_slot_and_allows_retry` は hook 失敗時の旧 session/online 状態と予約枠返却、その後の再試行を検査する。既存の `Simultaneous_entries_into_the_last_room_admit_exactly_one` も同クラスで通過した。修正前の切替テストは full-system の user error で失敗した。

## P2: 選択時の subscription-time 通知欠落

**確認根拠:** EveJS は `server/src/services/character/charService.js:1531-1547` で選択処理の先頭に `sendSubscriptionTimeUpdate` を呼び、`server/src/services/subscription/subscriptionNotices.js:90-103` は `OnSubscriptionTimeUpdateServer` を FILETIME または null で送る。V24.01 の `eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:22-26,145-163` はこの event を購読して Omega/Alpha 表示を更新する。旧 `SelectCharacterID` には同等の通知がなかった。

**修正:** 共有 subscription state owner の `SubscriptionMgrService.AnnounceSubscriptionTime` を Host から `CharUnboundMgrService` に渡し、選択ハンドラの最初の処理として呼ぶ。Omega の終端 FILETIME または Alpha/期限切れの `None` を送り、clone grade 通知は重複させない。

**テスト:** Host 登録を通す `CharacterHostTests.Host_character_selection_sends_the_subscription_time_before_the_session_change` は Omega の time 通知、`OnCharacterSelected`、session change packet の順序と packet 種別を検査する。`Host_character_selection_tells_alpha_and_expired_accounts_none_even_when_selection_fails` は Alpha 相当および期限切れ account について、選択失敗時も先頭に time=`None` のみが届くことを検査する。headless の login-to-character 比較も EveJS の選択先頭通知（`charService.js:1543-1545`）と V24 の PLEX panel handler（`plexPanel.py:160-163`）に合わせ、`OnSubscriptionTimeUpdateServer`、`OnCharacterSelected` の順を明示してから記録済み session/skill packet を照合する。修正前の Host テストでは subscription-time 通知が欠落して失敗した。

## 検証記録

- DB `eve_p14` 上で CharacterSelect の `CharUnboundMgrTests`、`PopulationCapTests`、`CharacterLogoffTests` を Release 実行: 56 passed、0 failed、0 skipped。
- Host の `CharacterHostTests`、`SubscriptionTransitionsTests`、`PopulationCapHostWireTests`、`P0CharacterClientWireTests` を Release 実行: 55 passed、0 failed、1 skipped。skip は `RealAbyssalSimPgFact` で、`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` が未設定のため。成功件数には含めていない。
- `Eve.Services.Tests.SessionChangeTests`: 11 passed、0 skipped。
- `Eve.Services.Core.Tests.OmegaStateTests`: 8 passed、0 skipped。
- `Eve.Services.Core.Tests.Wave1GoldenTests`: 52 passed、0 skipped。
- Host の `NewEdenStoreHostTests` と `ServiceNotificationHostWireTests`: 16 passed、0 skipped。
- 変更した .NET source/test projects の Release build はすべて警告0、error 0。
- 未確認: グラフィカルな V24.01 client での実通信、EveJS を起動した scenario 実行、および GitHub Actions の結果。
