# Wallet journal persistence and recovery handoff

Date: 2026-11-01
Worktree: `/home/user/wt-cx4`

## Scope and result

This increment covers a core daily flow: opening personal or corporation wallet transactions, selecting the last 30 days or a calendar month, and recovering the same persistent journal after a Host restart and a wallet-schema upgrade.

Two query defects were fixed at the account service boundary:

- Month queries used to fetch only the latest 100 personal or 1,000 corporation journal IDs, then filter those rows by the requested month. Older rows in the selected month were omitted.
- Last-30-day queries also capped rows by journal ID before filtering by time. A busy wallet could therefore omit valid rows within the 30-day window.

Both modes now apply their time bounds in PostgreSQL and return every matching row. Migration `009_journal_account_time` adds an index for the owner, account division, and timestamp filter. It does not rewrite journal data.

## Whole-client path and contract

The V24.01 default date option is `(None, None)`, defined by `eve/client/script/ui/shared/neocom/wallet/panels/transactionsPanel.py:25`. The date menu presents that value as “Last 30 Days” and offers six months of calendar selections at `transactionsPanel.py:105-110`; `_GetDateTimeSelection` returns the selected value at `:156-157`.

- Personal caller: `personalTransactionsPanel.py:9-11` -> `accountsvc.py:114-121` -> `GetAccountMgr().GetTransactions(1000, year, month, False)`.
- Corporation caller: `corpTransactionsPanel.py:11-14` -> `accountsvc.py:123-128` -> `GetAccountMgr().GetTransactions(accountKey, year, month, True)`. The selected division key is preserved.
- The account service caches by year/month (and division for corporation rows) at `accountsvc.py:119-128`. On a cache miss it sends that four-argument call.
- The panel consumes the returned rows at `transactionsPanel.py:126-146`: it primes owner IDs, builds `Transaction` objects and loads the whole result into a client-side list. The UI pages and sorts that loaded list locally at `:151-193`; it does not request later pages from the server. `baseTransactionsPanel.py:16-20` adds the client-required `currency=ISK` and `sortValue=transactionID` fields.
- Display reads the date, type, amount, balance, description, and owner IDs at `transactionsPanel.py:172-190`; donation context menus use both owner IDs at `:192-199`. Therefore server-side truncation changes what the client can display.
- A character-less account call returns an empty list. Corporation division access failures remain `CrpAccessDenied` with the existing wallet-division reason. The corporation panel has no wallet-specific catch that changes the remote error. These paths are in `AccountService.cs:61-87`. Reads emit no wallet mutation notification.

The Host registers the same account service with its wallet and corporation providers at `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:42`. `AccountService.GetTransactions` parses the optional date, derives a half-open month range or a 30-day lower bound, and maps persisted journal rows to the existing KeyVal wire fields at `dotnet/src/Eve.Services.Wallet/AccountService.cs:61-119`. The store applies owner, account-key, and time predicates before ordering at `dotnet/src/Eve.Wallet/WalletTransactions.cs:219-259`.

Whole-tree callsite search found the two wallet-panel paths above for this service method; market transaction panels call separate marketProxy RPCs. The client keeps its transaction cache until its own existing refresh path runs.

## Reference comparison

EveJS 261028.1 handles the same four arguments in `/home/user/evejs-261028.1/src/server/src/services/account/accountService.js:882-912`. Its personal journal filter at `server/src/services/account/walletState.js:472-492` and corporation filter at `server/src/services/corporation/corpWalletState.js:1160-1172` filter the stored transaction list by account and selected month, or by the last 30 days, without the 100/1,000-row limits. Its `normalizeNumber` preserves the no-month meaning of null at `accountService.js:180-217`.

This is a journal query and schema-index change; it introduces no gameplay statistic or formula, so no SDE or EVE University mechanic was used to derive behavior. Reply fields, access errors, ordering, and notification behavior remain unchanged.

## Changes

- `dotnet/src/Eve.Services.Wallet/AccountService.cs`: query month and recent rows by timestamp in PostgreSQL without truncating the returned set.
- `dotnet/src/Eve.Wallet/Migrations/009_journal_account_time.sql`: add an index on `(owner_id, account_key, time_ms DESC)`.
- `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs`: exercise a prior-version wallet schema, write 125 dated journal rows, apply migration 009, restart the account-service registry, and verify the real RPC for client-shaped last-30-day `None` arguments and month arguments. It checks all 128 recent personal rows, the requested month’s exact row count, the existing corporation response, balances, and notification order.

## Regression evidence and checks

The new Host-wire assertion was observed failing with the former query order: the selected month returned 97 rows instead of 125. A separate run with the former recent-row cap returned 100 instead of 128. With the timestamp-bounded SQL query and migration applied after journal writes, the Host-wire case passes.

Local checks used PostgreSQL database `eve_p4` through `tools/dev/db-lock.sh`:

- `dotnet build dotnet/src/Eve.Wallet`: passed, 0 warnings/errors.
- `dotnet build dotnet/src/Eve.Services.Wallet`: passed, 0 warnings/errors.
- `dotnet build dotnet/tests/Eve.Host.Tests`: passed, 0 warnings/errors.
- Host account wallet restart/wire test: 1 passed, 0 failed, 0 skipped.
- `Eve.Wallet.Tests`: 42 passed, 0 failed, 0 skipped.
- Wallet transaction-history test: 1 passed, 0 failed, 0 skipped.
- Corporation protocol class, including persisted transaction and division access cases: 51 passed, 0 failed, 0 skipped.

The native graphical V24.01 client and an executable EveJS scenario were not used. This increment does not audit every other persistence store; existing character, inventory, skills, clone, and travel handoffs cover adjacent player flows.
