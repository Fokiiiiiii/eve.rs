# 契約・取引・ウォレットの深掘り修正記録（cx4）

## この変更で確認・修正したこと

### 個人宛て請求の支払い

V24.01 の `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:125-127` は、債務者がログイン中のキャラクターなら `billMgr.CharPayBill(bill.billID)` を呼ぶ。EveJS の `server/src/services/account/billMgrService.js:314-324` はセッションのキャラクターで `payBillFromCharacter` を実行し、成功後に `null` を返す。失敗時は `:124-146` で残高不足を `NotEnoughMoney(amount,balance)`、それ以外を `CustomNotify` にする。

変更前の eve.rs は `CharacterServiceList.cs:71-76` で `BillMgrService` を登録していたが、支払い provider を渡していなかった。事前追加した PostgreSQL Host wire テスト `Character_bill_payment_settles_through_the_registered_host_route` は、実際の登録サービスを経由したとき `BILL_NOT_PAYABLE` で失敗し、EveJS と異なって残高も請求状態も変わらないことを確認した。

`CorporationStore` に `character_bills` の永続化、操作キーの内容照合、個人ウォレット決済を追加した。支払いは請求行をロックし、ウォレット仕訳・法人債権者への入金・paid 状態・transactional outbox を一つの PostgreSQL transaction で確定する。支払済み行を再度処理せず、同時要求でも一件だけを成功させる。Host の `CharPayBill` は成功時 `None`、残高不足時は EveJS と同じ `NotEnoughMoney` の置換値を返す。

`bill.issued` / `bill.paid` outbox イベントから通知メールボックスへ durable notification を書く。V24.01 の `notificationUtil.py:617-640,1102-1103` が使う bill row fields、filetime long、Bills group `2`、通知種別 `9`（発行）と `12`（個人宛て支払い）は EveJS の `billNotifications.js:34-47,50-89,96-115` と `notificationConstants.js:3-7,22-30` に照合した。通知 data に `externalID` も含めた。V24.01 は war / alliance-maintenance bill の表示時に `notificationUtil.py:629-634` でこの field を必須参照する一方、EveJS の共有 builder `billNotifications.js:36-47` は省いているため、クライアント表示を優先した互換差である。Host 回帰テストは wire 成功応答、残高不足の wire error と値、残高・paid 状態、再試行、二重支払い防止、並行要求、永続通知の型・グループ・bill ID・`externalID` を検証する。

支払いを eve.rs の wallet transaction と outbox が共有する PostgreSQL transaction にまとめた点は、EveJS の `billRuntimeState.js:613-641,723-793` の段階的 payment saga と実装方式が異なる。状態遷移、仕訳、再試行の結果を保ちながら、途中だけ debited / credited になる状態を作らないための設計差である。

### 契約、直接取引、その他のウォレット範囲

この変更では契約や直接取引のコードは変更していない。既存の証拠記録は [deepfix-contracts.md](service-audit/deepfix-contracts.md)、[audit-fixes-trade.md](audit-fixes-trade.md)、[trade-direct-handoff-CX11.md](trade-direct-handoff-CX11.md) にある。これらの記録は自己契約の受諾・入札、courier期限後の担保処理、取引窓口での item custody と通知など、以前の Host/DB 回帰で確認した範囲を示す。

ウォレット送金、corporation division、office rent 決済通知の記録は [deepfix-wallet-journal.md](service-audit/deepfix-wallet-journal.md) にある。同記録が残している journal entry type ごとの説明と市場税・注文約定の完全な追跡は、この変更では完了扱いにしない。

## 実行して確認した結果

- 変更前の赤テスト: 個人請求の登録 Host route が `BILL_NOT_PAYABLE` を返した。
- 通知 payload の `externalID` assertion も追加直後に失敗し、V24.01 が `FormatBillNotification` で参照する field を出していないことを実 Host 経路で再現した後、修正した。
- Release build: `Eve.Host.Tests` と `Eve.Corporations.Tests` を `-warnaserror` で build。参照先の変更済み production projects も含め、警告 0、エラー 0。
- `eve_p4` 上の個人請求 Host wire slice: 2 passed、0 failed、0 skipped。成功、残高不足、再試行、二重支払い防止、並行支払い、通知永続化を含む。
- `Activity_alpha_injector_and_bill_rpcs`, `BillMgrHostRpcTests`, 個人請求の Host tests は 4 passed、0 failed、0 skipped（上記2件を含む）。
- `eve_p4` 上の CorporationStore 参照クラス: `AccountRpcTests`, `AllianceTests`, `BillMgrServiceTests`, `CorpStationMgrTests`, `CorporationProtocolTests`, `CorporationTests`, `ItemLockTests`, `MembershipTests`, `StandingMgrServiceTests`, `WarRegistryTests` は 144 passed、0 failed、0 skipped。初回に migration 一覧 assertion が追加 migration を含まず失敗したため、期待一覧へ `018_character_bills` を明記して同じ範囲を再実行した。最新 `origin/main` には `018_medal_award_visibility` が入ったため、統合後の請求 migration は `019_character_bills` に採番し直した。

## 未確認の範囲

- 実際の V24.01 graphical client、実クライアント packet trace、通知 UI 上の表示は確認していない。
- EveJS の server process を起動して同一シナリオを実行していない。比較は指定された EveJS ソース経路の追跡による。
- この変更は個人請求を作る generic store API を提供する。既存 Host composition には個人請求を発行する production producer や、個人請求を列挙する RPC が見当たらず、その発行・発見経路は未実装のまま。
- corporation/alliance debtor の支払いレシート通知、すべての請求生成元と期限・更新・失効処理はこの変更で検証・修正していない。
- 契約の検索・絞り込み、入札順・延長などの残存経路、直接取引全体はこの変更では再実行していない。既存 handoff の範囲を超えて完了とは判定しない。
- 全 journal reference / description 種別、derived tax rows、market order/fill と sales tax の caller-to-wallet 経路は未確認。市場税の互換性判断は既存 deviation 記録に残る。
