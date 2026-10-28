# bountyProxy 全クライアント呼び出し・Host深掘り監査

対象はeve-logic V24.01 / build 3396210の全ツリー。Serena全ツリー検索で sm.ProxySvc('bountyProxy') の全呼び出しを確認した。該当サービス名は実在し、呼び出しは以下の19メソッド。bountyProxyのbound/moniker呼び出しは見つからない。近接する killRightMgr.BuyKillRight / ActivateKillRight は別サービス（bountySvc.py:324-337、killRights.py:263）。

## Host登録と共通wire

BountyProxyKillRightService.Name は bountyProxy。本番構成では CharacterServiceList.cs:52 が CrimewatchRpc() を含み、HostComposition.cs:261-265 が返されたサービスをregistryに登録する。CrimewatchRpc.cs:119-130 が BountyProxyKillRightService にPlayerBountyRpc、Crimewatch store、通知writer、組織resolverを接続する。登録メソッド一覧はサービスの HasMethod と一致する。

V24.01の ServiceCallGPCS.py:225-232 はアプリ引数を位置引数tupleで送信し、transport用 machoVersion=1 をkwargsに加える。Host PacketDispatcher.cs:215-225,307-317 はアプリ引数とkwargsをServiceCallへ渡す前に machoVersion を除く。クライアント側からこの19メソッドへのアプリkwargsはなく、全コールは位置引数。IDはclient marshalのPyInt/PyLong双方を受理する。ID collectionはHostでPyList/PyTupleを受理。SellKillRightの3番目の restrictedTo はUIでNoneまたはowner IDを渡す。wire回帰はPyInt/PyLong、machoVersion kwargs、Noneを含むSellKillRight呼び出しを通す。

## メソッド別結果

