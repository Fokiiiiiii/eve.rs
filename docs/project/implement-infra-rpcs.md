# Infrastructure-style RPC audit and implementation

## Scope and result

Audited the MISSING rows for `machoNet` (13), `DB2` (4), `cache` (3), `authentication` (3), `i2` (2), and `externalQueueMgr` (2) in `service-audit/EVE_RS_COVERAGE.csv` against the full V24.01 client tree at `/home/user/eve-logic` and EveJS v0.12.9 at `/home/user/evejs-261108.1`.

Three client RPCs are implemented and have focused Host integration coverage: `machoNet.GetConnectedClusterName`, `i2.IsPrimed`, and `i2.GetItem`. One row is `CLIENT-LOCAL` (`machoNet.GetConnectionProperties`); the other 23 are `CLIENT-DEAD`. The 27-row disposition is recorded in the CSV. “Dead” here means the named server-side RPC callsite is never invoked by the V24 player client; it does not claim that the old server implementation is unused inside a cluster.

The full-tree recheck also corrected the pre-existing `RPC-00892 authentication.Login` classification. That CSV row was not in the 27 MISSING rows: its callsite is Carbon's server-side login callback, while the V24 client connects through `machoNet.ConnectToServer` and does not call `RemoteSvc('authentication').Login`. The row is now `CLIENT-DEAD`; the Host's separate `AuthRequest` login flow remains covered by `HostServerTests`.

## Existing eve.rs registrations and transport

### Current registration snapshot

The Host composition in this checkout registers the following services and exports:

| Service | Registration and current exports | State owner / notes |
| --- | --- | --- |
| `machoNet` | `HostComposition.BuildAsync` registers `MachoNetService` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:568`). Its exports are `GetTime`, `GetInitVals`, `GetServiceInfo`, `GetConnectedClusterName`, `GetClusterGameStatisticsForClient`, `GetGlobalConfig`, `GetGlobalConfigValue`, `SetGlobalConfigValue`, `GetServerStatus`, and `GetNodeID` (`dotnet/src/Eve.Host/MachoNetService.cs:80-181`). | `HostMachoNetInfo` reads the registered service map, current config, and live character sessions (`dotnet/src/Eve.Host/Hosting/HostMachoNetInfo.cs:39-66`). Global-config writes use `GlobalConfigStore` and the connected-session/cluster-config callbacks wired at registration. `GetServerStatus` is exported but its session-count response remains a separate open item in `docs/project/TODO.md`; it is outside these 27 rows. |
| `cache` | `CoreServices.Create` includes `CacheService` (`dotnet/src/Eve.Services.Core/CoreServices.cs:98-100`); it exports `GetServerVersionAndBuild` (`dotnet/src/Eve.Services.Core/CoreCompatibilityServiceAdapters.cs:98-110`). | Returns the configured server boot version/build; no mutation or notification. Invalid arguments are rejected. |
| `authentication` | `CoreServices.Create` registers the provided `AuthenticationRpcService` or its default (`dotnet/src/Eve.Services.Core/CoreServices.cs:126`); exports are `AccruedTime`, `AmUnderage`, and `SetLanguageID` (`dotnet/src/Eve.Services.Core/LoginSupportRpcServices.cs:7-59`). | Host composition wires accrued-time reads to `PgAccruedTimeStore` and age checks to `PgAccountDirectory` when configured (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:449-461`). `SetLanguageID` updates the live session. Login itself is the HostLink `AuthRequest` flow, not an `authentication.Login` RPC; there is no client `Logout` RPC. |
| `i2` | Added to the character-enabled inventory service list (`dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:60`) and registered with the other character services (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:517-519`). It exports `MachoResolveObject`, `MachoBindObject`, `IsPrimed`, and `GetItem` (`dotnet/src/Eve.Services.Inventory/I2Service.cs:20-73`). | Reads persisted item trees from `PgItemStore` and type group/category data from the pinned SDE; it owns no duplicate store and performs no writes. |
| `DB2` | No Host service is registered. | The four audited `CallProc` rows are server-internal database callers; no client-facing `CallProc` route was added. |
| `externalQueueMgr` | No Host service is registered. | The audited `PublishProxyEvent` calls are proxy startup registration, not player RPCs. |

The Rust `eve-net` front forwards raw client marshal packets over HostLink; it does not dispatch a service by name. After HostLink has opened the session, `HostServer` passes packets to `PacketDispatcher`, which decodes Macho calls and resolves them in `ServiceRegistry` (`dotnet/src/Eve.Host/HostServer.cs:755-756`; `dotnet/src/Eve.Services/PacketDispatcher.cs:120-183,216-255`). Bound `i2` Moniker calls use the same dispatcher and bound-object registry. This follows the transport boundary in ADR 0002; there is no separate `machoNet` transport handler for these RPC rows.

### Pre-implementation baseline

Before this change, `CoreServices.Create` registered `cache` and `authentication` (`dotnet/src/Eve.Services.Core/CoreServices.cs:98-100,126`). The `cache` service only exported `GetServerVersionAndBuild`, returning `(boot_version, boot_build)` (`dotnet/src/Eve.Services.Core/CoreCompatibilityServiceAdapters.cs:98-110`). `authentication` exported `AccruedTime`, `AmUnderage`, and `SetLanguageID`; it had no `Logout` method (`dotnet/src/Eve.Services.Core/LoginSupportRpcServices.cs:7-59`). There was no `DB2`, `i2`, or `externalQueueMgr` service registration.

The Host registered `machoNet` from `HostComposition` using `MachoNetService` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:543-569`). Before this change it exported `GetTime`, `GetInitVals`, `GetServiceInfo`, `GetClusterGameStatisticsForClient`, `GetGlobalConfig`, `GetGlobalConfigValue`, `SetGlobalConfigValue`, `GetServerStatus`, and `GetNodeID` (`dotnet/src/Eve.Host/MachoNetService.cs:64-177`). It did not export any of the target methods.

