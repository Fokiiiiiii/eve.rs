# ProjectDiscovery coverage handoff (cx3)

## Completed in this increment

- Registered and persisted tutorial `reset_tutorial`, `skip_tutorial`, and `increase_tutorial_level` using the existing character tutorial store. `get_tutorial_level` reads the same durable state.
- Added `get_project_id` and the EveJS 261028.1 XP threshold `(rank - 1) * rank * 50`, plus the tutorial XP value. Skip sends `OnPlayerStateUpdated` then `OnAnalysisKreditsChange` after the RPC response.
- `get_is_player_entitled_to_tutorial_reward` and `give_tutorial_rewards` are registered but fail closed with `false`. They remain HOLLOW: the current character store does not record earned tutorial proof or own Project Discovery rewards.

## V24.01 client evidence

- `projectdiscovery/client/baseservice.py:38-43` caches `is_enabled` as a bool. `projectdiscovery/client/projects/subcellular/tutorial.py:37-39` uses `get_project_id()` to select the local tutorial task list; `projectdiscovery/client/const.py:78-116` builds that list from the project ID.
- Rank thresholds are read by `projectdiscovery/client/ui/projectdiscoveryheader.py:48-49,182-184`, `projectdiscovery/client/projects/covid/ui/rewards.py:366-368`, `projectdiscovery/client/projects/exoplanets/ui/tiertracker.py:258-260`, and the Exoplanets/Subcellular reward views. They use the numeric threshold for current/next rank display.
- `projectdiscovery/client/projects/exoplanets/gameplaymodes/exoplanetstutorial.py:234-245,275-280,311-324` consumes tutorial level, completion, entitlement, reward, reset, skip, and level advancement. `projectdiscovery/client/projects/subcellular/tutorial.py:125-136,350-369` consumes skip/reset, tutorial XP/reward, player state, and level advancement.
- The refreshed player state fields are consumed at `projectdiscovery/client/projects/covid/ui/rewards.py:106-108`, `projectdiscovery/client/projects/subcellular/tutorial.py:327-369`, and `projectdiscovery/client/projects/exoplanets/gameplaymodes/exoplanetstutorial.py:278-279`.
- Notifications are consumed by `projectdiscovery/client/ui/projectdiscoveryheader.py:26,154`, `projectdiscovery/client/projects/covid/ui/rewards.py:93,341`, and `projectdiscovery/client/baseservice.py:14,33`.
- The open task flow is called by `projectdiscovery/client/projects/covid/gameplaymodes/covidproject.py:225-235,373-381`, `projectdiscovery/client/projects/exoplanets/gameplaymodes/exoplanetsproject.py:424-435,527-540`, and `projectdiscovery/client/projects/subcellular/subcellularatlas.py:110-117,200-204`. The Subcellular tutorial training task is called at `projectdiscovery/client/projects/subcellular/tutorial.py:169-180`; developer task correction is `projectdiscovery/client/projects/covid/ui/devpopup.py:44-45`.

## Rows still open

- `RPC-00679-00680` `fix_task_id`: a positive input still fails closed because no content-authorized persistent task pin owner is wired.
- `RPC-00682-00683` and `RPC-00708-00709`: maximum/remaining bonus sample progression is not stored.
- `RPC-00684-00686` `get_new_task`, `RPC-00739` `new_training_task`, and `RPC-00740-00742` `post_classification`: task delivery and scoring need a real Host-owned task/progress backend. Do not substitute fake task or score payloads.
- `RPC-00681` and `RPC-00734-00735` remain HOLLOW until earned tutorial proof and a durable reward owner are available.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false --no-restore` passed.
- `dotnet test dotnet/tests/Eve.Host.Tests --filter 'FullyQualifiedName~ProjectDiscovery|FullyQualifiedName~Project_discovery'` passed 7, skipped 0; includes the PostgreSQL tutorial transition check on `eve_p3` through `tools/dev/db-lock.sh`.
- `dotnet test dotnet/tests/Eve.Services.Core.Tests --filter 'FullyQualifiedName~ProjectDiscovery'` passed 9, skipped 0.
- `dotnet test dotnet/tests/Eve.Characters.Tests --filter 'FullyQualifiedName~ProjectDiscovery'` passed 1, skipped 0.
- The focused Host wire test passed 1, skipped 0. No graphical-client session was run.