| メソッド | 全クライアント呼び出しと返信消費 | Hostの引数・返信・状態・通知・失敗 |
|---|---|---|
| AddToBounty | bountySvc.py:82 が (ownerID, amount) を送り、戻り値を CacheBounties に渡す（83-84）。UIは bountyWindow.py:446-452。 | int/long target IDとISK amountをPlayerBountyRpcへ委譲。返すKeyValは targetID (ID wire型), bounty (PyReal), 必要時 corporation/alliance ID。wallet debitとpool/contribution/journal書き込みを同一transactionでcommitし、CallId由来のkeyでretryを一重化。commit後、placer以外へtype 112/113/114、payload bountyPlacerID,bounty を通知（CrimewatchRpc.cs:48-82）。エラーはpayerなし CharacterNotFound、不明target/最低額未満 CustomNotify、残高不足 NotEnoughMoney(amount,balance)。 |
| GetBounties | bountySvc.py:147。要求IDを集める処理は bountySvc.py:130-147。返信は (targetID,bountyKeyVal) 群としてcacheへ入る。 | ID collectionから重複を除いた各IDについて (ID, KeyVal) のPyList。存在しないpoolもbounty=0で返す。DB readのみ、通知なし。bounty provider未接続またはnull応答は CustomNotify。 |
| GetTopPilotBounties | bountySvc.py:202。wrapperは (rows,lastFetchingTime) をunpackしbounty降順で返す（211-216）。UI page 0: bountyWindow.py:496。rowは targetID,bounty を読む（481-490）。 | positional page (clientは0) を受け、Top 10 pool rowsとFILETIME PyLongのtuple。rowのIDはID wire型、bountyはPyReal。DB readのみ。未接続時 CustomNotify。 |
| GetTopCorpBounties | bountySvc.py:205; UI page 0は bountyWindow.py:498、row利用は bountyWindow.py:481-490。 | GetTopPilotBounties と同じreply型でcorporation poolを読む。pageは位置引数、現client UIは0。状態変更・通知なし。未接続時 CustomNotify。 |
| GetTopAllianceBounties | bountySvc.py:208; UI page 0は bountyWindow.py:500、row利用は bountyWindow.py:481-490。 | GetTopPilotBounties と同じreply型でalliance poolを読む。pageは位置引数、現client UIは0。状態変更・通知なし。未接続時 CustomNotify。 |
| GetTopPilotBountyHunters | bountySvc.py:218 はreplyの [0] のみ返す。UI page 0は bountyWindow.py:541; row消費は bountyWindow.py:526-535。 | (rows, FILETIME PyLong); rowsはPyList。各rowは bountyHunterID (ID型), bountiesClaimed (PyReal), numberOfKills,rowNumber (PyInt)。DB readのみ。未接続時 CustomNotify。 |
| GetTopCorporationBountyHunters | bountySvc.py:221 は [0]; UI page 0は bountyWindow.py:543; row利用は bountyWindow.py:526-535。 | GetTopPilotBountyHunters と同じreply型でcorporation hunter順位を読む。未接続時 CustomNotify。 |
| GetTopAllianceBountyHunters | bountySvc.py:224 は [0]; UI page 0は bountyWindow.py:545; row利用は bountyWindow.py:526-535。 | GetTopPilotBountyHunters と同じreply型でalliance hunter順位を読む。未接続時 CustomNotify。 |
| GetMyBounties | bountySvc.py:228; UI callは bountyWindow.py:554、row fieldsは bountyWindow.py:579-583。 | 引数なし。contribution rowsのPyList。各rowは contributionID PyLong、targetID ID型、amount PyReal、corporation/alliance ID。永続contributionを読むだけ。未接続時 CustomNotify。 |
| GMReimburseBounties | bountySvc.py:231; slash caller svc_slash.py:1529-1531 は Ok を期待。 | 引数なし。副作用を偽装せず CustomNotify("GM bounty operations are not supported on this server.")。状態・通知なし。 |
| GMClearBountyCache | bountySvc.py:240; slash caller svc_slash.py:1533-1536。clientはRPC前にbountyProxy cacheをinvalidate（bountySvc.py:234-241）。 | 引数なし。サーバ側cache mutationは未実装なので CustomNotify。クライアントlocal cache invalidationはRPC前に行われる。サーバ状態・通知なし。 |
| SearchCharBounties | bountySvc.py:245; 全Search結果のcache処理 bountySvc.py:250-253。UIは bountyWindow.py:307,315-336 で (rank,bounty) を読む。 | 位置引数のtarget ID。存在する場合 [(PyInt rank, bounty KeyVal)]、順位なしは空PyList。DB readのみ。未接続時 CustomNotify。 |
| SearchCorpBounties | bountySvc.py:247; consumer/cacheは bountySvc.py:250-253, bountyWindow.py:307,315-336。 | SearchCharBounties と同じreply型でcorp pool順位を読む。未接続時 CustomNotify。 |
| SearchAllianceBounties | bountySvc.py:249; consumer/cacheは bountySvc.py:250-253, bountyWindow.py:307,315-336。 | SearchCharBounties と同じreply型でalliance pool順位を読む。未接続時 CustomNotify。 |
| CancelSellKillRight | bountySvc.py:255 が (killRightID,toID) を送る。UI: killRights.py:119; client側tracker/cache/event更新は bountySvc.py:255-258。 | IDはint/long。store未接続は CustomNotify; 未知/期限切れ/target不一致はNoneで何も変えない。成功時 sale price/restrictionを永続storeで消しNoneを返す。for-saleだった場合のみtype 119 (restricted) / 120 (open)、payload charID と任意 toEntityID をtargetへ通知。 |
| SellKillRight | bountySvc.py:262 が (killRightID,price,restrictedTo) を送り、返ったrow群をtrackerへ追加しcacheをinvalidate（262-268）。UIはownerIDまたはNoneを渡す killRights.py:324。 | ID/priceはPyIntまたはPyLong/PyReal、restrictedToはNoneまたはID。storeにsale stateを永続化し、replyは1件のKR KeyValを含むPyList。fields: killRightID PyLong, fromID,toID,restrictedTo ID型/None, expiryTime FILETIME PyLong, price PyReal/None。無効/他人所有/型不正は NoValidKillRight; expiredは KillRightExpired; store未接続は CustomNotify。成功時type 115 restricted / 116 open、payload charID,price とrestricted時の toEntityID をtargetへ通知。 |
| GetMyKillRights | bountySvc.py:274; panel consumer killRightsPanel.py:44-48; cacheも同method。 | 引数なし。期限内のKR KeyValのPyList。fieldsはSellと同じ。session characterが無い場合空PyList。state readのみ。characterありでstore/read sourceなしは CustomNotify。 |
| GetKillRightsOnCharacters | bountySvc.py:289 がID listを送る。trackerは eve/common/lib/killRightTracker.py:19-38 で toID ごとにcacheし、restrictedTo をsession所有者IDと照合する。 | positional ID list。available rightsのPyListで、各KeyValに killRightID,fromID,toID,expiryTime,price,restrictedTo。trackerが使うのは killRightID,toID,restrictedTo。session characterなしまたはID list空なら空PyList。store未接続で有効要求が来た時 CustomNotify。state readのみ。 |
| GetBountiesAndKillRights | bountySvc.py:302 は2つのID collectionを位置引数で渡す。clientは myKillRights,(bounties,killRights) として解く（293-304）。 | (bountyIDs,killRightTargetIDs) を受け、replyは (GetBounties rows, available kill-right rows) の2要素PyTuple。両方DB readのみ。bounty側/有効KR側いずれか未接続なら CustomNotify。 |

