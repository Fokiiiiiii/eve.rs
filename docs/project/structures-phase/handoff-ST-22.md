# ST-22 handoff: wire comparison, structures phase (2026-10-16)

Worktree `wt-st22` from origin/main `b474dae2`. Database `eve_p3` (SDE tables copied from `eve_p4`). MITM lane eve_p3 (26112 EVE.js side, 26212 eve.rs side); eve.rs front 26102; EVE.js on its own port per run (checked free first: 26032, 26060, 26140 were used).

## Done

- `docs/project/structures-phase/WIRE-COMPARE.md`: the login scenario (equal calls; the role bits kept as a deviation) and the 16 `test_structures.py` scenarios, one capture pair per test. Per test: both rc, comparison result, and the open items.
- `tools/mitm/mitm.py`: `--ignore-key-order` and `--ignore-notification-order` (opt-in). Tests in `tools/headless-client/tests/test_mitm.py` (19 passing).
- `tools/headless-client/tests/runid.py` (`run_token`): deterministic names when `EVE_LIVE_RUN_ID` is set. Used by `test_structures.py` and `test_undock.py`.
- `tools/headless-client/tests/test_structures.py`: the seed fixture skips its SQL seed when `EVE_LIVE_SERVER=evejs`.
- `docs/reference/compat/DEVIATIONS.md`: section "Structure ST-22" (session role bits, kept).

## Not done (open)

- No eve.rs fix: no divergence was shown to be an eve.rs defect. Open and undecided: T07 (hacking order), T09 and T10 (moon refusals), T16 (Standup Moon Drill rule). See WIRE-COMPARE.md "Next steps".
- T08, T11, T13: only the first event (the Dock/Deliver id encoding) was compared. The rest is not verified.
- EVE.js seed: the moon resources (structure 40000001) and a fitted Standup Moon Drill are not seeded. The moon rows need them.
- The EVE.js side of the character and inventory scenarios (T03, T14, T15) needs the DB seed, which is not available to EVE.js: those are not capturable on the reference.

## Things that will bite

- A stale EVE.js server holds a port: check `/proc` for a listener before a run; stop only your own processes (match `argv` exactly; a `pkill -f` pattern matches your own shell and kills it).
- eve.rs host start takes longer than 60 s under load (load average 6 to 9 here): run with `EVE_LIVE_START_TENTHS=3000`; keep the logs with `KEEP_LOGS=1 EVE_LIVE_LOG_DIR=...`.
- `tools/headless-client/.venv` is not in a new worktree: link it `ln -s /home/user/eve.rs/tools/headless-client/.venv tools/headless-client/.venv` (not committed).
- The EVE.js boot takes 5 to 10 minutes; `run-evejs-live.sh` deletes its log on exit, so boot EVE.js by hand (`EVEJS_SERVER_PORT`, `EVEJS_DATA_ROOT`, `EVEJS_GAMESTORE_DATA_DIR`) and keep the output.
- `eve_p3` needs the SDE tables: `tools/dev/copy-sde-database.py <eve_p4 url> eve_p3` (the source `eve` has none now).
