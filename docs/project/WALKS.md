# Headless play-through coverage

This matrix records the live-client walks available for the cross-system play-through plan. A focused walk validates its own RPC sequence and persisted result; it does not imply that adjacent systems are joined into one play-through. `pytest.ini` enforces a 300-second timeout per test through `pytest-timeout`. Database-backed walks require the assigned lane's `EVE_PG_URL` and lock.

| Activity | Existing live walk | What it exercises | Cross-system coverage |
|---|---|---|---|
| Login and character | `tests/test_login_to_character.py`, `tests/test_undock.py` | Account login, character creation/selection, station session | Standalone entry point; not chained to the other rows |
| Skills | `tests/test_skill_plan_live.py`, `tests/test_multi_training_live.py` | Skill plan and training state | Separate skill workflows |
| Ship fitting | `tests/test_module.py`, `tests/test_cargo.py` | Module activation and ship cargo, not the fitting manager | Fitting-manager-to-undock chain is open |
| Undock and warp | `tests/test_undock.py`, `tests/test_warp.py` | Session transition, ballpark movement and docking/warp | Standalone movement walks |
| Mining | — | No mining-to-market live walk is present | Open |
| NPC combat | `tests/test_npc.py` | NPC aggro, weapon damage and bounty notification | Combat is exercised independently |
| Loot | `tests/test_loot.py` | Space container access and transfer | No combat-to-loot-to-market chain |
| Market sell | `tests/test_market.py` | Market reads and two-party order settlement | No loot-to-market chain |
| Contracts | `tests/test_walk_contracts.py` | Public item exchange, acceptance, escrow and wallet settlement | Standalone two-character walk |
| Mail | `tests/test_login_to_character.py` | New-character unread count and `mailMgr.SyncMail` welcome-mail read | No send/reply/second-character flow |
| Chat | `tests/test_walk_xmpp_live.py` | Two-character Local, corporation and private chat | Requires XMPP listener |
| Corporation | `tests/test_walk_corp_station.py`, `tests/test_walk_industry.py` | Station corporation door; corporation industry job | No member-management play-through |
| Industry | `tests/test_walk_industry.py` | Install, list, cancel, deliver, research, copy and invention | Standalone industry workflows |
| Planetary interaction | `tests/test_planet_walk.py` | Colony setup, facility placement, production and customs launch | Standalone PI workflow |
| Wormhole jump | `tests/test_wormhole_live.py` | Undock, warp/fly to both ends, jump out and return | One connected travel workflow |
| Fleet | `tests/test_command_burst_live.py` | Two-pilot fleet creation, invitation, acceptance and burst delivery | No fleet-to-combat walk |

## Current work

The repository has focused walks for most requested activities, but the full requested sequence is not yet covered by an integrated play-through. Mining, mail composition/delivery, the fitting-to-undock chain, combat-to-loot-to-market, and fleet-to-combat remain uncovered here. Before treating the matrix as complete, chain those transitions through real RPCs and authoritative state, record each walk's measured duration under five minutes, and resolve every surfaced RPC error, timeout, or payload mismatch. Graphical-client verification is tracked separately from headless execution.
