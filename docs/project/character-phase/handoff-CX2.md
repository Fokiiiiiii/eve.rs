# Character phase handoff: CX2

## CH-18 completed

The pilot-vs-pilot bounty walk passes on `eve_p2` / live port 26101. It creates two pilots and a placer, puts them in low-security Futzchag, lands real 125mm Railgun hits, destroys the bountied Rifter, and checks the paid `kill:<ship>` row, one type-17 wallet journal entry, the hunter wallet increase, and the exact victim-pool reduction. Rookie Ibis type 601 has no SDE base price, so the victim uses Rifter type 587 (base price 400,000 ISK). The sim chance formula matched EveJS; equal-speed ships about 10,305 m apart have a 1.89% chance from falloff. The live walk now closes during undock protection and stops both ships before firing.

Verified: focused `eve-space` test passed; all 888 `eve-space` library tests passed; pinned Rust fmt and workspace clippy passed; `PlayerBountyKillsTests` passed (2) and `PlayerBountyHostTests` passed (4) on `eve_p2`; the live walk passed on `eve_p2`.

## Next work

1. CH-15: finish the ship clone installation live walk, including the accepting pilot's cancel path, notifications and settlement row. `JumpCloneService.cs`, its focused C# regression and `test_clone_ship_install_live.py` are currently dirty worktree changes; run the focused C# test and live walk before committing them.
2. CH-19: complete the source search for season data, then verify the exact inactive-season read shapes with the live test and record the source evidence.
3. Audit each remaining S3-S9 handoff item against current code and close it with a test or a recorded root cause.
4. CH-23: update section 4.1 and stale status rows, run character test projects and live walks on this lane, and record remaining gaps.

The current worktree also contains uncommitted CH-19 live assertions. Do not discard those along with the CH-15 changes.
