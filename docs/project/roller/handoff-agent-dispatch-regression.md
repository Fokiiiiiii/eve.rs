# Agent DoAction dispatch regression handoff

## Status

Implemented Host dispatcher regression coverage in `dotnet/tests/Eve.Host.Tests/AgentMissionDispatchTests.cs`.

The test sends a MachoNet `MachoBindObject` request to `agentMgr`, extracts the returned bound OID, and sends separate bound `DoAction` packets through `PacketDispatcher.DispatchAsync`. It checks the greeting's request button, the offered mission's accept button and persisted Offered state, then the accepted response and Accepted state read through a fresh `AgentMissionStore` instance. It also gates `DoAction` out of the registered service's `HasMethod` table and checks the dispatcher returns `exceptions.AttributeError`; it checks unknown bound methods the same way and unknown agent IDs as `eveexceptions.UserError`.

## Contract evidence

- V24.01 client: `/home/user/eve-logic/eve/client/script/ui/station/agents/agentDialogueWindow.py`, `ExecuteAgentAction` calls `self.GetAgentMoniker().DoAction(actionID)`; `_GetConversation` passes its action ID through the window.
- V24.01 client: `/home/user/eve-logic/eve/client/script/ui/station/agents/agents.py`, `Agents.DoAction(agentID, actionID=None)` invokes the open agent window's `InteractWithAgent(actionID)`.
- EveJS v0.12.9: `/home/user/evejs-v0.12.9/server/src/services/agent/agentMgrService.js`, `Handle_DoAction` resolves the bound agent and handles the action ID.
- EveJS 261018.1: `/home/user/evejs-261018.1/src/server/src/services/agent/agentMgrService.js`, `Handle_DoAction` retains the bound-agent and action-ID dispatch path.
- eve.rs: `AgentMissionService.HasMethod` exports `DoAction`; `PacketDispatcher` checks `HasMethod` before calling service handlers and maps unknown methods to wrapped AttributeError.

## Verification

The test is database-backed through the existing `PgFact` harness. Local compilation was killed by Roslyn exit code 137 under shared-machine memory pressure. The first Actions run found two stale helper references in the greeting and offered assertions; both were corrected. The next run reached acceptance and exposed an incorrect assertion: at the agent's station the accepted conversation includes the Quit action. The test now asserts that client-visible button and token. A rerun is pending. No graphical client was used.
