# UI services deep fix handoff

## Scope completed

This pass found and fixed two daily-flow defects: concurrent calendar invite updates could exceed the client cap, and another character could align or warp to a shared bookmark before its availability delay ended. Overview presets, notepad, neocom and shared settings remain outside this pass.

## Client evidence

- The client exposes the 50-invitee ceiling in `eve/client/script/ui/shared/eveCalendar.py:1429-1432`; `CreateOrEditEvent` calls `UpdateEventParticipants` at `eve/client/script/ui/shared/eveCalendar.py:1587-1588`.
- The limit is `calendarMaxInvitees = 50` in the V24.01 root `appConst.py:1823` (also mirrored in `eve/common/lib/appConst.py:1823`).
- `eve/client/script/ui/services/eveCalendarsvc.py:195-203` sends the participant update RPC, charges CSPA for additions, and invalidates its cache after the call.
- The client uses `GetResponsesToEvent` to populate invitees at `eve/client/script/ui/shared/eveCalendar.py:1664`, then maps response states at line 1447; `eve/client/script/ui/services/eveCalendarsvc.py:413-422` converts the RPC rows for those UI consumers. The service invalidates the cached response call after update at lines 195-201.
- The calendar service applies `OnNewCalendarEvent` to month data at `eve/client/script/ui/services/eveCalendarsvc.py:332-375` and removes events at lines 378 onward; the event editor listens for `OnRemoveCalendarEvent` and closes for its event at `eve/client/script/ui/shared/eveCalendar.py:1042,1672-1674`.
- The EVE University in-game calendar guidance says calendar events can be edited in game, describes editing as a normal calendar operation, and notes that an event cannot be edited after it starts: [EVE University Calendar](https://wiki.eveuniversity.org/EVE_University_Calendar). The 50-person limit itself is client evidence, not a wiki gameplay rule.

## EveJS comparison

EveJS 261028.1 performs its participant normalization and 50-invitee validation in one synchronous operation in `server/src/services/calendar/calendarRuntimeState.js:665-710`. Its RPC handler and notification path are in `server/src/services/calendar/calendarMgrService.js:129-150`. In Node's synchronous state mutation, concurrent RPC handlers cannot both commit from the same stale attendee count.

## Shared bookmark warp availability

The full V24.01 client path was checked, including list refresh and relevant consumers. `bookmarkSvc.py:40` binds `accessGroupBookmarkMgr`; `:69-71` primes/refreshes the bookmark cache; `:373-392` filters active bookmarks for map/navigation consumers. `bookmarkUtil.py:117-123` permits the creator immediately and otherwise permits use only when `created + SHARED_BOOKMARK_DELAY < now`; `bookmarkSvc.py:186-200` schedules the delayed cache refresh. Navigation consumers issue `CmdWarpToStuff('bookmark', bookmark.bookmarkID)` from `menuSvcExtras/movementFunctions.py:418,432` and `addressBookEntries.py:155`; align menus issue `CmdAlignTo(..., bookmarkID=...)` at `menusvc.py:2784-2789`. The system map and map view consume filtered active bookmarks at `systemMapSvc.py:400` and `mapViewBookmarkHandler.py:88`.

The EVE University [Bookmarks guide](https://wiki.eveuniversity.org/Bookmarks) specifies a two-minute delay before shared-folder bookmarks become usable. EveJS 261028.1 enforces availability at the ship action boundary in `server/src/services/ship/beyonceService.js:358-372`, including the creator exception. Its time comparison allows the exact 120-second boundary; V24.01 still rejects at equality (`bookmarkUtil.py:122`), so eve.rs follows the client. This one-millisecond edge is recorded in `docs/reference/compat/DEVIATIONS.md`.

In eve.rs, the Host callback is wired from `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:89-92`; both align and warp resolve through `BeyonceService.cs:361-362,488-489`. Before the fix, `AccessGroupBookmarkMgrService.ResolveActiveBookmarkAsync` returned any bookmark in an active folder. It now applies the caller-aware creator and timestamp rule in `dotnet/src/Eve.Bookmarks/BookmarkStore.cs:48,113-114` before the shared resolver returns the target (`dotnet/src/Eve.Services.Core/AccessGroupBookmarkMgrService.cs:38-46`).

The Host wire regression in `dotnet/tests/Eve.Host.Tests/BeyonceBookmarkPersistenceWireTests.cs:17-137` creates a shared bookmark by another character, confirms it cannot trigger `CmdAlignTo` before the delay, remains blocked at the exact boundary, and resolves one millisecond later. It also verifies creator-created/moved/copied bookmarks remain immediately usable. The test failed before the resolver fix: the pre-delay call produced a second `AlignTo` command.

## eve.rs defect and fix

Before the fix, `CalendarMgrService.UpdateEventParticipants` read current responses and checked the final active attendee count before calling the store. Two concurrent requests could both pass from the same 49-invitee state and commit 51 invitees. The regression test forced that interleaving and failed before the fix because both requests succeeded.

The authoritative check now runs inside `CalendarStore.UpdateParticipantsAsync` after it locks the event row with `FOR UPDATE`. It applies removals before additions, excludes the event creator, and rejects a final active set above `CalendarLimits.MaxInvitees` before writing responses. The service maps the store result to the existing `CustomNotify` error and sends notifications only after a committed update. Existing add/remove notification ordering remains new-event notification followed by removal notification.

Implementation: `dotnet/src/Eve.Calendar/CalendarStore.cs:235-275`, `dotnet/src/Eve.Services.Core/CalendarMgrService.cs:159-200`. Regression: `dotnet/tests/Eve.Host.Tests/CalendarParticipantConcurrencyTests.cs:14-58`.

## Checks

- Regression before fix: failed as expected; both concurrent calls succeeded and the active set reached 51.
- Regression after fix: passed on PostgreSQL database `eve_p3`; one call succeeds, the other receives `CustomNotify`, and the persisted active set remains 50.
- `CalendarMgrServiceTests`: 10 passed, 0 skipped.
- Calendar Host RPC and server test classes passed against `eve_p3`: 399 passed, 0 skipped.
- Builds completed for `Eve.Calendar`, `Eve.Bookmarks`, `Eve.Services.Core`, and `Eve.Host.Tests`. The `Eve.Services.Core.Tests` project also built after restoring its missing assets.
- No graphical client session was used.

- Shared bookmark regression: failed before the fix (2 `AlignTo` commands instead of 1), then passed after the fix on `eve_p3` (1 passed, 0 skipped). The 2-minute boundary and creator, move and copy cases are asserted.
- `Eve.Bookmarks.Tests`: 34 passed, 0 skipped on `eve_p3`; this includes store, shared-folder, notification, expiry and manager tests.
- `AccessGroupBookmarkMgrHostWireTests` and `BeyonceBookmarkPersistenceWireTests`: 3 passed, 0 skipped on `eve_p3`.
- `OwnerGroupManagerServiceTests`: 7 passed, 0 skipped on `eve_p3`.
- After syncing main, the selected calendar and bookmark Host classes passed together: 402 passed, 0 skipped. `CalendarMgrServiceTests` plus `AccessGroupBookmarkMgrWireContractTests`: 12 passed, 0 skipped.
- Pushed as `3bed36cdf1669029e7c456888c34510153882e6d`; main CI run 1397 and Agent test run 1230 both succeeded.

## Remaining audit work

Overview presets, notepad, neocom and shared settings have not been traced in this pass. Calendar reads/responses were not audited; only participant editing was. The bookmark review covered shared-bookmark align/warp availability, not every folder-edit or bookmark notification transition.
