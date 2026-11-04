# eve-content optimization sweep

## Change

`ContentRuntime.OnEventAsync` previously selected matching instances, then loaded each instance and its objectives separately: 1 + 2K reads for K candidates. It now selects candidates through active objectives and fetches the matching instance/objective rows in one joined query, so discovery uses at most two reads independent of K. The existing `objectives_by_topic (topic, scope_id)` partial index remains the filter path. Objective state evaluation and writes still run per instance under the existing instance lock.

The event payload is parsed once and shared with built-in Counter and Forbid evaluators. Counter matching compares JSON elements directly, avoiding repeated payload parsing, temporary arrays and per-objective placeholder documents. The public evaluator path remains available for custom objective types.

This is a complexity/allocation argument; no wall-clock benchmark was run. No client-visible wire shape or gameplay rule changed.

## Behavior evidence

The whole V24.01 client search found `GetMissionObjectiveInfo` callers in:
- `eve/client/script/ui/station/agents/agentDialogueWindow.py:221-223,430-432`
- `evemissions/client/missioncontroller.py:251-252`
- `jobboard/client/features/agent_missions/job.py:411-412,471-472`

Consumers include `evemissions/client/mission.py:164-172` (filters by `contentID`) and `agentDialogueWindow.py:282-406` (renders objective data); Journal callers are `eve/client/script/ui/shared/neocom/journal.py:289-290,372-374`. No reply or notification shape was changed. EveJS 261028.1's mission progress path is `src/server/src/services/agent/agentMissionRuntime.js:3164-3351`; the optimization is in eve.rs's shared content runtime and does not change mission semantics.

## Verification

- Content project build: passed, 0 warnings/errors.
- Content tests: 41 passed, 0 failed, 0 skipped. The first run had five SDE catalog failures because the private test database was empty; after loading pinned build 3396210 into `eve_p8`, the full project passed.
- Focused regression `ContentTests.An_objective_can_name_the_instance_so_an_anomalys_spawn_counts_only_its_own_kills`: passed.
- Related `Eve.Services.Space.Tests`: 46 passed, 0 failed, 0 skipped.
- Related `Eve.Host.Tests`: 3 passed, 0 failed, 0 skipped.
- No real graphical V24.01 client session or wall-clock benchmark was run.

## Delivery

- Commit `75879222fa5c00bbe4348a51390c5f429a1d9841` was pushed to `main`.
- Push CI run 1503 (Actions run 41838757824): success; no failed tests or jobs.
- Focused Agent test run 41842802147: success.
