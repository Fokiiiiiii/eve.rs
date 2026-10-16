# Handoff S6 (database lane eve_p2, live port 26101)

Scope: CH-15 remainder (structure clone install, ship-offered clone install), CH-13/CH-14/CH-18 gaps (placement notifications 112-114,
kill and payout notifications, fleet sharing, pod kills, NPC standing client notification, live kill helper), CH-3 GetSkillBundleInfo.
Worktree /home/user/wt-ch-s6, branch ch-s6 (from origin/main 53395a6a). Push: fetch, merge origin/main, git push origin HEAD:main.
Private build output: CARGO_TARGET_DIR=/home/user/wt-ch-s6/target (never /home/user/eve.rs/target). Postgres: tools/dev/pg18.sh is
run from the main checkout (the worktree has no .cache/pg18); the cluster is up on 127.0.0.1:5432.

## Status

| Item | State | Verified |
|---|---|---|
| CH-3 GetSkillBundleInfo | DONE (commit e24e6652) | CharacterHostTests (dispatch, empty shape) 9 passed on eve_p2 with the filter CharacterHostTests; live test_skill_bundle_info_live.py: see below |

## Next (in order)

1. CH-15 structure install: StructureServiceIds MEDICAL 6 and JUMP_CLONE 10; rights = online medical service, or online jump clone
   service and StructureDockingAllowedAsync; fee = CLONINGBAY_TAX profile setting (id 23) else 900,000; owner corporation credited.
2. CH-15 ship offer/accept/cancel: JumpCloneService methods; offers in memory (EveJS pendingShipCloneOffers); Clone Vat Bay I
   (type 23735) fitted and online; canReceiveCloneJumps; maxJumpClones (attribute 979) capacity; maxOperationalDistance range via
   PositionOfAsync; notifications OnShipJumpCloneInstallationOffered / Done / Canceled to both pilots.
3. Settlement record: migration 0012 (next free number at push time), table of settlements in the characters schema.
4. CH-13 placement notifications 112-114 (EveJS bountyProxy), CH-18 kill and payout notifications, fleet sharing, pod kills.
5. CH-14 OnNPCStandingChange client notification (eve-logic standingsvc).
6. Live kill helper in tools/headless-client.

## Notes for the record

- The live tests are run by tools/dev/run-eve-live.sh (EVE_RUN for one file) under db-lock.sh with EVE_PG_URL=Host=...Database=eve_p2.
- The release build for the live front and sim: private target, started in background (log in the worktree, not committed).
