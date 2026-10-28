# contractProxy 深掘り監査

確認日: 2026-10-27
対象: V24.01 eve-logic client、eve.rs の Host / Core、EveJS 261018.1 source

## 調査結論

- eve-logic 全体をサービス参照で走査し、クライアントが呼ぶ contractProxy RPC は下表の25メソッド。取得経路は contracts.py:93 の sm.ProxySvc('contractProxy') と同じ ProxySvc の直接呼出しだけで、bound-object RPC 呼出しは見つからなかった。
- 実際の TCP Host ルートは CoreServices.cs:75-76 で ContractProxyService と inventory reader を構成し、HostComposition.cs:261 で registry.Register(service)、PacketDispatcher が registry 経由で呼び出す。テストは Packets.CallWithKwargs で contractProxy を指定して TCP Host に送る。
- 全 RPC の wire tests で machoVersion=1 を付与する。CreateContract の confirm=None/1、GetContractListForOwner の num と startContractID=None、SearchContracts の名前付き引数と None / Python set、複数 RPC の PyInt / PyLong を実行経路で確認する。
- GameRpcServices.cs:849 の SearchContracts crateID は開始ステーションIDを返していた。EveJS は escrowLocationID を使い、契約作成時の Host は EscrowLocationBase + contractID を escrow location として作るため、検索行をその計算値に修正した。GetContract は同じ派生値を既に返す。EveJS source: contractRuntimeState.js:298-300, 953-1007; contractProxyService.js:341-384, 480-506。Host source: PersistenceContractWriteModel.cs:20, 103; GameRpcServices.cs:849, 953。TCP wire test では検索 KeyVal の crateID が PyLong、詳細 packed row の crateID が PyInt と復号されることを確認した。クライアント全体では crateID の消費箇所を確認できなかった。
- Host の検索行 bids=[] と numBids=0 は EveJS 261018.1 の buildContractSearchEntry と一致する。クライアントはその numBids を表示する (contractentry.py:705)。詳細取得の実 bid rows は別途 GetContract から受け取る。
- 変更した create / bid の UserError と crateID は Host wire tests に追加した。既存テスト群と今回追加テストの実行結果は、この監査票の最終節に記す。

## RPCごとの照合

