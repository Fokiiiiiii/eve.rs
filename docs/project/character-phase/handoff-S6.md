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

## Final state (pushed to origin main, last commit 9b7612b5)

- CH-3 GetSkillBundleInfo: done (empty name and icon pair; no source; DEVIATIONS S6).
- CH-15: structure install (Medical or Jump Clone + docking; fee to the owner corporation) and ship offer/accept/cancel are implemented and tested (Host, unit, live refusal). Open: a live offer accepted between two pilots (needs a fitted Clone Vat Bay in space).
- CH-13: placement notifications 112-114 done and Host-tested; no live placement notification test yet.
- CH-14: OnStandingSet to the killer after a faction kill standing loss is sent; not yet tested (no Host or live kill).
- CH-18: NOT done: kill and payout notifications, fleet sharing, pod kills.
- Live kill helper in tools/headless-client: NOT built. Existing walks (test_npc.py, test_reload.py) fire real weapons and can be the base.

Tests run on eve_p2 (port 26101 for live): CharacterHostTests 11 passed; PlayerBountyHostTests 4 passed; Services.Core 745 passed; Characters StoreTests 28 passed; live test_clone_install_live.py 2 passed; live test_skill_bundle_info_live.py 1 passed.

## Second round (pushed 09e19715)

- CH-18 fleet sharing: DONE and pushed. A kill's claim is split equally across the killer's fleet (all members, the hunter first, who keeps the rounding); each sharer has a pending payout (`kill:{ship}` for the hunter, `kill:{ship}:{character}` for the others) credited by its own job. Tests: `PlayerBountyKillsTests` (2 passed, eve_p2), `PlayerBountyHostTests` and `CharacterHostTests` (17 passed). Division among fleet members and the requirement of proximity are NOT stated by the wiki (DEVIATIONS S6).
- CH-18 kill and payout notifications: NOT added. EveJS sends no notification for a player claim or its payout (`playerBountyState.claimBountyPayout`, `settlePendingPayout`): the payout is a wallet journal entry only. Not invented here; the journal entry is the record.
- CH-18 pod kills: NOT done. The wiki says the pod is a separate event with its implants' value; eve.rs raises `ShipLost` only for ships (capsule losses return early in the Host subscriber), and the capsule destruction path (`SpaceSessions.Death` / `CharacterShips.DestroyCapsuleAsync`) raises no event with a killer. Needs a new space event.
- Live kill helper: `tools/headless-client/tests/npc_kill.py` (WIP, UNCOMMITTED, not pushed): flies at a rat, locks it, orbits, fires the gun until `OnBountyAddedToPayout`. It does not work yet (see below).
- CH-14 live walk: WIP `tests/test_faction_standing_live.py` (UNCOMMITTED): not green. Blocker found: a spawned faction NPC (Blood Visionary 8 km spawn, Guristas Invader 5 km) is 46-54 km from the pilot; the pilot's `CmdFollowBall` is accepted (returns None) but the distance grows (46 -> 54 km) rather than shrinks, also with `CmdSetSpeedFraction(1.0)` first. Not diagnosed further.
- CH-18 live walk (pilot kills pilot, claim, payout): NOT done. Reason: the test system 30100032 is high-security (security 1.0); pilot-on-pilot weapons are refused there by the safety, and the same pursuit problem above applies.
- CH-15 live two-pilot ship offer: NOT done. Reason: a Clone Vat Bay must be fitted, both pilots must be in one system within the hull's operational range (the same movement problem), and the Host rig has no space simulation (`EVE_SIM` off), so positions cannot be read in a Host test. The offer, accept and cancel flow is covered by `JumpCloneShipInstallTests` (Services.Core, two client sessions with sinks, real notification order).

