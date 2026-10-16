# Handoff S5 (database lane eve_p2, live port 26101)

Scope: CH-1 (deletion purge), CH-3 (GetSkillBundleInfo), CH-6 (multi-character training), CH-15 (clone installation).
Worktree /home/user/wt-ch-s5, branch ch-s5. Push: `git fetch origin main && git merge origin/main`, then `git push origin HEAD:main`.

## Status

- CH-6 (multi-character training): slot expiry and stack splitting done on branch ch-s5.
  - Stack split: `ActivateMultiTraining` takes one certificate from a stack (commit 375d5ab1). Host test `A_stack_of_multiple_character_training_certificates...`.
  - Slot expiry: every granted slot schedules a durable job at the slot end (`MultipleTrainingSlotExpiry`, job kind `skills.mctSlotExpiry`). The job pauses the queues beyond the remaining slots, the character with the fewest skill points first (wiki), and tells the account (commit 68ac003f). Tests: `TrainingServiceTests.When_an_extra_slot_runs_out...` (13/13 in the file pass), `CharacterHostTests.A_granted_training_slot_schedules_its_expiry...` (dispatch through the scheduler).
  - Notification assertion: the Host test registers the session (`rig.Sessions.Enter`) and asserts `OnMultipleCharactersTrainingUpdated`.
  - Live: `test_multi_training_live.py::test_a_stack_of_certificates...` (commit on ch-s5); run status in the report.
  - Gap: slots granted before this change have no expiry job (no backfill at startup).
- CH-1 (deletion purge): in progress. `CharacterPrivateStatePurge` (Eve.Host/Composition) removes the EveJS purge set: PostgreSQL agents (missions, journal, storyline counters, research projects, locate requests), scanned probes, bookmark folders and calendar events owned by the character, skill tables (training state, free SP, injector uses, ISIS budget, neural remaps); GameStore paths through `GameStoreCache` (mail mailboxes, notification boxes, LP character wallets, character notes, character settings, expert systems, mission and research runtime state, probe state). Tests: `CharacterPrivateStatePurgeTests` (game-store removal, flush and restart), `CharacterHostTests.Deleting_a_character_of_an_npc_corporation...`.
  - Gap: skill plans, skill queues and skill trading state (GameStore tables `skillPlans`, `skillQueues`, `skillTradingState`) are not purged: eve.rs keeps queue state in PostgreSQL and no eve.rs code reads the GameStore plan tables (CH-21 owns plans).
- CH-3 (GetSkillBundleInfo): NOT DONE. The V24.01 client reads (name label, icon) from `skillsvc.GetSkillBundleInfo`, and the bundle id comes from the crate type's static data. Searched: eve-logic (only the callers, no bundle table), EveJS (no name or icon for bundles; crate customInfo carries only the contents), SDE (no skill bundle type or table), EVE University wiki (no skill bundle page). No verifiable name or icon source, so nothing is invented.
- CH-15 (clone installation): NOT STARTED in this session.

## Decisions

- Slot expiry pause follows the wiki: the lowest total skill points among the running queues is paused first. EveJS does not pause at expiry (it only refuses new activations); recorded in DEVIATIONS.
- The GameStore purge goes through `GameStoreCache` (a row removed underneath the cache would be written back by its next flush).

## Tests run

- `dotnet test Eve.Host.Tests --filter CharacterHostTests`: 7 passed (before the NPC and purge tests were added).
- `dotnet test Eve.Services.Skills.Tests --filter TrainingServiceTests`: 13 passed.
