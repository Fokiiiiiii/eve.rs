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

#### オークションの売却税、預託金と wallet journal

V24.01 の `createContractWnd.py:627-669` は Accounting level から税率を出し、auction の表示を `PercentOfSalesPriceAtTax` に切り替える。最大額は buyout を元に表示するが、実際の売却税額は売却成立時に確定する。`contracts.py:673-677` は seller/winner の claim を別々の `FinishAuction` RPC にする。`appConst.py:701-723` は auction sale=67、sales tax=73、deposit=74、deposit sales tax=75、deposit refund=82 を定義する。

EveJS の `contractRuntimeState.js:1150-1152,1184-1190` は broker fee と deposit を作成時に扱い、Accounting level を記録する。`contractAuctionClaims.js:84-108` は issuer claim 時に実際の最高入札額と保存した Accounting level から税を算出し、winner claim とは分離して売却代金を処理する。`:99-104` の no-bid 経路はアイテム返却だけを作り、EveJS の runtime policy コメントは no-bid 時の deposit forfeiture を明記する。

変更前の登録 Host 経路は `CreateContract` 時に auction `SalesTax` も wallet から引いていた。新しい PostgreSQL Host wire 回帰 `ContractAuctionTaxHostWireTests.cs:18-218` は、修正前に creation row の期待値 `sales_tax_isk=0` に対して `10000` が保存される赤を再現した。現在は Host が税を作成時に請求せず、Accounting level を契約 row に保存する。seller claim 時に bid を ref 67、bid からの税を ref 73、bid で賄えない税を既払 deposit から ref 75、deposit 全額の返却を ref 82 として仕訳する。deposit 由来の税は作成時に wallet から既に引かれた deposit の消費として扱い、claim 時に二重 debit しない。税は bid と deposit の合計を上限とする。no-bid claim はアイテムを issuer に戻し、deposit を返さない。

EveJS は seller proceeds を単一の wallet leg にまとめ、`contractRuntimeState.js:735-744` の default ref=1 (`PLAYER_TRADING`) を使う。また `contractAuctionClaims.js:92-98` は税の cap より前に net proceeds を計算する。この journal 粒度と順序は CCP client の `appConst.py` が定義する専用 contract refs と合わず、低額 bid では cap 後の税額と payout が一致しない。そこで eve.rs は gross auction sale、税、deposit tax、refund を専用 ref IDs へ分けた。`ContractReadModelTests.cs` の低 bid ケースは sale 1000、sale tax 1000、deposit tax 50、deposit return 50 の各仕訳と、残高が作成時の既払 deposit を二重控除しないことを検証する。Host wire test は Accounting 4 の 10M bid から 60K を課税し、売却金・税・deposit 返却・no-bid forfeiture を実 DB で検証する。`Corporation_auction_uses_its_selected_wallet_division_and_contract_journal_refs` は法人 division 1002 の broker/deposit refs 80/81 と、bidder corporation division 1003 の bid ref 77 を `eve_p4` で確認する。

#### Courier 作成時の売却税

V24.01 は contract type 3 を courier とする (`eve/common/lib/appConst.py:1041-1043`)。共通 `CalcContractFees` は courier に 0.1 multiplier を使い売却税額も計算する (`eve/common/script/util/contractscommon.py:84-105`) が、作成画面は courier では `salesTaxText` を `NoneParen` に置き換え、sales-tax row に額を表示しない (`eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:624-669`)。同じ確認画面では broker fee と deposit は計算済みの値を表示する (`:643-653`)。CreateContract が RPC に送る `ContractStruct` には fee/tax amount field がなく、契約条件だけを渡す (`createContractWnd.py:853-889`)。

