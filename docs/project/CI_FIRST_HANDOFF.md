# CI First Handoff

## Current state

- Current `origin/main` is `e1628eae`, following the already-pushed SHA-specific concurrency fix.
- Required CI run 550 (`39920654219`, SHA `e7c06a86`) failed only in `.NET (host tests 4/4)`: `Paper_doll_Rpcs_round_trip_character_state_and_emit_portrait_dbrows` passed `characterId + 1` to `GetMyPaperDollData`, which correctly requires the session character ID. The pending increment corrects the test input.
- Recent CI runs also showed canceled nested Windows jobs while parent main runs continued. The reusable Windows workflow's shared ref group could replace a pending call; the pending increment makes its concurrency group unique per caller run.
- The pending increment contains the selected-test workflow and runner, status commands and parser tests, nightly extended coverage, and local/Actions testing protocol.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed.
- No database or broad local suite was used.
- The agent-test workflow still needs an Actions dispatch. The push run and its concurrent-run behavior also need confirmation after the increment is pushed.

## Next steps

1. Commit the reviewed increment, fetch and merge the current `origin/main`, then push `HEAD:main`.
2. Confirm earlier main CI runs remain active/completed and inspect the new required gate.
3. Dispatch `agent-test.yml` against `main` with the focused paper-doll test, fast live files, and Rust tests; inspect completion and artifacts, then fix any failures at their root.
4. Update this note before every push with the latest run IDs and outcomes.
