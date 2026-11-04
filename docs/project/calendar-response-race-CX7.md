# Calendar response and invite removal race (CX7)

## Implementation

`CalendarStore.SendResponseAsync` locks the event row and checks the personal invitee response in the same transaction before upserting. Non-owner personal responses fail when the row is absent or `Uninvited`; the owner bypass remains intact. Corporation and alliance tags skip this personal-only check. `CalendarMgrService.SendEventResponse` emits its response notification only after the store succeeds.

The PostgreSQL regression pauses a response after the service precheck, commits invite removal, and resumes the response. It asserts the response fails, the row stays `Uninvited`, the owner receives no response notification, and the revoked character cannot load details. It then verifies a response that commits first can be followed by removal, leaving the row `Uninvited`.

## Client and reference evidence

- V24.01 `eve/client/script/ui/services/eveCalendarsvc.py:294-311` sends `SendEventResponse` before changing local response/cache/UI state; `eveCalendarsvc.py:408-410` applies `OnEventResponseByExternal` through the same state update. The RPC failure therefore prevents local success handling and the server must not emit the external response notification on failure.
- Participant edits reach the manager at `eve/client/script/ui/services/eveCalendarsvc.py:195-201` and `eve/client/script/ui/shared/eveCalendar.py:1588`. Event detail reads reach the proxy at `eve/client/script/ui/shared/eveCalendar.py:1106-1109` and are cached by `eve/client/script/ui/services/eveCalendarsvc.py:258-261`. The notification formatter also reads details at `notifications/common/formatters/calendar.py:25-29`.
- Whole-source searches found the RPC call and notification consumer above; the UI response buttons call `eveCalendarsvc.py:294-296` from `eveCalendar.py:1596-1598`.
- EveJS 261028.1 removes participants and writes `Uninvited` at `src/server/src/services/calendar/calendarRuntimeState.js:665-710`; its response path checks the personal invitee set before writing at `:732-770`, and event detail reads check visibility at `:282-302`. Its in-memory flow is synchronous; the eve.rs check now shares PostgreSQL's event lock and transaction with the response write.

## Checks

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Calendar -p:UseSharedCompilation=false`: passed, 0 warnings.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings.
- PostgreSQL database `eve_p7`, through `tools/dev/db-lock.sh`: `CalendarParticipantConcurrencyTests` 2 passed; `CalendarBatchWriteTests` 2 passed; Host calendar RPC tests (`FullyQualifiedName~Calendar_rpc_`) 2 passed, including the real Host RPC path.
- `CalendarMgrServiceTests`: 10 passed.
- No graphical EVE client session was used.

## Delivery gate

At the last status check, completed main CI run 1498 (SHA `141310d7`) was red; run 1499 was still in progress. No merge or push has been made. Recheck the latest completed main CI status before merging or pushing, and update this note with the resulting commit and Actions run after delivery.
