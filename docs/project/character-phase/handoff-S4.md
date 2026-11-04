# Handoff S4 (CH-11, CH-17, CH-16; database lane eve_p3, live port 26102)

Worktree `/home/user/wt-ch-s4`, branch `ch-s4`. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

| Package | State | Verified (eve_p3, nothing skipped) |
|---|---|---|
| CH-11 | DONE | `CharacterTrainingLearningTests` 3, `PilotLearningBonusTests` 2 (Ships 233 in all), Skills.Tests 95 in all, Services.Skills.Tests 66, Services.Dogma.Tests 153, Host.Tests 388 (on origin/main at d8b328e1 + this commit); live `test_implant_live.py` 1 passed (booster rate 1.5, plug-in, refusal, unplug, notification order) |
| CH-17 | DONE | `ExpertSystemTests` 6, `ExpertSystemStoreTests` 1 (Skills.Tests 95 in all); live `test_expert_system_live.py` 1 passed (consume, list, granted skill, remove, lapse with `OnExpertSystemExpired` after the expiry job) |
| CH-16 | NOT DONE (analysis only) | depends on CH-15 (clone installation, S1: station install and fee are on origin/main, d8b328e1; structure and ship installs are open). Blocker: the jump moves a logged-in character to another station, and eve.rs has no re-apply helper (EveJS `applyCharacterToSession` in `jumpCloneRuntime.js`); the session fields and the character record must change together. See Next. |

Not verified: a host restart between the steps of the live tests (the state is in PostgreSQL: items, `active_skill_boosters`, `character_expert_systems`; no restart test). `Eve.Services.Core.Tests` does not compile on origin/main (CH-6 test double, see TODO S4).

## Next

1. CH-16: (a) a session relocation helper for a jump (EveJS `applyCharacterToSession` with `emitNotifications: false`, then `flushCharacterSessionNotificationPlan`; eve.rs has `CharacterLogoff` and the select path, not a relocation). (b) `CloneJump` on `jumpCloneSvc` (EveJS `buildCloneJumpState` at `jumpCloneRuntime.js` 1821: target clone, cooldown `isCloneJumpCooldownActive`, the replacement clone at the origin, implants `mergeImplantsBySlot`, `timeLastCloneJump`). (c) fee: `REF_JUMP_CLONE_ACTIVATION_FEE` = 128 (`jumpCloneRules.js`), station cost `JUMP_CLONE_INSTALL_COST` = 900000 (`getCloneServiceCostForLocation`); debit with no counterparty as S1's installation does. (d) cooldown = 24 h less the Infomorph Synchronizing level (`getCharacterCloneJumpCooldownHours`). (e) death: the respawn is at the home station, which is the clone station by the CH-12 decision; a jump does not change it in the EveJS code read so far (not verified further). Keep cerebral accelerators through a jump and a death (wiki, DEVIATIONS S4).
2. Live steps need a jump clone: seed one through the CH-12 store until CH-15's structure and ship installs land.

## Risks

- The heavy build lock (`/tmp/eve-heavy.lock`) is held by MSBuild worker nodes left behind by earlier builds (node reuse keeps them, and they inherit the lock fd). Kill orphaned `MSBuild.dll` workers (parent pid 1) before a build, and set `MSBUILDDISABLENODEREUSE=1`.
- The live run needs `EVE_MARKET_SEED=skip` (no ESI) and `EVE_SDE_DIR` pointing at the main checkout's `.cache/sde` copy.
