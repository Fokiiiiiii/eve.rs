# Deep check: client-facing machoNet calls

Checked against the full V24.01 client source, the current Host route, and EveJS 261018.1. The client has an exact `machoNet` service; no other service name is needed.

## Client call inventory

The explicit client RPC set is three methods:

| RPC | Client call and consumed result |
|---|---|
| `GetTime` | `carbon/client/script/remote/connectionService.py:208,250` calls `sm.ProxySvc('machoNet').GetTime()` without application arguments. The caller adds the returned clock value to half the measured round-trip time and uses it to adjust clock drift (`carbon/client/script/remote/connectionService.py:211-220,253-260`). |
| `GetClusterGameStatisticsForClient` | `eve/client/script/ui/shared/maps/mapSvc.py:429` calls `sm.ProxySvc('machoNet')` with positional `'EVE'` and `({}, {}, 0)`. It unpacks three values, iterates `sol`, reads station counts from `sta`, divides by `statDivisor`, and adds 30,000,000 to each system key (`eve/client/script/ui/shared/maps/mapSvc.py:429-442`). |
| `SetGlobalConfigValue` | `eve/devtools/script/windowManager.py:251` is the sole direct `sm.RemoteSvc('machoNet')` call. It sends keyword `key` and `value` strings and ignores the result. Before this call the client reads the current local config and selects whether to send it (`eve/devtools/script/windowManager.py:241-251`). |

`carbon/common/script/net/ServiceCallGPCS.py:222-226` adds `machoVersion=1` when no cached version is available. `PacketDispatcher.cs:219-220,307-317` removes that transport keyword before application dispatch. The new wire checks send it on all three actual client shapes.

No client call binds or resolves a moniker whose target is `machoNet`. `carbon/common/script/net/moniker.py:80,134,217-221,232,239` shows its `machoNet` references use the local service table for node lookup and caching while the resolved target is `self.__serviceName`. `eve/client/script/ui/login/loginII.py:361` calls `GetServerStatus` through `sm.GetService` and its raw login transport; that similarly named local API is not a client RPC to this Host route. A whole-client search for explicit `ProxySvc('machoNet')` and `RemoteSvc('machoNet')` calls found only the three methods above.

## Per-method result

### GetTime

Host registers `[Service("machoNet")]` (`MachoNetService.cs:65-69`) and production composition registers it in the service registry (`HostComposition.cs:294`). `GetTime` ignores application args and returns a `PyLong` FILETIME (`MachoNetService.cs:79`). The direct Host wire test verifies the actual call route, `machoVersion` kwargs, `CALL_RSP`, and long result. It changes no state and emits no notification; there is no method-level error branch for the client’s no-arg call.

EveJS `Handle_GetTime` also returns a 100-nanosecond FILETIME as a long (`server/src/services/machoNet/machoNetService.js:432-437`). The client’s arithmetic use above accepts that same type.

### GetClusterGameStatisticsForClient

Host reads the first positional argument and returns live Host data for `EVE`; for another cluster it returns the caller’s second positional fallback (`MachoNetService.cs:90-97`). Production data comes from connected character sessions and is returned as `(solDict, staDict, 1)`, with system IDs reduced by 30,000,000 and docked sessions counted separately (`HostMachoNetInfo.cs:48-66`). The real wire test sends the client’s two positional args, including the `({}, {}, 0)` fallback, and checks that Host returns its own divisor `1`. It has no persistent mutation, notification, or method-level error branch for the client’s valid `EVE` call.

EveJS currently returns `[{}, {}, 1]` (`server/src/services/machoNet/machoNetService.js:500-509`). The client unpacks the three values and uses the first two as mappings, so the tuple/list outer sequence distinction is consumed safely. EveJS supplies empty counts; Host uses the connected-session read model.

### SetGlobalConfigValue

The tested Host service accepts the client’s keyword-only `key` and `value` call, plus its injected `machoVersion`. Its positional compatibility path also accepts the optional flags; integer and long flag values are truth-tested, and `None` deletes a value (`MachoNetService.cs:128-161,174-198`). The real wire test covers the actual keyword call, a positional long value with both integer and long flags, and a positional `None` removal. Both keyword and positional cases include the client-injected `machoVersion`. Successful calls answer `None`.

The error path requires `ROLE_ADMIN` or `ROLE_SERVICE` in `accountRole` (`MachoNetService.cs:131-145`). The wire test asserts a wrapped `exceptions.RuntimeError` with exact args `RoleNotAssigned` and `machoNet.SetGlobalConfigValue requires ROLE_ADMIN or ROLE_SERVICE`. A protected bug-report key is refused as the same exception class with args `ProtectedGlobalConfigKey` and the exact key-specific message (`GlobalConfigStore.cs:193-201`). Neither refusal reaches the mutation/notification callback. A persistence failure is returned as the Host’s wrapped `exceptions.RuntimeError` with a correlation ID (`PacketDispatcher.cs:292-294`; `MachoErrors.cs:90-104`); the wire test injects a failed save and verifies the key stays absent from the current store and next handshake.

On success, the configured store saves before changing its visible override (`GlobalConfigStore.cs:156-171`). When `isUpdateClients` is true, each connected session receives `OnGlobalConfigUpdated(key, value)`, with `None` for deletion, then the Host publishes the new cluster config (`GlobalConfigStore.cs:202-212`). The wire test decodes both notification argument tuples, checks recipients and confirms notifications precede cluster config and `CALL_RSP`. The client updates or removes the local cache and scatters `OnGlobalConfigChanged` (`carbon/common/script/net/machoNet.py:3356-3368`); the agents listener then refreshes mission-giver windows (`eve/client/script/ui/station/agents/agents.py:380-382`).

The Host uses its configured persistence provider. Its in-memory test provider is process-local; PostgreSQL persistence is used when configured. The wire test uses only in-memory persistence and does not exercise a PostgreSQL restart.

EveJS mutates runtime overrides and has no account-role check (`server/src/services/machoNet/globalConfig.js:99-117`; `server/src/services/machoNet/machoNetService.js:357-422`). It silently ignores protected keys and the override does not survive restart. eve.rs requires the role, refuses protected-key writes with an error, and saves through the configured store; these existing intentional differences are recorded in `docs/reference/compat/DEVIATIONS.md:1270-1285`.

## Coverage

`dotnet/tests/Eve.Host.Tests/MachoNetAuditWireTests.cs` adds the real Host socket/dispatcher checks for all three client RPCs, including reply types, the errors, state changes, notification arguments, and send order. The focused audit test passed all 3 cases and the Host.Tests build completed with no warnings. After syncing origin/main, the full Host.Tests project ran with `EVE_PG_URL` unset to avoid database access: 543 passed, 229 skipped, and 102 failed. Failures include unguarded database fixtures dereferencing the missing URL (for example `IndustryJobPersistenceTests.cs` and `MoonOwnerNotificationTests.cs`) and paper-doll assertions in `CompatibilityRpcServiceTests.cs`; none are in the new audit test. Skips are not counted as passes. No graphical client was used. The remaining Host `machoNet` exports are not explicit calls from the client Python source; `GetServerStatus` and local config helpers are local APIs, not missing client-facing Host routes.
