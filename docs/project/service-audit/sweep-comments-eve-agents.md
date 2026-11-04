# Eve.Agents comment and dead-code sweep

## Changes

- Repaired incomplete or misleading XML summaries in `AgentMissionStore.cs`: the transition result, refusal reason, transaction and journal behavior, completed history, offer/reset/removal operations, and the two completion methods.
- Tightened `ResearchProjectStore.LockAsync` documentation to describe the per-character transaction lock used by research starts and datacore purchases. `CancelAsync` does not take that lock; its behavior was left unchanged.
- No runtime logic, SQL, wire contract, notification, or test code changed. No private member was removed: the indexed `home-user-eve.rs` CBM snapshot from 2026-10-08 has no graph records for `Eve.Agents`, so it cannot establish that a member is unused.
- Serena `replace_content` failed with “No language servers available in the manager”; the native patch fallback applied the comment edits after the source was read through Serena.

## Client and call-site evidence

- The V24.01 dialogue passes the selected action through `GetAgentMoniker().DoAction(actionID)` (`eve/client/script/ui/station/agents/agentDialogueWindow.py:426-427`). Button labels map the complete action to “Complete Mission” (`agentConst.py:19-37`); the completion state is `'completed'` (`eve/common/lib/appConst.py:1601`).
- The Host turn-in path completes the mission with rewards and other writes in one transaction, then announces after commit (`dotnet/src/Eve.Host/Composition/MissionComposition.cs:833-900`). `OnMissionTurnInAsync` requires an accepted mission with completed objectives before invoking that path (`dotnet/src/Eve.Services.Core/MissionDungeonRuntime.cs:313-325`). This supports the distinction between `CompleteAsync` and `CompleteInAsync`.
- A whole-client search found 15 consumers of `OnAgentMissionChange` / `OnAgentMissionChanged`: `sensorSuiteService.py:498-500`; `agencyNew/contentProviders/contentProviderAgents.py:192-194`, `contentProviderEpicArcs.py:24-26`, `contentProviderStorylineAgents.py:46-49`; `dockedUI/agents.py:140-143`; `industry/browserResearch.py:76-78`; `neocom/journal.py:168-184,1019-1021`; `station/agents/agentDialogueWindow.py:86-90`, `agents.py:688-713`, `missionDetailsWindow.py:69-76`; `evemissions/client/missioncontroller.py:514-517`; `jobboard/client/features/agent_missions/provider.py:79-114`; `missions/client/missionObjectivesTracker.py:161-178`; and `nodegraph/client/events/agent.py:12-14`. None of these contracts changed.

## Local verification

- After merging `origin/main` at `5f804ac193d00c65dd830469962ac4170ed0b9f2`, both project builds passed with 0 warnings and 0 errors. The same database-backed selection passed again: Eve.Agents.Tests 19/19, the eight referencing Eve.Services.Core.Tests classes 49/49, and MissionCompletionTransactionTests plus AgentMissionDispatchTests 13/13. All reported 0 skipped; database runs used `eve_p3` through `tools/dev/db-lock.sh`.
- No graphical-client session was used for this comment-only cleanup.

## Main CI gate

- The newest completed main workflow is run 1475 for that upstream commit and is red. Its failing steps include the Rust workspace test, generated-fixture comparison (`EnsureAbyssalScene`), and live undock race checks. The red-main gate blocks pushing this unrelated comment cleanup; no push was made.
