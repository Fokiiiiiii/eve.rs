# Inventory UI action audit — 2026-10-26

## V24.01 client flow

| Action | Client evidence | Observed protocol and consumer |
| --- | --- | --- |
| Open inventory/container | `eve/client/script/environment/invCache.py:501-562`; `eve/client/script/ui/shared/inventory/invWindow.py:110-121`; `eve/common/script/net/eveMoniker.py:122-132` | Selects the station or solar-system inventory manager, binds the inventory, checks session scope, and primes the window before it opens. |
| Drag/drop and merge | `invCache.py:998-1129`; `invControllers.py:327-380` | `Add`, `MultiAdd`, and `MultiMerge` are guarded by client item locks; active-ship and fitted-item checks run before RPC. Ammo tuple IDs are unloaded locally. |
| Stack all | `invCache.py:1065-1098`; `invControllers.py:382-413`; `ui/shared/container.py:867-870` | Calls the bound inventory with its selected location flag, holds local busy locks, calls only when duplicate stackable types exist, then refreshes the UI. |
| Split stack | `ui/services/menuSvcExtras/invItemFunctions.py:386-403`; `ui/services/menusvc.py:478-479` | One item, docked station only; calls `GetInventoryMgr().SplitStack(stationID, itemID, qty, ownerID)`, then invalidates the source location cache. |
| Jettison | `ui/services/menuSvcExtras/invItemFunctions.py:56-73`; `ui/services/menusvc.py:3472-3473` | Calls the ship access object and treats the returned can-item IDs and standalone item IDs as the handled set; missing requested IDs raise `NotAllItemsWereJettisoned`. |
| Repackage | `ui/services/menuSvcExtras/invItemFunctions.py:544-585`; `ui/services/menusvc.py:3412-3414` | Groups `(itemID, locationID)` by station and calls `repackagingSvc.RepackageItems`; a confirmed upgrade-destruction prompt retries with `destroyable=True`. |
| Assemble cargo container | `ui/services/menusvc.py:3561-3566` | Calls `GetInventoryMgr().AssembleCargoContainer(itemID, None, 0.0)`. This is separate from assembling a ship. |
| Cargo, drone bay, and fitting lists | `invCache.py:1167-1213` | `ListByFlags` backs flag-specific lists; `ListCargo` uses cargo flag, `ListDroneBay` uses drone-bay flag, and `ListHardware` filters slot flags. |
| Corp hangar divisions | `ui/shared/inventory/invWindow.py:1144-1175`; `1451-1605` | Window tree checks corporation hangar validity and builds division nodes from the client’s role/session state. |
| Change consumers | `invCache.py:219-309`; `ui/shared/inventory/invWindow.py:1189-1226` | The cache scatters one `OnItemChange` per row, updates cached inventories and services, then scatters `OnMultipleItemChange`; the window refreshes selection/tree state. |

A full client search found no inventory action consumer for `OnInventoryChange`: the only exact-name hit is the session-attribute migration log in `eve/common/script/sys/eveSessions.py:318-320`. Inventory UI busy locks and `eventLog` are client-side concerns; targeted searches of the inventory cache, window, container, and menu action files found no inventory `eventLog` call.

## Host and EveJS comparison

| Action | Host | EveJS v0.12.9 |
| --- | --- | --- |
| Bind/list/capacity | Present in `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:379-449, 546-584, 1011-1035`. Ship hold flag/capacity rules are in `dotnet/src/Eve.Ships/CharacterShips.Cargo.cs:100-190`. | Bound inventory methods are implemented in `server/src/services/inventory/invBrokerService.js`. |
| Drag/drop, add, and merge | Present in `InvBrokerService.cs:1144-1208, 1270-1472`; mutations use the existing item/ship state owners. | `server/src/services/inventory/invBrokerService.js:11609-12085, 12087-12673, 12779-13476`. |
| Stack all | `InvBrokerService.cs:1209-1249` emits each `OnItemChange` in returned order and answers True or None. `dotnet/src/Eve.Ships/CharacterShips.Cargo.cs:605-642` merges under row locks. Corp hangar selection now authorizes Take and passes the corporation owner ID into the state owner. | `server/src/services/inventory/invBrokerService.js:11510-11607` groups by type, owner, and compartment and emits the resulting changes; `server/src/services/inventory/invBrokerItemCustody.js:61-118` rejects cross-owner merges. |
| Jettison | `dotnet/src/Eve.Services.Space/ShipService.cs:126-137` delegates to the space-container owner; `dotnet/src/Eve.Services.Space/SpaceContainers.cs:397-470` performs the operation and sends item changes. | `server/src/services/ship/shipService.js:1416-1443` returns the two lists consumed by the client and preserves rejected IDs as missing. |
| Repackage | `InvBrokerService.cs:1484-1576` validates station, ownership, and corporation Take access, calls the ship state owner, and sends resulting item changes. | `server/src/services/station/repackagingService.js:16-24` routes the request to its inventory implementation. |
| Split stack | No Host RPC method exists. | No inventory-broker handler exists; the exact `SplitStack` hit is an unrelated contract-proxy endpoint (`server/src/services/_other/contractProxyService.js:864-867`). |
| Assemble cargo container | No Host RPC method exists. Host `ShipService.AssembleShip` is a different action. | `server/src/services/inventory/invBroker/packaging.js:116-253` implements the client’s cargo-container call, including owner/location/access and cargo-capacity checks. |

The Host's item notifications use `ShipNotifications.SendItemChange` in `dotnet/src/Eve.Services.Ships/ShipNotifications.cs:30-47`. Its RPC handlers do not emit an `OnInventoryChange` inventory notification.

## Change and checks in this worktree

- Fixed corp-hangar StackAll owner selection at the Host boundary. Existing personal and ship inventory calls still default to the character owner.
- Added `dotnet/tests/Eve.Host.Tests/InventoryStackAllHostWireTests.cs`: real Host socket/RPC binding, corp Take success and denial, owner/flag propagation, True/None/ErrorResponse reply shapes, ordered `OnItemChange` notifications, packed-row fields, and previous-value dictionaries. Its injected state delegate isolates the wire boundary; this test does not exercise database mutation.
- Added `dotnet/tests/Eve.Ships.Tests/CorporateCargoTests.cs` for the persistent corporation-owner merge path. It compiles; local database execution was excluded because this job forbids PostgreSQL.
- Host.Tests focused wire test passed (1 passed, 0 skipped). Eve.Host.Tests and Eve.Ships.Tests compile checks passed. The full Host.Tests shard remains pending because it includes PostgreSQL-backed tests and this job prohibits database use.
- A push remains pending: the CI rule requires the full Host.Tests shard locally before any push, which conflicts with this task’s no-database constraint. No PostgreSQL database was used.

## Remaining work

Implement the V24 `SplitStack` RPC and `AssembleCargoContainer` RPC with durable state transitions, precise access/capacity checks, wire replies, notifications, and focused Host wire tests. Then exercise the persistent StackAll regression and the full Host.Tests shard in the permitted CI/database workflow before pushing.
