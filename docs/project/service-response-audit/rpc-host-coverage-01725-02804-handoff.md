# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Increment: inventory descriptor

RPC-01725 and RPC-01726 use the same no-argument invbroker.GetItemDescriptor call at eve/client/script/environment/invCache.py:430. GetItemHeader caches its reply (invCache.py:428-432); godma.itemrd returns that cached descriptor (eve/client/script/environment/godma.py:124-127). invCacheContainer.List constructs a CRowset from the descriptor and filters every returned inventory row by its header length (invCache.py:1131-1165); ListByFlags does the same before returning its rows (invCache.py:1167-1201). The descriptor is therefore consumed as the inventory DB row header, not as arbitrary metadata.

Current eve.rs has the handler at dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:326-327, where it returns InventoryWire.DescriptorHeader. Host composition creates the broker in dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:43-46, wraps it with IndustryInventoryChangePublisher (which forwards the inner service name and method calls at dotnet/src/Eve.Host/IndustryInventoryChangePublisher.cs:16-26), and includes it in the registered character service list at dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:47-70.

EveJS v0.12.9 returns the result of _buildInventoryRowDescriptor() from server/src/services/inventory/invBrokerService.js:13567-13571; that delegates to buildPackedRowDescriptor at invBrokerService.js:9966-9971. The current shared descriptor declares the stored item columns plus virtual stacksize and singleton at dotnet/src/Eve.Services/InventoryWire.cs:9-29.

Added dotnet/tests/Eve.Host.Tests/InvBrokerDescriptorHostTests.cs. It sends a client packet with machoVersion=1 through HostServer, registers the real broker behind the Host inventory notification wrapper, and compares the decoded CallRsp descriptor against the shared inventory header. The test does not need a database because this descriptor is static.

Verified locally: Host.Tests build passed with 0 warnings and 0 errors; the focused test passed 1/1 with 0 skipped. No database or graphical client was used.

The generated EVE_RS_COVERAGE.csv still reports RPC-01725/01726 as MISSING: its current route scanner does not follow IndustryInventoryChangePublisher from InventoryRpc.cs to the wrapped broker, despite the concrete registration above. Fixing scanner resolution or updating the generated row is pending. RPC-01420 through RPC-02804 remain to be covered by this handoff.
