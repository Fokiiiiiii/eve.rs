# ST-22 handoff: wire comparison, structures phase (2026-10-16)

Worktree `wt-st22`; database `eve_p3` (SDE tables copied from `eve_p4`); MITM lane eve_p3 (26112 EVE.js side, 26212 eve.rs side); eve.rs front 26102; EVE.js on its own port per run.

## Done

- `docs/project/structures-phase/WIRE-COMPARE.md`: login (equal calls; role bits kept) and the 16 `test_structures.py` scenarios, one capture pair each, with the decisions.
- `tools/mitm/mitm.py`: `--ignore-key-order`, `--ignore-notification-order` (opt-in, 19 mitm tests pass).
- `tools/headless-client/tests/runid.py` (`run_token`); `test_structures.py` skips its SQL seed on the EVE.js reference (`EVE_LIVE_SERVER=evejs`).
- eve.rs fixes:
  - T07 hacking: with no attempt open, `QuitHackingAttempt` sends nothing; the turn events are a tuple (EVE.js). `HackingMgrServiceTests` 10 passing.
  - T10 moon start: the controlling gate follows EVE.js (docked and ship = structure, else "You must be controlling this structure."); the pilot record keeps `StructureDefenseDenied`. `MoonExtractionStartHostTests` 13 passing; the T10 scenario test corrected to the notice.
- DEVIATIONS (ST-22 section): session role bits; invalid moon duration (T09); Standup drill module rule (T16, ST-8a).

## Open

- ST-8a: fitted structure service modules. The Standup drill rule (T16) closes when eve.rs models the fitted drill module.
- `mitm diff` still reports dictionary key order and volatile ids as divergences unless the flags are used; the id masking lives in the analysis, not in the tool.
- T03, T14 and T15 are not capturable on EVE.js (they need the character or inventory DB seed).

## Things that will bite

- The shared target directory `/home/user/eve.rs/target` is also used by another worktree: a build from there left a protocol-74 `eve-sim` that refused the host (73). Touch the Rust sources and rebuild from this worktree (`cargo build --release -p eve-net -p eve-sim`) when the sim reports another version.
- eve.rs host start under load needs `EVE_LIVE_START_TENTHS=3000`; keep the logs with `KEEP_LOGS=1 EVE_LIVE_LOG_DIR=...`.
- The EVE.js reference: a maintenance lease is held after a seed process exits; seed in one process (`seed_all.js`: structure, moon profile, drill). The drill must be granted with `moduleState.online = true` (an update afterwards is refused with `INVALID_ITEM_STATE`).
- `tools/headless-client/.venv` is not in a new worktree: link it (not committed).
- Disk: the shared disk had about 3 GB free at the end; scratch EVE.js data roots were deleted.
