# Handoff: minor review findings for c247f18c and 7f50c62e

## Verified findings

1. **ESS assertion:** Keep `undocked is None`. Current EveJS `ShipService.Handle_Undock` returns `null` on success (`server/src/services/ship/shipService.js:2139-2143`). The headless `Client.call_bound` returns `packet.result_of(...)` (`tools/headless-client/evecli/client.py:214-229`), and `result_of` unwraps the CallDown and marshal-stream wrappers (`tools/headless-client/evecli/packet.py:250-257`). V24.01's station caller passes `shipsvc.Undock` through `PerformSessionChange` and ignores its result (`eve/client/script/ui/station/base.py:497`; `carbon/common/script/sys/sessions.py:417,476`). The separate structure docking RPC also discards its response (`eve/client/script/ui/services/structure/structureDocking.py:30-41`). No assertion change is needed.

2. **Event replay / duplicate guard:** Added a Host integration test that publishes before subscribing with `StartAt.Beginning`, pumps until delivered, settles for one second, pumps again, and asserts exactly one receipt. The existing tower job test now makes the same settle-and-pump check before asserting one receipt. These checks exercise replay and delayed duplicate delivery; they do not prove the cause of the earlier sharded CI failure.

3. **Movement match:** `stop()` currently returns only `Steering::Nothing` or `Steering::Actions` (`crates/eve-space/src/scene/movement.rs:416-420`). The caller now handles both explicitly and marks `Steering::Refused(_)` unreachable, since the shared enum also represents refusals for other commands. The focused movement test passes.

4. **CI investigation note:** Removed machine-specific worktree, database, and host details from `docs/project/service-audit/ci-red-1479.md`; retained the verified CI outcomes and investigation facts.

## Checks completed

- After merging `origin/main` at `4c9cf157`, Eve.Host.Tests built successfully with 0 warnings and 0 errors. Both focused database tests passed through `tools/dev/db-lock.sh` on `eve_p3`: 2 passed, 0 failed, 0 skipped.
- Rust 1.94.1 format check, `cargo check -p eve-space`, the focused movement test, and workspace Clippy all passed. The focused test passed 1/1; other binaries had only filtered tests.
- Selected Agent Test run 1375 passed. Main CI run 1578 completed successfully, including the end-to-end job. No red main gate remains.
- The live ESS journey and graphical V24.01 client were not exercised locally.
- Existing unrelated worktree changes in `dotnet/src/Eve.Services.Skills/SkillBoosterLapse.cs` and `audit-eve-insurgency-r10.md` were preserved and must stay out of this commit.

## Remaining

Commit only the task files and this handoff note with the required identity, fetch and merge `origin/main`, rerun touched checks after that merge, push `HEAD:main`, then wait for Actions. Keep this note current with the pushed revision and its Actions result. Serena editor tools were unavailable because no language servers were running; the approved native patch fallback was used for existing-file edits.
