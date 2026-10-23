# CI First Handoff

## Current state

- Remote `main` is `df08707e`. The pending worktree merges that commit and adds the test-input and short-SHA fixes.
- Agent-test run `39923041081` succeeded for `main`: the focused paper-doll RPC test, fast live files, and Rust workspace tests passed. Artifact `agent-test-logs-39923041081` exists (52,187 bytes).
- Push CI run 556 (`39922996473`, SHA `e8d7ed04`) completed with one failure in `.NET (host tests 4/4)`: `Handlers_return_reference_shapes` called `GetPaperDollData` without its required character ID. All other Linux jobs and Windows passed; run 556 stayed active after later main pushes.
- The missing ID is corrected in the pending worktree. The earlier `GetMyPaperDollData` test typo was corrected in `e8d7ed04`.
- Main push CI uses a SHA-specific group with cancellation disabled. The reusable Windows workflow has a per-caller run ID group, so pending Windows jobs are not replaced by later pushes.
- `ci-status.py failures` handles full SHA values and prefixes; it returned run 556's failure name and log tail for `e8d7ed046b20`.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed.
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -c Release -nologo --verbosity minimal -nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Handlers_return_reference_shapes'`: 1 passed, 0 failed, 0 skipped. This test did not use PostgreSQL.
- The selected agent-test workflow completed end to end and uploaded its full logs.
- MSBuild and compiler servers were shut down after the local focused test. No broad local suite or database was used.

## Next steps

1. Push the pending worktree, then inspect the new push CI and confirm the required gate succeeds.
2. Dispatch the focused agent test again only if the new push run exposes another issue.
3. Record final run IDs and outcomes here before the final handoff.
