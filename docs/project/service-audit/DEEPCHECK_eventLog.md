# eventLog Host service deep check

Scope: V24.01 client source under `/home/user/eve-logic`, eve.rs Host route, and EveJS 261018.1. Client source was searched across the tree for the exact service name and remote/bound-service call forms. No graphical client or database was used.

## Client call inventory

All remote calls use `sm.ProxySvc('eventLog')`; no client `RemoteSvc`, bound-service, or moniker variant for this service was found. The closest non-RPC use is the local counter at `eve/common/script/sys/eveProcessHealthSvc.py:57` (`GetService('eventLog').logEventCounter`). Server-internal `BaseEventLogSvc` calls are also local, not this client RPC surface.

### LogClientEvent

The whole-tree client callsites are:

- `kiring/client/apigateway.py:429`
- `eve/client/script/ui/shared/vgs/events.py:25`
- `eve/client/script/ui/shared/skins/event.py:23`
- `eve/client/script/ui/shared/monetization/events.py:9`
- `eve/client/script/ui/shared/cloneGrade/cloneUpgradeWindow.py:247`
- `eve/client/script/ui/shared/cloneGrade/cloneStateWindow.py:225`
- `eve/client/script/ui/shared/cloneGrade/util.py:29`
- `eve/client/script/ui/services/flightControls.py:393`
- `eve/client/script/ui/services/corporation/base_corporation.py:1054`
- `eve/client/script/ui/view/aurumstore/aurumStoreView.py:196`
- `eve/client/script/parklife/autopilot.py:77`
- `fastcheckout/client/eventlogging/eventLoggers.py:32`
- `expertSystems/client/eventlogging/eventLoggers.py:24`
- `projectdiscovery/client/projects/exoplanets/ClientLogger.py:11`
- `raffles/client/event_log.py:30`

Every caller supplies the application arguments positionally: category, columns, event name, then zero or more values. In particular, `flightControls.py:393` can pass `None` as a value because it uses `self.ships.get(shipID)` (see `flightControls.py:388-406`). The shared call layer inserts `machoVersion` as a keyword when needed (`carbon/common/script/net/ServiceCallGPCS.py:222-226`); Host packet dispatch removes that transport keyword before invoking the method (`dotnet/src/Eve.Host/PacketDispatcher.cs:307-317`). Client callers ignore the reply. Callers with error handling catch `UserError` (including `autopilot.py:77` and `base_corporation.py:1054`); asynchronous/logging helpers also contain exceptions at their call boundary.

Host binds `LogClientEvent` on the production `eventLog` service registration. It accepts at least the first three positional arguments, list or tuple columns, and variadic values including `None`; it returns `PyNone` (`CALL_RSP`). The host intentionally logs category and event name only. Its validation UserErrors are `CLIENT_EVENT_ARGUMENTS_INVALID`, `CLIENT_EVENT_COLUMNS_INVALID`, `CLIENT_EVENT_VALUES_INVALID`, and `CLIENT_EVENT_KEYWORDS_INVALID`. Transport failures/cancellation remain transport or cancellation errors. No client consumer requires an event-log payload.

### LogClientStats

The only remote callsites are `eve/client/script/sys/clientStatsSvc.py:310` and `:892`.

The periodic path at `:878-896` sends session ID, location ID, package rows, and crash rows. The package and crash collections are lists initialized even when empty by `:788-875`. The client uses no reply body. The shutdown path at `:297-359`, specifically `:310`, sends `session.sid`, `session.locationid`, a one-element package list, and `isShutdown=True`; it has no non-None location guard after checking the character and connection. If the stats call raises, the same `try` skips the later disconnect call; on success it sets `statsSent` and calls `LogPlayerRequestedDisconnect()` at `:313`. Periodic session-change reporting explicitly guards a missing new location at `:899-903`.

Host accepts the three-argument shutdown form and four-argument periodic form; session and location IDs accept nonnegative `PyInt` or `PyLong`. The shutdown form also accepts a `None` location only with `isShutdown=True`. Periodic rows and optional crashes accept list or tuple, capped at 5,000 entries each. The transport `machoVersion` keyword is stripped by dispatch. Reply is `PyNone` (`CALL_RSP`), and no client code consumes data from it.

Host UserErrors are `CLIENT_STATS_ARGUMENTS_INVALID`, `CLIENT_STATS_SESSION_INVALID`, `CLIENT_STATS_LOCATION_INVALID`, `CLIENT_STATS_ROWS_INVALID`, `CLIENT_STATS_ROW_LIMIT_EXCEEDED`, `CLIENT_STATS_CRASHES_INVALID`, `CLIENT_STATS_CRASH_LIMIT_EXCEEDED`, and `CLIENT_STATS_SHUTDOWN_FLAG_INVALID`. The dispatcher can separately reject calls that exceed its event-log budget. The client only checks that the call did not raise before marking stats as sent; it otherwise ignores the result.

### LogPlayerRequestedDisconnect

The only client call is `eve/client/script/sys/clientStatsSvc.py:313`, immediately after successful final stats in the connected shutdown path (`:297-359`). It has no application arguments, ignores the reply, and has no client notification consumer. Host accepts the empty positional call (and transport `machoVersion`), replies with `PyNone` (`CALL_RSP`), and emits only its local diagnostic log line. A cancelled request raises cancellation; socket and dispatch failures use the normal Host error path.

## Registration, state, and behavior comparison

Production registration is `HostComposition.cs:294`; the methods are registered on the actual Macho service named `eventLog`. The real socket tests exercise that route, packet response type, return value, and representative UserError payloads.

EveJS 261018.1 registers `eventLog` and implements the same three RPCs in `src/server/src/services/logging/eventLogService.js:110-131`. `LogClientEvent` stores complete event data in a bounded process-local array (limit 250, lines 13, 73-87); `LogClientStats` stores full stats records in a process-local array (lines 89-103). Its getters and test reset are at lines 135-144. These arrays are neither durable persistence nor client notifications.

EveJS `LogPlayerRequestedDisconnect` marks the transient session at `src/server/src/services/logging/eventLogService.js:127-131`; `src/server/src/socket/socketLifecycle.js:21-53` uses that marker to classify an expected socket reset. Host instead writes bounded metadata to its configured local logger and does not retain client-controlled event/stat payloads or set a disconnect marker. No downstream client consumer or Host notification depends on that EveJS-only state. This preserves the existing Host boundary against retaining arbitrary client telemetry; the operational marker difference has no client-visible reply or transition.

Host replies with `None`, matching the client-visible EveJS response for all three methods. Host adds validation and exact UserErrors where EveJS accepts unvalidated arguments. Host's `LogClientStats` previously rejected the shutdown client's valid `None` location; it now permits that one shape only when `isShutdown=True`, matching the client call at `clientStatsSvc.py:310`. No other client-valid shape gap was found.

## Verification performed

Real Host wire coverage is in `EventLogClientEventHostWireTests.cs` and `EventLogHostWireAuditTests.cs`: client-event positional values including `PyLong` and `None`, stats `PyInt` and `PyLong` locations, shutdown `None`, disconnect with no application arguments, injected transport keyword handling, `CALL_RSP`/None replies, and exact location/session/argument UserErrors. No database or graphical client was used.
