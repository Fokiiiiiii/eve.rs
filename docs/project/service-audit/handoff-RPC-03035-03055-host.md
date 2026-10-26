# Host coverage handoff: RPC-03035–03055

## Scope and decisions

The active Host composition registers WarsInfoMgrService in CorporationRpc and WormholeMgrService in SpaceRpc. WarsInfoMgrService reads the shared war and war.ally RelationStore; its existing service tests exercise persisted relation rows and the client rowset, dictionary, and KeyVal shapes. WormholeMgrService delegates the jump to SpaceSessions.WormholeJumpAsync in the production composition and converts the returned tuple to the list consumed by the client.

Whole-client V24.01 evidence (eve-logic commit 1dbb5711a072a146e1d228365668ac05655eefe1):

- RPC-03035–03037 GetTop50: corp_ui_wars.py:172–183 uses the cursor ID for paging; returned row fields feed owner priming and war entries at :186–202.
- RPC-03038–03045 GetWarsByOwnerID: infosvc.py:2712–2745 groups returned rows by timeFinished and timeStarted; corp_ui_wars.py:227–228 indexes by warID and constructs warUtil.War; warHistoryCont.py:61–66 returns corporation/alliance rows.
- RPC-03046–03047 GetWarsByOwners: war_cso.py:132–134 stores each returned owner mapping through the local cache update. The receiver of iteritems at :133 is this returned mapping; it is a local Python dictionary operation, not an RPC. EveJS v0.12.9 Handle_GetWarsByOwners returns a dictionary at server/src/services/corporation/warsInfoMgrService.js:161–174 and has no iteritems handler. RPC-03052 is therefore excluded as a client-local false positive.
- RPC-03048–03049 GetWarsForStructure: panelWarHQ.py:39–42 caches the returned wars list and :31–38 renders WarEntrySimple rows.
- RPC-03050–03051 GetWarsRequiringAssistance: corp_ui_wars.py:170–173 calls without application arguments; returned rows enter the same war population logic at :186–202.
- RPC-03055 WormholeJump: movementFunctions.py:594–598 passes the bound RPC and endpoint ID into sessionMgr.PerformSessionChange. EveJS v0.12.9 server/src/services/exploration/wormholes/wormholeMgrService.js:207–327 validates, prepares, schedules, and returns buildBoundResult(session).

All client calls here have no application kwargs. The normal transport adds machoVersion in carbon/common/script/net/ServiceCallGPCS.py:221–225.

## Regression coverage

dotnet/tests/Eve.Host.Tests/WarsAndWormholeRpcHostWireTests.cs adds two tests through the actual TCP Host, PacketDispatcher, and ServiceRegistry route:

- The wars route test seeds real RelationStore war and war.ally rows and invokes all five called WarsInfoMgr methods with machoVersion=1. It is tagged PgFact and must be run on an Actions worker configured for PostgreSQL; no database was used locally.
- The wormhole route test invokes the registered WormholeMgrService through Host and checks the returned client list shape and transport metadata.

Local checks: Eve.Host.Tests build succeeded with zero warnings/errors; the focused wormhole Host wire test passed 1/1 with zero skips. Actions verification is pending for the PostgreSQL-backed wars route test.

The incoming batch's IDs differ from the checked-in service-audit IDs after client-local row corrections. Reconcile rows by service, method, and client callsite. The existing TODO entry about the wormhole RPC route test should be updated after Actions reports the focused result.
