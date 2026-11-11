# Wallet audit fixes

## Current metadata follow-up (2026-11-11)

The earlier type-26 fix was only a partial metadata list. `GetEntryTypes` now serves all 218 native build-3396210 rows from the embedded snapshot described in [deepfix-contracts-trade-wallet.md](deepfix-contracts-trade-wallet.md). The raw type-26 name is `LP Store` with `entryJournalMessageID=234618`; V24 localizes its `entryTypeNameID=59818`. The registered auction Host regression also proves its wallet refs 67, 73, and 82 each resolve through `GetEntryTypes`.

This records the findings checked against `90-audit-eve-wallet-r14.md`, the V24.01 client at `/home/user/eve-logic`, and EveJS v0.12.9 at `/home/user/evejs-261028.1`.

## Confirmed findings

### P2: corporation wallet reads default to the selected division

V24's `WalletSvc.GetCorpWealth(accountKey=None)` calls `account.GetCashBalance(1, accountKey=accountKey)` (`eve/client/script/ui/shared/neocom/wallet/walletSvc.py:156-161`). The full client callsite search also found callers that omit the key in `corpISKTab.py:44`, `corpISKPanel.py:83`, `market/wallet_balance.py:105`, and the sovereignty cleanup views. The LP Store passes `session.corpAccountKey` (`eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:320-333`). EveJS resolves an explicit key first and then the selected session key (`server/src/services/account/accountService.js:723-744`).

`AccountService.GetCashBalance` now treats absent and `PyNone` keys as unspecified, honors positional/keyword precedence, then reads `corpAccountKey` or `corpaccountkey`; the existing key parser supplies its master-wallet fallback. The existing authorization check still applies to the resolved division. `WalletAuditHostRpcTests.GetCorpCashBalance_defaults_omitted_account_key_to_the_selected_division_over_host_wire` exercises actual registered Host RPCs against PostgreSQL for explicit keys 1000-1006, omitted and `None` arguments, precedence, both session spellings, master-wallet fallback, and denied access.

### P2: LP Store journal type 26 was missing from `GetEntryTypes`

V24 defines `refPaymentToLPStore = 26` (`eve/common/lib/appConst.py:667`). The purchase implementation in eve.rs records that ID (`dotnet/src/Eve.Services.Wallet/LpStorePurchases.cs:56-57,165-171`). V24 translates each `entryTypeNameID` in `accountsvc.GetEntryTypes` (`eve/client/script/ui/services/accountsvc.py:86-104`); the transaction panel dereferences `entryTypeName` and skips a row when it raises `AttributeError` (`transactionsPanel.py:30-39,126-143`).

`GetEntryTypes` now includes type 26 as `PaymentToLPStore`. Its localization message ID, 59818, is calculated from the repeated mapping in the checked-in metadata (`entryTypeNameID = entryTypeID + 59792`, e.g. 19 -> 59811 and 33 -> 59825). The matching `accountingEntryTypes.fsdbinary` is not present in the checked-out client or pinned SDE export, so 59818 is a verified calculation from neighboring/known rows, not a direct read of that binary. The UI's localized display string was not checked in a graphical client.

The audit's statement that EveJS has no implemented LP Store take handler is false. The cited `services/corporation/lpService.js` is not the V24 `LPStoreMgr` route: `server/src/services/evermarks/lpStoreMgrService.js:383-387` implements `Handle_TakeOfferForCharacter`, and `lpStorePurchaseState.js:548-620` settles the LP and ISK legs. EveJS does omit entry type 26 from its account metadata and does not pass an `entryTypeID` in that ISK settlement (`lpStorePurchaseState.js:593-605`); its wallet writers then use their defaults (`walletState.js:634-645`, `corporation/corpWalletState.js:449-467`). eve.rs intentionally follows the client's explicit reference type 26 rather than reproducing that EveJS journal classification.

`WalletAuditHostRpcTests.Lp_store_journal_entry_type_resolves_through_the_registered_account_rpc` goes through the registered LP Store and account Host routes, buys an offer, checks persisted journal values and notification order, and verifies type 26 is returned by `GetEntryTypes`.

### P3: a missing personal wallet must return numeric zero

For an authenticated character, the wallet client keeps a numeric wealth value and subtracts it from the new balance on `OnAccountChange` (`walletSvc.py:39-49,92-99`). EveJS returns a money real from zero when the wallet record is absent (`server/src/services/account/accountService.js:748-750`). `GetCashBalance` now returns `PyReal(0)` for an identified character without a wallet row and retains `PyNone` when there is no character ID. The new Host RPC test checks the wire result; `AccountTests.Without_a_character_balance_is_none_and_a_missing_wallet_balance_is_zero` covers both cases.

## Verification

The three new database-backed Host RPC tests were run before the implementation change and failed on the reported behavior: selected division returned the master balance, missing entry type 26 was absent, and a walletless character returned `PyNone`. They pass after the fixes.

All final .NET checks used `eve_p4` where database-backed fixtures were involved. Release builds completed with zero warnings for `Eve.Services.Wallet`, `Eve.Host.Tests`, `Eve.Services.CharacterInfo.Tests`, and `Eve.Corporations.Tests`. Focused runs passed with no skips: Host wallet/customs tests (7), additional Host LP Store/account wire tests (2), CharacterInfo account tests (3), Corporation protocol and account RPC tests (57), and Wallet transaction-history/LP Store service tests (7).

An EveJS runtime comparison was attempted, but loading its account service fails because its `better-sqlite3` native binding is missing in this checkout. Source tracing was completed; runtime parity against EveJS and graphical-client localization/display remain unchecked. Serena/codebase-memory MCP tools were not available, so local source search and reads were used.
