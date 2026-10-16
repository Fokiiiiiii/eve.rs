# Handoff S9 (pod death walk, booster lapse, public gateway leftovers, full suite; database lane eve_p3, live port 26102)

Worktree /home/user/wt-ch-s9, branch ch-s9 (from origin/main e0b388ff). Push: git fetch origin main && git merge origin/main, then git push origin HEAD:main.
Build output is private: CARGO_TARGET_DIR=/home/user/wt-ch-s9/target-s9 (gitignored), dotnet bin/obj in the worktree (delete at the end).

## Status

| Item | State | Verified (eve_p3) |
|---|---|---|
| 2. A_skill_booster_lapses | DONE (test fix) | CharacterHostTests 15 passed (serial class run) |
| 1. test_death.py live walk | IN PROGRESS | |
| 3. Public gateway leftovers | NOT STARTED | |
| 4. Full .NET suite serialised, fmt, clippy | NOT STARTED | |

## Item 2 root cause

Foundations.StartAsync starts the scheduler runner in the composed host. ScheduleAsync wakes it, so it claims the job first;
RunDueAsync then claims nothing and the test read the job while the handler was still running (the job is left running for a
few milliseconds, and the test read it then). Reproduced 2 of 2 failing; with a diagnostic log in ExecuteAsync the same run passed,
which is the race. Fix: the test waits, bounded (20 s), for the job to be removed, as MultipleTrainingSlotExpiry's test does.

## Notes

- The worktree needs EVE_SDE_DIR=/home/user/eve.rs/.cache/sde/eve-online-static-data-3396210-jsonl for the live run (the worktree has no .cache).
- The headless venv is only in /home/user/eve.rs/tools/headless-client/.venv (use that python for live walks).
