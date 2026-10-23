# Handoff S9 (pod death walk, booster lapse, public gateway leftovers, full suite; database lane eve_p3, live port 26102)

Worktree /home/user/wt-ch-s9, branch ch-s9. Push: git fetch origin main && git merge origin/main, then git push origin HEAD:main.
Build output is private: CARGO_TARGET_DIR=/home/user/wt-ch-s9/target-s9 (gitignored); dotnet bin/obj in the worktree (delete at the end).
The headless venv is /home/user/eve.rs/tools/headless-client/.venv (grpcio and protobuf installed in S9).

## Status

| Item | State | Verified (eve_p3) |
|---|---|---|
| 2. A_skill_booster_lapses | DONE, pushed (c6ca79ec) | CharacterHostTests 15 passed (serial class run) |
| 1. test_death.py live walk | PARTLY | suicide walk PASSES (live, EVE_LIVE_SLOW=1); hull walk FAILS (open, diagnosed below). Space Death tests 15 passed |
| 3. Public gateway leftovers | PARTLY | PublicGatewayTests 5 passed; live test_skill_plan_live.py and test_title_live.py passed (gateway on, token) |
| 3a. skill plan canonical form | DONE | Eve.Skills.Tests SkillPlanCanonicalTests 2 passed; live skill plan walk |
| 3b. JWT check | DONE, signature open | 401 without token, 403 for another account's character |
| 3c. Notices/Consume | DONE for character and account targets | PublicGatewayTests notice stream test |
| 3d. title notices | SetNotice DONE; GrantedNotice/RevokedNotice open | |
| 3e. reward grants | NOT DONE (no achievement reward claim in eve.rs) | |
| 3f. TLS | NOT SERVED (documented, DEVIATIONS S9) | |
| 4. full .NET suite serialised, fmt/clippy | RUNNING (see below) | |

## Open: hull walk (test_death.py::test_a_hull_that_blows_up...)

- Two pilots undock from the same undock point (`scene/docking.rs` `undock_place`). `collision.rs` `bump_ships` bumps them every tick, so the victim and the watcher get `SetBallPosition/Velocity/SpeedFraction/Stop` at about 4 Hz; the recording has none in that step. Cause of the repeat not isolated.
- The hull's arrival is sent 0.25 s after the command; the recording's is 3.36 s after it.

## Decisions (read before changing)

- Undock clears `stationid2` silently (EveJS applyTqDockingTransitionSessionChangeShape + characterState undock); the death relocation announces it.
- The survivor and wreck enter the simulation after the session change, the cosmetics and the skin (recording order).
- Gateway identity: the bearer token (`authorization` metadata, subject `USER:EVE:<id>`) names the account; the character must belong to it. Signature not verified (no identity key in repo).
- Canonical skill plans list every level of each skill (EveJS normalizePlanRecord).

## Pushes

- c6ca79ec booster lapse test (and S9 handoff)
- later pushes: see git log of ch-s9
- 693c67cc (Host composition: no EVE_SDE_SCHEMA env read; Characters migration list 0013), 7b011e25 (Space undock test: stationid2 cleared), merge of origin/main 00c01a0f plus fix commit; Characters, Host and Space green on the merged tree
