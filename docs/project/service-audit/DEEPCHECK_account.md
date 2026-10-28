# account 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリーとcoverage CSVのaccount 22行。重複した同一callsite行をまとめると7 method。

## 登録と共通wire

EconomyRpc.cs:42-46 はAccountServiceにwallets, corporations, economyWalletsを注入してaccount routeを登録する。clientは通常 positional args を送り、corp GetCashBalance / GiveCash / GiveCashFromCorpAccountの一部だけでアプリkwargsを使う。machoVersion=1はtransport kwargsでありアプリ引数ではない。IDはPyInt/PyLong両方を扱う。通貨はPyRealでreplyする。

## メソッド別結果

| メソッド | 全client caller / reply consumer | Host state, errors, notifications |
|---|---|---|
| GetCashBalance | walletSvc.py:41,48がpersonal [0]をwealthへ保持。corpはwalletSvc.py:158で [1] と keyword accountKey=accountKey、corp_ui_home.py:197で [1,1000]。httpGate.py:34もpersonal [0]。 | personal/corp balanceはPyReal、personal wallet無しはNone。corp division readを拒否する時は正確に CrpAccessDenied(reason=(101, "UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))。walletSvc.GetCorpWealthはUserErrorを0へ変換する (:152-159)。DB readのみ。 |
| GetDefaultContactCost | accountsvc.py:154。Noneならclientは-1へcache。 | nullable integerまたはNone。contact-cost callbackからcharacter設定を読む。 |
| SetContactCost | accountsvc.py:148はcost、:160はNone。clientはNoneをBlockAllとして扱う。負値はremote呼び出し前にContactCostNegativeAmountを送出 (:145-150)。 | 保存callbackへnullable costを渡し、replyはNone。AccountContactCostHostWireTestsで数値とNoneを往復確認。 |
| GetTransactions | accountsvc.py:116 personal (accountingKeyCash, year, month, False)、:129 corp (accountKey,year,month,True)。transactionsPanel.py:121-140は各entryからownerID1/ownerID2をprimeしTransactionへ渡す。transactionsPanel.py:27-40,188-191がtransactionDate, sortValue, currency, amount, balance, entryTypeID, referenceID, descriptionを読む。 | PyList of KeyVal rows。Host rowはtransactionID/date/referenceID/entryTypeID/ownerID1/ownerID2/accountKey/amount/balance/description/currency/sortValueを含み、日付はFILETIME PyLong、ISKはPyReal。Corp read permission errorはCrpAccessDenied。通知なし。 |
| GetWalletDivisionsInfo | accountsvc.py:131-135。結果を5分cache (:131-136)。corpDivisionsPanel.py:34-51はlistを反復しdivision.keyとdivision.balanceを読む。 | corpはread可能な1000..1006 divisionだけを返す。personalはkey=1000のcash rowだけ。dependency/character欠落ならNone。read-only。 |
| GiveCash | giveIskPanel.py:103-106はtarget/amountを渡し、truthy replyでsuccess UIを確定。transferMoneyWnd.py:148もpersonal callするがreplyを捨てる。 | toAccountKeyとreasonはkwargsとして読む。成功時transaction commit後にOnAccountChange通知を送り、personal destinationはTrue、corp destinationはNone。invalid args / insufficient funds等はNone。wallet上限はCustomInfo、reason長超過はWALLET_REASON_LIMIT_EXCEEDED。 |
| GiveCashFromCorpAccount | transferMoneyWnd.py:150は(toID, amount, fromAccountKey) とtoAccountKey, reason kwargsを渡し、replyは使わない。 | corporation permissionをpersisted membershipから確認し、source/destination両側のwallet stateを一 transactionでcommitする。成功後OnAccountChangeを通知、replyはNone。アクセス拒否はCrpAccessDenied(reason=(101,"UI/Corporations/AccessRestrictions/NoAccessToWalletDivision"))、同一division移動はCustomInfo、reason長超過はWALLET_REASON_LIMIT_EXCEEDED。 |

GiveCashのnotification orderはAccountWalletHostRpcTests.csのpacket captureでOnAccountChangeがCallRspより先に届くことを検査している。recipient/corp account changeも通知される。wallet transaction retry用OperationKeyはsession/call ID由来。corp routeのbanking stateはWalletTransactionsとCorporationStoreに保存される。

## EveJS比較と残る差

EveJS v0.12.9 server/src/services/account/accountService.js:677-687,735-770,831-860 は同じbalance, divisions, transactions fieldsとcorporation read refusalを実装し、walletState.js:430-459はjournal全体からaccountKey/dateをfilterする。Host AccountService.cs:82-103は先にjournalを100 personal / 1000 corpへ切ってから指定月でfilterする。このためjournal件数が上限を超える時、古い月を要求しても該当行が落ちる。EveJSに対する機能差で、修正はDB fixtureを使う月別履歴wire regressionが必要。今回の指定はDBなしのため変更していない。

さらにEveJS GetWalletDivisionsInfoのpersonal branchはcashとaurumの2行を返す(accountService.js:735-770)。Hostはcash 1行のみ。現clientのdivision panelはkey/balanceを消費するが、personal wallet panelでaurum rowを必要とする画面のwhole-tree callerは今回特定できていないため、wire mismatchの確定とはしていない。

## Wire testsと実行範囲

- GetCashBalance/GiveCash/GiveCashFromCorpAccountのpersist+notification testはAccountWalletHostRpcTests.cs:15-130のPgFactだが、このjobでは実行していない。GetTransactions と GetWalletDivisionsInfoの専用Host wire testは未追加。
- Account_contact_cost_calls_use_character_state_and_keep_client_wire_values: 1 passed、0 skipped。PostgreSQL databaseは使っていない。
- 実client GUI、account transaction DBの履歴上限、DB restart後のwallet journal persistenceは確認していない。


## 引数型
GetTransactionsは accountKey, year, month, isCorpWallet の順にpositional。個人/法人フラグはFalse/True。GetCashBalanceの法人呼び出しは positional isCorpWallet=1 と keyword accountKey、GiveCashの法人宛destination divisionとreasonはkwargs、GiveCashFromCorpAccountはsource divisionを第3 positional、destination division/reasonをkwargsで送る。Contact cost setterはcostのintegerまたはNone。balance / amountはclient numericで、HostはPyInt/PyLong/PyReal/string numberを処理する。machoVersion=1はtransport kwargs。
