# Handoff S1 (database lane eve_p2, live port 26101)

Order: CH-12, CH-1, CH-3, CH-6, CH-10, CH-15. Worktree /home/user/wt-ch-s1, branch ch-s1. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

- CH-12 (jump-clone store, clone state reads, rename, destroy, reset, bind): done, pushed (c31b0fa2). Tests run: dotnet Eve.Characters.Tests JumpCloneStoreTests 5 passed; Eve.Host.Tests JumpCloneHostTests 1 passed; live `test_jump_clone_live.py` 1 passed on eve_p2 port 26101 (EVE_SDE_DIR from main checkout .cache/sde).
- CH-1 (deletion): done, pushed (4cf8c4b3). Asset move/destroy, graveyard corporation, skills and saved fittings purge added (CH-1 gap work). Still not purged: mail, notifications, notes, LP wallets, bookmarks, calendar, skill plans, expert systems, missions, research, probes, portraits. NPC destroy path untested. Tests: CharacterDeletionStoreTests 2 passed; CharacterHostTests 2 passed; live test_character_delete.py 1 passed (plus CH-12 live 1 passed). Gaps: asset transfer/destruction and private-state purge not implemented (EveJS deleteCharacter); the successful delete is not exercised live (no test clock). Wiki pages for character deletion returned 404; delay from EveJS default 600 min.
- CH-10 (votes, reports, playtime): done. GATE-C applied (votes and reports answer true). Tests: UserSvc RedeemUserSvcCompatibilityTests 5 passed; live test_user_votes_live.py 1 passed. Playtime stays 0 (EveJS constant; no V24.01 caller, no stored source). Recorded in DEVIATIONS.
- CH-3 (partial): CheckAndSendNotifications done (empty list, as EveJS). GetSkillBundleInfo blocked on bundle name/icon data (no SDE or static record; EveJS lacks it). Tests: CharacterHostTests 3 passed.
- CH-6 (partial): item activation done (see TODO). Tests: CharacterHostTests 4 passed; live test_multi_training_live.py 1 passed. Gaps: slot-expiry pause, notification assertion, stack split. Note: the plan's "second activation of the same slot is refused" conflicts with the wiki (certificates add days); followed the wiki.
- CH-15: not started. Next: CH-3 (skill notifications, bundle info), then CH-6 (multi-character training), then CH-15 (install, needs CH-12, done).

## Decisions

- Clone station: kept equal to the home station (EveJS `homeStationService.js:382-383` writes both together). No separate column.
- Clone limit: the sum of Infomorph Psychology (24242), Advanced (33407), Elite (73910) levels. Wiki: "Jump clone" confirms one clone per level for each. Cooldown (24 h less Infomorph Synchronizing 33399) is CH-16.
- Structure clone-service rights in ValidateInstallJumpClone are a shortcut until CH-15.

## Risks / gaps

- DestroyInstalledClone does not yet send the jump clone deletion notification (EveJS createNotification, types 56/57).
- Live test of CH-12 covers reads and refusals only; install (CH-15) is needed for rename and destroy on a live clone.

## Plan deviations found while working

- Plan acceptance for CH-1 says a second prepare is refused; the reference (EveJS `prepareCharacterForDelete`) returns the existing time. Followed the reference.
- The EVE University wiki pages for character deletion (`Character_deletion`, `Biomass`) returned 404. Delay taken from EveJS.
