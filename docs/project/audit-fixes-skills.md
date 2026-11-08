# Eve.Services.Skills R14 監査所見の確認と修正

監査報告 `/home/user/reports/90-audit-eve-services-skills-r14.md` の各所見を、作業ブランチの実装、V24.01 クライアント全体 (`/home/user/eve-logic`)、EveJS (`/home/user/evejs-261028.1`) と照合した。

## 確認済みの不具合と修正

### P1: Expert System 失効時の仮想スキル差分と Dogma 同期

期限切れ行は有効期限を過ぎた時刻に削除されるため、削除前の付与状態を現在時刻で評価すると、失効対象スキルを変更前状態に含められなかった。期限処理は現在時刻を一度取得し、キャラクター単位のトランザクションで保存済み行から変更前の付与状態と訓練済みスキルを取り、期限到来行を削除する。コミット後、既存の `Told` 経路で Expert System 一覧、スキル差分、Dogma 更新、失効通知を送る。複数行が同時に期限切れした場合もキャラクターごとに一度だけスキル差分と Dogma 更新を行い、各種別の期限切れを記録する。

根拠: CCP は期限切れ時に一時的なスキルとレベルへのアクセスが取り除かれると明記している ([Expert Systems – Live Now](https://www.eveonline.com/news/view/expert-systems-now-live))。実装は `dotnet/src/Eve.Services.Skills/ExpertSystemMgrService.cs` の `ExpireDueAsync` / `Told`、`dotnet/src/Eve.Skills/ExpertSystems.cs` の `GrantsFromEntries`。V24.01 のスキルキャッシュ更新入口は `eve/client/script/ui/services/skillsvc.py:159-170`、Dogma 脳更新の受信箇所は `eve/client/script/dogma/clientDogmaLocation.py:1313-1327`。Expert System サービスは `OnExpertSystemsUpdated` で一覧を更新する (`expertSystems/client/service.py:14-16,46-52`)。EveJS は削除前のスキルマップを使って同じ差分を通知する (`server/src/services/skills/expertSystems/expertSystemExpiryScheduler.js:196-216`, `expertSystemNotifications.js:47-73`)。

### P2: 3 の倍数でない小型インジェクター合成

クライアントのメニューはスタック全数を送り、クライアントサービスは 3 個未満だけを拒否する (`eve/client/script/ui/services/menusvc.py:388-391`, `eve/client/script/ui/services/skillsvc.py:1014-1017`)。EveJS は整数商の大型インジェクターを生成し、使用した 3 個単位だけを消費する (`server/src/services/skills/trading/skillTradingRuntime.js:983-1018`)。eve.rs は 3 個未満だけを拒否し、4 個・5 個の場合は大型 1 個を作って余りを元スタックに残す。

### P2: インジェクター分割・合成の在庫通知

クライアントの `invCache.OnItemChange` は数量とロケーション差分を反映し、該当コンテナと UI に通知する (`eve/client/script/environment/invCache.py:229-304`)。EveJS は消費元と新規スタックの両方を在庫セッションへ同期する (`server/src/services/skills/trading/skillTradingRuntime.js:966-970,1026-1030`)。eve.rs は DB コミット後に、元スタックの削除または数量差分と新規スタック行の `OnItemChange` を送る。削除時は移動元ロケーション、数量変更時は変更前数量を差分に含める。

これら P2 の変更は `dotnet/src/Eve.Services.Skills/SkillHandlerService.Injection.cs` に実装し、合成の端数、分割の全量・部分量、通知の行データと差分、同一スタックの並行合成・再試行、および再起動後の永続状態を Host 結合テストに追加した。回帰テストは修正前に先に追加して実行し、期限切れのスキル差分/Dogma 通知欠落、4個合成の拒否、分割・合成の在庫通知欠落を失敗として確認してから実装した。

## 誤検知として除外

### P1: スキル本注入後のスキル・在庫通知欠落

この所見は現在のブランチでは誤検知。`InjectSkillBooksAsync` はトランザクション後に `announcer.AnnounceSkillsChangedAsync` を呼び、消費または減数した本の `OnItemChange` を送っている (`SkillHandlerService.Injection.cs:26-87`)。V24.01 の注入 RPC は Dogma IM 経由 (`skillsvc.py:775-792`)、EveJS もスキル差分と在庫差分を通知する (`skillbookRuntime.js:264-273,489-522`)。既存の `CharacterHostTests.DogmaIM_skill_injection_consumes_the_skillbook_and_persists_the_skill_through_the_host` は実 Host の Dogma IM ルート、DB 状態、`OnServerSkillsChanged` / `OnSkillsChanged`、在庫行と差分、再起動後のスキルを確認する (`dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:1844-1882`)。この動作に変更は加えていない。

## 検証状況

### 確認済み

- クライアント側の RPC 呼び出し・通知受信箇所を `/home/user/eve-logic` 全体から検索して照合した。上記参照は該当箇所のファイルと行である。
- EveJS の期限切れ、スキル本注入、インジェクター分割・合成処理を実装から照合した。
- 実 GUI クライアントでは検証していない。EveJS の実行可能なエンドツーエンド比較もしておらず、挙動根拠はソース追跡である。
- `Eve.Host.Tests` の `SkillsHostTests`: 7件成功、失敗0、スキップ0 (merge 後)。
- `Eve.Host.Tests` の `CharacterHostTests`: 47件成功、失敗0、スキップ0 (merge 前)。
- `Eve.Skills.Tests` の `ExpertSystem` に一致するテスト: 7件成功、失敗0、スキップ0 (merge 後)。
- `Eve.Services.Skills.Tests` の `SkillHandlerTests`: 56件成功、失敗0、スキップ0 (merge 後、`eve_p3`)。
- Host RPC のクライアント wire 検証: 1件成功、失敗0、スキップ0。
- Host の composed RPC injector mutation テスト: 1件成功、失敗0、スキップ0 (`eve_p3`)。
- 既存のスキル本注入 Host 回帰テスト: 1件成功、失敗0、スキップ0 (`eve_p3`, merge 後)。
- `Eve.Skills`, `Eve.Services.Skills`, `Eve.Host.Tests` の Release build: すべて成功、警告0 (最新 Host テスト変更後も Host.Tests を再 build)。

### 未確認

- 実 GUI クライアントでの画面表示・Dogma セッション挙動。
- EveJS と eve.rs を同一シナリオで実行したライブ比較。
