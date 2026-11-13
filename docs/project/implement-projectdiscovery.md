# Project Discovery implementation record

## Scope and current wiring

The V24.01 callsite inventory in [`service-audit/EVE_RS_COVERAGE.csv`](service-audit/EVE_RS_COVERAGE.csv) contains the ProjectDiscovery rows `RPC-00679` through `RPC-00747`. It records each observed alias/accessor separately with its caller path, line, positional arguments, and keyword arguments. The service is registered by `dotnet/src/Eve.Services.Core/CoreServices.cs:104-120`; `dotnet/src/Eve.Host/Hosting/HostComposition.cs:343-402` selects the disabled, scientific-replay, or configured remote provider and attaches the Host callbacks.

The default `scientific-replay` path is an offline COVID client flow. `remote` is a configured MMOS adapter for COVID, Exoplanet, or Subcellular. Subcellular tutorial tasks can additionally come from an operator-supplied `EVE_PROJECT_DISCOVERY_TRAINING_CATALOG`. An absent provider/catalog returns a user error; it does not fabricate a successful task. Core state, rank, tutorial, reward-claim, task-pin, bonus-sample, and statistics calls remain registered independently of the selected task provider.

## V24.01 call contract

These call shapes come from the complete `projectdiscovery/client` client scan, not only from the initially visible COVID screen. The exact per-call evidence is retained in the coverage CSV rows.

| RPC | Observed arguments | Client-consumed return |
| --- | --- | --- |
| `is_enabled` | none | Boolean. The reference EveJS service returns `true`. |
| `fix_task_id` | one task identifier | `None`; V24 QA task-fix control (`client/projects/covid/ui/devpopup.py:45`). Host requires the QA role and a configured pin store. |
| `get_new_task` | no positional arguments; COVID tutorial additionally sends `original_code=<string>, is_tutorial_task=True` | Task dictionary. COVID reads `assets.url` (`client/projects/covid/gameplaymodes/covidproject.py:223,236`) and optional `taskID` (`:387-390`). Exoplanet and Subcellular paths read their project-specific task keys. |
| `get_maximum_bonus_samples`, `get_remaining_bonus_samples` | none | Integer sample counts. |
| `new_training_task` | one integer task number | Subcellular training task with task id, assets, and solution. It requires the configured catalog and rejects missing or invalid entries. |
| `post_classification` | COVID: `(result, duration)`; Exoplanet: `(task, classification, duration, remark=False)`; Subcellular: `(task, classification, duration, remark=<bool>)` | COVID result UI reads `player.score` (`client/projects/covid/gameplaymodes/covidproject.py:374-376`) and `isSolved`, `score`, and `classificationCount` (`client/projects/covid/ui/results.py:291-314`); the solution renderer reads `task.solution.clusters[].polygons[]` (`covidproject.py:444-452`). Exoplanet reads task training/consensus fields and votes (`client/projects/exoplanets/gameplaymodes/exoplanetsproject.py:541-562`). Subcellular reads result score and project task. |
| `get_player_analysis_kredits` | none | Nonnegative numeric balance for the LP store (`eve/client/script/ui/station/loyaltyPointStore/lpStoreSvc.py:346-349`). |
| `get_player_state` | none | KeyVal fields `rank`, `experience`, `analysisKredits`, `finishedTutorial`, `tutorialLevel`; read by the window and header (`client/ui/projectdiscoverywindow.py:60-75`, `client/ui/projectdiscoveryheader.py:38-50`). |
| `get_player_statistics` | `get_history` as one positional boolean or keyword boolean | Statistics dictionary: `project.score` for the COVID/Exoplanet headers, `projects[0].score` for the Subcellular header, otherwise the V24 error/message shape. Callers: `projectdiscoverywindow.py:60-63`, `projectdiscoveryheader.py:129-136`, `projects/subcellular/subcellularwindow.py:402-409,426-442`. |
| `get_tutorial_completion_status`, `initialize_tutorial_status` | none | Boolean completion state. |
| `get_project_id` | none | Integer project id for the legacy Subcellular tutorial. |
| `get_total_needed_xp` | one rank number | Numeric cumulative XP threshold. The retained curve is `(rank - 1) * rank * 50`, matching the EveJS reference. |
| `get_tutorial_level`, `get_tutorial_xp_reward` | none | Numeric level and XP reward. |
| `get_is_player_entitled_to_tutorial_reward` | none | Boolean entitlement derived from persisted progress/proofs. |
| `give_tutorial_rewards` | none | Boolean claim result; successful claims persist a receipt and queue wallet/item notices, then `OnPlayerStateUpdated` followed by `OnAnalysisKreditsChange`. |
| `increase_tutorial_level`, `reset_tutorial`, `skip_tutorial` | none | Updated player-state KeyVal. `skip_tutorial` queues `OnPlayerStateUpdated` followed by `OnAnalysisKreditsChange`, as the V24 header and base service consume these events (`client/ui/projectdiscoveryheader.py:25-27`, `client/baseservice.py:14-16,34-37`). |

