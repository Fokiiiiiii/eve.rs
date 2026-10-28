# Deep check: structureCompressionMgr

## Client call paths and wire shapes

| Method | V24 caller and consumed result |
|---|---|
| CompressItemInStructure(itemID) | packages/itemcompression/client/itemCompression.py:17-23 passes one positional item ID. If the result is not None, it unpacks six values: sourceItemID, sourceTypeID, sourceQuantity, outputItemID, outputTypeID, outputQuantity, then displays source/output type and output quantity. |
| DecompressGasInStructure(itemID) | packages/itemcompression/client/gasDecompression.py:8-17 passes one positional compressed-gas item ID, unpacks the same six values, and displays a result only when outputItemID is not None and outputQuantity is nonzero. |
| GetMyGasDecompressionEfficiency() | gasDecompression.py:20-23 and ui/decompression_window.py:101-102,187-194, no args; caller destructures structureEfficiency and characterEfficiency and computes the combined value locally. |

The client accepts a sequence for the six-value result and two-value efficiency result. It ignores machoVersion after transport dispatch; it sends no method keywords.

## Host wiring and state

The generic StructureRpcService registers the three methods for structureCompressionMgr in StructureRpcServices.cs:77,101-129. StructureRpc.cs:71 registers that service in Host composition. StructureComposition.cs:434-436 wires CompressAsync, DecompressAsync and EfficiencyAsync to StructureCompressionAuthority. CompressionResult emits None for no outcome and a six-element PyList of integer fields after a completed mutation. Efficiency returns a two-element PyList of numeric values.

The callback owner performs the item operation against the existing inventory/structure authority. A valid mutation returns the actual source/output item identities and quantities; the client confirmation is driven by that result. There is no service-specific player progress store. EveJS v0.12.9 uses the same six-field result tuple and checks docked structure, ownership, item location and allowed type before it mutates inventory (server/src/services/mining/structureCompressionMgrService.js:71-113). Its result synchronization is in lines 31-64. The Host uses the shared structure/inventory owner rather than an in-memory service cache.

## Gap and error behavior

The generated CSV labels all ten duplicate rows MISSING because this service is routed through generic StructureRpcService rather than a class named after the service. The active Host route and production callbacks exist. The remaining gap is real Host wire coverage: StructureRpcServiceTests.cs:10-12 checks the method catalog/service defaults, while StructureCompressionTests.cs is database-backed and is skipped without EVE_PG_URL. It is not a passing no-database wire test.

The service currently returns None for a missing compression/decompression callback outcome, which the client treats as no result. Production composition supplies the actual authority callbacks. Efficiency defaults to (0, 0) only when constructed without a provider; production composition wires the provider. The authority maps invalid-operation paths to CustomNotify using these client-facing messages: “That item cannot be compressed in a structure.”, “That item is not compressed gas.”, “You must be docked at a structure to compress here.”, “The reprocessing service of this structure is offline.”, “That item is not yours or cannot be used here.”, and “That item is not in your hangar or your ship at this structure.” Its item mutations and inventory synchronization still need the production-backed Host test before this service is marked tested.

No PostgreSQL database or graphical V24 client was used in this pass. No code or Host test was changed.