EveJS は courier type=3 と定義し (`server/src/services/contracts/contractRuntimeState.js:79-83`)、public 非auction契約の作成時には wallet leg を作らず、private contract の flat broker fee だけを別扱いする (`:1385-1407,1969-1979`)。そのため EveJS は V24 画面に出る public courier broker fee/deposit も精算しない。Host はその二項目と reward を従来どおり精算するが、非表示の売却税は作成時に請求しないようにした。公式 CCP courier help は collateral と失敗/成功時の処理を説明するが、courier tax amount の backend settlement は記載していないため、この判断は V24.01 の作成画面・RPC shape と EveJS runtime を合わせたもの。

登録 Host + PostgreSQL 回帰 `Registered_public_courier_creation_does_not_charge_sales_tax` は type 3 の実 `contractProxy.CreateContract` call から契約行、reward/broker/deposit journal refs、wallet balance、package item, expiry job, `OnContractCreated` outbox event を確認する。修正前に row の期待 `sales_tax_isk=0` に対して 20,000 ISK の保存を再現した。修正後は RPC mapper で auction と courier の作成時税を 0 にし、item-exchange の表示・課金は維持する。

ウォレット送金、corporation division、office rent 決済通知の記録は [deepfix-wallet-journal.md](service-audit/deepfix-wallet-journal.md) にある。同記録が残している journal entry type ごとの説明と市場税・注文約定の完全な追跡は、この変更では完了扱いにしない。

### PI 税と wallet journal

V24.01 の `planet/importExportUI.py:537-550` は customs office の bound inventory で `ImportExportWithPlanet(spaceportPinID, importData, exportData, taxRate)` を呼び、`eve/common/script/planet/entities/spaceportPin.py:70-86` は import/export 税額を同じ UI の rate で計算する。`accountsvc.py:108-130` は個人・法人 wallet の月指定を `GetTransactions(accountKey, year, month, isCorpWallet)` へ渡す。`transactionsPanel.py:34-40,126-134` は各 transaction の `entryTypeID`, `referenceID`, `ownerID1`, `ownerID2` を表示・owner cache に使用し、`walletEntryFormat.py:124-141` は Planetary import / export / construction の `referenceID` を planet として解釈する。

