# Market ask reply contract

## Decision

Station and system ask maps now carry four-field `util.Row` values with header `price, volRemaining, typeID, stationID`. This supports both V24 consumers of the same server result:

- `quote.py:94-101` calls `GetStationAsks` directly and reads `ask.typeID`.
- `marketbase.py:649-667` calls `GetStationAsks` or `GetSystemAsks` and passes each value to `ConvertTuplesToBestByOrders`; `marketutil/__init__.py:8-11` expands the four values into `BestByOrder(price, volRemaining, typeID, stationID)`.
- `carbon/common/script/sys/row.py:23-36` defines `util.Row` attribute access and numeric indexing, so the value supports both call paths. The existing server-side `InventoryWire.UtilRow` uses the same `util.Row` wire representation.

EveJS v0.12.9 returns a raw four-value tuple from `buildBestByTuple` (`server/src/services/market/marketProxyService.js:334-341`), which supports the market-range conversion but not the direct `ask.typeID` consumer. eve.rs now uses `util.Row` for station and system asks; region-best values remain named KeyVals.

## Checks

`MarketStructureWireTests` verifies the row header, values, and marshal round trip. `MarketRpcTests`, `ProtocolTests`, and the Host market test assert the same row shape with populated orders. The graphical V24 client was not used.