クライアント通知側では notificationconst.py:115-123 にbounty/KR notification typeがあり、notificationUtil.py:1309-1342 が各notification payloadを読む。bountySvc.py:153-173 はKR/bounty通知を受けてcacheやlocal eventを更新する。HostはAddToBounty通知をtransaction commit後に送り、KR売買通知をstore mutation後に送る。

## EveJS 261018.1照合・wire検証

EveJS 261018.1の src/server/src/services/bounty/bountyProxyService.js はAdd 378-460、GetBounties 462-469、combined 471-490、MyBounties 492-500、MyKillRights 502-510、KRs on characters 512-525、Sell 527-554、Cancel 556-592、top bounty 594-604、top hunters 606-616、search 618-628、GM 630-640を実装する。stateは playerBountyState.js:199-212,972-1031 と killRightState.js:520-621。Wire payload buildersは bountyProxyService.js:188-287。Hostは同じ可視reply/通知の形を提供し、永続性をPostgreSQL storeへ接続する。GMだけはEveJSがnullでackする一方、Hostは未実装操作を CustomNotify で表す意図的差分（DEVIATIONS.md）。

Host wire testsは実Macho HostServer/dispatcher経由で bountyProxy routeへ送信し、bounty row、KR row、ID PyInt/PyLong、transport machoVersion の除去、SellのNone positional arg、未接続provider/store、GM errorを確認する（BountyProxyKillRightHostWireTests.cs, BountyProxyHostWireTests.cs）。サービスunit checkは全19 routeとprovider/store未接続時のfalse success防止を確認する。

## 実行した確認と未実施範囲

- origin/mainマージ後focused Host wire: 2 passed、0 skipped。focused Crimewatch unit: 1 passed、0 skipped。Crimewatch.Tests全体は45件中10 passed、35 skipped、0 failed（PgFact/DB依存はDB URLなしでskip）。
- RuntimeTests.Bounty_proxy_read_rpcs_match_the_reference_routes_and_sale_access はPgFactのためDB URLなしではskip。PostgreSQLは使っていない。旧テストがprovider未接続時のcombined fake successを期待していたため、そのassertを削除した。BountyProxyKillRightServiceTestsが同じ未接続経路のCustomNotifyを検証する。
- Host.Tests全体（origin/mainマージ後）: 882件中555 passed、100 failed、227 skipped。失敗はEVE_PG_URLを要求するDB persistence / AssetSafety / ShipCosmetics系で、task指示によりDBは使用していない。
- 修正を48190335にpush後、focused agent-test run 40682794561 とpush-triggered ci.yml run 40682783308は両方成功し、失敗test/jobなし。
- 実際の本番CompositionからのPostgreSQL接続・DB transaction/restart検証、および実GUI client確認は実施していない。Host wireはHost socket/dispatcherを通るがテスト用providerを注入している。