Client の `eveplanet/taxRate.py:55-64` は player corporation の税率と高セキュリティ時の CONCORD 税率を別 owner ID の成分として返す。CCP の [Customs Offices](https://support.eveonline.com/hc/en-us/articles/203269921-Customs-Offices) と [Planetary Interaction](https://support.eveonline.com/hc/en-us/articles/203269871-Planetary-Interaction) は owner corporation と CONCORD に払う二税、および別々の wallet journal entry を説明する。pin 3396210 の SDE `npcCorporations.jsonl:125` は owner ID 1000125 を CONCORD とする。

EveJS の `planetaryCustomsSettlementState.js:260-285` は import total を一行、export total を同額の二行に分ける。`:724-735` は debit 行を `ownerID1=character`, `ownerID2=office owner`, `referenceID=planetID` とし、`:991-1024` は各 debit leg 全額を owner corporation へ credit する。`accountService.js:573-624,817-846` は巨大な customs office item ID を `ownerID2` にできない理由、planetary journal row fields、月単位の取得を明示する。

eve.rs は数値を作らず、合算した税額と owner-only 税額を既存の SDE 計算から取得する。各 ref 96/97 の個人 debit を office-owner portion と CONCORD portion に分けて記録し、法人 wallet には owner portion だけを同一 DB transaction 内で credit する。支払い各行は Unix ms、planet を `referenceID`、owner corporation または SDE 上の CONCORD (1000125) を `ownerID2` にする。EveJS の均等な export 二分割と full-total owner credit は、CCP の納税先説明と区別できないため採用せず、計算済み成分ごとの額にする。

`PlanetMgrService.ChangeCoreAsync` と `ImportExportCoreAsync` は simulation の保存時刻に FILETIME を使う一方、wallet の `TimeMs` は Unix ms である。以前は同じ FILETIME を仕訳へ渡し、月指定 `account.GetTransactions` から行が消えていた。PI Host 回帰は登録済み `account.GetTransactions` 経由で 96/97 journal の件数、owner IDs、planet references、owner credit を確認する。また construction ref 98 の `referenceID` も以前は 0 だったため、同じ Host query から planet ID を確認する。

### EveJS 261108.1 の現行ソース照合

参照は `/home/user/evejs-261108.1` のローカル配布スナップショットで、Git commit ID はない。契約・trade・wallet の service ごとの test script は `server/package.json` に定義されていないため、以下はソース経路の照合であり EveJS process を用いた実行比較ではない。

契約作成の wallet legs は `server/src/services/contracts/contractRuntimeState.js:1385-1399,1963-1984` にあり、auction は public でも broker/deposit を払う一方、item-exchange/courier は private broker fee だけを作成時に払う。V24.01 courier 作成画面は broker fee と deposit を表示し、sales-tax row は非表示にする (`/home/user/eve-logic/eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:624-669`)。`ContractStruct` は契約条件のみを RPC に送る (`:853-889`)。eve.rs は画面に出る public courier broker/deposit を請求し、画面で非表示の sales tax を作成時に請求しない。EveJS の public courier fee omission と異なる判断は、Host+DB 回帰 `Registered_public_courier_creation_does_not_charge_sales_tax` が fee/deposit/reward の仕訳、残高、crate、expiry job、outbox と併せて検証する。

auction は EveJS `contractAuctionRuntime.js:91-146` が最低入札・outbid refund・15分延長/buyout deadline を一つの versioned settlement に記録し、`contractAuctionClaims.js:63-108` が issuer と winner の claim を独立に処理する。V24.01 は `contracts.py:562-667,673-677` から PlaceBid と各側の FinishAuction を別 RPC として呼び、`contractsDetailsWnd.py:591-602` が claim side を選ぶ。eve.rs の expiry/buyout/税・deposit 仕訳の Host 回帰はこの状態遷移と CCP wallet reference IDs を確認する。EveJS の売却税計算順序と一行 seller proceeds は client 専用 ref 67/73/75/82 と合わないため、eve.rs は別仕訳にしている。

direct trade は V24.01 `pvptradesvc.py:35`, `pvptradewnd.py:108,160-163`, `invControllers.py:558-575,1648-1691` の操作に対し、EveJS `server/src/services/trade/tradeMgrService.js:697-743,1057-1082,2249-2443` が両者の session/dock 状態を mutation 入口で再確認し、Add/MultiAdd/OfferMoney/MakeOffer/Abort を処理する。fitted charge と corporation office item の custody はこの worktree の既存 Host regressions と [取引監査所見](audit-fixes-trade.md) に記録した。今回 direct-trade production code に変更はない。

wallet 側は V24.01 `walletSvc.py:124-139,142-150` が personal/corporation/alliance bill payment と wallet transfer を分け、`accountsvc.py:108-130` が月指定 transactions を呼ぶ。EveJS 261108.1 の対応経路は `server/src/services/account/billMgrService.js:284-325`, `billRuntimeState.js:644-743`, `accountService.js:777-845` にあり、corporation division kwarg、bill settlement と journal read を別々に扱う。eve.rs は個人請求支払い、transfer errors、division journal、PI 96/97/98 refs、office-rent bill notices を Host/DB tests で確認済み。全 bill producer / debtor kind と全 journal entry renderer は今回の変更対象として再検証していない。

market tax は EveJS 261108.1 `server/src/services/market/marketRules.js:43,48,165-178,278-282` で base 8% と Accounting 11%/level を使う。V24.01 client preview も 8% fallback を持つが、CCP は [Broker Fee and Sales Tax](https://support.eveonline.com/hc/en-us/articles/203218962-Broker-Fee-and-Sales-Tax) で現行基礎率 7.5% を示し、2025-03-12 の 22.02 patch notes が 4% から 7.5% への更新を記録する。eve.rs は現行ルールの 7.5% を維持する。古い V24/EveJS preview と現行 settlement の差は [互換差記録](../reference/compat/DEVIATIONS.md#market-slice-m2-the-rules-as-data-and-the-decisions-2026-10-10) に根拠付きで残し、今回税率を変更しない。

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
- auction deadline/buyout、blueprint 検索、seller sales tax と法人 division 仕訳を追加した後、登録 Host 経路を含む `Eve.Host.Tests` の `FullyQualifiedName~Contract` slice は Release で 43 passed、0 failed、0 skipped（`eve_p4`）。auction deadline、別々の issuer/winner claim、BPO/BPC 詳細 wire 行、作成時税 deferred、売却後税、deposit 税・返却、no-bid forfeiture、法人 division 1002/1003 の refs 77/80/81 を含む。最初の全 slice では古い gross-payout balance 期待値が2件失敗したため、V24 wallet ref と税の差引に照らして仕訳・残高 assertion を更新し、slice を再実行した。
- 登録 Host tax wire test を法人 issuer に切り替える試行は `ConNotInStation` で失敗したため、その一時的な入力変更を戻し、personal auction の Host 経路と corporation wallet の persistence DB test を分けて検証した。この試行は pass に数えていない。
- 新しい auction tax Host wire test は変更前に `sales_tax_isk` 10000 の赤を確認し、修正後の単体 slice は 1 passed、0 failed、0 skipped。Core service/write-model slice (`ContractProxyServiceTests`, `ContractWriteModelTests`) は Release で 37 passed、0 failed、0 skipped。
- push後のmain CI run 1701 で `Finish_auction_claims_items_and_proceeds_independently` が失敗した。元fixtureは DB に直接 500 ISK bid を作り、作成時に必要な deposit を持たない contract を claim していた。V24.01 の `contracts.py:571-582` は minimum bid を開始価格と既存 bid 加算から決め、`contractscommon.py:22` は開始 bid minimum 1M ISK を定義する。EveJS `contractAuctionRuntime.js:117-123` も初回 bid を開始価格以上とし、`contractAuctionClaims.js:89-98` は最低10K税をbid+depositまでcapする。500 ISKの不可能な手動DB状態では売上500の全額が税になるため、旧期待値500は有効なゲーム状態の検証ではなかった。fixtureを開始価格/bid 1M、作成時 broker 10K・deposit 100K、bidder ISK debit/ref 63 を記録した既払状態にし、tax10K後の払出合計1.09Mと refs 67/73/82 を確認するよう直した。assertionは削除・緩和せず、V24/EveJS両方で成立する契約状態に修正した。
- 新しい courier Host wire test は変更前に registered `CreateContract` が `sales_tax_isk=20000` を保存する赤を確認した。修正後は `eve_p4` 上で 1 passed、0 failed、0 skipped。0 tax の契約行、reward/broker/deposit refs、wallet balance、包み item、expiry job と `OnContractCreated` outbox row を確認した。
- courier修正後、Host Release build と `Contract|Courier` Host DB slice は warnings-as-errors で成功し、それぞれ 0 warning/0 error、47 passed/0 failed/0 skipped (`eve_p4`)。Core Release build も 0 warning/0 error、contract proxy/write-model/fee-calculator slice は 54 passed/0 failed/0 skipped。
- direct trade / wallet の再確認: `MarketTradeHostWireTests`, `NewEdenStoreHostTests` direct-trade wire flows, `ContractReadModelTests`, `ActivityAlphaBillRpcHostWireTests`, `AccountWalletHostRpcTests` の Host+DB slice は `eve_p4` で 30 passed、0 failed、0 skipped。`TradeAuthorityTests` は 12、`TradeMgrRpcTests` は 8、wallet `TransactionTests` は 9、`TransactionHistoryTests` は 1、いずれも 0 failed/0 skipped。Office rent、corporation bills、wallet audit/journal の Host slice は 15 passed、0 failed、0 skipped、`BillMgrServiceTests` は 1 passed。
- main CI run 1702 (`43f571e9dd56`) は `Registered_auction_buyout_waits_for_issuer_and_winner_claims` で Expected 70,000 / Actual 60,000 により失敗した。元 fixture は issuer balance 50,000 で auction を作り、broker fee/deposit を 0 にし、20,000 buyout bid に対する sales tax を含めない payout を期待していた。V24 の auction claim と EveJS の `contractAuctionClaims.js:84-108` は売却税控除後の payout を作る。fixture を 200,000 balance、作成時 broker 10,000/deposit 100,000、seller claim journal 20,000 sale / 10,000 tax / 100,000 deposit return に直し、既存の issuer/winner の独立 claim・item custody・通知 assertions を保った。修正した7つの関連 Host wire tests はすべて `eve_p4` で通り、上記47件の contract/courier slice も再通過した。CI 1702 自体は修正前 main の実行であり、修正後 commit の CI はまだ実行していない。同runでは Abyssal tournament の3テストが skip と記録されたため、成功に数えていない。
- main CI run 1701 で `Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` の teardown が outbox `SELECT` 中の `idle in transaction` セッションを一度検出した。run 1702 のほかの Host shards は成功し、同テストを `eve_p4` で単独実行して 1 passed / 0 skipped としたため、現時点で再現していない。teardown の処理 (`HostRuntime.ShutdownAsync` -> `Foundations.DisposeAsync` -> `EventBus.StopAsync`) は event loop の完了を await している。安定した再現原因を特定できていないため、テストや assertion を緩めず、本変更には推測に基づく teardown 改変を入れていない。
- `Eve.Host.Tests` と `Eve.Services.Core.Tests` の Release build は warnings-as-errors で各 0 warning、0 error。`git diff --check` も通過した。
- 変更前に auction deadline test は期限更新なし、buyout test は `FinishAuction` 前に status=4 となる挙動を再現した。BPO Host test は search hint 未対応の応答を再現した。blueprint 関連の最初の DB slice では fixture inventory schema がなく失敗したため fixture migration を追加し、当該 class と上記 contract slice を再実行して通過した。

## 未確認の範囲

- 実際の V24.01 graphical client、実クライアント packet trace、通知 UI 上の表示は確認していない。
- EveJS の server process を起動して同一シナリオを実行していない。比較は指定された EveJS ソース経路の追跡による。
- この変更は個人請求を作る generic store API を提供する。既存 Host composition には個人請求を発行する production producer や、個人請求を列挙する RPC が見当たらず、その発行・発見経路は未実装のまま。
- 実 V24 graphical client、実 packet trace、EveJS server process での同一 PI 操作は動かしていない。PI Host 回帰は `ImportExportWithPlanet` の production service 実装をテスト用 space provider から bound call し、journal read は登録 Host service を通す。inventory broker の production space registration を使うライブ PI walk はこの変更後に未実行。
- corporation/alliance debtor の支払いレシート通知、すべての請求生成元と期限・更新・失効処理はこの変更で検証・修正していない。
- 今回は auction expiry/buyout claim と blueprint 検索の差分を Host/DB 経路で検証した。ほかの全 contract search filter 組合せ、courier expiry/claim、item exchange の全ての collateral/reward・期限・retry 組合せ、direct trade 全体を同じ作業で網羅したとは判定しない。既存 handoff の範囲を超える経路は残る。
- auction の税率・winner/seller claim・no-bid deposit と journal refs はソース照合および Host/DB 回帰済みだが、実 V24 client と live server の end-to-end 表示は未確認。売却税の CCP backend 実装は client source から直接観察できないため、V24 UI、client journal refs、EveJS policy を基にした判定である。
- Host RPC を通した corporation-issued auction creation はこの変更では未確認。corporation wallet division 1002/1003 の fee/deposit/bid refs は Persistence write model の PostgreSQL 回帰で確認した。
- PI 以外の全 journal reference / description 種別、derived tax rows、market order/fill と sales tax の caller-to-wallet 経路は未確認。市場税の互換性判断は既存 deviation 記録に残る。
- オークション以外の corporation wallet division による contract/market/bill entry routing、全請求種別の期限・更新・失効・領収通知、wallet transaction retry/restart 横断は今回の追加差分では再検証していない。
