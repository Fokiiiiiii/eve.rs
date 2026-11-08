# Calendar audit fixes

Source audit: `/home/user/reports/90-audit-eve-calendar-r14.md`. Each finding below was checked against the V24.01 client at `/home/user/eve-logic`, EveJS 261028.1 at `/home/user/evejs-261028.1`, the Host/service path, and PostgreSQL-backed regressions. Graphical-client execution is tracked separately as an unchecked item.

## Finding 1 — participant-change notification diff under concurrent remove and re-invite

**Confirmed.** Before the fix, `CalendarMgrService.UpdateEventParticipants` read the response rows before and after the store transaction and compared those independent reads. A Host regression reproduced this sequence: both requests observe invitee X as active; removal commits and sends `OnRemoveCalendarEvent`; re-invitation commits `Undecided`; the re-invitation request's stale before/after sets compare equal, so it sends no `OnNewCalendarEvent` although PostgreSQL says X is invited.

**Client and reference evidence.** V24.01 builds participant changes as separate additions and removals after editing (`eve/client/script/ui/shared/eveCalendar.py:1583-1589`). `OnRemoveCalendarEvent` removes a non-deleted event from the cached month (`eve/client/script/ui/services/eveCalendarsvc.py:379-397`), while `OnNewCalendarEvent` adds it (`:333-350`). EveJS computes the added and removed IDs during the participant update (`server/src/services/calendar/calendarRuntimeState.js:665-709`) and sends those notifications in that order (`server/src/services/calendar/calendarMgrService.js:129-149`).

**Fix.** `CalendarStore.UpdateParticipantsAsync` now returns the event snapshot and the exact invitee transitions computed while holding the event-row lock. The service sends only those IDs. An event-keyed gate keeps participant mutations and their notifications in the same order within the Host process; its entries are removed when no request is using them.

**Verification.** The new PostgreSQL Host regression first failed because the re-invited character received no `OnNewCalendarEvent`; after the fix it passed and checked `OnRemoveCalendarEvent` then `OnNewCalendarEvent`, with the persisted response ending at `Undecided`. Calendar Host regressions passed 11/11, and `CalendarMgrServiceTests` passed 10/10 on `eve_p7`.

## Audit r14 follow-up (2026-11-08)

No finding in `/home/user/reports/90-audit-eve-calendar-r14.md` was false. Finding 1 was confirmed and already fixed on this branch before this follow-up; findings 2 and 3 were confirmed and repaired here.

### Finding 1 — already fixed: participant notifications under concurrent remove and re-invite

The current `CalendarStore.UpdateParticipantsAsync` holds the event row lock while calculating the actual invitee transitions and returns those IDs. `CalendarMgrService.UpdateEventParticipants` sends notifications from that result. The existing PostgreSQL Host regression `Calendar_host_reinvite_after_concurrent_removal_notifies_the_committed_transition` covers the removal/re-invitation race and persistence. No additional code change was needed for this finding.

### Finding 2 — confirmed and fixed: response notification date and mutation ordering

**Evidence.** V24.01 calls `SendEventResponse` and updates its next-event cache in `eve/client/script/ui/services/eveCalendarsvc.py:295-313`; `OnEventResponseByExternal` passes the notification to `_RespondToEvent` at `:409-411`, where `eventDateTime` is used to create the cached next event at `:300-310`. The full client search also found the event edit and participant delta flow in `eve/client/script/ui/shared/eveCalendar.py:1583-1589`, plus cache updates in `eveCalendarsvc.py:333-397`. EveJS returns the current event from `sendEventResponse` (`server/src/services/calendar/calendarRuntimeState.js:732-769`) and passes that event to its notification (`server/src/services/calendar/calendarMgrService.js:112-126`).

The original PostgreSQL Host regression reproduced the defect before the fix: while the response write was held before its transaction, an owner edit committed a new date, then the response notification sent the old date (the assertion expected the edited FILETIME but received the original one).

**Fix.** `CalendarStore.SendResponseAsync` now reads the complete event row with `FOR UPDATE` in the same transaction as the response write and returns that committed-operation snapshot. The service uses that snapshot for `OnEventResponseByExternal`. The existing per-event gate now serializes edit, delete, participant, and response mutations through their notifications, so a post-commit notification cannot arrive after a later same-process mutation and restore an obsolete cache value. This also keeps event notifications and their response-state effects in mutation order.

**Verification.** `Calendar_host_serializes_response_and_edit_notifications_in_mutation_order` checks that the accepted-response notification arrives before the edit notification, that the edit carries the old and new times, and that the date and response reset (`Undecided`) persist after the calls. `Calendar_host_response_notification_uses_the_locked_event_snapshot_after_another_writer_edits` separately changes the database after the service's pre-read and checks that the response notification uses the event snapshot returned by the response transaction. The original Host regression first failed with the edited FILETIME expected and the original FILETIME received; both regressions passed on `eve_p4` after the fix.

### Finding 3 — confirmed and fixed: event deletion preserves an invitee's decline

**Evidence.** V24.01 loads response statuses through `GetResponsesForCharacter` (`eve/client/script/ui/services/eveCalendarsvc.py:250-257`). On deletion, `OnRemoveCalendarEvent` marks the event deleted but deliberately retains a cached `Declined` response (`:379-397`); the reconnect update-list logic excludes a deleted event when that response is `Declined` (`:577-585`). EveJS `deleteEventRecord` tombstones the event without rewriting its responses (`server/src/services/calendar/calendarRuntimeState.js:712-729`).

The new PostgreSQL Host regression failed before the fix: after decline, delete, and reconnect, it expected response state `Declined` (2) and read `Deleted` (1). It also verifies the event remains visible in the month rowset with `isDeleted=true` and that the stored response is unchanged.

**Fix.** `CalendarStore.DeleteEventAsync` now changes only the event's `is_deleted` and `date_modified` fields. It leaves all response rows intact; `isDeleted` is the event-cancellation state. The existing wire and service tests now assert that accepted, undecided, declined, and uninvited responses retain their pre-delete values.

**Verification.** The reconnect Host regression passed on `eve_p4` after the pre-fix status mismatch. The existing calendar Host and service slices are included in the follow-up verification for this branch.

### Follow-up verification results

- `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --configuration Release --filter 'FullyQualifiedName~Calendar'`: 14 passed, 0 failed, 0 skipped, using `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`.
- `dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CalendarMgrServiceTests'`: 10 passed, 0 failed, 0 skipped.
- Release builds of `Eve.Calendar`, `Eve.Services.Core`, `Eve.Host.Tests`, and `Eve.Services.Core.Tests`: 0 warnings, 0 errors each.

### Not checked

- The graphical V24.01 client was not run. Client behavior was checked across the repository by source search and file/line review.
- EveJS source paths were compared. Its `server/package.json` has no test script and no calendar test files were present, so its calendar RPC flow was not executed as a separate runtime test.
- `codebase-memory-mcp` and Serena were unavailable in this environment; shell search and line-numbered source reads were used as the fallback.
