# Handoff S3 (database lane eve_p4, live port 26103)

Order: CH-4, CH-19, CH-13, CH-18, CH-14, CH-8, CH-20, CH-21, CH-22. Worktree `/home/user/wt-ch-s3`, branch `ch-s3`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`. Private build output: `CARGO_TARGET_DIR=/home/user/wt-ch-s3/target` (never the shared `/home/user/eve.rs/target`).

## Status

| Package | State | Verified (tests run, eve_p4, nothing skipped) |
|---|---|---|
| CH-4 | DONE (331b05df) | Characters `ProjectDiscoveryTutorialStoreTests` 1; Host `ProjectDiscoveryHostTests` 2; Core 10 (with CH-19); live `test_project_discovery_live.py` 1 |
| CH-19 | PARTIAL (331b05df) | Reads answer the EveJS no-challenge values. No season or challenge definitions in SDE, EveJS (stub) or the repo; wiki has no season-system page. Progress, claims, rewards not done. |
| CH-13 | PARTIAL (2b5f3bc2) | `PlayerBountyHostTests` 2; crimewatch project 41; live `test_player_bounty_live.py` 1. Gaps: placement notifications 112-114; hunter lists fed by CH-18. |
| CH-18 | PARTIAL (1d7f9e41) | `PlayerBountyKillsTests` 1; Host payout-job test (in `PlayerBountyHostTests`, 3 tests with CH-13 passing). Gaps: fleet sharing (wiki), pod kills, the live kill (no pilot-versus-pilot helper), payout notifications beyond the wallet journal. |
| CH-14 | PARTIAL (911f0a0a) | `NpcFactionStandingTests` 1; Host tests 6 passed after wiring. Gaps: live NPC kill, client notification (OnNPCStandingChange), rowset not re-read. Rule is EveJS's (wiki page empty). |
| CH-8 | DONE (b92946a7) | Host `CharacterReSculptHostTests` 1; live `test_resculpt_live.py` 1; Characters project 120 (after updating the migration-list assertion for 0010 and 0011). Not checked: the client's save with a doll payload. |
| CH-20 | NOT STARTED | Blocked on tooling and scope, not on source. The client gateway is gRPC (`eve-logic/publicGateway/grpc/*`, `eveProto`). The headless venv has no gRPC or protobuf package, and packages must not be installed, so no live gRPC client exists. Server side needs an HTTP/2 listener, gRPC framing and a hand-written protobuf codec; `EVE_PUBLIC_GATEWAY_ADDRESS` makes the client plaintext. Owner decision needed on tooling (a .NET-only test client is possible with System.Net.Http). |
| CH-21, CH-22 | NOT STARTED | Depend on CH-20. |

## Next

1. Owner: decide the CH-20 tooling question (gRPC test client, or a .NET-side test only) and whether GATE-A stays open.
2. CH-19 (challenges, progress, claims) needs season and challenge definitions: a source decision (none in SDE, EveJS or repo).
3. CH-18 and CH-14 live kills: a pilot-versus-pilot and pilot-versus-NPC live helper.

## Risks

- CH-4 changed the Characters migration set; the store test that pins the list was updated in b92946a7 (it had failed since 331b05df until then).
- The bounty payout uses journal type 17 and the bounty placement uses 16 (EveJS ids, recorded in DEVIATIONS).
- Sync-over-async in two space subscribers (`ShipLost` payout claim, `NpcDestroyed` standing), as the insurance subscriber does; a failure is logged, not raised.
