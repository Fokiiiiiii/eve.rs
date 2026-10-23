# Character phase handoff: CX2

## CH-18 completed

The two-client live walk passes on `eve_p2` / port 26101. It creates two pilots and a placer in low-security Futzchag, kills the Rifter with real 125mm Railgun hits, then kills the victim's capsule with a real 200,000 ISK SDE implant. The walk checks both `kill:<item>` payout rows, hunter wallet changes and type-17 journal entries, exact bounty-pool reductions, implant removal and `OnJumpCloneCacheInvalidated`. The Rifter is type 587 (base price 400,000 ISK); the capsule type 670 has no SDE base price, so the implant alone yields the asserted 40,000 ISK claim. The sim chance formula matches EveJS; equal-speed ships about 10,305 m apart have a 1.89% chance from falloff. The CH-18 Rust test pins that case and the stopped-in-range case, so the live walk stops both ships before firing.

Pod-loss implementation now reads installed items in flag 89, filters them using SDE category/slot and the SDE `nonDestructible` attribute, deletes destructible implants, carries their type and quantity through `PodLoss`, and feeds the existing Host bounty claim. Verified this turn: `Eve.Ships.Tests` focused test 1/1, `Eve.Services.Space.Tests` focused test 1/1, `Eve.Services.Crimewatch.Tests` focused payout test 1/1, and the live two-client walk 1/1. The live walk verifies claim, settlement, journal, wallet and cache invalidation. The graphical client was not used.

## CH-15 completed

The two-pilot ship clone walk passes on `eve_p2` / port 26101. The offering pilot had a fitted SDE Clone Vat Bay I on a Rorqual; the target accepted the offer and the live test checked Offered and Done payloads, both completion notifications, cache invalidation order, the installed ship clone, one `character_jump_clone_settlements` row, the recipient debit and the type-55 journal counterparty. The target canceled the next offer; each participant received one Canceled payload, and no second settlement or debit was written. The service fix covers cancellation by either participant; EveJS sends a duplicate to the target and none to the offering pilot when the target cancels, so eve.rs sends exactly once to each connected participant.

The first live attempt timed out because station type 92885 has SDE small- and capital-ship undock points 12,878 m apart, while the Rorqual's SDE top speed is 60 m/s. The fixture now uses NPC station 60015252 (type 1531), where both ship classes share one undock locator. No sim change was needed. The live target's rank-1 Infomorph Psychology II seed is 1,414 points, matching `SkillPoints.At(1, 2)`.

Verified: `JumpCloneShipInstallTests` 4 passed; all `Eve.Services.Core.Tests` 862 passed with none skipped on `eve_p2`; Python compile and `git diff --check` passed; the two-pilot live walk passed. Host tests still cover station and structure installation, fees, settlement, refusal and restart. The graphical client was not used.

## CH-19 completed for the inactive-season client surface

The live no-season walk passed on `eve_p2` / port 26101. It checks `get_season_data_for_character`, `select_season`, the current FILETIME season end, all client challenge reads and claims, and the exact empty `util.KeyVal` progress record. The source audit found no season/challenge definitions in the pinned SDE, EveJS data directories or checked-out V24.01 resource payloads/indexes. The client source names `seasonSelection.fsdbinary`, but that binary is absent. EveJS's only challenge ID is a placeholder with a fabricated notification, which eve.rs does not send. Active-season progress and rewards remain unsupported because the checked sources provide no definition or reward data.

Verified: live `test_project_discovery_live.py` passed with no skipped test. The graphical client was not used.

## CH-13 and CH-14 live checks completed

`test_player_kill_live.py` now also reads the target's notification mailbox immediately after placement and asserts one type-112 notice with the placer as sender, the target as receiver, and the exact placer ID and 2,000,000 ISK amount. This test passed together with the CH-18 PvP kill, claim and payout assertions. `test_faction_standing_live.py` passed on the same run; it asserts the `OnStandingSet` fields and confirms the `standingMgr.GetCharStandings` row equals the value in the notification. That closes the old live-notification and rowset gaps in S3/S6.

Verified: both live tests passed on `eve_p2` / port 26101 on 2026-10-23, with no skipped test. The graphical client was not used.

## Next work

1. Resolve the hull-death live mismatch from S8/S9. The current test compares command-to-arrival timing, while EveJS schedules the developer death field relative to its spawn in simulation time; preserve that distinction and keep the state/event checks exact.
2. Close or document the remaining CH-22 public gateway gaps: group target scopes, title grant/revoke notices and reward claims, TLS, and token signature validation. Inspect current branch code before using older handoff conclusions.
3. Finish the source audit for CH-19 no-season behavior, then CH-23: reconcile plan section 4.1, TODO/DEVIATIONS and project status docs, run the relevant character test projects and live walks on `eve_p2`, and record package status.

The worktree contains capsule implant destruction, loss-value wiring and focused regression coverage, plus a live two-pilot capsule bounty extension. Those checks passed; the broader phase sweep and CH-23 acceptance update remain open.