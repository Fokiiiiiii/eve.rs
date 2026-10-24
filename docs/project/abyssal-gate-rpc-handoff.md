# Abyssal gate RPC coverage handoff

Date: 2026-10-24

## Rows

- RPC-00707: `abyssalMgr.AbyssalEndGateActivation`
- RPC-00710: `abyssalMgr.AbyssalGateActivation`

Both routes were already registered from `CharacterServicesComposition.ProgressRpc()` as `new AbyssalMgrService(HandleAbyssalRpcAsync)` (`dotnet/src/Eve.Host/Rpc/ProgressRpc.cs:105`). The Host dispatch switch routes both names to the persisted Abyssal gate operations (`dotnet/src/Eve.Host/Composition/CharacterServicesComposition.Abyssal.cs:554-555`). The missing check was an actual Macho dispatcher wire regression.

## Client contract across eve-logic

Whole-tree search found one direct callsite for each RPC and no other references to either exact method name.

- `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:567-570`: `RealActivateAbyssalGate` calls `abyssalMgr.AbyssalGateActivation(itemID, violateSafetyTimer=True)` via `PerformSessionChange('abyssToAbyssJump', ...)`.
- `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:577-579`: `RealActivateAbyssalEndGate` calls `abyssalMgr.AbyssalEndGateActivation(itemID)` via `PerformSessionChange('abyssToKnownJump', ...)`.
- `eve/client/script/ui/services/menusvc.py:3042-3046` exposes both menu actions; `menusvc.py:3728-3735` selects them from Abyss gate type (encounter versus exit).
- Both movement functions end with a bare `return`, so the UI does not consume the RPC result. `carbon/common/script/sys/sessions.py:418-500` strips the local `violateSafetyTimer` session-control keyword before forwarding the RPC, invokes the supplied function, and manages session mutation notifications. No result field, default, or `None` check is used by the gate UI.

Thus the valid call shapes are one positive item ID for each RPC, with the encounter call's boolean `violateSafetyTimer=True` keyword. The normal server answer is `None`; client-visible movement and notices follow from the authoritative transition.

## EveJS comparison

At `server/src/abyssal/transport/abyssalMgrService.js:56-88`, EveJS validates encounter-gate use and forwards accepted exits to its end-gate handler; rejected cases throw the gate error. At lines 90-92 that handler delegates to runtime. `server/src/abyssal/runtime/abyssalEndGateRequest.js:517-524` returns null when request preparation rejects, otherwise schedules either a room transfer or final exit. The eve.rs implementation retains its authoritative Host checks and the V24 client error envelope.

## Regression and verification

`dotnet/tests/Eve.Host.Tests/AbyssalGateWireTests.cs` sends both validly shaped calls through a `ServiceRegistry`, the production `AbyssalMgrService`, `PacketDispatcher`, and the real `CharacterServicesComposition.HandleAbyssalRpcAsync` callback. With no pilot attached, each response is asserted as a Macho `ERRORRESPONSE` containing Abyss client error 30; the encounter request includes the observed safety keyword. The check needs no PostgreSQL connection.

The existing stateful room-transfer and final-exit checks remain in `AbyssalHostFlowTests.cs` (lines 496-507 and 738-746), where the real Host callback mutates persisted state and the durable notification subscriber checks ordering. Those database-backed cases were not exercised for this job; no database was used.

Local checks for the Abyssal change: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false` succeeded (0 warnings, 0 errors); the focused `AbyssalGateWireTests` ran 1 passed, 0 failed, 0 skipped. Filtered GitHub Actions run 40086432553 passed on `993a47d4`; push-triggered CI run 40086426904 exposed unrelated upstream Space and Reprocessing test failures. Follow-up diagnosis and checks are tracked below.


## Push-gate follow-up

The push-triggered CI failures were traced to three causes: `SpaceSessions.SpawnAsync` invoked the `NO_SCENE` callback without logging the refusal, despite its helper contract and regression expecting the reason in the log; Reprocessing assertions omitted pre-existing Tritanium stacks or doubled a one-time yield; the restart test passed owner and location in reverse order to `PgItemStore.InLocationAsync(locationId, ownerId, ...)`. These are fixed in the follow-up commit: log the scene refusal, correct the three expected quantities (415, 416, 415), and query with `(StationId, character)`.

The Space, Station, and Host test projects compile locally with 0 warnings/errors. No PostgreSQL was used locally. The filtered Actions run 40090111448 on commit `17e33e5b` passed all five selected Space/Station/Host regression tests with no failures or skips. Push-trigger CI run 40090103812 passed the selected .NET host and Station jobs and Space shard 1/2; Space shard 2/2 was cancelled after 30.3 minutes. The E2E job failed at `tools/headless-client/tests/test_structures.py:490` in `test_a_director_anchors_an_astrahus_from_its_ship_in_a_legal_system_and_the_structure_is_placed_and_told`: `structureDeployment.Anchor` returned `CustomNotify` saying the pilot must be in space. A dedicated `test_structures.py` Actions rerun, run 40092452855 (recorded source SHA `9febc5aac559` as main advanced), remains in progress. The push-trigger run also skipped eight slow live cases that require the nightly `EVE_LIVE_SLOW` setting; those skips are not passes.
