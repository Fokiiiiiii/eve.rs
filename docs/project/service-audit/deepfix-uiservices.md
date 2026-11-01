# UI services deep fix handoff

## Scope completed

This change audits one core daily flow: editing participants on a personal calendar event. Other UI service flows (overview presets, bookmarks, notepad, neocom and shared settings) still need their full client-to-Host audit.

## Client evidence

- The client exposes the 50-invitee ceiling in `eve/client/script/ui/shared/eveCalendar.py:1429-1432`; `CreateOrEditEvent` calls `UpdateEventParticipants` at `eve/client/script/ui/shared/eveCalendar.py:1587-1588`.
- The limit is `calendarMaxInvitees = 50` in the V24.01 root `appConst.py:1823` (also mirrored in `eve/common/lib/appConst.py:1823`).
- `eve/client/script/ui/services/eveCalendarsvc.py:195-203` sends the participant update RPC, charges CSPA for additions, and invalidates its cache after the call.
- The client uses `GetResponsesToEvent` to populate invitees at `eve/client/script/ui/shared/eveCalendar.py:1664`, then maps response states at line 1447; `eve/client/script/ui/services/eveCalendarsvc.py:413-422` converts the RPC rows for those UI consumers. The service invalidates the cached response call after update at lines 195-201.
- The calendar service applies `OnNewCalendarEvent` to month data at `eve/client/script/ui/services/eveCalendarsvc.py:332-375` and removes events at lines 378 onward; the event editor listens for `OnRemoveCalendarEvent` and closes for its event at `eve/client/script/ui/shared/eveCalendar.py:1042,1672-1674`.
- The EVE University in-game calendar guidance says calendar events can be edited in game, describes editing as a normal calendar operation, and notes that an event cannot be edited after it starts: [EVE University Calendar](https://wiki.eveuniversity.org/EVE_University_Calendar). The 50-person limit itself is client evidence, not a wiki gameplay rule.

## EveJS comparison

EveJS 261028.1 performs its participant normalization and 50-invitee validation in one synchronous operation in `server/src/services/calendar/calendarRuntimeState.js:665-710`. Its RPC handler and notification path are in `server/src/services/calendar/calendarMgrService.js:129-150`. In Node's synchronous state mutation, concurrent RPC handlers cannot both commit from the same stale attendee count.

## eve.rs defect and fix

Before the fix, `CalendarMgrService.UpdateEventParticipants` read current responses and checked the final active attendee count before calling the store. Two concurrent requests could both pass from the same 49-invitee state and commit 51 invitees. The regression test forced that interleaving and failed before the fix because both requests succeeded.

The authoritative check now runs inside `CalendarStore.UpdateParticipantsAsync` after it locks the event row with `FOR UPDATE`. It applies removals before additions, excludes the event creator, and rejects a final active set above `CalendarLimits.MaxInvitees` before writing responses. The service maps the store result to the existing `CustomNotify` error and sends notifications only after a committed update. Existing add/remove notification ordering remains new-event notification followed by removal notification.

Implementation: `dotnet/src/Eve.Calendar/CalendarStore.cs:235-275`, `dotnet/src/Eve.Services.Core/CalendarMgrService.cs:159-200`. Regression: `dotnet/tests/Eve.Host.Tests/CalendarParticipantConcurrencyTests.cs:14-58`.

## Checks

- Regression before fix: failed as expected; both concurrent calls succeeded and the active set reached 51.
- Regression after fix: passed on PostgreSQL database `eve_p3`; one call succeeds, the other receives `CustomNotify`, and the persisted active set remains 50.
- `CalendarMgrServiceTests`: 10 passed, 0 skipped.
- Host RPC and server test classes passed against `eve_p3`: 399 passed, 0 skipped.
- Builds completed for `Eve.Calendar`, `Eve.Services.Core`, and `Eve.Host.Tests`. The `Eve.Services.Core.Tests` project also built after restoring its missing assets.
- No graphical client session was used. This fix has not yet received its push-triggered Actions result.

## Remaining audit work

The other listed UI-service flows have not been traced through every V24.01 caller and consumer in this pass. Do not treat this note as an audit verdict for overview presets, bookmarks, notepad, calendar reads/responses, neocom or shared settings.
