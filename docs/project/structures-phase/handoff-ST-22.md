# ST-22 handoff: wire comparison, structures phase (2026-10-16)

Worktree `wt-st22` from origin/main `b474dae2`; database `eve_p3` (created by copying the SDE schemas of `eve_p4`; the live run loads the static data itself); MITM lane eve_p3 (26112 EVE.js side, 26212 eve.rs side); eve.rs front 26102; EVE.js on 26032.

## Done

- `docs/project/structures-phase/WIRE-COMPARE.md`: the scenario table. Login (`tests/test_live.py`) captured on both sides: 9 client calls equal; one kept deviation (session role bits); `userid` and key order not divergences.
- `docs/reference/compat/DEVIATIONS.md`: section "Structure ST-22 (2026-10-16)" with the role-bit decision and its source (eve-logic `carbon/common/script/sys/serviceConst.py`).

## Not done

- Structure scenarios (`test_structures.py`): not captured. The EVE.js reference has no seeded structure; see WIRE-COMPARE.md "Why the structure scenarios were not captured".
- `mitm diff` reports key order in a dictionary as a divergence; the tool is unchanged.

## Things that will bite

- A stale EVE.js server can hold port 26002 (or another EVE.js port) on this host; check `/proc` for a listener before a run, and stop only your own processes. The EVE.js boot takes 5 to 10 minutes.
- `tools/headless-client/.venv` is not inside a new worktree. Link it: `ln -s /home/user/eve.rs/tools/headless-client/.venv tools/headless-client/.venv`. The symlink is not committed.
- `CARGO_TARGET_DIR=/home/user/cargo-target` (the recipe) does not exist; this session built into `/home/user/eve.rs/target` because the disk had about 8 GB free.
- The `run-evejs-live.sh` harness deletes its server log on exit, so a failing EVE.js boot leaves no log; start the server by hand with `EVEJS_SERVER_PORT` and keep its output.
