# Salvage and free skill point review handoff

Date: 2026-11-06

## Findings and decisions

1. **Salvage pool fallback: no code fix.** The current selector intentionally returns no product when no configured salvage material matches any SDE product. EveJS 261028.1 builds faction and common pools and skips a reward when weighted selection has no candidate (`server/src/space/modules/salvagerRuntime.js:734-789`; empty pools return null at `:705-719`). EVE University documents successful salvage attempts that yield no salvage, and CCP describes salvage as predetermined per wreck. Restoring the previous fallback would award an unrelated material. Added a regression for a nonempty product list with no configured match in `SalvagePoolTests`.

   Sources: [EVE University: Salvage](https://wiki.eveuniversity.org/Salvage); [CCP: Salvaging](https://support.eveonline.com/hc/en-us/articles/213014209-Salvaging).

2. **Fixed salvage item assertion: no code fix.** The integration test injects `SalvageRewardRoll = () => rewardRoll` before wreck access; its 0.99 input makes the expected item deterministic. `SalvagePoolTests.Build_pool_matches_faction_and_size_candidates` already asserts the pool contents and weights across faction/size combinations. The exact output assertion remains useful as an end-to-end selection check.

3. **Free SP reads and Alpha grade data: fixed.** Read previews now return an empty `PyDict` if character/account/required Alpha grade state cannot be resolved. Omega paths do not require Alpha grade data. Mutations still reject invalid state, and only Alpha training invokes the Alpha grade guard.

4. **Empty selected-skill preview: fixed.** The selected-skill getter returns an empty map before loading clone state for an empty selection. Empty apply requests also return without requiring state.

## V24.01 client evidence

A whole-tree search found the preview RPCs used by `applySkillPointsWindow.py:120-122` and `skillQueueSvc.py:602,609`. The window disables Apply for an empty result and uses the map for progress and totals (`applySkillPointsWindow.py:130-143`); the service only calls the mutation when the map is truthy (`skillQueueSvc.py:600-612`). The queue panel handles the Alpha training limit as a `UserError` at `skillQueuePanelNew.py:325-333`; unrelated runtime failures do not enter that user-facing branch. This supports empty preview results for unavailable read-only state while preserving mutation validation.

## Local checks completed

- Builds: `Eve.Skills`, `Eve.Services.Skills`, `Eve.Services.Skills.Tests`, `Eve.Services.Space.Tests`, `Eve.Skills.Tests`, and `Eve.Host.Tests` all built successfully with shared compilation disabled.
- PostgreSQL database `eve_p3`, through `tools/dev/db-lock.sh`:
  - `SalvagePoolTests` plus the focused wreck salvage cycle: 37 passed, 0 failed, 0 skipped.
  - `SkillServiceTests`, `TrainingServiceTests`, and `SkillQueueContractTests`: 16 passed, 0 failed, 0 skipped.
  - `CharacterTrainingTests` and `CharacterTrainingLearningTests`: 27 passed, 0 failed, 0 skipped. An initial invocation without `EVE_PG_URL` skipped these 27 cases; the same filter was immediately rerun on `eve_p3` under the database lock and all passed.
  - Focused Host RPC shape and composed route checks: 1 passed each, 0 failed, 0 skipped.
- No graphical V24.01 client session was run.
- No intentional behavior deviation from EveJS was found; no compatibility deviation record was needed.

## CI follow-up

The first push (`35dff52c`) passed the selected agent-test run (`42267897348`), but its filter did not include `SkillHandlerTests`. The push-triggered CI run (`42267845646`) caught `SkillHandlerTests.Omega_free_skill_point_paths_do_not_require_alpha_clone_grade_data`: the test expected 550 skill points but got 450. This was a test expectation error: the test starts at 250 SP and applies two grants of 100 SP, so 450 is the correct total. The failure was reproduced on `eve_p3`, the expected total was corrected, and the focused test passed (1/1); `SkillHandlerTests`, `TrainingServiceTests`, and `SkillQueueContractTests` then passed (72/72, no skips).
