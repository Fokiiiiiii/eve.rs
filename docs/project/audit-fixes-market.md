# 市場サービス監査の修正記録

対象: `/home/user/reports/90-audit-eve-services-market-r14.md`

## 確認済みの所見と修正

### P1 — Buy Multi の完了行と完了通知

**所見を確認しました。** `BuyMultipleItems` は duration 0 で取引します。旧実装は永続注文の `Created` 行だけを応答に含めており、即時約定と未約定数量の行が欠けていました。V24.01 クライアントは応答の `orderID` を追跡し、`Expiry` 通知で残数量を買い物リストへ戻すかウィンドウを閉じます。応答と通知の前後順も、通知を一時保留して `VerifyExpiredOrders` で処理するコードで確認しました。

確認根拠:

- V24.01: `eve/client/script/ui/services/marketsvc.py:270-273`; `eve/client/script/ui/shared/market/buyMultiFromBase.py:296-311,558-602`
- EveJS: `server/src/services/market/marketProxyService.js:1015-1092,7014-7076`
- eve.rs 修正: `dotnet/src/Eve.Market/MarketTrading.cs:24-33,459-495`; `dotnet/src/Eve.Services.Market/MarketProxyService.cs:613-627`; `dotnet/src/Eve.Services.Market/MarketNotifier.cs:54-70`

即時行は入力行ごとに実行時 ID、要求数量、未約定数量、duration 0 を持ちます。DB の注文として保存しません。処理後に同じ行を `Expiry` の一括 `OnOwnOrdersChanged` 通知と RPC 応答の両方へ載せます。操作の再送では、保存済みレシートに完了行がなければ行を生成してレシートへ書き戻します。

回帰テストは Host 登録経由で全約定・部分約定・未約定を一度に実行し、3 行の数量、永続注文がないこと、残高、応答と通知の一致を確認します。RPC テストは後続の資金不足で全体をロールバックし、通知も送らないことを確認します。

### P2 — sell エスクロー後の在庫キャッシュ

**所見を確認しました。** sell 出品では元 hangar のスタックが減るか、市場エスクローへ移ります。旧レシートはこの変更を持たず、`MarketNotifier` が buyer の配送だけを通知していました。

確認根拠:

- V24.01: `eve/client/script/environment/invCache.py:219-260,279-303,533-562`; station hangar の `GetInventory` と bound inventory の `List` は同ファイル `501-531,1131-1151`
- EveJS: sell エスクロー後の出品者通知と corporation fan-out は `server/src/services/market/marketProxyService.js:5975-5984`; 1 行ごとの出品者 `OnItemChange` は `server/src/services/raffles/raffleInventory.js:131-141` と `server/src/services/character/characterState.js:2701-2742`
- EveJS の office 購読・Query 権限と境界投影: `server/src/services/inventory/invBrokerService.js:6139-6199,6202-6237,6240-6378`
- eve.rs 修正: `dotnet/src/Eve.Market/MarketTrading.cs:22-33,459-469,822-848`; `dotnet/src/Eve.Services.Market/MarketNotifier.cs:58-100`; `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:89-148,242-255,672-708`; Host 結線は `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:41-52` と `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:41-51`

レシートは sell の移動前後を保存し、出品後に売り手へ `OnItemChange` を送ります。全量移動、部分スタック、アイテムが消費された場合を扱います。注文取消と期限切れで hangar に戻る行も同じ経路で通知し、seller の返却を buyer 配送として扱いません。通知は出品者の注文状態通知の後です。

Corp hangar では、station hangar の bound object に対して `List` / `ListByFlags` を実行したセッションだけを購読対象にします。通知時にも corporation 所属、office、Query 権限、division flag を検査します。一方の場所だけ閲覧できる場合は、その側の削除または追加として投影し、非表示の division や escrow 場所を通知へ含めません。Host テストは実 office、SAG1 Query の閲覧者、SAG2 だけに Query 権限を持つ閲覧者でこの境界を確認します。

監査報告の修正案は seller に `OnItemsChanged` を送る形でした。EveJS の該当処理は `notifyInventoryChangesToCharacter` から 1 行ずつ `syncInventoryItemForSession` を呼び、`OnItemChange` を送ります。実装はその wire shape に合わせています。buyer の新規配送に使う `OnItemsChanged` は引き続き buyer 側で送ります。

## 誤りだった所見

なし。P1 と P2 の状態不整合はいずれもソース、V24.01 クライアント、EveJS を照合して確認しました。

## 検証済みと未確認

**検証済み:** PostgreSQL の専用 DB `eve_p3` を使う Host／RPC／Inventory 回帰テストで、wire 行、通知順、数量、残高、注文状態、権限の境界、取消後の返却を確認しました。実クライアントの呼び出しとキャッシュ更新は上記の V24.01 ソースで追跡しました。

**未確認:** V24.01 グラフィカルクライアントでの実操作と UI 表示、main ブランチへの push 後の GitHub Actions 結果。この記録時点で対象差分のテストスイートはスキップなしで実行しています。
