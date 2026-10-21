# Headless play-through coverage

This matrix records the live-client walks available for the cross-system play-through plan. A focused walk validates its own RPC sequence and persisted result; it does not imply that adjacent systems are joined into one play-through. `pytest.ini` enforces a 300-second timeout per test through `pytest-timeout`. Database-backed walks require the assigned lane's `EVE_PG_URL` and lock. Durations below are measured wall time from the eve_p3 run or focused reruns; grouped values are the sum of the named tests.

| Activity | Existing live walk | What it exercises | Measured duration | Cross-system coverage |
|---|---|---|---|---|
| Login and character | `tests/test_login_to_character.py`, `tests/test_undock.py` | Account login, character creation/selection, station session | Recorded login walk 1.26 s; undock 19.40 s per race | Standalone entry point; not chained to the other rows |
| Skills | `tests/test_skill_plan_live.py`, `tests/test_multi_training_live.py`, `tests/test_login_to_character.py::test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` | Skill plan and training state; accelerated queue checks each completed level notification and settlement | Accelerated queue 11.04 s on eve_p3; skill plan skipped (gateway not configured) | Separate skill workflows |
| Ship fitting | `tests/test_module.py`, `tests/test_cargo.py` | Module activation and ship cargo, not the fitting manager | Module 77.69 s; cargo 67.12 s | Fitting-manager-to-undock chain is open |
| Undock and warp | `tests/test_undock.py`, `tests/test_warp.py` | Session transition, ballpark movement and docking/warp | Undock 19.40 s per race; warp 94.36 s | Standalone movement walks |
| Mining | — | No mining-to-market live walk is present | No walk | Open |
| NPC combat | `tests/test_npc.py` | NPC aggro, weapon damage and bounty notification | Bounty 79.15 s; missile combat 63.69 s | Combat is exercised independently |
| Loot | `tests/test_loot.py` | Space container access and transfer | 45.90 s | No combat-to-loot-to-market chain |
| Market sell | `tests/test_market.py` | Market reads and two-party order settlement; the live walk checks the too-early modification refusal without waiting five minutes | 1.15 s | No loot-to-market chain; the modification deadline and expiry use controlled-clock tests |
| Contracts | `tests/test_walk_contracts.py` | Public item exchange, acceptance, escrow and wallet settlement | 4.25 s | Standalone two-character walk |
| Mail | `tests/test_login_to_character.py` | New-character unread count and `mailMgr.SyncMail` welcome-mail read | Included in recorded login walk (1.26 s) | No send/reply/second-character flow |
| Chat | `tests/test_walk_xmpp_live.py` | Two-character Local, corporation and private chat; exercises SASL stream restart, resource binding, room join, message delivery and private invite | 5.78 s | Passed live on eve_p3 after the parser restart fix |
| Corporation | `tests/test_walk_corp_station.py`, `tests/test_walk_industry.py` | Station corporation door; corporation industry job | 1.35 s and 8.05 s | No member-management play-through |
| Industry | `tests/test_walk_industry.py` | Install, list, cancel, deliver, research, copy and invention | 3.29–8.05 s per case | Standalone industry workflows |
| Planetary interaction | `tests/test_planet_walk.py` | Colony setup, facility placement, production and customs launch | 38.95 s for both gated tests | Standalone PI workflow |
| Wormhole jump | `tests/test_wormhole_live.py` | Undock, warp/fly to both ends, jump out and return | 64.17 s | One connected travel workflow |
| Fleet | `tests/test_command_burst_live.py` | Two-pilot fleet creation, invitation, acceptance and burst delivery | 229.95 s | No fleet-to-combat walk |

## Current work

The repository has focused walks for most requested activities, but the full requested sequence is not yet covered by an integrated play-through. Mining, mail composition/delivery, the fitting-to-undock chain, combat-to-loot-to-market, and fleet-to-combat remain uncovered here. Before treating the matrix as complete, chain those transitions through real RPCs and authoritative state, record each walk's measured duration under five minutes, and resolve every surfaced RPC error, timeout, or payload mismatch. Graphical-client verification is tracked separately from headless execution.
