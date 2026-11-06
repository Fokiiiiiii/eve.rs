# Mission standing settlement (cx7)

## Status

Implementation and selected local checks are complete. Main push and Actions verification are pending.

## Behavior

- Mission completions lock affected character/standing-owner pairs in stable owner-ID order in the completion transaction. Each gain reads the standing after obtaining that lock, applies the diminishing gain, rounds to 0.001 using JavaScript-compatible rounding, and clamps to -10..10.
- Mission standing history uses eventTypeID 73, the completion FILETIME, the raw modification, the mission name in `msg`, and null `int_1` through `int_3`. A standing already at its cap still writes a history event; a zero gain produces no standing event.
- After commit, the production Host callback sends `OnStandingsModified` addressed to `charid`, with one modifications argument containing five-field rows. It runs before the mission-store committed event. Rollback and duplicate completion do not invoke it.
- No compatibility deviation was recorded.

Implementation: `dotnet/src/Eve.Host/Composition/MissionComposition.cs:42,810,917-926`; `dotnet/src/Eve.Services.Core/MissionDungeonRuntime.cs:402-407`; `dotnet/src/Eve.Relations/RelationStore.cs:83-89,148-170`.

## V24.01 client evidence

- `eve/client/script/ui/services/standingsvc.py:64-80` unpacks each modification as `fromID, toID, rawChange, minAbs, maxAbs`, invalidates transaction history, updates the character/corporation standing cache, and emits `OnNPCStandingChange`.
- `GetStandingTransactions` is consumed by the standings panel at `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:107` and the Resource Wars first-completion tracker at `resourcewars/client/firstcompletiontracker.py:35`. The standing service fetches it and scales history modification by ten at `eve/client/script/ui/services/standingsvc.py:173-183`.
- History rows use `eventTypeID`, `eventDateTime`, and `modification` at `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsHistoryEntry.py:22-32`. Mission history displays `int_1` as the localized message ID when present, otherwise `msg`, at `eve/common/script/util/eveFormat.py:126-140`.
- The full-client `OnNPCStandingChange` consumers are `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:178-183`, `eve/client/script/ui/shared/standings/standingEntry.py:118-121`, `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderAgents.py:176-181`, and `eve/client/script/ui/services/facWarSvc.py:307-319`.

## Reference behavior

- EVE University NPC standings: https://wiki.eveuniversity.org/NPC_standings — diminishing updates move toward +10 and standings remain within -10..10.
- EveJS 261028.1 applies mission changes to the current character standing at `server/src/services/agent/missionRewardSettlementState.js:1319-1323`; rounds at 0.001 in `server/src/services/character/standingRuntime.js:176-179`; sends the five-field notification rows at `standingRuntime.js:1122-1144`; and persists event type, time, modification, message, and integer fields at `standingRuntime.js:1295-1313`.

## Local checks

All database-backed checks used `eve_p7` through `tools/dev/db-lock.sh`.

- Host build: passed.
- `MissionCompletionTransactionTests`: 15 passed, 0 skipped.
- Core mission runtime and Arc29 tests: 26 passed, 0 skipped.
- Relations and standing-direction tests: 15 passed, 0 skipped.
- Corporation standing/relation callers: 115 passed, 0 skipped.
- Space standings callers: 7 passed, 0 skipped.
- Crimewatch relation callers: 37 passed, 0 skipped.
- Character creation relation caller: 21 passed, 0 skipped.
- Station reprocessing relation caller: 13 passed, 0 skipped.
- Host standing history wire-shape check: 1 passed, 0 skipped.

The graphical V24.01 client was not exercised. The worktree fast-forwarded to `origin/main` at `abcdcc4c`; touched builds and selected checks passed again after that merge. Commit `d7449b80` was pushed to `main`. Its push-triggered Actions run `42160492961` completed successfully with no failed jobs or tests. This note is the docs-only follow-up to record that result.