`get_current_task_index` and `get_data_from_url` are not remote ProjectDiscovery RPCs in V24. `exoplanetstutorial.py:227-233,256-258,295-296` implements them on the local `TutorialService`; the latter loads the MMO gateway URL. EveJS has same-named handlers, but V24 has no remote callsite for them, so they are intentionally absent from the RPC registry and coverage rows.

## Implemented task, score, and persistence paths

### Offline COVID science replay

`ScientificReplayProvider` assigns one fixed research sample per character/account and dataset revision. PostgreSQL stores the assignment and the submission receipt. An advisory transaction lock serializes assignment/submission for a character; call id plus payload fingerprint makes retries idempotent and rejects call-id reuse with different input. Completed/skipped assignments cannot be silently reissued. `ScientificReplayWireTests.RegisteredEncodedRpcServesRealImageAndPersistsScientificAnswer` exercises the registered service, actual Macho encoding, image HTTP delivery, result payload, restart, statistics, account isolation, and exhausted-task error on PostgreSQL.

The bundled flow uses the existing FRDR DOI `10.20383/103.01043` dataset, with attribution and CC BY 4.0 terms in `dotnet/tests/Eve.Host.Tests/fixtures/project-discovery/SOURCE.md`. The task score is the fixed-cohort peer F1 calculation implemented in `ScientificReplayScorer`; the consensus solution is selected from the submitted cohort by leave-one-out peer agreement. The response identifies itself as a public research replay and explicitly says it is not live MMOS. It does not claim a private-game gold answer or live MMOS score. No new research data file is added by this change.

The response task includes `id`, `taskID`, `taskId`, `projectID`, `isTrainingSet`, `isTutorial`, `localMode`, `assets.url`, `assets.dimensions`, `url`, `votes.highScore`, `votes.avgScore`, and task `classificationCount`; the submitted result includes `isSolved`, `score`, result `classificationCount`, `player.score`, and the scored task with solution polygons. Strings are asserted as Python byte strings after actual Macho wire round-trip.

### COVID tutorial samples

`CovidTutorialCorpus.cs` is original, explicitly labelled training geometry and five UUIDs are the ones requested by the V24 tutorial caller (`projectdiscovery/client/projects/covid/gameplaymodes/tutorial/tutorialproject.py:59-63`). It is not an adaptation of EveJS's AGPL corpus or a claim to be a CCP/MMOS task. Assignments, active sample, answer fingerprints, scores, and the completion-reward receipt persist in PostgreSQL. The tutorial result returns the same score/player/task/solution fields the COVID client consumes. The completion reward is granted only after all five distinct tutorial codes have a persisted score greater than 0.5, is atomic with its receipt, and is credited once; it follows the reference tutorial reward values. Replay issue times, tutorial receipt times, daily reward dates, Subcellular progress timestamps, and MMOS request-signature timestamps use the Host's injected millisecond clock. This keeps elapsed-time and UTC-day reward behavior deterministic under the same test clock as the rest of the Host.

### Remote Exoplanet and Subcellular profiles

Remote mode preserves the V24 task and result dictionaries from the configured MMOS endpoint, checks character/account ownership and idempotent call receipts, and applies only the explicitly configured local bonus policy. The integration tests use a controlled mock MMOS endpoint. They do not establish live endpoint credentials, real MMOS availability, or real Exoplanet/Subcellular task content. `new_training_task` is supported only when an operator provides a validated task catalog; no catalog or synthetic fallback is shipped.

## EveJS 261108.1 comparison

