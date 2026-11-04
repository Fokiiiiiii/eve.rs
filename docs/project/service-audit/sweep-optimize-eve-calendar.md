# Eve.Calendar Optimization Sweep

## Scope and change

`CalendarStore.CreateEventAsync` now upserts all distinct valid invitees with one array-backed SQL statement. `UpdateParticipantsAsync` batches additions into one upsert and removals into one `UPDATE ... ANY(...)`. Both operations keep their existing transaction, validation, and add-before-remove order; removal still changes existing rows and does not create missing responses.

The client and Host registrations, response values, notification calls, and query row ordering are unchanged.

## Cost evidence

For `N` distinct valid invitees, event creation previously executed `N` invitee upserts, one creator upsert, and one event insert. It now executes one invitee upsert when `N > 0`, plus the same creator upsert and event insert: `N + 2` statements become `3` (52 become 3 at the 50-invitee client limit). With no invitees, the count stays at two.

For `A` distinct additions and `R` removals, `UpdateParticipantsAsync` keeps its two reads and changes its response writes from `A + R` commands to at most two array-backed commands. At 50 additions and 50 removals, the store path goes from 102 statements to 4. PostgreSQL still processes each affected row; network round trips no longer grow with the list sizes.

No wall-clock benchmark was added: the number of executed SQL statements is directly visible in the old and new command paths, while timing a local database would add noise without changing that result.

## Client and reference checks

- The V24.01 editor computes additions and removals as set differences before calling `UpdateEventParticipants` (`eve/client/script/ui/shared/eveCalendar.py:1584-1588`). The service wrapper forwards those lists and invalidates the `GetResponsesToEvent` cache (`eve/client/script/ui/services/eveCalendarsvc.py:195-201`). Whole-client search found no other callsite for that RPC.
- The response consumer maps character IDs to status and hides `Uninvited` rows (`eve/client/script/ui/services/eveCalendarsvc.py:413-427`). Creation calls `CreatePersonalEvent` with invitees and records the creator as accepted (`eve/client/script/ui/services/eveCalendarsvc.py:176-188`). Neither wire response nor notification behavior changed; Host RPC tests still exercise these flows.
- EveJS 261028.1 creates an accepted creator response and undecided invitee responses, and its participant update applies removal and addition status updates (`/home/user/evejs-261028.1/src/server/src/services/calendar/calendarRuntimeState.js:405-489, 665-710`). For the disjoint lists produced by the client, the resulting statuses match eve.rs. EveJS gives additions precedence when an ID appears in both lists; eve.rs previously gave removals precedence. The batch change preserves eve.rs behavior, and the client set-difference callsite prevents that overlap.

## Verification

- `dotnet build dotnet/src/Eve.Calendar/Eve.Calendar.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- Calendar Host tests through `tools/dev/db-lock.sh` on `eve_p8`: 5 passed, 0 failed, 0 skipped. This includes the new batch-write cases, the participant concurrency case, and Host RPC/notification coverage.
- `HomeStationHostTests` through `tools/dev/db-lock.sh` on `eve_p8`: 2 passed, 0 failed, 0 skipped. The isolated database contains pinned SDE build 3396210 and mission build 3405148 for Host composition startup.
- `CalendarMgrServiceTests`: 10 passed, 0 failed, 0 skipped.
- `git diff --check`: passed.

The checks used the real PostgreSQL store and Host RPC test harness. A graphical V24.01 client session was not used.
