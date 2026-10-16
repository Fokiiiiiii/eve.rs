# Handoff S3 (database lane eve_p4, live port 26103)

Order: CH-4, CH-19, CH-13, CH-18, CH-14, CH-8, CH-20, CH-21, CH-22. Worktree `/home/user/wt-ch-s3`, branch `ch-s3`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`. Private build output: `CARGO_TARGET_DIR=/home/user/wt-ch-s3/target` (never the shared `/home/user/eve.rs/target`).

## Status

| Package | State | Notes |
|---|---|---|
| CH-4 | code and tests written; verification in progress | Tutorial store `ProjectDiscoveryTutorialStore` (migration 0010, `project_discovery_tutorials`); `ProjectDiscovery.initialize_tutorial_status` reads it through `GameRpcCallbacks.ReadProjectDiscoveryState`; `seasonManager` answers no active season (`get_season_data_for_character` None, `select_season` None, `get_season_end_time` now). Tests: Characters `ProjectDiscoveryTutorialStoreTests` 1 passed (eve_p4); Core `ProjectDiscoveryTutorialTests` + existing ProjectDiscovery/SessionStartup 10 passed; Host `ProjectDiscoveryHostTests` 2 passed before the CH-19 edit; live `test_project_discovery_live.py` passed once (CH-4 part, before CH-19 edit; log had no summary line, rerun with -v needed). |
| CH-19 | partial (answers only) | No season or challenge definitions exist in the SDE (only `sde.characterTitles`), EveJS (`seasonManagerService.js` is a stub that sends notifications for a placeholder id 192491367) or eve-logic (client reads FSD season data the repo does not carry). Wiki search for seasons/challenges found no season-system page. Done: the reference no-challenge answers (`get_max_active_challenges` 0, `challenge_is_dormant` true, `fill_up_and_return_challenges_for_character` {}, claims False, `get_character_progress_by_character_id` no-season record). Not done: progress from `process_client_event`, persisted challenges, rewards. Tests: Core unit `The_challenge_reads_answer_no_challenge_as_the_reference_does`, Host and live asserts. |

## Next

1. Verify CH-4 after the restart (PostgreSQL was recovering): Host test, live run with `-v`. Then push CH-4 and CH-19 (two commits: split the WIP commit `17ec39a6` with a reset-and-recommit if needed).
2. CH-13 (player bounties), CH-18 (bounty payout): wiki source is "Bounty Hunting System" (the page named `Bounties` returns 404; the wiki marks this page deprecated, it is the only bounty mechanics page found). EveJS `services/bounty/playerBountyState.js` next.
3. CH-14, CH-8 (GATE-B: wiki/SDE as written, record in DEVIATIONS), CH-20 (GATE-A: minimal gateway), CH-21, CH-22.

## Risks

- The WIP commit `17ec39a6` mixes CH-4 and CH-19 and was made to survive a container restart; its message says not yet verified.
- Tutorial state has no writer yet (`reset_tutorial`, `skip_tutorial`, `increase_tutorial_level` are not in scope of CH-4): the table is read only.
- Session `CharacterId` is taken from the session; the live test relies on a fresh pilot's character id.
