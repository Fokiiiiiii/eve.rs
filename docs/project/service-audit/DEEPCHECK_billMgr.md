# Deep check: billMgr

## Whole-client calls and consumed data

| Method | V24 caller, arguments and reply use |
|---|---|
| CharPayBill | eve/client/script/ui/shared/neocom/wallet/walletSvc.py:124-127 passes bill.billID positionally when debtorID is the character; reply is ignored and the client does not emit the corporation-bills event. |
| GetAutomaticPaySettings | corpBillsAutoPaidPanel.py:47 and corpBillsPayablePanel.py:46 read a dictionary keyed by owner ID; each setting has bill-type booleans and divisionID. corpBillsAutoPaySettingsPanel.py:36 reads the same map. |
| GetCorporationBills | panels at corpBillsAutoPaidPanel.py:87-88 and corpBillsPayablePanel.py:88-89, plus warLandingPage.py:184, consume a list of bill records and read owner/debtor/creditor, bill type, amount, due time, and auto-pay state. |
| GetCorporationBillsReceivable | corpBillsReceivablePanel.py:74-75 consumes a list of bill records. |
| PayCorporationBill | walletSvc.py:128-131 passes billID positionally and fromAccountKey=session.corpAccountKey by keyword; the client then emits OnCorpWalletBillsChanged. |
| SendAutomaticPaySettings | corpBillsAutoPaySettingsPanel.py:83 passes the settings map positionally; its reply is ignored. |

The full tree search found only these callers. Character, corporation, and alliance debtors take separate paths in walletSvc.py:124-133.

## Host and EveJS behavior

CharacterServiceList.cs:70-75 registers BillMgrService. The corporation bill list, payable path, receivable path and automatic-pay settings use CorporationStore, CorporationOfficeStore, WalletTransactions, and corporation role checks in BillMgrService.cs. The normal corporation pay method returns no payload and its client-visible notification is emitted by the client after the RPC reply.

CharPayBill is a production gap: the registered service at CharacterServiceList.cs:70 does not supply PayCharacterBillAsync. A valid character bill therefore raises CorporationError BILL_NOT_PAYABLE; it never debits a wallet or marks a bill paid. ActivityAlphaBillRpcHostWireTests.cs:115-137 proves a successful wire reply only with an injected fake callback. BillMgrHostRpcTests.cs:51 and ActivityAlphaBillRpcHostWireTests.cs:104-113 cover the no-provider refusal shape.

EveJS v0.12.9’s Handle_CharPayBill at server/src/services/account/billMgrService.js:305-316 loads the bill, calls payBillFromCharacter with the session character, throws the payment failure when unsuccessful, and returns None only after success. Its real bill owner is not wired in eve.rs: TODO.md:73 and TODO.md:2180 identify the missing personal bill/wallet transaction and idempotent state update. The corporation bill store is not a valid replacement for that personal debtor flow.

## Exact boundary and status

No new payment behavior is claimed. The current fail-closed UserError prevents a false paid success; a complete fix needs a character-owned bill record, atomic wallet debit plus paid-state transition, idempotency, and Host composition. The call needs strict wire tests for both insufficient funds / absent bill and successful persistent retry before a production callback can be wired.

No PostgreSQL database or graphical V24 client was used for this audit. No bill code or test was changed in this documentation group.
