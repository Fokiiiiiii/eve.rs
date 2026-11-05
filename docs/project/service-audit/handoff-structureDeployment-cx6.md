# structureDeployment service audit handoff (cx6)

## Result

The eight P1 coverage rows for `structureDeployment` (RPC-02641 through RPC-02648) now record `IMPLEMENTED-TESTED`. No `structureDeployment` rows remain open in this CSV. The audit found the implementation already present in the worktree, so this change records verified coverage instead of duplicating its backends. Other service rows in the audit were left unchanged.

## Client contract and consumer trace

A whole-source Serena search in `/home/user/eve-logic` found the outgoing calls:

- `eve/client/script/ui/services/structure/structureDeployment.py:64-68`: after the user confirms decommissioning, `Unanchor(structureID)` is sent; `CancelUnanchor` sends only `structureID` even though the local UI method also receives `typeID`. Neither caller stores or inspects the RPC reply.
- `eve/client/script/ui/services/structure/structureDeployment.py:172-174`: `Anchor` sends ten positional values in the recorded order, then cleans up the deployment UI and reloads the structure directory. The reply is not consumed.
- `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:375-378`: `RenameStructure(structureID, newName)` is followed by `structureDirectory.OnCorporationStructuresUpdated()`; the reply is not consumed.

The current EveJS reference delegates at `/home/user/evejs-261028.1/src/server/src/services/structure/structureDeploymentService.js:20-63`; all four handlers return `null`. eve.rs preserves this client-visible `PyNone` result. The registered route is `dotnet/src/Eve.Host/Rpc/StructureRpc.cs:63-75`; Host composition supplies the persistent Anchor, Unanchor, CancelUnanchor, and Rename callbacks at `dotnet/src/Eve.Host/Composition/StructureComposition.cs:368-371,477-481`.

## State, persistence, and notifications

- Anchor uses `StructureAnchorAuthority` with the inventory, structure store, timers, scheduler, and event bus. `StructureAnchorTests.Anchoring_an_item_from_the_ship_consumes_it_and_creates_the_structure_with_its_event_and_placement` checks item removal, persisted structure fields and anchoring state, placement, and the `structure.anchored` event.
- Unanchor and CancelUnanchor use `StructureDeploymentAuthority`; `StructureCompositionTests.Unanchor_starts_a_seven_day_timer_for_a_director_and_cancelling_stops_it` exercises the registered Host service, role refusals, persisted timer, and cancellation.
- Rename uses the same authority; `StructureCompositionTests.Rename_normalizes_the_name_the_directory_shows_refuses_a_short_name_and_survives_a_restart` checks normalized persisted name, validation and access errors, directory visibility, and restart persistence.
- `StructureNotificationsTests.The_owner_and_docked_pilots_receive_persisted_hangar_view_updates_in_order_and_unrelated_sessions_hear_nothing` covers the client hangar-view notification after unanchoring begins. The registered TCP wire route and all four `PyNone` replies and argument mappings are covered by `StructureRpcHostCoverageTests.Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes`.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- On PostgreSQL database `eve_p6`, via `tools/dev/db-lock.sh`: selected Host classes passed, 33 passed and 0 skipped.
- On PostgreSQL database `eve_p6`, via `tools/dev/db-lock.sh`: `Eve.Structures.Tests` filtered to `StructureTimerAnchoringDelayTests`, 3 passed and 0 skipped.
- `dotnet test dotnet/tests/Eve.Services.Core.Tests --filter 'FullyQualifiedName~StructureRpcServiceTests' -p:UseSharedCompilation=false`: 41 passed and 0 skipped.
- The newest completed push-triggered CI run at review time was run 1363 and was green.

No native graphical-client session was used. This audit does not change gameplay mechanics or cover unrelated service rows.
