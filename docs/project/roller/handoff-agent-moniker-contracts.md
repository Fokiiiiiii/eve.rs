# Agent moniker mission contracts handoff

## Status

The four missing bound agentMgr methods are implemented in eve.rs. `GetMissionJournalInfo` projects the held mission and objectives (including courier cargo state); `GetMissionKeywords` projects client message arguments; `GetStandingGainsForMission` uses configured mission standing gains and the Relation Store; `RemoveOfferFromJournal` transitions only Offered records through AgentMissionStore. Repeated removal returns None without a second journal transition. Bound reads use session character ID plus the bound agent ID.

Local verification: the focused `AgentMissionServiceTests.Bound_agent_mission_queries_project_courier_cargo_keywords_standing_gains_and_offer_removal` passed (1 passed, 0 failed, 0 skipped). Actions run `40050160162` passed for the briefing and objective regressions. A broader selection for `Eve.Services.Core.Tests`, `Eve.Host.Tests` (`FullyQualifiedName~AgentMission`) and `test_agent_moniker_live.py` was dispatched as run `40052035257` against ref `04bcba460dfc7b8a33d769bc0fba9235431f7b50`; it was still in progress on 2026-10-24.

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

1. Inspect selected Actions run `40052035257` and latest-main CI run `40052261614`; record their final outcomes here.
2. Continue the next open Agent RPC differential from current main after these checks complete.

## Client-contract differential follow-up (2026-10-24)

EveJS 261018.1 `buildMissionBriefingInfo` returns mission keywords, decline time, acceptance timestamp, and expiration time. The V24.01 client reads and caches the `Mission Keywords` field from `GetMissionBriefingInfo`. The bound handler now resolves the agent from the live agent rows, reuses the existing keyword projection, and emits timestamps and remaining decline ticks only from the held mission record.

EveJS 261018.1 `buildMissionObjectivePayload` derives `importantStandings` from `missionRecord.importantMission`. Eve.rs now projects the persisted `AgentMissionContent.Important` value, which Host populates from the static agent's `ImportantMission` field. Fields without an authoritative source in the held mission/catalog, such as content and image, remain `None`.

Changed in this follow-up:
- `dotnet/src/Eve.Services.Core/AgentMissionService.cs`
- `dotnet/src/Eve.Services.Core/MissionViews.cs`
- `dotnet/tests/Eve.Services.Core.Tests/AgentMissionServiceTests.cs`
- `dotnet/tests/Eve.Services.Core.Tests/MissionViewsTests.cs`

Local checks succeeded: `Eve.Services.Core` build, `Eve.Services.Core.Tests` build, and `Bound_agent_mission_briefing_projects_keywords_and_persisted_times` (1 passed, 0 failed, 0 skipped). Actions run `40050160162` passed for `Bound_agent_mission_briefing_projects_keywords_and_persisted_times` and `Mission_objectives_report_important_standing_status_from_the_held_record`. Push-triggered CI run `40050146894` failed in `test_docked_corporation_members_trade_at_an_eligible_upwell_structure`; the failure was in the existing Upwell station-item query path, not these agent mission changes. Main commit `9c4b120a` fixed the docked-structure station item query and added `A_docked_structure_lists_its_own_station_items_but_not_a_remote_structure`. The follow-up AgentMission plus live moniker selection (run `40052035257`) and latest-main CI (run `40052261614`) were still running or queued when this update was prepared. The graphical client was not used.