| RPC | eve-logic 呼出しと消費箇所 | Host 引数 → wire 応答 / エラー | 状態と通知 | EveJS / 実 Host wire |
|---|---|---|---|---|
| AcceptContract | contracts.py:421; 返却の type / contractID / dateAccepted を同ファイル:427-429で使う | GameRpcServices.cs:308。contractID, forCorp → KeyVal (contractID, type, status, issuerID, issuerCorpID, forCorp, acceptorID, dateAccepted, numDays, startStationID, endStationID)。IDは int/long、forCorp は bool/int/long。 | AcceptContractAsync が永続状態と escrow を更新し OnContractAccepted を outbox に記録 (PersistenceContractWriteModel.cs:288, 383)。 | EJS 840-845; ContractProxyBatchWireTests.cs:171 |
| CollectMyPageInfo | contracts.py:727; counts と outstandingContracts を :733-740 で使用 | ContractProxyBatchService.cs:98。引数なし → KeyVal の outstanding / progress / bid / attention counts と issuer/corp/assignee/type 4-tuples。 | DB read model から集約、通知なし。 | EJS 696-712; ContractProxyBatchWireTests.cs:117 |
| CompleteContract | contracts.py:481, 515, 545。Courier 配送完了、失敗、拒否の3状態 | GameRpcServices.cs:361。contractID, targetStatus → bool。status 4/7/6 を処理し、provider の UserError を同じキーで返す。 | 永続状態を変更。status 4 は OnContractCompleted、7 は OnContractFailed、6 は OnContractRejected (PersistenceContractWriteModel.cs:434-437, 544)。 | EJS 847-854; ContractProxyBatchWireTests.cs:173-177 |
| CreateContract | contracts.py:554。createContractWnd.py:888 は通常呼出し、:890-892 は NotEnoughCargoSpace 後に confirm=1 で retry | GameRpcServices.cs:139。位置引数 ContractStruct 1個、confirm は任意の keyword。返却は contract ID 1件の list。provider UserError のキーと値を保持。 | 1つの transaction で contract / escrow / wallet / expiry job を保存。OnContractCreated、private/corp/alliance 割当て時は OnContractAssigned を outbox へ記録 (PersistenceContractWriteModel.cs:103-170)。 | EJS 824-838。runtime parser は confirm を消費しない (contractRuntimeState.js:1575-1624); Host wire: ContractProxyAuditWireTests.cs:15-56 |
| DeleteContract | contracts.py:444-445; 成功時にローカル OnDeleteContracts を通知 | GameRpcServices.cs:392。contractID → bool。UserError はキーを維持。 | contract を永続削除/終了状態へ更新。V24 に server-side delete 通知 handler はなく、client が local cache/event を更新。 | EJS 856-860; ContractProxyBatchWireTests.cs:179 |
| DeleteMultipleContracts | contracts.py:458-465; deleted / failed の2 list を表示 | ContractProxyBatchService.cs:8。PyList[int/long] → (deleted list, failed list)。個別失敗は failed に残し、全体を汎用エラーにしない。 | 各 ID に DeleteContractAsync を実行。追加 notification なし。 | EJS 862-869; ContractProxyBatchWireTests.cs:69-72 |
| DeleteContractNotification | contracts.py:851-852; 戻り値は読まず期限切れ cache を破棄 | GameRpcServices.cs:507。contractID, forCorp → None。| dismissal を永続化。client callback 通知なし。 | EJS 912-915; ContractProxyBatchWireTests.cs:192 |
| DeleteNotification | contracts.py:722; contractentry.py:197 は真の時だけ行を消す | GameRpcServices.cs:486。contractID, forCorp → bool。provider UserError を維持。 | dismissal を永続化し、GetLoginInfo から除外。通知 callback なし。 | EJS 907-910; ContractProxyBatchWireTests.cs:191 |
| FinishAuction | contracts.py:676; contractentry.py:203, 209 は真の時に行を消す。詳細 UI は :590, 600 | GameRpcServices.cs:529。contractID, isIssuer → bool。契約所有者または落札者を検証。 | auction settlement は永続処理を実行し、完了通知を outbox に記録。EveJS false との既存差分は DEVIATIONS.md:155 に記録済み。 | EJS 876-879; ContractProxyBatchWireTests.cs:193-195 |
| GetContract | contracts.py:335-350 で応答を cache。items / bids の消費例は contractentry.py:126, 246 と contractsDetailsWnd.py:135-168 | GameRpcServices.cs:468。ID → None または KeyVal(contract: packed row, items: packed rows, bids: rows, metadata sets)。crateID は escrow location ID。Host TCP wire では詳細 row の値 9_300_005_002 が PyInt に復号される。client code 全体では crateID の利用箇所を確認できなかった。 | DB read model。通知なし。 | EJS 818-822, row builder 341-384; ContractProxyBatchWireTests.cs:183-187、ContractProxyAuditWireTests.cs:79-96 |
| GetContractListForOwner | contractPanels.py:418-420。contracts/items を panel に渡す | GameRpcServices.cs:627。ownerID, status, contractType, issuedBy は位置引数。num と startContractID は keyword、startContractID=None は paging cursor なし。返却は bundle。 | DB read model。通知なし。 | EJS 762-771; ContractProxyBatchWireTests.cs:154-157 |
| GetCourierContractFromItemID | contracts.py:879。:864-875 は None を ConContractNotFoundForCrate に変換し、contractID を完了処理へ渡す | GameRpcServices.cs:477。wrap item ID → row または None。 | plastic_wrap_item_id で契約を引く DB read。通知なし。 | EJS 917-922; ContractProxyBatchWireTests.cs:188-189 |
| GetItemsInContainer | contracts.py:712; createContractWnd.py:1343 が typeID 順に表示 | GameRpcServices.cs:83。stationID, containerID, forCorp, flag → packed row list。空 list は client の or [] 処理に合う。 | Inventory read。通知なし。 | EJS 886-892; ContractProxyBatchWireTests.cs:97-100 |
| GetItemsInDockableLocation | contracts.py:718; createContractWnd.py:92, 933, 1647 | GameRpcServices.cs:117。stationID, forCorp → Python set of packed item rows。 | Inventory read。通知なし。 | EJS 899-905; ContractProxyBatchWireTests.cs:82-95 |
| GetLoginInfo | contracts.py:190-205。assignedToMe.contractID / issuerID、needsAttention、inProgress の contractID / stations / expires を読む | GameRpcServices.cs:568。引数なし → KeyVal に3 rowset。 | dismissed attention rows と現在の契約状態を DB から読む。通知なし。 | EJS 691-694; ContractProxyBatchWireTests.cs:141-142 |
| GetMyBids | contracts.py:761; contractPanels.py が cache した list を表示 | GameRpcServices.cs:604。forCorp bool/int/long → contract bundle。 | DB read model。通知なし。EveJS は空 bundle を返すが Host は bid rows を返す。既存互換記録 DEVIATIONS.md:5601。 | EJS 748-751; ContractProxyBatchWireTests.cs:148-152 |
| GetMyContractEscrow | contracts.py:773-782。iskEscrow/itemsEscrow を float 化し失敗時は0に戻す | GameRpcServices.cs:590。引数なし → KeyVal(iskEscrow: number, itemsEscrow: int)。 | DB read model の escrow 合計。通知なし。 | EJS 714-721; ContractProxyBatchWireTests.cs:144-146 |
| GetMyCurrentContractList | contracts.py:788。contracts/items bundle を cache | GameRpcServices.cs:658。isAccepted, forCorp bool/int/long → bundle。 | DB read model。通知なし。 | EJS 753-760; ContractProxyBatchWireTests.cs:159-163 |
| GetMyExpiredContractList | contracts.py:747-748。personal/corp bundle を cache | GameRpcServices.cs:688。forCorp bool/int/long → bundle。 | DB read model。通知なし。 | EJS 742-746; ContractProxyBatchWireTests.cs:165-169 |
| GetNumItemsInContainers | contracts.py:715; createContractWnd.py:1320 で container ID ごとの count を参照 | GameRpcServices.cs:96。stationID, IDs collection, forCorp, flag → ID-to-count dict。空 container は欠落キーとして表す。 | Inventory read。通知なし。 | EJS 894-897; ContractProxyBatchWireTests.cs:101-110 |
| NumOutstandingContracts | contracts.py:689。CollectMyPageInfo の page counts にも使う (:727-740) | ContractProxyBatchService.cs:49。引数なし → 4 int fields: nonCorpForMyChar, myCorpTotal, nonCorpForMyCorp, myCharTotal。 | DB owner paging で計数。通知なし。 | EJS 723-726; ContractProxyBatchWireTests.cs:75-80 |
| PlaceBid | contracts.py:666。:608-610 は ConBidTooLow を表示して retry。contractentry.py:191、contractsDetailsWnd.py:610 も呼出し | GameRpcServices.cs:266。contractID, bid, forCorp → bool。bid は int/long/finite real を受ける。provider の ConBidTooLow をそのまま UserError 化。 | bid を永続化。前の bidder に OnContractOutbid、buyout 時 issuer に OnContractBuyout を outbox (PersistenceContractWriteModel.cs:699, 762-823)。 | EJS 871-874; ContractProxyRowsWireTests.cs:12-30; provider error wire: ContractProxyAuditWireTests.cs:59-77 |
| SearchContracts | contractsearch.py:1366-1404。result fields、各 row の contract/items/bids、表示用 numBids を読む。contractentry.py:705 が numBids を表示 | GameRpcServices.cs:417。位置引数なし、filters は全て keyword。None、Python set、int/long を受け、KeyVal(contracts, numFound, searchTime, maxResults) を返す。crateID は 9_300_000_000 + contractID。 | DB read model と item metadata query。通知なし。検索行の bids=[] / numBids=0 は EveJS と一致。 | EJS 728-740, row 480-506; ContractProxyRowsWireTests.cs:34-89 は None / set / result row と crateID を検査 |
| SplitStack | contracts.py:693-696 が成功時に create window を更新。createContractWnd.py:432 | ContractProxyStackSplit.cs:7。stationID, itemID, qty, forCorp, flag → bool。IDs/qty の int/long を受ける。 | item move を DB transaction で保存。client がその場で window を refresh。contract notification なし。 | EJS 881-884; ContractProxyRowsWireTests.cs:99-116 |
| GM_ExpireContract | contractentry.py:167 | GameRpcServices.cs:558。contractID → staff のみ bool。player は ConContractNotAvailable。 | expiry job で永続状態と expiry settlement を更新し、適用状態に応じ Completed/Failed event を記録。 | EJS 929-932; ContractProxyBatchWireTests.cs:196 と staff refusal test |

