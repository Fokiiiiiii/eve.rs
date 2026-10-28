# Insurance / market Host RPC coverage handoff

## Implementation and verification scope

Every requested insurance method is registered on the production `insuranceSvc` through `CharacterServicesComposition.MailRpc` and uses the persisted insurance read/write models (`dotnet/src/Eve.Host/Rpc/MailRpc.cs:141-145`, `dotnet/src/Eve.Host/Composition/CharacterComposition.cs:201-212`). Market methods are registered through `CharacterServicesComposition.EconomyRpc` and the production `MarketProxyService` (`dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:31-40`). Insurance purchase and cancellation write contract and wallet state; market fills, cancellations and transaction history use the same market, inventory and wallet stores. No no-op handlers or duplicate stores were added.

`dotnet/tests/Eve.Host.Tests/InsuranceRpcHostWireTests.cs` exercises all six insurance calls through the Host registry: price and bulk-price replies, an empty initial state, a valid purchase from a persisted ship, contract lookup/list wire fields, duplicate-purchase refusal, the client's named `voidOld=True` replacement retry, cancellation and the resulting state. The wallet balance is checked after purchase and cancellation.

The replacement retry mirrors V24.01 `base_insurance.py:533-542`: the client catches `InsureShipFailedSingleContract`, asks whether to replace the contract, and retries `InsureShip(..., voidOld=True)`. The Host regression asserts a new contract ID and exactly one listed contract afterward. Its PostgreSQL-backed run is assigned to GitHub Actions; the local `Eve.Services.Core.Tests` recontract test passed 1/1 without a database. No graphical-client run was performed.

Market read wire cases are in `MarketRpcHostWireTests.cs`; buy/fill, wallet debit, transaction contents, cancellation, orders, asks, concurrency and restart persistence are exercised by `MarketHostTests.cs`. These tests use `HostComposition.BuildAsync` and the registered `marketProxy`.

## V24.01 client callers and consumers

The whole `/home/user/eve-logic/eve/client` tree was searched for every target RPC name and its wrapper/data consumers.

| Rows | Direct call and client use |
| --- | --- |
| RPC-01706–01707 `GetContractForShip` | `base_insurance.py:123-124`; called from ship information and contract creation at `infoContentBase.py:655`, `panelOverview.py:191`, and `createContractWnd.py:1379`. Consumers inspect `ownerID` and `endDate` at `infoContentBase.py:655-656`, `panelOverview.py:191-192`, and `base_insurance.py:493-504`. |
| RPC-01708–01709 `GetContracts` | `base_insurance.py:113-121`; the second call supplies owner kind 1 for corporation contracts when the session has accountant role. Results become a map keyed by `shipID` and populate the insurance window at `base_insurance.py:286-300`; inventory menu actions also consume the list at `menuSvcExtras/invItemFunctions.py:522`. |
| RPC-01710–01712 `GetInsurancePrice` | `base_insurance.py:126-134`; cached price gates ship eligibility at `:160-177` and builds coverage quotes at `:184-192`, `:466`. The information panels and tooltip also use the value at `infoContentBase.py:656`, `panelOverview.py:192`, and `insuranceTooltip.py:18`. |
| RPC-01713–01715 `GetInsurancePrices` | `base_insurance.py:136-143`; returned typeID-to-price entries update `insurancePrice`, then drive the same eligibility and quote consumers at `:160-192`. |
| RPC-01716–01717 `InsureShip` | The bound insurance manager receives item, quoted amount and owner kind at `base_insurance.py:532`; retry passes `voidOld=True` at `:541`. The client catches the existing-contract error at `:533-542`, then refreshes via `GetContracts` at `:493`. |
| RPC-01718 `UnInsureShip` | The bound insurance manager call is at `base_insurance.py:400`; the insurance window updates its contract view at `:390-401`. |
| RPC-02061 `BuyMultipleItems` | `marketsvc.py:266-271`; returned created-order rows are read for `typeID` to emit the order-placed event at `:270`. |
| RPC-02062 `CancelCharOrder` | `marketsvc.py:280-282`; the caller consumes no return value. Cache invalidation for own orders and ask views is at `marketsvc.py:113-120`. |
| RPC-02063–02064 `CharGetTransactions` / `CorpGetTransactions` | Direct calls are in `ui/shared/marketSvc.py:17-29`. The transaction panel reads the returned transaction fields at `:37-64`; wallet panel caller is `ui/shared/neocom/wallet/panels/marketTransactionsPanel.py`. |
| RPC-02065 `GetCharEscrow` | `marketsvc.py:398-407`; consumer reads `iskEscrow` and `itemsEscrow`, converting missing values to zero. |
| RPC-02066 `GetCharOrders` | `marketsvc.py:388`; feeds personal order displays and the cancel/modify flows (`:113-120`, `:281`, `:389`). |
| RPC-02067 `GetCorporationOrders` | `marketsvc.py:390-391`; corporation order listing/export consumes the result at `ui/shared/market/marketOrdersWnd.py:75` and `ui/shared/market/corpMarketOrders.py:11`. |
| RPC-02068 `GetHistoryForManyTypeIDs` | `marketsvc.py:317-318`; the returned typeID-to-(old,new) histories update the cache used by `:322-336`. |
| RPC-02075–02076 `GetPlexNewPriceHistory` / `GetPlexOldPriceHistory` | `marketsvc.py:334-335`; both history sequences are passed into `GetHistoryRowList` at `:339-340`. |
| RPC-02079 `GetStationAsks` | `marketsvc.py:296-299`; market quote and range selection consume its ask mapping at `ui/shared/market/quote.py:90-100` and `marketbase.py:653-661`. |
| RPC-02080 `GetSystemAsks` | `marketsvc.py:301-304`; range selection consumes its ask mapping at `ui/shared/market/marketbase.py:661-663`. |

## EveJS v0.12.9 comparison

Insurance delegates all six operations through `server/src/services/insurance/insuranceService.js`: contracts `:26-35`, price methods `:46-60`, ship lookup `:62-67`, purchase `:69-83`, cancellation `:85-89`. Market proxy handlers cover station/system asks `server/src/services/market/marketProxyService.js:6211-6292`, history and owner rowsets `:6424-6531`, transactions/escrow `:6582-6694`, PLEX history `:6799-6821`, cancellation `:6823-6856`, and multi-buy `:6915-6965`. eve.rs preserves the client-visible return categories and mutations while using its persisted domain stores. No compatibility deviation was introduced.

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -p:BuildProjectReferences=false --no-restore` (0 warnings, 0 errors).
- The first build attempt reported missing NuGet assets; restore completed during the subsequent successful build.
- Database-backed Host tests were not executed locally, per the no-database instruction. Focused Actions runs 40224752533, 40224967561 and 40225210644 all failed in `actions/setup-dotnet` before test startup: the .NET 10.0.14 runtime and 10.0.403 SDK downloads returned HTTP 409. Push CI run 40224742683 failed at the same setup step; no push CI run was found for 2babe92a at the latest status check. No test cases started. Retry when the distribution endpoint recovers.
- The graphical V24.01 client was not used.
