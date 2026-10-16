# ST-22: wire comparison of the structures phase, eve.rs against EveJS

Date: 2026-10-16. Worktree `wt-st22` from origin/main `b474dae2`; database `eve_p3` (SDE schemas copied empty-of-data from `eve_p4`, the run loads the static data itself); MITM lane eve_p3 (listen 26112 for EVE.js, 26212 for eve.rs); eve.rs front 26102; EVE.js on 26032 (its own port, not 26000). Procedure: `docs/project/MITM_COMPARE.md`. Captures are local under `captures/` (git-ignored).

## Result per scenario

| Scenario | Captured on both sides | Result |
|---|---|---|
| Login and session walk (`tests/test_live.py`, the recipe's scenario) | yes, both | diverged-kept (one deliberate difference: the session role bits of the test account); the rest identical |
| structureDirectory reads | no | not capturable here: the scenario needs a seeded structure on EVE.js (see below) |
| structureDocking Dock / Undock | no | not capturable here: needs a docked pilot at a seeded structure |
| structureControl GetStructurePilot / TakeControl / Release | no | not capturable here: needs the seeded structure |
| structureDeployment.Anchor refusals | no | not capturable here: the refusal scenarios need the pilots of the structures module, whose fixture seeds the structure on eve.rs only |
| moonExtractions reads | no | not capturable here: needs the seeded refinery and its moon resources |
| structureHangarViewMgr | no | not run |
| hackingMgr (no attempt) | no | not run |
| OnStructureStateChanged and the other notices | no | not run |

## What was compared

- Login (`tests/test_live.py`): both runs exit 0 with the same four tests passing (the strict-password test is skipped as in the recipe). The client calls match: 9 client-to-server calls on each side. Server events: 12 on each side.
- `mitm diff --ignore-volatile` stops at the first divergence, which is event 0 (the `SessionInitialStateNotification`). It reports the dictionary keys in a different order and the values below. The order-insensitive comparison of the rest of the stream (done ad hoc, not committed) shows one further difference in the same event, the `role` value.
- Differences in that event:
  - `userid` 3 (EVE.js) against 1 (eve.rs): a different account row per server; volatile.
  - dictionary key order: not a wire difference; the client reads the dictionary by key. `mitm diff` still flags it (tool limitation, not changed here).
  - `role` bits: EVE.js 21 bits, eve.rs 3 bits. The extra EVE.js bits are the client's administrative roles. Kept on eve.rs; recorded in `docs/reference/compat/DEVIATIONS.md` (section "Structure ST-22").

## Divergences

- Fixed: none. No eve.rs code change was needed for the login scenario.
- Kept: the session role bits of the test account (DEVIATIONS, ST-22 section).

## Why the structure scenarios were not captured

1. The EVE.js reference has no seeded structure. `tests/test_structures.py` seeds the structure by SQL into the eve.rs structures schema (`seeded_structure`); on the EVE.js run the module skips. Seeding the EVE.js structure store (the `structures` table of its gameStore, `structureState.js` `normalizeStructureRecord`) into the scratch copy was not finished in this session.
2. The EVE.js server needs port 26032 to itself. During this session another EVE.js instance held port 26002 on this host (the server logged `EVE.JS SERVER ALREADY RUNNING` and `EADDRINUSE`), so the first captures went to a dead listener. Those runs were discarded; the reported login capture is from the clean run on 26032.
3. Time: each EVE.js boot takes about 5 to 10 minutes on this machine.

## Next steps

- Seed the EVE.js structure (scratch copy) with the same refinery as `seeded_structure`, or give the structures tests a server-neutral seed, then capture `test_structures.py` on both sides.
- Teach `mitm diff` to compare dictionaries by key (order-insensitive); then the first reported divergence is the real one.
