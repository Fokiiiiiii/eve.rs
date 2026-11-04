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

CoreServices.cs registers ProjectDiscoveryService. CharacterComposition.cs uses ProjectDiscoveryTutorialStore for persisted per-character tutorial progression. Static project identifiers, XP threshold, tutorial XP, training/task flows, reward policy, remote task profiles, and the offline scientific replay now have registered Host implementations; their data sources and limitations differ by project mode.

EveJS 261028.1 implements static methods and local authored geometry tasks in `server/src/services/character/projectDiscoveryService.js` and `projectDiscoveryRuntime.js`. Its XP threshold is `(rank - 1) * rank * 50` (`projectDiscoveryRuntime.js:50-56`), project ID is 1 and tutorial XP is 100 (`projectDiscoveryRuntime.js:11-14`, `projectDiscoveryService.js:34,41`). The Host does not treat public research data as live MMOS game tasks or invent task gold.

## Current registered Host evidence (2026-11-02)

All 69 ProjectDiscovery client-callsite rows, covering 20 distinct methods, now have positive registered-Host test evidence in the coverage ledger. The latest five-task scope total is 306/306 callsite rows and 143/143 distinct RPC methods. This measures observed caller and test coverage; it does not establish complete gameplay fidelity, authentic reward catalogs, or live MMOS behavior.

- Exoplanets/Subcellular remote profiles: `Legacy_remote_profile_roundtrips_full_task_and_local_rewards_on_wire` passed four normal/training profile cases with registered encoded Host dispatch, PostgreSQL persistence, and a mocked upstream. It checks full task round trips, actor/account ownership, keyword fields, local reward receipts, and restart replay. The real upstream endpoint/auth path was not exercised and requires explicit operator configuration.
- Exoplanet tutorial finish: `Exoplanet_local_finish_grants_atomic_receipt_and_replays_same_call` passed through the encoded Host route and PostgreSQL. The reward is issued only under configured operator policy using an SDE crate, with atomic XP/ISK/item receipt and retry/restart checks. This does not supply an authentic Subcellular catalog.
- Subcellular progression/reward: `Participation_policy_grants_xp_once_and_skip_is_not_eligible` passed with a controlled synthetic catalog and PostgreSQL; the Subcellular reward claim now asserts its encoded Host boolean response. This proves the implemented policy/test fixture, not a complete production training catalog.
- COVID task and answer: `ScientificReplayWireTests.RegisteredEncodedRpcServesRealImageAndPersistsScientificAnswer` passed through registered encoded Host dispatch with PostgreSQL and a bundled public research image/corpus. `ProjectDiscoveryHostTests.Bundled_replay_is_registered_and_restores_score_after_host_restart` also passed. This is an offline research replay, not a live MMOS task, CCP gold answer, or game score.

No live MMOS credentials/upstream, authentic Subcellular catalog, or graphical client session was used. Configured rewards and synthetic catalogs remain explicit test/operator inputs; 100% callsite coverage should not be read as proof that every scientific or gameplay requirement is complete.