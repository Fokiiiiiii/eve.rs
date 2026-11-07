# Calendar audit fixes

Source audit: `/home/user/reports/90-audit-eve-calendar-r14.md`. Each finding below was checked against the V24.01 client at `/home/user/eve-logic`, EveJS 261028.1 at `/home/user/evejs-261028.1`, the Host/service path, and PostgreSQL-backed regressions. Graphical-client execution is tracked separately as an unchecked item.

## Finding 1 — participant-change notification diff under concurrent remove and re-invite

**Confirmed.** Before the fix, `CalendarMgrService.UpdateEventParticipants` read the response rows before and after the store transaction and compared those independent reads. A Host regression reproduced this sequence: both requests observe invitee X as active; removal commits and sends `OnRemoveCalendarEvent`; re-invitation commits `Undecided`; the re-invitation request's stale before/after sets compare equal, so it sends no `OnNewCalendarEvent` although PostgreSQL says X is invited.

**Client and reference evidence.** V24.01 builds participant changes as separate additions and removals after editing (`eve/client/script/ui/shared/eveCalendar.py:1583-1589`). `OnRemoveCalendarEvent` removes a non-deleted event from the cached month (`eve/client/script/ui/services/eveCalendarsvc.py:379-397`), while `OnNewCalendarEvent` adds it (`:333-350`). EveJS computes the added and removed IDs during the participant update (`server/src/services/calendar/calendarRuntimeState.js:665-709`) and sends those notifications in that order (`server/src/services/calendar/calendarMgrService.js:129-149`).

**Fix.** `CalendarStore.UpdateParticipantsAsync` now returns the event snapshot and the exact invitee transitions computed while holding the event-row lock. The service sends only those IDs. An event-keyed gate keeps participant mutations and their notifications in the same order within the Host process; its entries are removed when no request is using them.

**Verification.** The new PostgreSQL Host regression first failed because the re-invited character received no `OnNewCalendarEvent`; after the fix it passed and checked `OnRemoveCalendarEvent` then `OnNewCalendarEvent`, with the persisted response ending at `Undecided`. Calendar Host regressions passed 11/11, and `CalendarMgrServiceTests` passed 10/10 on `eve_p7`.

## Finding 2 — response notification may carry a stale event date

**Confirmed.** `CalendarMgrService.SendEventResponse` read the event before the response transaction and used that earlier row for `OnEventResponseByExternal`. A Host regression paused the response store call after the service read, edited the event through the registered Host RPC, then resumed the response. Before the fix, the response notification carried the pre-edit date although PostgreSQL already held the new date.

**Client and reference evidence.** The V24.01 client calls `SendEventResponse` with the event row's owner and later applies `OnEventResponseByExternal` through `_RespondToEvent`, which uses the supplied `eventDateTime` to update next-event state (`eve/client/script/ui/services/eveCalendarsvc.py:292-310,409-411`). EveJS returns the current event from `sendEventResponse` (`server/src/services/calendar/calendarRuntimeState.js:732-769`) and passes that event to its response notification (`server/src/services/calendar/calendarMgrService.js:112-126`).

**Fix.** `CalendarStore.SendResponseAsync` now reads and locks the event row in the same transaction as the response update, then returns that snapshot with the update result. The service builds the notification from the returned row instead of the pre-transaction read.

**Verification.** The new PostgreSQL Host regression first failed because the notification date equaled the old event date; after the fix it passed and asserted the edited date in the actual `OnEventResponseByExternal` payload and `Accepted` in persisted responses.

## Finding 3 — deleting an event overwrites a declined response

**Confirmed.** `CalendarStore.DeleteEventAsync` changed every response other than `Uninvited` to `Deleted`, erasing `Declined`. A Host regression created an event, recorded a `Declined` response, deleted the event, reconnected the invitee, and read the response through Host RPC. Before the fix, the client-visible response was `Deleted`.

**Client and reference evidence.** For a deleted event, the V24.01 client marks the event row deleted and keeps the cached response when it is `Declined` (`eve/client/script/ui/services/eveCalendarsvc.py:379-397`). Its update-event list excludes deleted events when the cached response is `Declined` (`:577-585`). EveJS deletion marks and persists the event record without changing the response root (`server/src/services/calendar/calendarRuntimeState.js:712-729`), and its manager sends the deleted-event notification (`server/src/services/calendar/calendarMgrService.js:102-110`).

**Fix.** Deletion now soft-deletes only the event row. Existing response states, including `Declined` and `Uninvited`, remain persisted and available after reconnect.

**Verification.** The new PostgreSQL Host regression first failed because the reconnected invitee received `Deleted` instead of `Declined`; after the fix it passed, checked the deleted event row from `GetEventList`, and confirmed the persisted response remained `Declined`. The end-to-end calendar wire test now asserts that deletion preserves the owner's `Accepted`, invitee `Undecided`, removed invitee `Uninvited`, and added invitee `Undecided` states.

## Audit scope

All three findings in the audit report were confirmed; none were skipped as false. Source behavior was compared against V24.01 eve-logic and EveJS 261028.1. No focused EveJS calendar test harness was found, so its implementation was source-traced rather than executed. The graphical client was not run.
