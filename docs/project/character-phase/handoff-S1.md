# Handoff S1 (database lane eve_p2, live port 26101)

Order: CH-12, CH-1, CH-3, CH-6, CH-10, CH-15. Worktree /home/user/wt-ch-s1, branch ch-s1. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

- CH-12 (jump-clone store, clone state reads, rename, destroy, reset, bind): done in code and tests. Pushed when green. Tests run: dotnet Eve.Characters.Tests JumpCloneStoreTests 5 passed; Eve.Host.Tests JumpCloneHostTests 1 passed; live `test_jump_clone_live.py` 1 passed on eve_p2 port 26101 (EVE_SDE_DIR from main checkout .cache/sde).
- CH-1, CH-3, CH-6, CH-10, CH-15: not started.

## Decisions

- Clone station: kept equal to the home station (EveJS `homeStationService.js:382-383` writes both together). No separate column.
- Clone limit: the sum of Infomorph Psychology (24242), Advanced (33407), Elite (73910) levels. Wiki: "Jump clone" confirms one clone per level for each. Cooldown (24 h less Infomorph Synchronizing 33399) is CH-16.
- Structure clone-service rights in ValidateInstallJumpClone are a shortcut until CH-15.

## Risks / gaps

- DestroyInstalledClone does not yet send the jump clone deletion notification (EveJS createNotification, types 56/57).
- Live test of CH-12 covers reads and refusals only; install (CH-15) is needed for rename and destroy on a live clone.
