# ST-22: wire comparison of the structures phase, eve.rs against EveJS

Date: 2026-10-16. Worktree `wt-st22` from origin/main `b474dae2`. Database `eve_p3` (SDE tables copied from `eve_p4`, 62 + 3 tables). MITM lane eve_p3: 26112 (EVE.js side), 26212 (eve.rs side); eve.rs front 26102; EVE.js on its own port per run (26002/26032 were taken by other EVE.js instances; the runs used 26032, 26060 and 26140, each checked free first). `CARGO_TARGET_DIR=/home/user/eve.rs/target`. Captures are local under `captures/` (git-ignored).

## How the EVE.js side was seeded (scratch copy only)

- The refinery of `test_structures.py` (`seeded_structure`) is created in EVE.js's own gameStore by `seed_structure.js` (in the scratchpad, not the repo): `createStructure` with type 35835 (Athanor), system 30100032, owner corporation 1000006, state 110, services 1 and 14 online, and `nextStructureID` 1040000000777. It runs once per fresh data root with `EVEJS_GAMESTORE_OWNER_ROLE=maintenance` (a `standalone` role is refused by the store).
- Not seeded: the moon resources of structure 40000001 and a fitted Standup Moon Drill. EVE.js refuses moon starts for those reasons (see the moon rows).
- `tests/test_structures.py` skips its SQL seed when `EVE_LIVE_SERVER=evejs` (the refinery comes from the EVE.js data root).
- Names: `tests/runid.py` (`run_token`) gives deterministic names when `EVE_LIVE_RUN_ID` is set, so both servers create the same characters and accounts.

## Comparison tool

`tools/mitm/mitm diff` gained two opt-in flags (tests in `tools/headless-client/tests/test_mitm.py`, 19 passing):

- `--ignore-key-order`: marshal dictionaries compared by key (the client reads them by key).
- `--ignore-notification-order`: server notifications compared as a set after the results (independent notices may race).

Still not normalised by the tool: integer ids passed as call arguments (character, item and ship ids). Their magnitude differs between servers (eve.rs active-ship ids below 2^31, EVE.js ids above it), so the marshal encoding differs (`$long` against a plain int). These are volatile ids; the comparisons below mask them by aliasing the ids in the range 10^8 to 10^14 in order of first appearance, and report the rest.

## Login scenario (`tests/test_live.py`, recipe run)

Both runs exit 0 with the same four tests passing. Client calls: 9 on each side, equal. Server events: 12 on each side. Differences, all accounted for:

- `userid` 3 (EVE.js) against 1 (eve.rs): a different account row per server; volatile.
- Dictionary key order: not a difference for the client; flagged by the old tool, accepted by `--ignore-key-order`.
- Session role bits: EVE.js grants 21 bits, eve.rs 3. The extra EVE.js bits are the client's administrative roles (eve-logic `carbon/common/script/sys/serviceConst.py`). Kept on eve.rs; DEVIATIONS "Structure ST-22".

## Structure scenarios (`tests/test_structures.py`, one capture pair per test)

Each test ran alone on both servers with its own run id. "EVE.js rc" and "eve.rs rc" are the pytest exit codes. "Stream" is the structure-service calls, their answers and the structure notices (services `structure*`, `moonExtractions`, `hackingMgr`, notices matching Structure, Moon, Hacking, Deliver, Cargo), after the normalisation above.

