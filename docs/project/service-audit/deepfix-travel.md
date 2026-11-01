# Travel deep fix: enforce session change cooldown on location changes

## Decision and client evidence

The whole V24.01 client search found three direct `CmdStargateJump` callsites: `eve-logic/eve/client/script/ui/services/stargate/client/gateJumpSvc.py:98-127`, `eve-logic/eve/client/script/parklife/autopilot.py:343-363`, and `eve-logic/eve/devtools/script/insider.py:630`. GateJumpSvc and autopilot wrap the RPC in `PerformSessionChange` at `gateJumpSvc.py:123` and `autopilot.py:357-360`. The shared client guard raises localized `PerformSessionChangeInProgress` in `eve-logic/carbon/common/script/sys/sessions.py:388-397` and checks the active timer at :418-500. GateJumpSvc consumes the resulting solar-system session change at `gateJumpSvc.py:51-54`; `eve-logic/eve/client/script/parklife/gameui.py:430-472` applies station/space session transitions.

For structure docking, `eve-logic/eve/client/script/ui/services/structure/structureDocking.py:26-53` wraps undock in `PerformSessionChange`. Docking uses `NavigateSystemTo(structureID, 2500, RequestDocking)` and then invokes `structureDocking.Dock` at :62-81; `eve-logic/eve/client/script/ui/services/menusvc.py:2981-2989` routes the menu action there. Thus normal client calls wait for the session timer, while the server must also reject direct RPC calls that bypass the UI guard.

The CCP session-change article specifies a 10-second timer started by docking, undocking, gate/wormhole/system jumps and says it blocks docking and gate jumps: [Session Change](https://support.eveonline.com/hc/en-us/articles/205294121-Session-Change). EVE University lists the same 10-second timer and its blocked actions ([Timers](https://wiki.eveuniversity.org/GCC)); the 2,500m gate interaction range is separately listed at [Stargates](https://wiki.eveuniversity.org/Stargates).

EveJS 261028.1 routes the gate call from `server/src/services/ship/beyonceService.js:4283-4324` to `server/src/space/transitions.js:3687-3927`. That server validates the gate, route, ship, criminal/weapon timers and distance but does not enforce the session-change timer. The V24 client and published mechanics support enforcing the timer server-side.

## eve.rs change and resulting behavior

Host registration is `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:123`; the bound `CmdStargateJump` endpoint is `dotnet/src/Eve.Services.Space/BeyonceService.cs:716-723`. Docking uses `SpaceSessions.DockAsync` in `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:18-54`. Both go through shared `MovementPolicyAsync`; `SpaceSessions.Movement.cs:303` now supplies the actual active session timer from `_transitions.SessionChangeActive(...)`, instead of excluding Dock and GateJump. The shared policy also protects wormhole and cyno jumps through `SpaceWormholes.cs:131` and `SpaceSessions.CynoJump.cs:70,196`. Refusals occur before sending a sim command, using existing movement error mappings.

Database-backed regressions in `dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2374-2408` undock and immediately attempt structure docking and gate jumping. They assert a refusal and no additional sim message. Both fail if the old Dock/GateJump exemptions are restored, and pass with the fix. Related dock, gate, cyno, wormhole and tether tests use a test-local timer fixture that completes only the normal 10-second session timer; tests explicitly injecting active timers retain their intended coverage. The shared default in `Rig.cs:102-106,270` now models that elapsed timer for movement test scenarios.

The live structure flow in `tools/headless-client/tests/test_structures.py:181-215,299-321` waits out undock's 10 seconds, retries `DockingApproach` while the ship reaches the 2,500m docking range, and the shared `_docked_pilot` helper allows one additional 11-second wait if the server still reports `DockingRequestDenied`. It then checks `OnDockingFinished` and undocks. This follows the client's navigation call instead of treating an out-of-range request as immediate docking.

## Verification and follow-up

- The focused two cooldown regressions failed when run against the prior exemptions and passed after the fix.
- On PostgreSQL `eve_p5`, through `tools/dev/db-lock.sh`, 205 tests passed, 0 failed, 0 skipped across `CynoJumpHostTests`, `StructureDockingTests`, `StructureHangarTests`, `StructureUndockingTests`, `WormholeJumpServiceTests`, `TetherPolicyTests`, `SpaceServiceTests`, and `StargateJumpWireTests`.
- After merging `ae0758eb`, `dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false` passed with 0 warnings and errors.
- After merging `ae0758eb`, the `eve_p5` selection through `tools/dev/db-lock.sh` passed all 205 related database tests, with 0 failures and 0 skips.
- The focused headless `test_a_pilot_docks_at_the_structure_and_undocks_from_it` flow passed against the live stack after the cooldown and approach waits.
- CI runs `f8396506` and `ae0758eb` both failed the structure docking flow; `ae0758eb` also showed the movement tests still using an active default timer. `Rig.cs` now models the elapsed timer for ordinary movement scenarios, while tests that need an active one inject it. The headless test waits for the timer and retries approach refusals. These are local passing corrections for the failures in both CI runs; the corrective push and its CI result remain to be recorded.
- The graphical V24.01 client was not launched.
