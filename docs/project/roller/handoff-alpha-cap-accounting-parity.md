# Alpha queue cap accounting parity review

Date: 2026-11-07

## Decision

Keep the save-time Alpha cap refusal. It enforces the 5,000,000 SP hard limit using the live trained total plus the remaining queue. The V24.01 queue panel can undercount when its cached total was captured before the active head made progress; that local forecast does not override the server-side cap.

## Evidence

- EVE University states that Alpha free training is limited to 5,000,000 SP and that unallocated SP counts toward the total: [Alpha clones](https://wiki.eveuniversity.org/Alpha_clones).
- V24.01 `GetSkillsIncludingLapsed` returns the cached `GetAllSkills` response (`eve/client/script/ui/services/skillsvc.py:139-143,252-256`). `GetTotalSkillPointsForCharacter` sums returned `trainedSkillPoints` and free SP (`skillsvc.py:734-737`). The queue panel adds each level's remaining SP after subtracting estimated progress in that level (`skillQueuePanelNew.py:409-440`; estimator `skillQueueSvc.py:692-700`). It uses `GetTotalSkillPointsForCharacterCached` (`skillQueuePanelNew.py:412`), which caches the value (`skillsvc.py:738-741`); queue refresh does not clear it (`skillQueueSvc.py:151-160`). A total captured before head progress can therefore undercount the actual current total by the elapsed head SP. The panel handles the save-time `SkillInQueueOverAlphaSpTrainingSize` refusal (`skillQueuePanelNew.py:318-345`).
- Whole-client use of this skill data was searched: `GetSkillsIncludingLapsed` also feeds skill-service helpers (`skillsvc.py:182,298,327,342,628`), queue calculations (`skillQueueSvc.py:470,515,552,589`), the queue panel (`skillQueuePanelNew.py:386,656`), skill extraction (`skillExtractorController.py:484,575`), and skill requirement checks (`menucheckers/baseCheckers.py:70`). Other `GetTotalSkillPointsForCharacter` consumers are the agency and skills catalogue (`agencyUtil.py:69`; `skillsCataloguePanel.py:140`) and queue projection (`skillQueueSvc.py:702`). Estimated SP also drives queue-entry progress, the apply-SP window, skill progress and required-skill summaries (`skillQueueSkillEntry.py:305,330`; `applySkillPointsWindow.py:349`; `skills/client/skillController.py:193`; `characterskills/util.py:80,93`).
- In eve.rs, `GetAllSkills` delegates to `GetSkills` (`dotnet/src/Eve.Services.Skills/SkillHandlerService.cs:486-500`), which reads `ViewOf`. `CharacterTraining.BuildView` computes and returns current live head points (`dotnet/src/Eve.Skills/CharacterTraining.cs:108-119`); `SkillWire.Entry` serializes those points (`dotnet/src/Eve.Services.Skills/SkillWire.cs:31-35`). A fresh skill read therefore includes live head progress, though the client's cached total may remain stale.
- EveJS 261028.1's `GetAllSkills` path overlays the active skill with `trainingStartSP` (`src/server/src/services/skills/skillMgrService.js:264-284,431-434`). Its `saveQueue` path passes `currentSnapshot.projectedSkills` into `getAlphaTrainingCapBreakdown` (`skillQueueRuntime.js:1305-1424`; calculation `skillCloneRestrictions.js:126-207`), so server-side validation includes live progress plus the remaining queue. EveJS and eve.rs server-side outcomes agree; the stale client forecast is not full UI parity.

## Regression rationale

`CharacterTrainingTests.Saving_an_alpha_queue_with_an_unchanged_running_head_counts_elapsed_points_once` checks that a queue whose projected total exceeds 5,000,000 is refused even when the excess is smaller than the elapsed head SP. It also accepts totals just below and at the cap, and verifies that `activate=false` pauses and rebases the active line. The test asserts server enforcement, not equality with the client's cached forecast.

## Verification

- `dotnet build dotnet/tests/Eve.Skills.Tests -p:UseSharedCompilation=false`: passed, zero warnings and errors.
- `CharacterTrainingTests`: passed 24/24 on PostgreSQL `eve_p3` through `tools/dev/db-lock.sh`.
- Focused `Saving_an_alpha_queue_with_an_unchanged_running_head_counts_elapsed_points_once`: passed 1/1 on `eve_p3` through `tools/dev/db-lock.sh`.
- `CharacterTrainingLearningTests` and `TrainingQueueTests`: passed 27/27 on `eve_p3` through `tools/dev/db-lock.sh`.
- `TrainingServiceTests`: passed 15/15 on `eve_p3` through `tools/dev/db-lock.sh`.
- No tests were skipped. The graphical V24.01 client was not exercised; the EveJS flow was source-traced.

Check GitHub Actions for the commit that carries this note before continuing with further changes.