| Test | EVE.js rc | eve.rs rc | Result |
|---|---|---|---|
| T01 pilot sees the structure (state and timer) | 0 | 0 | captured; no structure service call in the stream (the test reads ballpark notices, not in the filter): not compared |
| T02 pilot docks and undocks (`structureDocking`) | 0 | 0 | 4 and 4 events; only difference is the id encoding of the Dock/Undock arguments (volatile): identical otherwise |
| T03 NPC corporation refused the anchor by name | 1 | 0 | not capturable on EVE.js: its test needs the character DB seed (`EVE_CHARACTERS_SCHEMA`), 0 structure events |
| T04 no pilot: GetStructurePilot answers None | 0 | 0 | 2 and 2 events, identical |
| T05 take control from space refused | 0 | 0 | 2 and 2 events, identical |
| T06 boarding a missing structure: cancelled targeting | 0 | 0 | 2 and 2 events, identical |
| T07 hacking calls with no open attempt | 0 | 0 | 5 and 6 events; OPEN: eve.rs sends `ClickedOnTile` (c2s) where the EVE.js stream has `QuitHackingAttempt` answered; not analysed |
| T08 delivery from space refused | 0 | 0 | 2 and 2 events; differs at the id encoding of the Deliver arguments; the rest is not compared |
| T09 moon extraction, duration not positive | 1 | 0 | OPEN: EVE.js answers the start (None) where eve.rs refuses `MoonminingEventTooShort`; the EVE.js seed has no moon resources, so its moon check differs; not decided |
| T10 moon extraction from space | 1 | 0 | OPEN: EVE.js refuses `CustomNotify` ("You must be controlling this structure") where eve.rs refuses `StructureDefenseDenied`; the test cites `StructureDefenseDenied`; not decided |
| T11 docked pilot takes control and the structure names it | 0 | 0 | 10 and 10 events; differs at the Dock id encoding; the rest is not compared |
| T12 pilot in space boards and ejects | 0 | 0 (rerun) | 8 and 8 events, identical. The first eve.rs run failed because the host did not open its socket within 60 s under machine load (environment, not behaviour); the rerun with a 300 s start window passed |
| T13 second pilot displaces the first | 0 | 0 (rerun) | 13 and 13 events; differs at the Dock id encoding; the rest is not compared. First eve.rs run: host start timeout (environment) |
| T14 docked pilot delivers and the recipient is told | 1 | 0 | not capturable on EVE.js: the recipient step needs the inventory DB seed (`EVE_INVENTORY_SCHEMA`) |
| T15 pilot in space drops cargo | 1 | 0 (rerun) | not capturable on EVE.js: needs the inventory DB seed; 0 EVE.js structure events. First eve.rs run: host start timeout (environment) |
| T16 controlling pilot starts a moon extraction | 1 | 0 | OPEN: EVE.js refuses with "An online Standup Moon Drill is required" (its fitted-module rule); the seed has no drill; eve.rs passes the same call. Which rule is right is not decided |

Not run: `structureHangarViewMgr` (no test in `test_structures.py`), the `OnStructureStateChanged` notice family (only the legal-system anchor test raises it, and that test relocates the pilot through the eve.rs DB, so it is not capturable on EVE.js).

## Divergences

- Fixed in eve.rs: none. No divergence in this run was shown to be an eve.rs defect.
- Kept (DEVIATIONS): session role bits of the test account.
- Volatile, not a divergence: ids and ship ids (magnitude and encoding), `userid`, dictionary key order.
- Open, not decided: T07 hacking sequence; T09 and T10 moon refusals (EVE.js seed lacks the moon resources and the controller state); T16 Standup Moon Drill rule (EVE.js requires a fitted module, eve.rs passes the start). Each needs its own EVE.js seed (moon resources, a fitted drill) or a source check in eve-logic before anything is changed.
- Environment: the first runs of T12, T13 and T15 on eve.rs failed at host start under load (the rerun passed). The earlier EVE.js runs on 26002 were not valid: another EVE.js held that port.

## Next steps

1. Analyse T07 (the `ClickedOnTile` against `QuitHackingAttempt` order) against eve-logic `hackingMgr`.
2. Seed the EVE.js moon resources and a fitted Standup Moon Drill in the scratch data root, then rerun T09, T10 and T16.
3. Compare the rest of T08, T11 and T13 after normalising the Dock/Deliver id encoding (the tool masks ids only in 10^8 to 10^14; extend the masking, not the comparison).
