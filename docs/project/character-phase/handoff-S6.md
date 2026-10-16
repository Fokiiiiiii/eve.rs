# Handoff S6 (database lane eve_p2, live port 26101)

Scope: CH-15 remainder (structure clone install, ship-offered clone install), CH-13/CH-14/CH-18 gaps (placement notifications 112-114,
kill and payout notifications, fleet sharing, pod kills, NPC standing client notification, live kill helper), CH-3 GetSkillBundleInfo.
Worktree /home/user/wt-ch-s6, branch ch-s6 (from origin/main 53395a6a). Push: fetch, merge origin/main, git push origin HEAD:main.
Private build output: CARGO_TARGET_DIR=/home/user/wt-ch-s6/target (never /home/user/eve.rs/target). Postgres: tools/dev/pg18.sh is
run from the main checkout (the worktree has no .cache/pg18); the cluster is up on 127.0.0.1:5432.

## Status

| Item | State | Verified |
|---|---|---|
| CH-3 GetSkillBundleInfo | DONE, pushed (e24e6652) | CharacterHostTests 11 passed (eve_p2); live test_skill_bundle_info_live.py passed (port 26101) |
| CH-15 structure install and ship offer | PARTIAL, pushed (c8adb0b3, 0def42ab, 4f8b9a5e, merged 7b8d166c) | CharacterHostTests structure install and settlement rows (10 passed earlier, 11 with CH-3 after merge); JumpCloneShipInstallTests 4 passed (Services.Core, 745 total passed with eve_p2); Characters StoreTests 28 passed; live test_clone_install_live.py 2 passed (station refusal, offer InviteClone1). Open: a live offer accepted between two pilots (needs a fitted Clone Vat Bay in space). |
| CH-13 placement notifications 112-114 | DONE locally (dbefbeff), not pushed | PlayerBountyHostTests 4 passed (eve_p2) incl. A_placed_bounty_tells_the_target_pilot_or_the_members_of_its_corporation_once_and_never_the_placer |

## Next (in order)

After the usage reset (PostgreSQL in crash recovery): re-check `psql ... -Atc "select 1"` before any test run. Items 4-6 below are still open.


1. (done, see status) CH-15 structure install: StructureServiceIds MEDICAL 6 and JUMP_CLONE 10; rights = online medical service, or online jump clone
   service and StructureDockingAllowedAsync; fee = CLONINGBAY_TAX profile setting (id 23) else 900,000; owner corporation credited.
2. (done, see status) CH-15 ship offer/accept/cancel: JumpCloneService methods; offers in memory (EveJS pendingShipCloneOffers); Clone Vat Bay I
   (type 23735) fitted and online; canReceiveCloneJumps; maxJumpClones (attribute 979) capacity; maxOperationalDistance range via
   PositionOfAsync; notifications OnShipJumpCloneInstallationOffered / Done / Canceled to both pilots.
3. Settlement record: migration 0012 (next free number at push time), table of settlements in the characters schema.
4. CH-13 placement notifications 112-114 (EveJS bountyProxy), CH-18 kill and payout notifications, fleet sharing, pod kills.
5. CH-14 OnNPCStandingChange client notification (eve-logic standingsvc).
6. Live kill helper in tools/headless-client.

## Notes for the record

- The live tests are run by tools/dev/run-eve-live.sh (EVE_RUN for one file) under db-lock.sh with EVE_PG_URL=Host=...Database=eve_p2.
- The release build for the live front and sim: private target, started in background (log in the worktree, not committed).