## Host の共通処理

- ServiceRegistry は [Service("contractProxy")] を service name で登録し、PacketDispatcher の実ルートは HostServerTests の TCP サーバーから呼ぶ。method attribute は GameRpcServices.cs:72 以下、batch/split は ContractProxyBatchService.cs:8,49,98 と ContractProxyStackSplit.cs:7。
- ServiceCall の位置引数と keyword を分けて読み、ContractLong / ContractSearchLong は PyInt, PyLong を受ける。nullable search filters と startContractID は PyNone を欠損値として扱う。CreateContract confirm は引数として消費しないが、未知 app keyword として拒否されず client retry wire shape を受ける。EveJS 261018.1 も confirm を作成 request logic に使っていない。
- ContractUserErrorException の message key と values は各 write handler が MachoErrors.ThrowUserError へ渡す。今回の TCP test は CreateContract の NotEnoughCargoSpace と provider 起因 PlaceBid の ConBidTooLow が UserError class で返ることを確認した。DeleteMultipleContracts は個別失敗を結果 list に保持する。
- クライアントがエラーキーで分岐する例は CreateContract の NotEnoughCargoSpace 後 confirm=1 retry (createContractWnd.py:888-892) と PlaceBid の ConBidTooLow (contracts.py:608-610)。Accept / Delete / DeleteMultiple / Complete / FinishAuction の呼出し元は UserError を再送出する (contracts.py:421, 444-445, 458-465, 481, 515, 545, 676)。Courier crate が見つからない場合は RPC の None を受けて client が ConContractNotFoundForCrate を発生させる (contracts.py:864-879)。他の呼出しにはエラーキーに応じた個別分岐を確認できなかった。TCP test では Create と PlaceBid の具体的な provider UserError を検査した。
- PersistenceContractWriteModel は transaction commit 後に event bus wake を行い、HostComposition.cs:130 の ContractNotifications が outbox を受け取る。ContractNotifications.cs:27-31 は OnContractCreated を引数なし、その他を contractID の PyLong 1個で各 recipient session に送る。client callback は contracts.py:222-267 で同じ引数形を読む。recipient routing のテストは ContractNotificationTests.cs:16-36。
- Create, accept, completion, bid/outbid/buyout と expiry の保存・outbox 生成コードがある。ContractReadModelTests.cs は transaction/outbox を検査する DB tests を含む。今回の実行では DB を使わない。

## テストと確認範囲

確認済みのローカル検査:

- `dotnet build dotnet/src/Eve.Services.Core/Eve.Services.Core.csproj -p:UseSharedCompilation=false --no-restore` 成功。
- `dotnet build dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj -p:UseSharedCompilation=false` 成功。
- Host TCP 検索 wire test `Search_contracts_returns_the_fields_consumed_by_the_client_through_the_registered_host_service` 1件成功。
- `ContractProxyAuditWireTests` の CreateContract、PlaceBid、GetContract TCP tests 3件成功。Host と Eve.Host.Tests はこの実行でコンパイルされた。

Host TCP tests は実 registry / dispatcher / packet framing を通るが、recording read/write provider を使うため永続 commit・再起動後の可視性・outbox 配信 packet を実行では確認しない。job の指示どおり PostgreSQL を使う tests と Eve.Host.Tests 全件は実行していない。実 GUI client での動作も確認していない。
