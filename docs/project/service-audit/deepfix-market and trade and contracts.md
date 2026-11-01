# Market, trade and contracts deepfix handoff (cx5)

## Done in this increment

Fixed the sales-tax context for an existing player ask when it fills. The market context now uses the solar system of the order venue, so a pilot who leaves Zarzakh after listing an ask still receives the pirate-aligned tax reduction when that order fills. The exact path is `MarketTrading.Context` and its seller-side call in `dotnet/src/Eve.Market/MarketTrading.cs:92-101,325-326`.

The V24 client sends `stationID` in `marketsvc.py:255-266`; the UI reaches that wrapper from `buyThisTypeWindow.py:700`. Sell orders flow through `sellMulti.py:461-463` to `marketsvc.py:274-278`. After a successful mutation, the Host publishes `OnOwnOrdersChanged`; the client invalidates market order/book/history caches and raises `OnMarketEscrowChanged` in `marketsvc.py:113-127`. The server writes the fill, balances, item custody and receipt in one market transaction before the notifier runs.

EveJS 261028.1 derives the Zarzakh market discount from the venue station's `solarSystemID` and the character's pirate faction in `server/src/services/market/marketRules.js:142-173`. The V24 station and order RPC shape does not send the character's current system. EVE University's [Market](https://wiki.eveuniversity.org/Market) documents the 7.5% sales-tax base and Accounting reduction; CCP's [Zarzakh guide](https://support.eveonline.com/hc/en-us/articles/11158425410716-Zarzakh) describes market bonuses for pirate-aligned pilots. Pinned build 3396210 SDE maps faction keys 500010/500011 to Guristas Pirates/Angel Cartel. EveJS checks 500018/500019, which the pinned SDE maps to Mordu's Legion Command/Sansha's Nation; eve.rs keeps the SDE-aligned IDs. This intentional EveJS difference is also recorded in `docs/reference/compat/DEVIATIONS.md`.

Regression: `TradingTests.Zarzakh_sales_tax_uses_the_order_station_after_the_seller_changes_station` was run against `eve_p5`. With the old context, it failed (expected seller balance 9,996.25 ISK; actual 9,992.50 ISK). With the venue context, it passed. The sale is 100 ISK gross; the pinned rule produces 3.75 ISK tax after the Zarzakh discount.

## Checks

- The prior main CI run failed because `StoreTests.Home_station_cooldown_migration_preserves_existing_rows` did not include migration `0016_character_structure_location` in its expected list. Reproduced on `eve_p5`, corrected the list in `dotnet/tests/Eve.Characters.Tests/StoreTests.cs:196`, and confirmed all 28 `StoreTests` pass after the latest main merge. Fix commit `502a9eeb` was pushed as `34f4d4ad`; selected Actions run `41318781230` passed. `ci-status.py failures 34f4d4ad` found no push-triggered run, but main CI run `1377` for `43ece20c` completed successfully.
- `dotnet build dotnet/tests/Eve.Market.Tests --no-restore -p:UseSharedCompilation=false`: passed.
- Market sim/database tests covering the changed engine and TradeRig callers: 94 passed, 0 skipped, on `eve_p5` through `tools/dev/db-lock.sh`.
- Re-run after merging `34f4d4ad`: 76 market sim/database tests passed, 0 skipped, and 13 Host market/contract tests passed, 0 skipped, on `eve_p5` through `tools/dev/db-lock.sh`.
- `MarketTradeHostWireTests`: 1 passed, 0 skipped, on `eve_p5` through `tools/dev/db-lock.sh`. The first attempt exposed that this pre-existing lane DB lacked `sde_mission`; that static schema was copied additively from the complete local SDE database before rerunning.
- The focused regression was confirmed failing before the fix and passing after it.
- Market fix commit `7b952f63` was pushed to main. Selected Market/Host Actions run `41320090744` passed, and push CI run `41320080436` completed successfully with no failed jobs.
- No real graphical client session was run.

## Item-exchange contract trace

The V24 create window builds the `ContractStruct`, calls the contract service, and only retries with the confirm flag after the client receives `NotEnoughCargoSpace` (`createContractWnd.py:888-892`). The service forwards `CreateContract` and clears its cache on success (`contracts.py:551-561)). Acceptance first checks the contract endpoints, shows the items and ISK legs to the pilot, and calls `AcceptContract(contractID, forCorp)`; courier completion, failure and rejection use statuses 4, 7 and 6 (`contracts.py:344-430,475-550)). The Host registers ContractProxyService in `CoreServices.cs:76`; its create, accept and complete RPC handlers are at `GameRpcServices.cs:141,312,365`. The persistence writer moves offered items into escrow, records fees and wallet legs, and emits the create notification (`PersistenceContractWriteModel.cs:35-170)); item-exchange acceptance transfers the requested/offered items and settles the wallet legs transactionally (`:174-294`).

Current EveJS 261028.1 routes the same CreateContract, AcceptContract and CompleteContract calls through its contract settlement state machine (`server/src/services/_other/contractProxyService.js:837-867`, `server/src/services/contracts/contractRuntimeState.js:1949-1964,3146-3167,3778-3801`). EVE University's [Contracts](https://wiki.eveuniversity.org/Contract) page describes item-exchange and courier contracts. The implementation and test behavior agree on this sampled flow; no contract code change was needed.

Database-backed evidence on `eve_p5`: `ContractReadModelTests.Item_exchange_acceptance_transfers_requested_and_offered_items_and_settles_wallet_legs_atomically` passed (including concurrent duplicate acceptance); Host persistence/wire tests for item-exchange creation, courier acceptance/completion/rejection, and expired auction settlement passed (4 passed, 0 skipped). The client source also shows the local success/cache transitions and the cargo-space retry. Contract auction bid placement/outbidding and all search/filter consumer paths were not part of this increment.

## Remaining audit scope

This increment verifies the daily market order/fill path and the item-exchange, courier terminal-state, and expired-auction settlement slices. It does not establish full coverage for every marketProxy reader, trade transaction read, contract search/filter flow, auction bid placement/outbidding, or contract edge-case error path. The earlier `DEEPCHECK_marketProxy.md` and `DEEPCHECK_contractProxy.md` remain broader maps, not evidence that those remaining slices were checked here.
