# insurance / insuranceSvc 深掘り監査

確認日: 2026-10-27

## 判定範囲

/home/user/eve-logic 全体を Serena の検索で調べ、Insurance RPC の呼び出し元と、応答・通知を読む箇所を追跡した。クライアント側のローカルサービス名は insurance（InsuranceSvc）、サーバーのRPC名は insuranceSvc。根拠は eve/client/script/ui/station/insurance/base_insurance.py:35-42,108。

クライアントから実際に呼ばれる業務RPCは GetInsurancePrice、GetInsurancePrices、GetContracts、GetContractForShip、InsureShip、UnInsureShip の6つ。EveJSには GetItemsToInsure もあるが、クライアント全体で呼び出しを確認できなかったため対象RPCには含めない。Monikerライブラリが間接的に呼ぶ MachoResolveObject と MachoBindObject も別記する。

## クライアント呼び出し一覧

| RPC | 全クライアントの呼び出し箇所 |
|---|---|
| GetInsurancePrice | eve/client/script/ui/station/insurance/base_insurance.py:130,132。結果を使う箇所は同ファイル:172,186,466-471、eve/client/script/ui/shared/info/infoContentBase.py:656、eve/client/script/ui/shared/info/shipInfoPanels/panelOverview.py:192、eve/client/script/ui/station/insurance/insuranceTooltip.py:18、eve/client/script/ui/shared/neocom/menucheckers/itemCheckers.py:1227。 |
| GetInsurancePrices | base_insurance.py:138,140。PrimeInsurancePrices は未取得の typeID 集合をリストにして結果辞書を価格キャッシュへ入れる（同ファイル:136-143）。 |
| GetContracts | base_insurance.py:118,120,298,493、eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:522。返却行から shipID をキーに辞書化する（base_insurance.py:118-120,493-498）。 |
| GetContractForShip | base_insurance.py:124、eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1379、eve/client/script/ui/shared/info/infoContentBase.py:655、eve/client/script/ui/shared/info/shipInfoPanels/panelOverview.py:191。後二者は存在確認後に ownerID と endDate を読む（infoContentBase.py:655-675、panelOverview.py:191-205）。 |
| InsureShip | base_insurance.py:532,541。最初の呼び出しは itemID, quote.amount, isCorpItem の位置引数。再試行は同じ3引数に voidOld=True をキーワードで加える。InsureShipFailedSingleContract のときだけ処理し、e.args[1]['ownerName'] を読む（base_insurance.py:532-542）。 |
| UnInsureShip | base_insurance.py:400。itemID を位置引数で渡し、成功後に保険情報を再表示する。 |
| OnShipInsured | InsuranceSvc と ship panel の両方が購読し、引数なしハンドラを実装する（base_insurance.py:38-42,217-219、panelOverview.py:35-37,315-317）。 |

## Host経路とMoniker

InsuranceService は insuranceSvc として登録され、6 RPCと2つのMoniker用メソッドに Method 属性がある（dotnet/src/Eve.Services.Core/InsuranceService.cs:262,290-293,343,357,374,400,427,478）。本番構成は CharacterComposition で永続化 read/write model と権限判定を渡し（dotnet/src/Eve.Host/Composition/CharacterComposition.cs:202-210）、CharacterServiceList から MailRpc を含め、MailRpc のサービス一覧に insuranceService を登録する（dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:48,62、dotnet/src/Eve.Host/Rpc/MailRpc.cs:143）。insuranceSvc のサービス位置は station（dotnet/src/Eve.Host/MachoNetService.cs:49）。

クライアントは station では (stationID, stationID, groupStation)、structure では (structureID, solarsystemid2, groupSolarSystem) で Moniker を作る（base_insurance.py:96-110、inventorycommon/const.py:909,925）。moniker.py:111-156 が resolve 後に bind と初期RPC呼び出しを行い、:208-245 が MachoResolveObject を呼ぶ。古いbound先への UnMachoDestination は1回だけ再解決する（moniker.py:181-194）。ObjectCallGPCS.py:403-407 は machoVersion を既定値1またはキャッシュ値で kwargs に追加する。PacketDispatcher はそのキーをアプリ引数から除去し、返却bound objectを登録してから応答後処理を実行する（dotnet/src/Eve.Services/PacketDispatcher.cs:219,248,254,301-307,587）。

dotnet/tests/Eve.Host.Tests/InsuranceMonikerHostTests.cs:99 のテストは HostServer の実TCP/Macho経路を通し、直接RPC、station/structure bind、bind時の初期RPC、bound RPC、kwargs、UserError応答を検証する。

## メソッド別の挙動

### GetInsurancePrice

クライアントは typeID 1つを位置引数で渡す。stationではbound Moniker、それ以外では insuranceSvc への直接RPCを使う（base_insurance.py:126-134）。HostはPyInt/PyLongの正整数を読み、shipInsurancePrices GameStore 読取モデルから cents を ISK に変換して数値を返す。未知IDは0。UIは数値として比較・乗算する。wireテストは直接・bound双方、int/long、未知ID、数値の型を確認した。

### GetInsurancePrices

引数は未取得typeIDのリスト。返却は typeID から ISK価格への辞書で、未知の正整数にも0を返す。重複と不正・非正のIDは除外される。以前のHost側500件制限は、クライアントに件数制限がなくEveJSにもないため削除した。Coreテストと実Host wireテストで501件の要求と全件応答を確認した。関連する互換記録は docs/reference/compat/DEVIATIONS.md の「Insurance price batches」。

### GetContracts

