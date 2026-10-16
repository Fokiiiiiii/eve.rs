# Handoff S3 (database lane eve_p4, live port 26103)

Order: CH-4, CH-19, CH-13, CH-18, CH-14, CH-8, CH-20, CH-21, CH-22. Worktree `/home/user/wt-ch-s3`, branch `ch-s3`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`. Private build output: `CARGO_TARGET_DIR=/home/user/wt-ch-s3/target` (never the shared `/home/user/eve.rs/target`).

## Status

| Package | State | Notes |
|---|---|---|
| CH-4 | DONE, pushed (331b05df) | Tutorial store `ProjectDiscoveryTutorialStore` (migration 0010, `project_discovery_tutorials`); `initialize_tutorial_status` reads it via `GameRpcCallbacks.ReadProjectDiscoveryState`; season reads per EveJS. Tests (eve_p4, nothing skipped): Characters `ProjectDiscoveryTutorialStoreTests` 1 passed; Host `ProjectDiscoveryHostTests` 2 passed; Core unit 10 passed (incl. ProjectDiscovery/SessionStartup); live `test_project_discovery_live.py` 1 passed (port 26103). |
| CH-19 | PARTIAL, pushed | Reads answer the EveJS no-challenge values only. No season or challenge definitions in the SDE, EveJS (stub) or the repo; wiki has no season system page. Progress, claims, rewards not done. Recorded in TODO and DEVIATIONS. |

## Next

1. CH-13 (player bounties), CH-18 (bounty payout): wiki source is "Bounty Hunting System" (the page named `Bounties` returns 404; the wiki marks this page deprecated, it is the only bounty mechanics page found). EveJS `services/bounty/playerBountyState.js` next.
3. CH-14, CH-8 (GATE-B: wiki/SDE as written, record in DEVIATIONS), CH-20 (GATE-A: minimal gateway), CH-21, CH-22.

## Risks

- CH-4 and CH-19 share one commit (`331b05df`); the challenge reads are answers only.
- Tutorial state has no writer yet (`reset_tutorial`, `skip_tutorial`, `increase_tutorial_level` are not in scope of CH-4): the table is read only.
- Session `CharacterId` is taken from the session; the live test relies on a fresh pilot's character id.
