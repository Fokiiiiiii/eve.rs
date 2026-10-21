# Headless play-through coverage

This matrix records the live-client walks available for the cross-system play-through plan. A focused walk validates its own RPC sequence and persisted result; it does not imply that adjacent systems are joined into one play-through. `pytest.ini` enforces a 300-second timeout per test through `pytest-timeout`. Database-backed walks require the assigned lane's `EVE_PG_URL` and lock. Durations below are measured wall time from the eve_p3 run or focused reruns; grouped values are the sum of the named tests.

| Activity | Existing live walk | What it exercises | Measured duration | Cross-system coverage |
|---|---|---|---|---|
| Login and character | `tests/test_login_to_character.py`, `tests/test_undock.py` | Account login, character creation/selection, station session | Recorded login walk 1.26 s; undock 19.40 s per race | Standalone entry point; not chained to the other rows |
| Skills | `tests/test_skill_plan_live.py`, `tests/test_multi_training_live.py`, `tests/test_login_to_character.py::test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` | Skill plan and training state; accelerated queue checks each completed level notification and settlement | Accelerated queue 11.04 s on eve_p3; skill plan skipped (gateway not configured) | Separate skill workflows |
| Ship fitting | `tests/test_fitting_lists_live.py`, `tests/test_module.py`, `tests/test_cargo.py` | Character fitting list; corporation fitting list in its `CachedMethodCallResult` wrapper; module activation; ship cargo | Fitting lists and module walk passed on eve_p3 in the focused rerun; cargo 67.12 s | Fitting list and module activation are separate; saving/applying a fitting before undock remains open |
| Undock and warp | `tests/test_undock.py`, `tests/test_warp.py` | Session transition, ballpark movement and docking/warp | Undock 19.40 s per race; warp 94.36 s; both passed in the eve_p3 grouped movement run | Standalone movement walks |
| Mining | — | No mining-to-market live walk is present | No walk | Open |
| NPC combat | `tests/test_npc.py` | NPC aggro, weapon damage and bounty notification | Bounty 79.15 s; missile combat 63.69 s; both passed in the eve_p3 grouped movement/combat run | Combat is exercised independently |
| Loot | `tests/test_loot.py` | Space container access and transfer | 45.90 s; passed in the eve_p3 grouped movement/combat/loot run | No combat-to-loot-to-market chain |
| Market sell | `tests/test_market.py` | Market reads and two-party order settlement; the live walk checks the too-early modification refusal without waiting five minutes | 1.15 s | No loot-to-market chain; the modification deadline and expiry use controlled-clock tests |
| Contracts | `tests/test_walk_contracts.py` | Public item exchange, acceptance, escrow and wallet settlement | 4.25 s | Standalone two-character walk |
| Mail | `tests/test_login_to_character.py`, `tests/test_walk_xmpp_live.py` | Welcome-mail read plus two-pilot `mailMgr.SendMail`, recipient body read, read-status sync and reply | Combined live chat/mail harness 1m 25.65s including Release build and server startup; walk has a 300-second pytest timeout | Chat and mail share the two-pilot login flow; reply delivery and both message bodies are covered on eve_p3 |
| Chat | `tests/test_walk_xmpp_live.py` | Two-character Local, corporation and private chat; exercises SASL stream restart, resource binding, room join, message delivery and private invite | Same combined live harness: 1m 25.65s including Release build and server startup | Chat and mail are joined in one two-pilot walk on eve_p3 |
| Corporation | `tests/test_walk_corp_station.py`, `tests/test_walk_industry.py` | Station corporation door; corporation industry job | 1.35 s and 8.05 s | No member-management play-through |
| Industry | `tests/test_walk_industry.py` | Install, list, cancel, deliver, research, copy and invention | 3.29–8.05 s per case | Standalone industry workflows |
| Planetary interaction | `tests/test_planet_walk.py` | Colony setup, facility placement, production and customs launch | 38.95 s for both gated tests | Standalone PI workflow |
| Wormhole jump | `tests/test_wormhole_live.py` | Undock, warp/fly to both ends, jump out and return | 64.17 s | One connected travel workflow |
| Fleet | `tests/test_command_burst_live.py` | Two-pilot fleet creation, invitation, acceptance and burst delivery | 229.95 s; passed with contracts, corporation, industry, PI and wormhole walks in the eve_p3 grouped run | No fleet-to-combat walk |

## Current work

The focused eve_p3 live run passed 13 contract, corporation, industry, PI, wormhole and fleet cases. A second grouped run covered login/character, skills, fitting-list reads, module activation, undock, warp, NPC combat, loot and belt arrivals; it surfaced a fitting cache-wrapper assertion mismatch and Destiny updates crossing adjacent action windows. Both corrected checks passed in a focused live rerun. The two-pilot chat/mail flow covers composition, recipient delivery, body read, persisted read status and reply delivery.

The full requested sequence is not yet covered by integrated play-throughs. Mining-to-market, fitting-application-to-undock, combat-to-loot-to-market, and fleet-to-combat remain open; the current mining checks exercise module activation and server-side simulation tests, not an ore-to-market client walk. Each live case is constrained to five minutes by `pytest.ini`. Graphical-client verification is tracked separately from headless execution.
