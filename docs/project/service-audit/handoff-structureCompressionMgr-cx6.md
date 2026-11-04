# structureCompressionMgr Host coverage handoff (cx6)

## Completed

The ten duplicate coverage rows RPC-02617 through RPC-02626 now record `IMPLEMENTED-TESTED`. The prior MISSING labels came from the audit's class-name route scan: this service is served by the shared `StructureRpcService`.

No gameplay backend was duplicated. Production Host wiring in `dotnet/src/Eve.Host/Rpc/StructureRpc.cs:63-75` registers the service, and `dotnet/src/Eve.Host/Composition/StructureComposition.cs:434-436` connects all three callbacks to `StructureCompressionAuthority`. The authority uses the structure and inventory PostgreSQL stores and pinned SDE data.

## Client contract

A whole-source search and call trace of V24.01 eve-logic established:

- `itemcompression/client/itemCompression.py:18-23`: sends one positional item ID. If the reply is not `None`, unpacks six fields and displays source/output types and output quantity.
- `itemcompression/client/ui/compression_window.py:84-114,366-370` and `eve/client/script/ui/services/menusvc.py:3405-3406`: selected items route through that helper; the structure window marks submitted items compressed after the call.
- `itemcompression/client/gasDecompression.py:9-17` and `eve/client/script/ui/services/menusvc.py:3408-3409`: sends one item ID and unpacks the same six fields; it suppresses the confirmation when output ID is `None` or output quantity is zero.
- `itemcompression/client/gasDecompression.py:20-24` and `itemcompression/client/ui/decompression_window.py:101-109,187-194`: consume the two efficiency values and calculate/display the total locally.

EveJS 261028.1 uses the same six-value compression/decompression result and two-value efficiency result (`server/src/services/mining/structureCompressionMgrService.js:71-113`). Host keeps the list shape and uses actual SDE-derived output and efficiency values.

## Checks

- `StructureCompressionHostWireTests.Structure_compression_rpcs_use_registered_host_routes_with_persisted_mutations_and_notifications` passed through the Host TCP dispatcher on PostgreSQL database `eve_p6`. It calls the registered service backed by the real authority, checks the six-element compression/decompression responses, the two-element efficiency response, persisted item changes, and notifications arriving before each mutation reply.
- Neighbor classes `StructureCompressionTests` and `StructureRpcServiceTests` passed in the same DB-locked run: 10 passed, 0 failed, 0 skipped.
- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false -m:1` passed with 0 warnings and 0 errors.
- No graphical V24 client session was used.

## Rows still open in the two requested services

The direct `machoNet` gameplay calls for GetTime, GetClusterGameStatisticsForClient, and SetGlobalConfigValue are already marked IMPLEMENTED-TESTED. Remaining open rows are the P2 client/runtime-management calls below:

- RPC-01707-01708 `CheckACL`, called from `carbon/common/script/net/httpService.py:434`.
- RPC-01709 `ForwardCharacterNotification`, called from `carbon/common/script/net/machoNet.py:1275`.
- RPC-01716-01720 `GetNodeFromAddress`, called from `machoNet.py:1270,2717,4353`.
- RPC-01725-01726 `ReloadClientCodeHash`, called from `machoNet.py:1419`.

These rows have no registered Host implementation. They were not changed in this increment. Their client actions, reply consumers, and single-host semantics need a separate end-to-end trace before selecting real Host state and error behavior.
