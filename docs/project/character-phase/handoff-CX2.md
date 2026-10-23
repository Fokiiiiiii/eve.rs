# Character phase handoff: CX2

## CH-18 completed

The pilot-vs-pilot bounty walk passes on `eve_p2` / live port 26101. It creates two pilots and a placer, puts them in low-security Futzchag, lands real 125mm Railgun hits, destroys the bountied Rifter, and checks the paid `kill:<ship>` row, one type-17 wallet journal entry, the hunter wallet increase, and the exact victim-pool reduction. Rookie Ibis type 601 has no SDE base price, so the victim uses Rifter type 587 (base price 400,000 ISK). The sim chance formula matched EveJS; equal-speed ships about 10,305 m apart have a 1.89% chance from falloff. The live walk now closes during undock protection and stops both ships before firing.

Verified: focused `eve-space` test passed; all 888 `eve-space` library tests passed; pinned Rust fmt and workspace clippy passed; `PlayerBountyKillsTests` passed (2) and `PlayerBountyHostTests` passed (4) on `eve_p2`; the live walk passed on `eve_p2`.

## CH-15 completed

The two-pilot ship clone walk passes on `eve_p2` / port 26101. The offering pilot had a fitted SDE Clone Vat Bay I on a Rorqual; the target accepted the offer and the live test checked Offered and Done payloads, both completion notifications, cache invalidation order, the installed ship clone, one `character_jump_clone_settlements` row, the recipient debit and the type-55 journal counterparty. The target canceled the next offer; each participant received one Canceled payload, and no second settlement or debit was written. The service fix covers cancellation by either participant; EveJS sends a duplicate to the target and none to the offering pilot when the target cancels, so eve.rs sends exactly once to each connected participant.

The first live attempt timed out because station type 92885 has SDE small- and capital-ship undock points 12,878 m apart, while the Rorqual's SDE top speed is 60 m/s. The fixture now uses NPC station 60015252 (type 1531), where both ship classes share one undock locator. No sim change was needed. The live target's rank-1 Infomorph Psychology II seed is 1,414 points, matching `SkillPoints.At(1, 2)`.

Verified: `JumpCloneShipInstallTests` 4 passed; all `Eve.Services.Core.Tests` 862 passed with none skipped on `eve_p2`; Python compile and `git diff --check` passed; the two-pilot live walk passed. Host tests still cover station and structure installation, fees, settlement, refusal and restart. The graphical client was not used.

## Next work

1. CH-19: finish the source audit for season data, verify the exact inactive-season reads live, and record the available client/SDE/EveJS evidence.
2. Audit each remaining S3-S9 handoff item against current code; close it with a test or record a concrete root cause and status.
3. CH-23: update section 4.1 and stale status rows, run character test projects and live walks on this lane, and write any remaining limitations.

The worktree has uncommitted CH-19 assertions in `tools/headless-client/tests/test_project_discovery_live.py`; keep those separate from the CH-15 increment.
