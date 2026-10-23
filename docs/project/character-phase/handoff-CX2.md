# Character phase handoff: CX2

## CH-18 completed

The pilot-vs-pilot bounty walk passes on `eve_p2` / live port 26101. It creates two pilots and a placer, puts them in low-security Futzchag, lands real 125mm Railgun hits, destroys the bountied Rifter, and checks the paid `kill:<ship>` row, one type-17 wallet journal entry, the hunter wallet increase, and the exact victim-pool reduction. Rookie Ibis type 601 has no SDE base price, so the victim uses Rifter type 587 (base price 400,000 ISK). The sim chance formula matched EveJS; equal-speed ships about 10,305 m apart have a 1.89% chance from falloff. The live walk now closes during undock protection and stops both ships before firing.

Verified: focused `eve-space` test passed; all 888 `eve-space` library tests passed; pinned Rust fmt and workspace clippy passed; `PlayerBountyKillsTests` passed (2) and `PlayerBountyHostTests` passed (4) on `eve_p2`; the live walk passed on `eve_p2`.

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

1. Audit the remaining S3-S9 gaps, especially the capsule implant value, hull-death live comparison, gateway target scopes, and title reward path; close each or document the concrete missing source/input and package status.
2. CH-23: update section 4.1 and stale status rows, run character test projects and the lane's character live walks, and record remaining limitations.

The worktree currently has CH-13/CH-14 live assertion changes and acceptance-document edits; those two live walks now pass, while the final phase sweep remains open.