The host-link transport decodes each frame and passes it to `PacketDispatcher` (`dotnet/src/Eve.Host/HostServer.cs:755-756`). The dispatcher resolves the service and method through `ServiceRegistry`; unknown services and methods receive wrapped Macho errors, and returned bound-object substructs are registered for follow-up calls (`dotnet/src/Eve.Services/PacketDispatcher.cs:120-183,216-255,612-643`). The new `i2` service is registered alongside inventory services in the real character-enabled Host composition, where its dependencies already own authoritative state (`dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:42-61`). It reads from `PgItemStore` and obtains group/category data from `ItemTypeCatalog` and the pinned SDE; it adds no item store or writes.

After this change, `machoNet.GetConnectedClusterName` is registered with the existing service. `i2` exports the client’s Moniker setup methods `MachoResolveObject` and `MachoBindObject` plus the two coverage methods. The other audited calls were not added.

## V24.01 client call traces

### `machoNet.GetConnectedClusterName`

`carbon/common/script/net/machoNet.py:4543-4547` shows the client-side branch calling the proxy’s `machoNet.GetConnectedClusterName()` with no arguments; the server branch returns `prefs.clusterName`. `monolithconfig/__init__.py:393-397` calls this method, and `monolithhoneycomb/__init__.py:159-162` uses the result as the client telemetry tenant. This is a real client RPC even though its caller is telemetry. Its wire result is a string, and the client normalizes it with `.lower()` at the telemetry caller. The Host handler is read-only and sends no notification; the Host always supplies a configured/fallback string, so the normal path has no method-level error result.

The V24 preferences fallback is `COMPUTERNAME@USERDOMAIN` with `LOCALHOST@NODOMAIN` defaults, uppercased (`eveprefs/__init__.py:143-150`). eve.rs uses `EVE_CLUSTER_NAME` when set and otherwise matches that fallback. The RPC returns that configured string; it does not mutate state or send notifications. Its Host wire behavior is tested through the dispatcher.

