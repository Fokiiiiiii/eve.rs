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

The test is database-backed through the existing `PgFact` harness. Local compilation was killed by Roslyn exit code 137 under shared-machine memory pressure. Actions run `39962148006` on commit `b74153a` passed: 1 test passed, 0 failed, 0 skipped. The test checks the Quit button offered after acceptance at the agent's station. Push-triggered `ci.yml` run `39962135905` was still in progress at this handoff update. No graphical client was used. No graphical client was used.
