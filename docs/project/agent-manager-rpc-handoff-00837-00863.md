# Agent manager RPC coverage handoff

Date: 2026-10-24

## Disposition

Rows RPC-00837–00843 are client-side `Rowset.AddField` and `Rowset.Filter`, not remote services. The actual remote methods in this batch are already advertised by `AgentMissionService.HasMethod` and dispatched through the registered `agentMgr` service.

The Host composes the service with mission runtime state overlaid on the persisted agent-mission store, character bookmarks, research rows, the SDE agent catalog, and static mission data (`dotnet/src/Eve.Host/Composition/MissionComposition.cs:233-260`). `AgentRpcServices()` includes it (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:77-80`), and the Character service list includes AgentRpc (`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:64-67`).

The Host wire regression exposed one real gap: bound `GetMyJournalDetails` returned all agents' mission and research rows. V24.01 uses this bound call during partial journal refresh. The service now filters mission rows by row field 4 and research rows by row field 0 to the bound agent, matching EveJS v0.12.9.

## V24.01 callers and consumed replies

A whole-tree search of `/home/user/eve-logic` found these remote callsites and consumers:

| Rows / method | Client calls and reply use |
|---|---|
| 00837–00838 `AddField` | Local rowset operation at `eve/client/script/ui/station/agents/agents.py:92-94`; `Rowset.AddField` is implemented at `eve/common/script/sys/rowset.py:81-90`. The client adds `factionID` and `solarsystemID` to its clone. |
| 00839–00843 `Filter` | Local rowset operation at `agents.py:108-113`, grouping by station, solar system, corporation, faction, and agent type; `Rowset.Filter` is at `rowset.py:211-227`. These are not server RPCs. |
| 00844–00847 `GetAgents` | Only remote callers: `agents.py:91` and `eve/client/script/ui/shared/info/infosvc.py:2593`. The first clones the rowset, fills client-derived fields, and indexes/filters it at `agents.py:92-113`; the second indexes by `agentID` and reads `divisionID` at `infosvc.py:2593-2607`. |
| 00848–00849 `GetCompletedCareerAgentIDs` | `agents.py:218` merges the ID-to-bool response into a cache; `IsCareerAgentCompleted` reads that value at `agents.py:223-226`. |
| 00850–00851 `GetDisabledMissions` | `agents.py:358-360` caches the returned IDs; `IsMissionDisabled` tests membership at `agents.py:362-363`. |
| 00852–00853 `GetMessagesForEpicArcMissions` | `epicArcController.py:67-68` looks up `messageType -> missionID -> messageID`, returning `None` for absent keys. It sets chapter IDs at `epicArcController.py:72-73,150-156`; `epicArcBar.py:57-61` resolves and displays the chapter message. |
| 00854–00855 `GetMyEpicArcStatus` | `epicArcSvc.py:31-32` passes the mapping to the controller. `epicArcController.py:83-99` reads accepted/completed/quit dates and name IDs; mission nodes use those fields at `epicArcController.py:191,237,246,280`. |
| 00856–00859 `GetMyJournalDetails` | Full refresh calls it at `journal.py:311`; the client iterates mission bucket 0 and bookmark field 6 at `journal.py:312-314`. Partial refresh calls the bound agent moniker at `journal.py:324`, falls back to the unbound service at `journal.py:330-331`, replaces mission rows by agent ID at `journal.py:334-338`, and merges both returned buckets at `journal.py:345-348`. |
| 00862–00863 `GetSolarSystemOfAgent` | `agents.py:799-801` caches the scalar result; the agent info window consumes it at `infosvc.py:2020-2022` and shows a location only when it is not `None`. |

## EveJS comparison

EveJS v0.12.9 `server/src/services/agent/agentMgrService.js` implements GetAgents at 787-833, GetSolarSystemOfAgent at 835-838, GetCompletedCareerAgentIDs at 840-848, GetDisabledMissions at 850-852, GetMyJournalDetails at 868-878, GetMyEpicArcStatus at 888-892, and GetMessagesForEpicArcMissions at 894-904. Its `filterMissionJournalToAgent` at 226-242 filters both mission and research buckets. The updated eve.rs behavior matches that filter and the journal.py partial-refresh use.

EveJS v0.12.9 sends 19 GetAgents columns; eve.rs sends 15. The V24.01 callers above consume the first 15 fields; `agents.py` derives faction and solar-system fields locally, while the info window calls GetSolarSystemOfAgent separately. No V24.01 consumer was found for the four extra columns. The existing GetAgents column decision is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Verification

Added `dotnet/tests/Eve.Host.Tests/AgentMgrReadWireTests.cs`. It runs the actual HostServer socket and PacketDispatcher with the concrete AgentMissionService registered under `agentMgr`. It exercises all seven remote read methods, the transport-added `machoVersion` keyword, two character state rows, the 15-column rowset, nested epic-arc values, journal/bookmark/research records, bound-agent filtering of both journal buckets, and the solar-system scalar. These methods are read-only; no persistence or notification is expected. The test does not require PostgreSQL.

The first selected Actions run 40086984233 stopped during the .NET solution build before tests because the merged `CharMgrPublicInfoHostTests.cs` lacked `using Eve.Services;` for `Py` (CS0103 at lines 77-78). Added the missing import. The agentMgr focused test itself did not run in that job; after the fix, the Host test project build passed with 0 warnings and 0 errors and this focused test passed 1/1 locally. A corrected Actions rerun is pending.

Verified locally after the implementation change: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false` passed with 0 warnings and 0 errors. The focused Host wire test passed 1/1 with 0 skipped. No database or graphical client was used.
