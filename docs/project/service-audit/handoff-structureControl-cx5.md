# structureControl coverage handoff — CX5

## Scope and result

The eight assigned P1 coverage rows RPC-02627–02634 are covered by the active Host routes and PostgreSQL-backed control store. The production wiring predates this audit pass: `StructureRpc.cs` registers `structureControl`; `StructureComposition.cs:423-431` supplies the actual `StructureControlBackend` callbacks; `StructureControlBackend.cs` mutates `StructurePilotStore` and coordinates SpaceSessions. Added a successful-release assertion to the existing release refusal regression, and recorded the evidence in `EVE_RS_COVERAGE.csv`.

## Whole-client trace (eve-logic V24.01)

A whole-tree search for all five RPC names found their remote calls in `eve/client/script/ui/services/structure/structureControl.py`:

- `TakeControl(session.structureid)`: line 31. `structureController.py:81-82` and `structuremenu.py:31-32` invoke the wrapper/menu action.
- `BoardStructure(structureID)`: line 51. The caller checks same-system and ship/capsule state at lines 34-50, then calls through `PerformSessionChange('board', ...)`. The space context menu exposes it at `structuremenu.py:17-18`.
- `ReleaseControl()`: line 56. The client only issues it while `shipid == structureid` (lines 54-57); menu and HUD consumers are `structuremenu.py:23-25` and `shipUI.py:424`.
- `EjectFromStructure(shipID)`: line 86. `None` is the capsule sentinel; the client uses a `board` session change. The selected ship menu routes to this method at `menusvc.py:1611`.
- `GetStructurePilot(structureID)`: line 102. The result is an ID or None. `controlButton.py:54-60` treats None as unoccupied and otherwise opens takeover confirmation; `lobbyWnd.py:356-357` passes the pilot ID to `ConfirmTakeControl`, whose display uses it in `inControlCont.py:17-52`.

The RPC mutations return None. The registered Host socket regression exercises all five methods and asserts the pilot ID/None and mutation None reply shapes. The backend stores the control owner and original ship/location in `structure_pilots`; takeover and release update that row transactionally. Session and simulation updates are ordered, and failures roll back or compensate. This follows the structure phase ST-5 contract in `plan.md:72-75`.

## Reference and tests

EveJS 261028.1 `server/src/services/structure/structureControlService.js:81-202` defines the same five operations and board/eject flows. The Host keeps those client-visible behaviors while storing exclusive ownership and original-ship state durably in `Eve.Structures`.

- Registered Host wire: `StructureRpcHostCoverageTests.Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes`.
- DB lifecycle and notification order: `StructureControlBackendTests`, including takeover, pilot visibility, boarding/ejection, capsule return, and release refusal followed by successful release.
- DB read state: `StructureControlReadModelTests.Current_pilot_reads_authoritative_structure_items_and_online_state_without_replacing_the_personal_ship`.

## Verification

Host.Tests build passed with zero warnings and errors. On `eve_p5`, the focused combined Host filter passed 17 tests with zero failures and zero skips. The initial DB attempt lacked schema `sde`; I copied the pinned 62-table static schema into `eve_p5` and reran successfully. No graphical-client session was run.

## Rows still open

No `structureControl` MISSING or HOLLOW rows remain in this batch. Other services and their rows were not changed.
