# Contract Proxy RPC handoff: RPC-01232–RPC-01234

## Scope and client evidence

Whole-tree V24.01 eve-logic search found one RPC callsite for each row.

- **RPC-01232 contractProxy.PlaceBid:** ContractsSvc.DoPlaceBid calls PlaceBid(contractID, bid, forCorp) at eve/client/script/ui/shared/neocom/contracts/contracts.py:638-667. The enclosing ContractsSvc.PlaceBid returns the boolean at lines 606-620; ContractEntry.PlaceBid and ContractDetailsWindow.PlaceBid use it to reload at contractentry.py:189-192 and contractsDetailsWnd.py:607-612. Bidding permits a positive integer amount; the retry prompt for a too-low bid is at contracts.py:608-610.
- **RPC-01233 contractProxy.SearchContracts:** the only RPC call is ProxySvc(...).SearchContracts(...) with the V24 keyword filters at eve/client/script/ui/shared/neocom/contracts/contractsearch.py:1364-1367. The panel consumes contracts, numFound, searchTime, and maxResults at lines 1367-1377, then reads each result's contract, items, and bids at lines 1384-1393. The same method is entered by initial search, paging, “my contracts”, and related-contract flows at lines 892, 946, 1070, and 1109.
- **RPC-01234 contractProxy.SplitStack:** ContractItemSelect.SplitStack sends station, item, quantity, corporate flag, and division flag at eve/client/script/ui/shared/neocom/contracts/contractentry.py:427-432; the contract window wrapper calls the RPC at contracts.py:691-694 and, on a true reply, updates the selected quantity and refreshes at lines 694-700. CreateContractWnd.OnItemSplit subtracts the chosen quantity and removes an exhausted row at createContractWnd.py:1844-1848. The separate regular inventory flow calls invCacheSvc.GetInventoryMgr().SplitStack and invalidates the local inventory at eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:385-403; it is not an additional contractProxy RPC caller.

## EveJS comparison

EveJS v0.12.9 server/src/services/_other/contractProxyService.js implements SearchContracts at lines 721-724 by returning the runtime search result; contractRuntimeState.js:3889-3923 filters records and returns the page, total count, and elapsed time. The server adapter supplies the four result fields consumed by the client. EveJS PlaceBid (854-857) and SplitStack (864-867) only audit the call and return null, so they do not provide a working mutation reference. eve.rs uses its existing transactional auction writer for PlaceBid and the authoritative PgItemStore split operation for SplitStack, preserving the V24 caller's boolean and state-transition expectations.

## eve.rs changes

- PlaceBid and SearchContracts already had Host handlers, authoritative providers, and persistence/read-side behavior. ContractProxyRowsWireTests now sends both through the actual CoreServices registration and Host socket path. The bid wire case sends a positive integer amount of 1000 ISK; the existing persistence regression covers auction minimum-bid validation. The search wire case sends the client filter keywords plus transport machoVersion and checks all four consumed top-level fields and forwarded query values.
- SplitStack is registered on the existing contractProxy service. It validates the caller's station, current corporation Contract Manager authority for corporation stacks, item owner/location/division and quantity, then calls PgItemStore.MoveStackAsync in a PostgreSQL transaction with the same owner, station, and flag. The returned boolean drives the existing client refresh. Item changes are serialized against the persisted item snapshot; a race or locked item is returned as the contract's named refusal.
- Coverage rows are updated in service-audit/EVE_RS_COVERAGE.csv; the broader contract status row in TODO.md points to these wire regressions.

## Verification and remaining checks

Local checks passed: Eve.Host.Tests and Eve.Services.Core.Tests build with zero warnings/errors; focused registered-Host PlaceBid and SearchContracts wire tests passed individually (each 1 passed, 0 skipped). The initial selected Actions run had an incorrect class-name filter (zero tests); the corrected run executed all three new wire cases and exposed a test-only bytes-key assertion, which is fixed in commit 3cdc9827 and pushed in 062ede32. The corrected post-fix Actions run is pending. No PostgreSQL database was used, per task constraints. Real graphical-client acceptance was not performed.
