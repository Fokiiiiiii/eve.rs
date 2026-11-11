# 契約・取引・ウォレットの深掘り修正記録（cx4）

## この変更で確認・修正したこと

### 個人宛て請求の支払い

V24.01 の `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:125-127` は、債務者がログイン中のキャラクターなら `billMgr.CharPayBill(bill.billID)` を呼ぶ。EveJS の `server/src/services/account/billMgrService.js:314-324` はセッションのキャラクターで `payBillFromCharacter` を実行し、成功後に `null` を返す。失敗時は `:124-146` で残高不足を `NotEnoughMoney(amount,balance)`、それ以外を `CustomNotify` にする。

変更前の eve.rs は `CharacterServiceList.cs:71-76` で `BillMgrService` を登録していたが、支払い provider を渡していなかった。事前追加した PostgreSQL Host wire テスト `Character_bill_payment_settles_through_the_registered_host_route` は、実際の登録サービスを経由したとき `BILL_NOT_PAYABLE` で失敗し、EveJS と異なって残高も請求状態も変わらないことを確認した。

`CorporationStore` に `character_bills` の永続化、操作キーの内容照合、個人ウォレット決済を追加した。支払いは請求行をロックし、ウォレット仕訳・法人債権者への入金・paid 状態・transactional outbox を一つの PostgreSQL transaction で確定する。支払済み行を再度処理せず、同時要求でも一件だけを成功させる。Host の `CharPayBill` は成功時 `None`、残高不足時は EveJS と同じ `NotEnoughMoney` の置換値を返す。

`bill.issued` / `bill.paid` outbox イベントから通知メールボックスへ durable notification を書く。V24.01 の `notificationUtil.py:617-640,1102-1103` が使う bill row fields、filetime long、Bills group `2`、通知種別 `9`（発行）と `12`（個人宛て支払い）は EveJS の `billNotifications.js:34-47,50-89,96-115` と `notificationConstants.js:3-7,22-30` に照合した。通知 data に `externalID` も含めた。V24.01 は war / alliance-maintenance bill の表示時に `notificationUtil.py:629-634` でこの field を必須参照する一方、EveJS の共有 builder `billNotifications.js:36-47` は省いているため、クライアント表示を優先した互換差である。Host 回帰テストは wire 成功応答、残高不足の wire error と値、残高・paid 状態、再試行、二重支払い防止、並行要求、永続通知の型・グループ・bill ID・`externalID` を検証する。

支払いを eve.rs の wallet transaction と outbox が共有する PostgreSQL transaction にまとめた点は、EveJS の `billRuntimeState.js:613-641,723-793` の段階的 payment saga と実装方式が異なる。状態遷移、仕訳、再試行の結果を保ちながら、途中だけ debited / credited になる状態を作らないための設計差である。

### 契約、直接取引、その他のウォレット範囲

既存の証拠記録は [deepfix-contracts.md](service-audit/deepfix-contracts.md)、[audit-fixes-trade.md](audit-fixes-trade.md)、[trade-direct-handoff-CX11.md](trade-direct-handoff-CX11.md) にある。これらの記録は自己契約の受諾・入札、courier期限後の担保処理、取引窓口での item custody と通知など、以前の Host/DB 回帰で確認した範囲を示す。

#### オークションの延長、buyout と請求

V24.01 の `/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/contracts.py:562-667` は `contractProxy.PlaceBid` を呼び、`:673-677` は `FinishAuction(contractID, isIssuer)` を別 RPC として呼ぶ。詳細 UI は `contractsDetailsWnd.py:591-602` で落札者の item 受取と発行者の ISK 受取を別操作にしている。EveJS の `contractAuctionRuntime.js:134-148` は通常 bid ごとに `max(既存期限, 入札時刻+15分)` を記録し、buyout では期限を入札時刻より前にする。`:58-80` は bid と debit/refund を確定して scheduler を更新するが、status や item custody を完了させない。`contractAuctionClaims.js:63-110` は期限後に各側の `FinishAuction` を認証し、`:34-48` で issuer/winner の claim に従って status を段階的に更新する。EveJS の `contractRuntimeState.js:2036-2038` も auction は `expires <= now` で終了と扱う。

変更前の eve.rs は通常 bid で期限も永続 expiry job も更新せず、期限と同時刻の auction bid を許可していた。また buyout bid で直ちに status=4、issuer への支払い、落札者への item 移動を行い、クライアントが次に送る `FinishAuction` より先に契約を完了させていた。追加した PostgreSQL Host wire 回帰は、変更前に期限更新が欠けることと buyout の即時完了（期待 status=0、実値 status=4）を個別に再現した。

修正後は auction bid の受理条件を `expired <= now` にし、通常 bid の deadline と durable scheduler job を同一 transaction で更新する。期限が既に 15 分より先なら短縮しない。buyout は bid と prior-bid refund を確定した後に deadline を過去へ進めて job を取り消すが、status=0、acceptor=0、落札額の escrow debit、出品 item の escrow custody を保つ。発行者の `FinishAuction` は ISK を受け取り status=2 にし、落札者の `FinishAuction` は item を移し status=4 にする。Host wire 回帰は二つの account session からこの順序を実行し、作成・outbid・buyout・完了通知の outbox 順、各中間残高、item custody、最終 owner を照合する。

