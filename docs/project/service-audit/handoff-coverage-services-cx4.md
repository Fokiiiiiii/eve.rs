# Service coverage handoff — cx4

## Completed group: calendarProxy

The Host wire test `dotnet/tests/Eve.Host.Tests/CalendarRpcHostWireTests.cs:Calendar_rpc_methods_are_reachable_over_host_and_share_state_and_notifications` passed on PostgreSQL database `eve_p4` (1 passed, 0 failed, 0 skipped). It starts the Host listener with strict RPC routing, creates an event through `calendarMgr`, reads it through registered `calendarProxy.GetEventList` and `GetEventDetails`, mutates the event, then reads the persisted updates and checks notifications. The corresponding CSV rows RPC-00979 and RPC-00980 now point to this test.

### Whole-client evidence

- `eve/client/script/ui/services/eveCalendarsvc.py:235-248` binds the month/year call to `calendarProxy.GetEventList` and consumes the tuple slots and rows. `CalendarSvc.GetEventsNextXMonths` fans into `FetchNextEvents` and `FetchNextEventsDict` (same file: graph trace shows callers); UI callers include `eve/client/script/ui/shared/eveCalendar.py:1106,1447,1579,1584,1588,1597,1601,1664`.
- `eve/client/script/ui/services/eveCalendarsvc.py:259-262` calls `calendarProxy.GetEventDetails`; `eve/client/script/ui/shared/eveCalendar.py:1106-1111` reads `eventText` and `creatorID`. The notification formatter also consumes the facade at `notifications/common/formatters/calendar.py:25`.
- Current EveJS reference `server/src/services/calendar/calendarProxyService.js:15-20,22-26` delegates list and detail to runtime and payload builders. The Host test compares the returned rows/details with subsequent mutations and persisted database-backed state.

## Still pending

Remaining requested service groups: `account`, `corpmgr`, `voteManager`, and `repairSvc`. Do not change their CSV statuses until their Host wire tests pass locally on `eve_p4` with no skips. Keep this note current after each push.

A graphical V24.01 client session was not run.