The new release's `server/src/services/character/projectDiscoveryService.js` and `projectDiscoveryRuntime.js` were traced through task issuance, classification, persisted receipts, player statistics, tutorial transitions, and notifications. It confirms the V24-facing COVID result shape (`isSolved`, `score`, `classificationCount`, `player.score`, `playerState`, `task.solution`) and the five-proof, once-only 100 XP / 10 AK tutorial completion reward. For research tasks, `projectDiscoveryRewards.js:19-23,88-109` caps accuracy at 0.99, requires 12 seconds, awards XP from the recorded average accuracy, and doubles the first ten UTC-day samples; `projectDiscoveryRuntime.js:178-189` awards rounded Analysis Kredits on a passing classification. The offline replay follows those XP/AK rules and result fields, but reports zero ISK and no crates because this public research replay has no authoritative in-game wallet/item award. EveJS's `projectDiscoveryRuntime.js` and `projectDiscoveryCorpus.js` use an EveJS-authored task corpus; `projectDiscoveryRewards.js` labels its reward policy as local. That task corpus was not copied.

EveJS 261108.1 declares no package test script and has no Project Discovery test harness in its tracked server source. The comparison here is source-level; an EveJS service flow was not executed in its writable character store.

One intentional additive wire difference follows direct V24 evidence: EveJS's `get_player_statistics` provides `project.score`, which the COVID/Exoplanet headers consume, while the V24 Subcellular header reads `projects[0].score` (`projectdiscovery/client/projects/subcellular/subcellularwindow.py:438`). Eve.rs returns both fields with the same score; the regression test dispatches the call through the registered Host and checks both paths.

## Verification and limits

Verified locally on 2026-11-13 in Release configuration; the database-backed tests used `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p5`:

- `Eve.Host.Tests` filter `ProjectDiscovery|ScientificReplay|RemoteDiscovery|MmosApiClient`: 31 passed, 0 failed, 0 skipped. This exercises registered Macho calls, local research assignment/submission, HTTP image delivery, remote protocol profiles with a mock upstream, tutorial state/reward persistence and notification order.
- `Eve.Services.Core.Tests` filter `ProjectDiscovery|CoreCatalogTests`: 14 passed, 0 failed, 0 skipped.
- `Eve.Characters.Tests` filter `ProjectDiscovery`: 2 passed, 0 failed, 0 skipped, including PostgreSQL-backed tutorial progress persistence on `eve_p5`.
- Focused `Eve.Host.Tests` test `Covid_tutorial_task_calls_roundtrip_through_host_wire_without_consuming_research_assignment`: 1 passed, 0 failed, 0 skipped; it is included in the Host filter above.
- A first Host-slice run found the new clock assertion was using a real-clock Host after a test restart. The test setup was corrected to keep the fake clock on both Host instances; the focused regression and full Host slice then passed.
- A test-first encoded Exoplanet reward claim failed because the success path omitted `OnAnalysisKreditsChange`; after adding it after `OnPlayerStateUpdated`, the focused PostgreSQL-backed Host wire test passed.

After merging `origin/main`, Release warning-as-error builds succeeded with 0 warnings and 0 errors for `Eve.Host`, `Eve.Services.Core`, `Eve.Characters`, `Eve.Host.Tests`, `Eve.Services.Core.Tests`, and `Eve.Characters.Tests`. Rust is not changed by this feature.

Not checked in this implementation: graphical-client rendering/interaction, live MMOS calls or rewards, authentic Subcellular tutorial catalog delivery, and long-running deployment/restart against production configuration. The offline COVID research replay is one fixed sample per character and is not the live retail task queue or consensus service. The V24 COVID window returns to its drawing step and requests another task after the results/rewards steps (`/home/user/eve-logic/projectdiscovery/client/projects/covid/gameplaymodes/covidproject.py:328-360`); after this replay's single licensed analysis is completed or skipped, the next request returns `PROJECT_DISCOVERY_TASK_EXHAUSTED` (`ScientificReplayProvider.cs:199-214,346-349`). This exhaustion is covered by `ScientificReplayWireTests.RegisteredEncodedRpcServesRealImageAndPersistsScientificAnswer`. The configured remote provider can request subsequent tasks from MMOS, but that path was exercised only with a mock upstream. EveJS 261108.1 increments a per-character task sequence and issues further local corpus tasks (`/home/user/evejs-261108.1/server/src/services/character/projectDiscoveryRuntime.js:117-131`); its authored corpus is not copied into eve.rs. A repeatable production task queue therefore remains dependent on real MMOS configuration and was not verified here.