#### ブループリント検索と詳細行

V24.01 の `eve/common/script/util/contractscommon.py:65-66` は BPO hint=1、BPC hint=2 と定義し、`contractsearch.py:1240-1248,1367` は検索時に hint を取得して `contractProxy.SearchContracts` に渡す。`contractsDetailsWnd.py:642-651,727-738` は各 item の `copy`, `licensedProductionRunsRemaining`, `materialLevel`, `productivityLevel` を表示に使う。EveJS の `contractRuntimeState.js:4246-4253` は category 9 の item と `copy` を照合し、`:832-848` は同じ blueprint 属性を item row に出す。

変更前の Host read model は非ゼロ search hint を拒否し、contract item row は inventory blueprint 属性を返していなかった。追加した Host/DB wire test は、BPO 検索が未対応として返る赤を先に確認した。現在は SDE category 9 と authoritative `inventory.items.blueprint_copy` を結合して BPO/BPC を絞り、検索 bundle と `GetContract` 詳細行の双方に copy/run/ME/TE を載せる。DB 回帰は BPO/BPC の双方と結果行・詳細行の値を検証する。

ウォレット送金、corporation division、office rent 決済通知の記録は [deepfix-wallet-journal.md](service-audit/deepfix-wallet-journal.md) にある。同記録が残している journal entry type ごとの説明と市場税・注文約定の完全な追跡は、この変更では完了扱いにしない。

### PI 税と wallet journal

V24.01 の `planet/importExportUI.py:537-550` は customs office の bound inventory で `ImportExportWithPlanet(spaceportPinID, importData, exportData, taxRate)` を呼び、`eve/common/script/planet/entities/spaceportPin.py:70-86` は import/export 税額を同じ UI の rate で計算する。`accountsvc.py:108-130` は個人・法人 wallet の月指定を `GetTransactions(accountKey, year, month, isCorpWallet)` へ渡す。`transactionsPanel.py:34-40,126-134` は各 transaction の `entryTypeID`, `referenceID`, `ownerID1`, `ownerID2` を表示・owner cache に使用し、`walletEntryFormat.py:124-141` は Planetary import / export / construction の `referenceID` を planet として解釈する。