クライアントは個人契約を引数なし、法人契約を位置引数1で読む（base_insurance.py:113-121）。法人一覧はクライアントの junior accountant または accountant role に応じて要求する（:120）。Hostは個人/法人のownerをセッションから決め、権限なしの法人照会は空リストを返す。成功応答はcontract KeyVal行のリストであり、各行の shipID, contractID, typeID, ownerID, fraction, startDate, endDate を検査した。期限切れとvoidは除外され、ownerの異なる契約は表示されない。業務上の状態変更・通知はない。

### GetContractForShip

itemIDを位置引数で渡し、応答は所有者に見える有効契約のKeyValまたはNone。クライアントは応答のtruth値を見てから ownerID/endDate を読む（createContractWnd.py:1379-1381、infoContentBase.py:655-675、panelOverview.py:191-205）。Host wireテストは直接経路、bound経路、int/long ID、契約なしのNone、購入後に契約が見える状態を確認した。読み取り専用で通知はない。

### InsureShip

クライアントが送る通常形は (itemID, quote.amount, isCorpItem)、置換確認後は voidOld=True をキーワードで渡す（base_insurance.py:532-542）。Hostはship IDのint/long、ISK額のPyReal等、booleanを読み、voidOldは位置引数またはkwargsから読み取る。Noneまたは省略は置換なし。成功応答はNone。

EveJSに合わせたUserError:
- 対象外、無効入力、未dock等: InsCouldNotFindItem。
- 法人保険権限なし: CrpAccessDenied。
- 残高不足: NotEnoughMoney と amount/balance（ISK）。
- 既存契約あり: InsureShipFailedSingleContract と ownerName（既存owner ID）。

クライアントは最後のエラーの ownerName を読む。wireテストは各エラー名・置換値をUserError応答で確認した。既存契約の voidOld=True と4番目の位置引数も成功する。Applied時だけOnShipInsuredを応答送信後に引数なしで送る（InsuranceService.cs:472-475）。クライアント側ハンドラが引数を取らないこと、通知内容が空の引数タプルであることをwireで確認した。

### UnInsureShip

ship IDを位置引数で渡し、成功時はNone。未所有、非dock、契約なし、期限切れ・void、二度目の取消しは InsCouldNotFindItem。法人契約を見えない/取消権限がない場合も所有権を漏らさず同じエラーとなる。取消し成功後は GetContractForShip がNone、GetContractsからも消える。取消しにOnShipInsured通知は送らない。これはEveJSが通知を送らず、クライアントの取消し処理が保険情報を再表示する動作（base_insurance.py:400）と一致する。wireテストは成功と再試行エラーを確認した。

### MachoResolveObject / MachoBindObject

これらはクライアントの業務コードから直接呼ぶものではなく、insuranceSvc Monikerが呼ぶ。resolveはProxyNodeIdを返す。bindはbound OID、ファイル時刻、初期RPC応答を含む結果を返し、station/structure両方のbind引数を扱う。bound OIDは同じセッションに結び付け、解放後・他セッションのOIDは UnMachoDestination にする（InsuranceService.cs:290-339,535-540）。Host wireテストはresolve、station/structure bind、初期GetInsurancePrice、PyLongの期限値、解放済みOIDのエラーを確認した。

## 永続化・後続状態・EveJS比較

本番read modelはGameStoreのshipInsurancePricesとinsuranceContractsを読む（dotnet/src/Eve.Host/Persistence/Insurance/PersistenceInsuranceReadModel.cs:39-80）。InsureShipはship/owner/location/quoteを検査し、契約状態変更とWallet debitを同一transactionで確定する（PersistenceInsuranceWriteModel.cs:53-125）。不足残高はwallet lock中の残高をWalletTransactionsが保持し（dotnet/src/Eve.Wallet/WalletTransactions.cs:4-7,148）、NotEnoughMoneyのbalanceへ渡す。UnInsureShipはactive契約をvoidにしてcommitする（PersistenceInsuranceWriteModel.cs:141-177）。同じread modelを使う後続RPCは確定状態を読む。wireテストのインメモリmodelでも購入後の契約読取と取消し後の消失を検査した。

EveJSの service は insuranceSvc と6 RPC、およびresolve/bindを公開する（/home/user/evejs-261018.1/src/server/src/services/insurance/insuranceService.js:20-100）。EveJSの契約列挙/個別参照は insuranceRuntime.js:603-640、購入のbusiness errorとOnShipInsuredは :1087-1201、取消し後の見え方は :1256-1265。残高不足のamount/balanceは walletState.js:199-204。Hostはこれらのクライアント可視形状とエラーを合わせ、永続化はHostの既存transaction/GameStore/wallet基盤に載せている。

## 実行した確認と未確認事項

確認済み:
- InsuranceServiceTests: 34 passed, 0 failed, 0 skipped。
- 実Host TCP wire focused test: 1 passed, 0 failed, 0 skipped。501件バルク、直接/Moniker、station/structure、引数・応答型、全business error、通知順、後続read/cancelを含む。
- Eve.Host.Tests全体: 837件を実行し、508 passed、327 failed、2 skipped。失敗には空のEVE_PG_URLにより接続文字列のHostが不足してNpgsqlが起動できないケースが多数あり、DailyGoal/ShipCosmetics等の別のassertion失敗も含まれた。2 skippedは成功として数えていない。DBは使用していない。

未確認:
- DBを使う InsuranceRpcHostWireTests の永続化統合テストと、再起動後の永続状態。テストはPgFactであり、この依頼の「DBを使わない」に従って実行対象から外れた。
- 同一shipへの並行購入を実DB transaction上で競合させる検査。
- V24.01の実グラフィカルクライアントによる実機接続。ここで確認したのはクライアント全ソースと実Host wireテスト。
- Host.Tests全体は緑ではないため、広域回帰の合格とは扱わない。