EveJS v0.12.9 has no `GetConnectedClusterName` or `GetConnectedClusterMode` method in its `machoNetService.js`. Adding the name call is an evidence-based difference from EveJS, required by the V24 telemetry caller.

### `i2.IsPrimed` and `i2.GetItem`

The explicit client `i2` Moniker caller is the Attribute Inspector in `eve/devtools/script/svc_dgmattr.py:248-250`. It creates `Moniker('i2', GetLocationBindParams())`, calls `IsPrimed(self.itemID)`, and only when that returns true calls `GetItem(self.itemID).typeID`. Both direct calls take one positional item ID and no keywords. `eve/common/script/net/eveMoniker.py:12-17` selects `(session.solarsystemid, const.groupSolarSystem)` or `(session.stationid, const.groupStation)`; V24 defines those groups as 5 and 15 in `inventorycommon/const.py:910,926`. If neither location exists, the client raises locally before making an i2 call. The whole-tree scan also finds `mining/__init__.py:86-99`, where `WasteOre` invokes the same-named method on `dogmaLM.inventory2`; this is a Dogma Location object's `inventory2` property, not an explicit `Moniker('i2', ...)` callsite.

The Moniker setup uses `MachoResolveObject(bindParams)` and, on first use, `MachoBindObject(bindParams, (method, args, kwargs))` (`carbon/common/script/net/moniker.py:131-153,208-236`). The wire bind parameters are `(locationID, groupID)` and the nested call is `(methodName, argsTuple, kwargsDict)`. eve.rs resolves to its proxy node, creates a session-registered bound OID, and returns `(openedBoundObject, nestedResult)`. A bind for a location that no longer matches the character session returns `None`. `IsPrimed(itemID)` returns a Boolean: true only when the persisted item exists and its location/parent chain reaches that station or solar system; malformed, missing, or out-of-scope IDs return false. `GetItem(itemID)` applies the same scope and returns the existing `util.Row` shape with the item header and line, including `typeID`, `groupID`, and `categoryID` from the SDE; missing, out-of-scope, or unknown-type items return `None`. Both methods are read-only and send no notifications.

EveJS has no registered `i2` service or `IsPrimed` handler. It has a different `invbroker.GetItem` path for inventory-bound calls (`server/src/services/inventory/invBrokerService.js`), which is not the V24 client’s `i2` moniker contract. eve.rs therefore uses its existing inventory store and row wire helper rather than pretending that EveJS implements this RPC.

The focused Host test seeds persistent items and covers the Moniker resolution/bind, a present item, an absent item, an item in another system, a nested item under a system item, a station-bound item, changed session location, concurrent sessions, and a Host restart. It verifies the wire `util.Row` header/line and item type. The test was first run before the implementation and failed because `machoNet.GetConnectedClusterName` produced an error response instead of a call response.

## Client-dead and client-local rows