Client の `eveplanet/taxRate.py:55-64` は player corporation の税率と高セキュリティ時の CONCORD 税率を別 owner ID の成分として返す。CCP の [Customs Offices](https://support.eveonline.com/hc/en-us/articles/203269921-Customs-Offices) と [Planetary Interaction](https://support.eveonline.com/hc/en-us/articles/203269871-Planetary-Interaction) は owner corporation と CONCORD に払う二税、および別々の wallet journal entry を説明する。pin 3396210 の SDE `npcCorporations.jsonl:125` は owner ID 1000125 を CONCORD とする。

EveJS の `planetaryCustomsSettlementState.js:260-285` は import total を一行、export total を同額の二行に分ける。`:724-735` は debit 行を `ownerID1=character`, `ownerID2=office owner`, `referenceID=planetID` とし、`:991-1024` は各 debit leg 全額を owner corporation へ credit する。`accountService.js:573-624,817-846` は巨大な customs office item ID を `ownerID2` にできない理由、planetary journal row fields、月単位の取得を明示する。

eve.rs は数値を作らず、合算した税額と owner-only 税額を既存の SDE 計算から取得する。各 ref 96/97 の個人 debit を office-owner portion と CONCORD portion に分けて記録し、法人 wallet には owner portion だけを同一 DB transaction 内で credit する。支払い各行は Unix ms、planet を `referenceID`、owner corporation または SDE 上の CONCORD (1000125) を `ownerID2` にする。EveJS の均等な export 二分割と full-total owner credit は、CCP の納税先説明と区別できないため採用せず、計算済み成分ごとの額にする。

`PlanetMgrService.ChangeCoreAsync` と `ImportExportCoreAsync` は simulation の保存時刻に FILETIME を使う一方、wallet の `TimeMs` は Unix ms である。以前は同じ FILETIME を仕訳へ渡し、月指定 `account.GetTransactions` から行が消えていた。PI Host 回帰は登録済み `account.GetTransactions` 経由で 96/97 journal の件数、owner IDs、planet references、owner credit を確認する。また construction ref 98 の `referenceID` も以前は 0 だったため、同じ Host query から planet ID を確認する。

## 実行して確認した結果

- 変更前の赤テスト: 個人請求の登録 Host route が `BILL_NOT_PAYABLE` を返した。
- 通知 payload の `externalID` assertion も追加直後に失敗し、V24.01 が `FormatBillNotification` で参照する field を出していないことを実 Host 経路で再現した後、修正した。
- Release build: `Eve.Host.Tests`, `Eve.Corporations.Tests`, `Eve.Services.Station`, `Eve.Services.Station.Tests` はすべて警告 0、エラー 0。Station.Tests の初回 `--no-restore` build は assets file がなく失敗し、restore 後の build は成功。
- `eve_p4` 上の個人請求 Host wire slice: 2 passed、0 failed、0 skipped。成功、残高不足、再試行、二重支払い防止、並行支払い、通知永続化を含む。
- merge 後の `Activity_alpha_injector_and_bill_rpcs`, `BillMgrHostRpcTests`, 個人請求、登録 corporation medal/office Host tests は 6 passed、0 failed、0 skipped。
- merge 後の `eve_p4` 上の CorporationStore 参照クラス: `AccountRpcTests`, `AllianceTests`, `BillMgrServiceTests`, `CorpStationMgrTests`, `CorporationOfficeTests`, `CorporationProtocolTests`, `CorporationTests`, `ItemLockTests`, `MembershipTests`, `StandingMgrServiceTests`, `WarRegistryTests` は 159 passed、0 failed、0 skipped。初回 migration 一覧 assertion は追加 migration を含まず失敗したため、期待一覧を修正した。最新 `origin/main` に `018_medal_award_visibility` が入ったため、統合後の請求 migration は `019_character_bills` とし、migration 一覧と DB-backed class filter を再実行した。
- `eve_p4` 上の `StationRecordTests` は 1 passed、0 failed、0 skipped。V24 station cache fixture は office rental cost の値と wire 型を含め照合された。
- 変更前の PI Host journal 回帰は、月指定 `GetTransactions` が FILETIME 混入のため 0 行を返し失敗した。後続の赤テストで construction ref 98 の `referenceID=0` と高セキュリティ customs の二税内訳不足も個別に確認した後、planet reference と owner/CONCORD の各 tax component を加えた。旧 Host assertion が freeform reason や import の ref 97 を期待していたため、V24.01 の ref 96/97 に照らして更新した。
- `eve_p4` の PI / wallet Host 参照クラス (`CustomsOfficeHostTests`, `AccountWalletHostRpcTests`, `WalletAuditHostRpcTests`, `PlanetOwnerConcurrencyHostWireTests`) は 7 passed、0 failed、0 skipped。
- `eve_p4` の `CorporationProtocolTests` は 55 passed、0 failed、0 skipped。`Eve.Services.Wallet.Tests` の `TransactionHistoryTests` は 1 passed、0 failed、0 skipped。
- auction deadline/buyout と blueprint 検索を追加した後、登録 Host 経路を含む `Eve.Host.Tests` の `FullyQualifiedName~Contract` slice は Release で 42 passed、0 failed、0 skipped（`eve_p4`）。内訳には通常入札の期限延長と exact-expiry 拒否、issuer/winner 別の buyout claim、BPO/BPC 検索と詳細 wire 行が含まれる。buyout Host テスト単体も 1 passed、0 failed、0 skipped。
- 関連 Host/Core project の Release build は warnings-as-errors で 0 warning、0 error。`ContractProxyServiceTests` は Release で 23 passed、0 failed、0 skipped。`git diff --check` も通過した。
- 変更前に auction deadline test は期限更新なし、buyout test は `FinishAuction` 前に status=4 となる挙動を再現した。BPO Host test は search hint 未対応の応答を再現した。blueprint 関連の最初の DB slice では fixture inventory schema がなく失敗したため fixture migration を追加し、当該 class と上記 contract slice を再実行して通過した。

## 未確認の範囲

- 実際の V24.01 graphical client、実クライアント packet trace、通知 UI 上の表示は確認していない。
- EveJS の server process を起動して同一シナリオを実行していない。比較は指定された EveJS ソース経路の追跡による。
- この変更は個人請求を作る generic store API を提供する。既存 Host composition には個人請求を発行する production producer や、個人請求を列挙する RPC が見当たらず、その発行・発見経路は未実装のまま。
- 実 V24 graphical client、実 packet trace、EveJS server process での同一 PI 操作は動かしていない。PI Host 回帰は `ImportExportWithPlanet` の production service 実装をテスト用 space provider から bound call し、journal read は登録 Host service を通す。inventory broker の production space registration を使うライブ PI walk はこの変更後に未実行。
- corporation/alliance debtor の支払いレシート通知、すべての請求生成元と期限・更新・失効処理はこの変更で検証・修正していない。
- 今回は auction expiry/buyout claim と blueprint 検索の差分を Host/DB 経路で検証した。ほかの全 contract search filter 組合せ、courier expiry/claim、item exchange の全ての collateral/reward・期限・retry 組合せ、direct trade 全体を同じ作業で網羅したとは判定しない。既存 handoff の範囲を超える経路は残る。
- auction winner claim の ISK credit と EveJS の withheld sales-tax calculation (`contractAuctionClaims.js:84-98`) の対応、CCP/V24 が要求する税率・journal 行は追跡未完了で、互換性を確認・修正した扱いにしない。
- PI 以外の全 journal reference / description 種別、derived tax rows、market order/fill と sales tax の caller-to-wallet 経路は未確認。市場税の互換性判断は既存 deviation 記録に残る。
- corporation wallet division ごとの全 contract/market/bill entry routing、全請求種別の期限・更新・失効・領収通知、wallet transaction retry/restart 横断は今回の追加差分では再検証していない。
