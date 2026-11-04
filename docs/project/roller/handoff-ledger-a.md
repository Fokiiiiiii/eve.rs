# EveJS-only response ledger audit — handoff A

## Current scope

The assigned range is the first 336 rows of `docs/project/service-response-audit/evejs-only-responses.md`, by file order. This range is still in progress. EJS-0011 through EJS-0014 have been re-traced and resolved; the remaining rows in the range still need row-by-row caller and behavior review.

## Completed in this increment

The V24.01 callers in `eve/client/script/ui/services/activitiesSvc.py` call all four `activityMgr` methods: `OnActivitySeen` (144-154), `LoadActivityStatus` (46-49, 61-76), `GetStatus` (175-176), and `OnStatusChanged` (178-183). EveJS v0.12.9 `server/src/services/activity/activityMgrService.js` stores seen IDs and popup preference by character in process memory (12, 59-76, 100-145), sorts badge IDs, and sends `OnSystemActivityUpdated` after reading badges.

eve.rs now registers `activityMgr` in `CoreServices.Create` with the four methods. The implementation keeps the same character/process lifetime, rejects nonpositive seen IDs, sorts status IDs, normalizes popup state, and sends the named notification. The focused regression checks state isolation, return values, notification packet, and the Core service table.

## Verification

- `dotnet build dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `dotnet test dotnet/tests/Eve.Services.Core.Tests/Eve.Services.Core.Tests.csproj -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Activities_calls_track_character_state_and_refresh_badges'`: passed, 1 passed, 0 failed, 0 skipped.
- No database or graphical client was used.

## Continue

Continue the first 336 ledger rows in file order. For each row, record whether the target V24 client needs the response, with the precise caller and EveJS behavior; implement any needed missing flow end to end. Keep `evejs-only-summary.md`, `host-protobuf.md`, and `docs/project/TODO.md` aligned with any changed service and exit counts. Before each push, fetch and merge `origin/main`, push `HEAD:main`, then update this note with the pushed commit and checks.

The pre-existing nine deleted Rust files shown by `git status` were present before this work and were not changed by this increment.
