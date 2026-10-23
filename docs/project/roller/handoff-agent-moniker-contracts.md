# Agent moniker mission contracts handoff

## Status

The four missing bound agentMgr methods are implemented in eve.rs. `GetMissionJournalInfo` projects the held mission and objectives (including courier cargo state); `GetMissionKeywords` projects client message arguments; `GetStandingGainsForMission` uses configured mission standing gains and the Relation Store; `RemoveOfferFromJournal` transitions only Offered records through AgentMissionStore. Repeated removal returns None without a second journal transition. Bound reads use session character ID plus the bound agent ID.

Local verification: the focused `AgentMissionServiceTests.Bound_agent_mission_queries_project_courier_cargo_keywords_standing_gains_and_offer_removal` passed (1 passed, 0 failed, 0 skipped). Actions verification is pending for Host dispatch, existing AgentMissionService/AgentDialogue/HostServer tests, and `test_agent_moniker_live.py`.

## Contract sources

- V24.01 client: `/home/user/eve-logic/eve/client/script/ui/station/agents/agents.py:626-638` primes message arguments through `GetMissionKeywords(contentID)`; lines 746-763 read journal info and use `objectives.objectives` / `transport[4].hasCargo`.
- V24.01 client: `/home/user/eve-logic/eve/client/script/ui/station/missions/missionentry.py:74-76` removes an offer through the agent moniker.
- EveJS 261018.1: `server/src/services/agent/agentMgrService.js:1131-1192` dispatches all four methods using session character ID and the resolved bound agent. `server/src/services/agent/agentMissionRuntime.js:7359-7372` returns null when the agent or journal record is missing; :7388-7427 builds keyword and standing dictionaries; :7497-7512 removes only offered records.
- EveJS v0.12.9 retains the same bound dispatch for these agent moniker methods.
- Mission mechanics: https://wiki.eveuniversity.org/Mission

## Changed paths

- `dotnet/src/Eve.Services.Core/AgentMissionService.cs`
- `dotnet/src/Eve.Services.Core/MissionViews.cs`
- `dotnet/src/Eve.Host/Composition/MissionComposition.cs`
- `dotnet/src/Eve.Agents/AgentMissionStore.cs`
- `dotnet/tests/Eve.Services.Core.Tests/AgentMissionServiceTests.cs`
- `dotnet/tests/Eve.Host.Tests/AgentMissionDispatchTests.cs`
- `tools/headless-client/tests/test_agent_moniker_live.py`

## Next steps

1. Compile-check `Eve.Host.Tests` locally; do not run PostgreSQL tests locally.
2. Commit and push to main, then dispatch Actions with focused .NET filters plus `test_agent_moniker_live.py`.
3. Inspect the selected Actions run and push-triggered CI. Update this handoff with the run IDs and outcomes after each push.
