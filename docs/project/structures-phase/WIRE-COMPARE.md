# ST-22: wire comparison of the structures phase, eve.rs against EveJS

Date: 2026-10-16. Worktree `wt-st22` (origin/main at `b474dae2`, then the ST-22 commits). Database `eve_p3` (SDE tables copied from `eve_p4`). MITM lane eve_p3: 26112 (EVE.js side), 26212 (eve.rs side); eve.rs front 26102; EVE.js on its own port per run (26032, 26060, 26140, 26220, 26260 were used; each checked free first). `CARGO_TARGET_DIR=/home/user/eve.rs/target`. Captures are local under `captures/` (git-ignored).

## How the EVE.js side was seeded (scratch copy only, not committed)

- `seed_structure.js`: the refinery of `test_structures.py` (`seeded_structure`), created with `createStructure` in EVE.js's gameStore: type 35835 (Athanor), system 30100032, owner corporation 1000006, state 110, services 1 and 14 online, `nextStructureID` 1040000000777.
- `seed_moon_drill.js`: the moon profile of the structure (`moonExtractions.resourcesByStructureID`, moon 40000001, resources 34 at 0.75 and 20 at 0.25), and a Standup Moon Drill I (type 45009) fitted in service slot 164 with the module state online (granted with `moduleState.online = true`).
- Both run in one process (`EVEJS_GAMESTORE_OWNER_ROLE=maintenance`): a second process cannot take the maintenance lease that the first one leaves behind.
- `tests/test_structures.py` skips its SQL seed when `EVE_LIVE_SERVER=evejs`.
- `tests/runid.py` (`run_token`): deterministic names when `EVE_LIVE_RUN_ID` is set.

## Comparison tool

`tools/mitm/mitm diff` has two opt-in flags (`--ignore-key-order`, `--ignore-notification-order`; 19 mitm tests pass). The analysis below also unwraps `$long` values (the same id in a different marshal encoding) and masks ids (10^8 to 10^14, by first appearance) and time fields. Ids and times are volatile by the rule of `MITM_COMPARE.md`.

## Login scenario (`tests/test_live.py`)

Both runs exit 0, same four tests passing. Client calls: 9 each, equal. Server events: 12 each.

- `userid` (3 against 1) and dictionary key order: volatile.
- Session role bits: EVE.js grants 21 bits, eve.rs 3. Kept (DEVIATIONS, "Structure ST-22").

## Structure scenarios (`tests/test_structures.py`, one capture pair per test)

"Result" compares the structure-service calls, their answers and the structure notices of each test, event by event.

| Test | EVE.js rc | eve.rs rc | Result |
|---|---|---|---|
| T01 pilot sees the structure | 0 | 0 | not compared: no structure service call (the test reads ballpark notices, outside the filter) |
| T02 pilot docks and undocks | 0 | 0 | identical except the ship-id encoding of Dock/Undock (volatile) |
| T03 NPC corporation refused the anchor | 1 | 0 | not capturable on EVE.js: the test needs the character DB seed (`EVE_CHARACTERS_SCHEMA`) |
| T04 no pilot: GetStructurePilot answers None | 0 | 0 | identical |
| T05 take control from space refused | 0 | 0 | identical |
| T06 boarding a missing structure | 0 | 0 | identical |
| T07 hacking calls with no open attempt | 0 | 0 | identical after the fix below |
| T08 delivery from space refused | 0 | 0 | identical except the item and ship id encodings (volatile) |
| T09 moon extraction, duration not positive | 1 | 0 | diverged, kept: see DEVIATIONS ("Invalid moon duration") |
| T10 moon extraction from space | 0 | 0 | identical after the fix below |
| T11 docked pilot takes control | 0 | 0 | identical except ids (Dock encoding, the pilot's character id) |
| T12 pilot in space boards and ejects | 0 | 0 | identical (8 events); the first eve.rs run failed on the 60 s host start under load, the rerun passed |
| T13 second pilot displaces the first | 0 | 0 | identical except ids (Dock encoding, pilot and entering character ids) |
| T14 docked pilot delivers | 1 | 0 | not capturable on EVE.js: the recipient step needs the inventory DB seed (`EVE_INVENTORY_SCHEMA`) |
| T15 pilot in space drops cargo | 1 | 0 | not capturable on EVE.js: needs the inventory DB seed; the first eve.rs run failed on the host start, the rerun passed |
| T16 controlling pilot starts a moon extraction | 0 | 0 | identical after the drill seed (only the extraction timestamps and the Dock id encoding differ); see DEVIATIONS ("Standup drill") |

not exercised: `structureHangarViewMgr` (no test in `test_structures.py`), and the `OnStructureStateChanged` family (the only test that raises it relocates the pilot through the eve.rs DB, so it is not capturable on EVE.js).

## The four open items: decisions

- T07 hacking call order: fixed in eve.rs. With no attempt open, EVE.js `Handle_QuitHackingAttempt` returns null and sends nothing, while a tile click answers with the ACK turn. eve.rs sent an ACK for the quit as well. eve-logic (`hackingUISvc.py`) sends the quit without expecting a notice, so it is silent on this; eve.rs follows EVE.js. Also fixed: the turn events marshal as a tuple in EVE.js (the array of events) and as a list in eve.rs; eve-logic iterates the list and does not fix the type, so eve.rs follows EVE.js. Tests: `HackingMgrServiceTests` (10 passing), including `With_no_attempt_open_a_quit_sends_nothing_and_a_tile_click_sends_the_ack`.
- T09 moon duration not positive: kept, recorded (DEVIATIONS, "Invalid moon duration").
- T10 moon extraction from space: fixed in eve.rs. EVE.js checks the controlling session (docked in the structure, ship equals the structure) before the moon service, and refuses with the notice "You must be controlling this structure." eve.rs answered `StructureDefenseDenied`, and the scenario test expected it, citing EVE.js, which EVE.js does not send. The gate now follows EVE.js order; a forged or released session (pilot record mismatch) keeps `StructureDefenseDenied` (ST-5 persistent control, DEVIATIONS unchanged). The fracture by a pilot not docked keeps `StructureDefenseDenied` (EVE.js: no structure in the session). Tests: `MoonExtractionStartHostTests` (13 passing), `test_a_moon_extraction_started_from_space_is_refused_as_not_controlling`.
- T16 Standup Moon Drill rule: the EVE.js refusal came from the seed (no drill). With a fitted online drill both sides start the extraction. The rule difference is real: EVE.js requires a fitted, online drill module; eve.rs requires the moon mining service online. Kept and recorded (DEVIATIONS, "Standup drill"): eve.rs has no fitted-module model for structures (ST-8a).

## Divergences

- Fixed in eve.rs: T07 (no-attempt quit, turn list type), T10 (controlling gate and notice).
- Kept (DEVIATIONS, ST-22 section): session role bits (login); invalid moon duration (T09); Standup drill module rule (T16).
- Volatile, not divergences: ids (ships, items, characters, the Dock/Deliver encodings), the moon extraction timestamps, `userid`, dictionary key order.

## Environment notes

- The eve.rs simulation: the shared target directory is also used by another worktree; a build from another worktree left a protocol-74 `eve-sim` that refused the host (73). Rebuilding after touching the sources fixed it. Use a separate target directory per worktree when disk allows.
- eve.rs host start under load (load average 6 to 9) needs `EVE_LIVE_START_TENTHS=3000`.
