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

CoreServices.cs registers ProjectDiscoveryService. CharacterComposition.cs reads and writes the existing ProjectDiscoveryTutorialStore through GameRpcCallbacks. The Host now persists reset, skip, and level advancement. Static project ID, XP threshold, tutorial XP, level, entitlement, and reward calls are routed; entitlement and reward calls return false until an earned-proof and reward owner exists. Task feeds, classification, bonus samples, training tasks, and the developer task pin still need working backends.

EveJS 261028.1 implements the static methods and local authored geometry tasks in server/src/services/character/projectDiscoveryService.js and projectDiscoveryRuntime.js. Its XP threshold is `(rank - 1) * rank * 50` (projectDiscoveryRuntime.js:50-56), project ID is 1 and tutorial XP is 100 (projectDiscoveryRuntime.js:11-14, projectDiscoveryService.js:34,41). Tutorial state transitions persist through the character owner in EveJS (projectDiscoveryRuntime.js:132-151); its skip flow queues `OnPlayerStateUpdated` then `OnAnalysisKreditsChange` (projectDiscoveryService.js:16-29). This eve.rs increment implements the static values and persistent tutorial transitions. Local authored task delivery and scoring remain open pending their Host owner and regression tests.

## Gaps and checks

Open CSV rows include `fix_task_id` (RPC-00679-00680), maximum/remaining bonus samples (RPC-00682-00683, RPC-00708-00709), `get_new_task` (RPC-00684-00686), `new_training_task` (RPC-00739), and `post_classification` (RPC-00740-00742). `get_is_player_entitled_to_tutorial_reward` and `give_tutorial_rewards` remain HOLLOW because the stored state cannot establish earned proof or grant a reward. The tutorial writer now uses the existing character store; no task, score, or reward is fabricated.

`ProjectDiscoveryRpcHostWireTests.cs` verifies the registered route, project ID and XP values, tutorial reads and writes, and skip notification order. `ProjectDiscoveryHostTests.cs` verifies that skip, reset, and increase persist in PostgreSQL; existing analysis-credit and statistics wire tests remain. V24 callsites and consumers are cited above. No graphical client session was run.
