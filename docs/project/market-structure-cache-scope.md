# Market station ask cache scope

## Contract

`marketProxy.GetStationAsks` returns a `CachedMethodCallResult` whose details name the `stationid` scope and carry the resolved market location as `sessionInfoValue`. For an NPC station that value is the session's station ID; for an Upwell structure it is the structure ID resolved from the session. The ask rows must come from that same location.

## Source trace

- EveJS 261028.1 `server/src/services/market/marketProxyService.js:6306-6343` resolves the station/structure location and calls `buildCachedMethodCallResult` with `sessionInfo: "stationid"` and `sessionInfoValue: stationID`.
- eve.rs `dotnet/src/Eve.Services.Market/MarketProxyService.cs:216-241` resolves `stationid`, then `structureid`, then the persisted station fallback; it returns the same scope and the resolved ID. `dotnet/src/Eve.Services/ObjectCaching.cs:15-27` encodes those fields into the cached method result details.
- V24.01 `eve/client/script/ui/services/marketsvc.py:297-300` gates the quote call on being at a station or structure and calls `marketProxy.GetStationAsks`. The market window selects this call for station range in `eve/client/script/ui/shared/market/marketbase.py:649-667`; `eve/client/script/ui/shared/market/quote.py:95-100` also consumes its rows for a station ask purchase. Carbon's client cache derives its method key from the named session field in `carbon/common/script/net/objectCaching.py:411-419,468-475`.

## Verification

- `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart` exercises the production Host registry with database-backed orders. It now checks the wire metadata for an NPC station and for two Upwell structures in the same region, and verifies each structure request returns its own ask set. The structure scenario also checks denied market access.
- This checkout already had the EveJS-compatible production metadata behavior. The change for this job adds direct NPC-station metadata coverage; no service behavior change was needed.
- Release builds completed with warnings treated as errors and zero warnings for `Eve.Host.Tests` and `Eve.Market.Tests`. The filtered Host market tests passed 9/9 with no skips. The complete `Eve.Market.Tests` slice passed 149 tests and reported one skipped benchmark: `OrderBookBenchmarkTests.Placement_on_a_10000_order_book_locks_a_bounded_number_of_rows`.

## Not checked

- The graphical V24.01 client was not run, so the client-side cache behavior was not exercised in a live window.
- The EveJS handler was compared from its checked-in source; a live EveJS server scenario was not run for this metadata-only change.