| Service and rows | Client finding and source evidence | EveJS comparison |
| --- | --- | --- |
| `DB2` RPC-00659–00662 | `CallProc('zcluster.Nodes_PolarisID')` is in server ESP URL setup (`carbon/common/script/net/http.py:29-33`); `CallProc('zuser.Users_SelectByRole', ROLE_VIPLOGIN)` populates the server VIP allowlist (`carbon/common/script/net/machoNet.py:3389-3398`). No V24 client call to either procedure was found. | No generic client DB2 `CallProc` gameplay route. |
| `authentication.Login` RPC-00892 | The V24 login UI calls `self.machoNet.ConnectToServer(...)` (`carbon/client/script/remote/connectionService.py:70,85`); it does not call `RemoteSvc('authentication').Login`. The row's `Login` call is Carbon server code (`carbon/common/script/net/machoNet.py:3440-3451`). eve.rs receives the separate HostLink `AuthRequest` (`dotnet/src/Eve.Host/HostServer.cs:353-355,377-386`), so no `authentication.Login` service method was added. | EveJS `Handle_Login` (`server/src/services/login/authenticationService.js:268-293`) is the server-side callback shape, not a V24 client RPC callsite. |
| `authentication.Logout` RPC-00893–00895 | These are server authentication cleanup after VIP rejection, transport timeout, and server `LogOffSession` (`carbon/common/script/net/machoNet.py:3444-3466`). They are not client logout requests. | EveJS has `Handle_Logout` in `server/src/services/login/authenticationService.js:295`; its existence serves server cleanup, not a client caller. |
| `cache.Rowset` RPC-00965–00966 | Used by server HTML action authorization to read user roles (`carbon/common/script/util/htmlwriter.py:2710-2718`). | No client request for this HTML authorization read. |
| `cache.Setting` RPC-00967 | Used by server HTTP code to load the downloadable-file extension allowlist (`carbon/common/script/net/httpService.py:1205-1214`). | No client request for this server setting. |
| `externalQueueMgr.PublishProxyEvent` RPC-01450–01451 | Proxy process publishes its node/IP/metrics startup data in a retry loop (`carbon/common/script/net/machoNet.py:880-892`). No V24 client invokes this method. | EveJS has `Handle_PublishProxyEvent` in `server/src/services/machoNet/externalQueueMgrService.js:33`, also a server proxy registration path. |
| `machoNet.CheckACL` RPC-01707–01708 | Server HTTP ESP login ACL check (`carbon/common/script/net/httpService.py:426-439`). | No player-client RPC caller. |
| `machoNet.ForwardCharacterNotification` RPC-01709 | Forwards a character notification from one server node to another (`carbon/common/script/net/machoNet.py:1268-1278`). | Server routing only. |
| `machoNet.GetConnectedClusterMode` RPC-01711 | The wrapper exists at `carbon/common/script/net/machoNet.py:4549-4552`, but a whole-tree V24 caller search found no callsite. | No EveJS handler. |
| `machoNet.GetConnectionProperties` RPC-01713 | The client implementation returns its own local counters (`carbon/common/script/net/machoNet.py:1744-1751`); callers are `carbon/client/script/util/monitor.py:402,696` and `eve/client/script/util/profiling.py:142,153`. No server RPC is needed for this row. | Local-client behavior, not an EveJS server route. |
| `machoNet.GetNodeFromAddress` RPC-01716–01720 | The CSV callsites perform server node selection/cluster resolution. V24 client Moniker resolution first checks local guesses/cache and then calls remote `MachoResolveObject` (`carbon/common/script/net/moniker.py:208-236`); it does not call these server node-allocation procedures. Client `eveSessions.py` calls are guarded by `macho.mode == 'server'` (`eve/common/script/sys/eveSessions.py:50-55,280-284`). | Server cluster routing, not a player RPC handler. |
| `machoNet.ReloadClientCodeHash` RPC-01725–01726 | Called by the server `OnClientCodeUpdated` event handler (`carbon/common/script/net/machoNet.py:1417-1421`). No V24 client callsite was found. | No client handler. |

These distinctions update the earlier DB2 note: the four DB2 rows are now `CLIENT-DEAD`, not open client-compatibility gaps. The named server-only behaviors remain outside this player-client RPC implementation.

## Verification boundary

Verified here: V24 source callsites and arguments above; EveJS source matches/misses above; actual Host composition registration; marshal round-trip and dispatcher responses; persisted item reads through a Host shutdown/restart; focused DB-backed tests on `eve_p5` with pinned SDE data.

Recheck on 2026-11-14: `/root/.dotnet/dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -c Release -warnaserror` succeeded with 0 warnings and 0 errors. With `eve_p5` confirmed present through `psql`, `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p5 /root/.dotnet/dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release --filter FullyQualifiedName~InfrastructureRpcHostTests` passed (2 passed, 0 failed, 0 skipped). The database test exercises the registered Host dispatcher, marshaled replies, PostgreSQL-backed inventory reads and a Host restart.

Not verified here: behavior against the graphical V24 client, production cluster preferences or deployment-specific `EVE_CLUSTER_NAME`, or any of the `CLIENT-DEAD` server-only internal call flows. EveJS has no i2 implementation to execute as a direct comparison.
