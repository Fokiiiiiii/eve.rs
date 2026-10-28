# Deep check: ProjectDiscovery

## Client surface and wire contract

The complete V24.01 call surface is in the 69 ProjectDiscovery rows of EVE_RS_COVERAGE.csv. Client call sites and the consumed result are:

| Method | Request shape | Client caller and consumed result |
|---|---|---|
| is_enabled | no args | projectdiscovery/client/baseservice.py:38-43 caches a bool. |
| get_player_statistics | no args, get_history bool keyword, or bool positional | projectdiscovery/client/ui/projectdiscoverywindow.py:61-65; result is checked for project statistics, and nested project.score is consumed by projectdiscovery/client/projects/exoplanets/windows/rewardview.py:123-130 and projectdiscovery/client/projects/subcellular/rewardsview.py:202-204. Missing project data falls back to a neutral score. |
| get_player_state | no args | window.py:65; covid/rewards.py:106-108 consumes rank and experience; subcellular/tutorial.py:327-369 consumes tutorialLevel and finishedTutorial and later refreshed player state; exoplanetstutorial.py:278-279 reads refreshed state. Wire fields are rank, experience, analysisKredits, finishedTutorial, tutorialLevel. |
| get_player_analysis_kredits | no args | eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:345-350 caches the integer balance. |
| get_project_id | no args | subcellular/tutorial.py:37-39 chooses the local tutorial task list. |
| get_total_needed_xp | one positional rank | covid/rewards.py:366-368 and subcellular/rewardsview.py:37-39 consume a numeric threshold. EveJS uses ((rank - 1) * rank * 100) / 2 after rank normalization. |
| get_tutorial_completion_status | no args | projectdiscoverywindow.py:73-74 and subcellular/tutorial.py:125-136 consume a bool. |
| get_tutorial_level | no args | exoplanetstutorial.py:234-239 and subcellular/tutorial.py:126-127 consume a numeric level. |
| get_tutorial_xp_reward | no args | subcellular/tutorial.py:350-355 consumes the numeric reward. |
| get_is_player_entitled_to_tutorial_reward | no args | exoplanetstutorial.py:275-279 gates its reward call. |
| give_tutorial_rewards | no args | exoplanetstutorial.py:278-279 and subcellular/tutorial.py:350-354 consume a bool before refreshing state. |
| increase_tutorial_level | no args | exoplanetstutorial.py:311-317 and subcellular/tutorial.py:368-369 advance the tutorial; the first flow ignores the reply. |
| reset_tutorial | no args | exoplanetstutorial.py:320-321 and subcellular/tutorial.py:132-136; subcellular uses the returned player state. |
| skip_tutorial | no args | exoplanetstutorial.py:323-324 and subcellular/tutorial.py:124-126; reply ignored. |
| get_maximum_bonus_samples / get_remaining_bonus_samples | no args | exoplanetstutorial.py:323-325 consumes numeric counts. |
| get_new_task | no args | covidproject.py:225-235 and exoplanetsproject.py:424-435 consume a task object; the latter reads id and later url. It handles only its named rate-limit UserError and the client-local MissingKeyError / NoConnectionToAPIError paths. |
| new_training_task | one positional task number | subcellular/tutorial.py:169-180 consumes task.taskId; task content and answer come from the client’s local tutorial list. |
| post_classification | Covid: positional result, duration. Exoplanets: positional task, classification, duration, plus keyword remark=False. Subcellular: positional task, classification, duration, plus keyword remark derived from the client result. | covidproject.py:373-381, exoplanetsproject.py:527-540, and subcellularatlas.py:200-204 consume classification results including player.score and reward/player-state fields. |
| fix_task_id | one positional integer task id | covid/ui/devpopup.py:44-45 calls it from a developer popup and ignores the None reply. It is not a normal player flow. |

The client flow also uses projectdiscovery/client/projects/covid/rewards.py:355-368 for rank progression, exoplanetstutorial.py:225-325 for tutorial lifecycle, and subcellular/tutorial.py:30-45, 120-190, 320-369 for local training, project ID, and progression. The project client’s connection dialogs catch MissingKeyError / NoConnectionToAPIError at subcellular/tutorial.py:180-185 and exoplanetsproject.py:429-435; these are client-local exceptions, not remote UserErrors.

## Host and EveJS comparison

CoreServices.cs:97 registers ProjectDiscoveryService. CharacterComposition.cs:181-184 reads only ProjectDiscoveryTutorialStore; GameRpcServices.cs:33 and CharacterComposition.cs:268 carry that reader to the service. The store persists finishedTutorial and tutorialLevel, but there is no write callback in ProjectDiscoveryRpcCallbacks, so the service cannot persist reset, skip, or level advancement. The current service supports is_enabled, fix_task_id, analysis credits, player state/statistics, tutorial completion, and initialization. The remaining CSV methods have no registered implementation. Its validation errors are PROJECT_DISCOVERY_ARGUMENTS_INVALID, PROJECT_DISCOVERY_TASK_ID_INVALID, and PROJECT_DISCOVERY_TASK_FIX_UNAVAILABLE.

EveJS v0.12.9 implements the static methods, process-local tutorial transitions, a synthetic Covid task, a synthetic training task, and a synthetic successful classification in projectDiscoveryService.js:184-296. It keeps player state only in a process-local Map at lines 62-73. Its static tutorial rewards are false/zero and its XP formula is deterministic. The synthetic task and classification payloads are not valid substitutes for the unavailable live MMOS task feed and would fabricate success, so they are not treated as safe implementations here.

## Gaps and checks

The service returns is_enabled=true while external task data is unavailable. get_new_task and post_classification are gaps; returning EveJS synthetic payloads would violate the no-fabricated-success rule. Tutorial writes are also not connected to the existing persistent store. These need explicit Host wire regressions and a real persistence owner before being marked complete.

Existing wire evidence: ProjectDiscoveryHostWireTests.cs:12-53 tests registered analysis-credit reads and numeric normalization; ProjectDiscoveryClientStatsRpcHostWireTests.cs:17-48 checks the stats kwargs and reply shape. ProjectDiscoveryHostTests.cs:77-80 covers stored tutorial reads but uses PostgreSQL. No V24 graphical client session was run for this audit. No ProjectDiscovery writer or task-feed test was added in this documentation group.
