# Travel deep fix: undock cooldown blocks docking and gate jumps

## Decision and evidence

The client prevents a session change while the preceding session-change timer is active. The whole-client caller search found three direct `CmdStargateJump` callsites: `stargate/client/gateJumpSvc.py:98-127`, `eve/client/script/parklife/autopilot.py:343-363`, and `eve/devtools/script/insider.py:630`. The gate service calls `PerformSessionChange` at `gateJumpSvc.py:123`; autopilot calls it at `autopilot.py:357-360`; the insider call uses the same session manager path. `carbon/common/script/sys/sessions.py:388-397` raises the localized `PerformSessionChangeInProgress` user error (`/Carbon/UI/Sessions/SessionChangeInProgressBase`), and `sessions.py:418-500` applies the timer guard. GateJumpSvc observes the eventual solar-system session change at `stargate/client/gateJumpSvc.py:51-54`; `eve/client/script/parklife/gameui.py:430-472` consumes session changes to switch the client between station and space. The normal gate-jump caller ignores the RPC reply and reacts to session transitions.

CCP's session-change article documents a 10-second timer and explicitly lists docking and gate jumping among the actions it blocks: [Session Change](https://support.eveonline.com/hc/en-us/articles/205294121-Session-Change). EVE University describes the 10-second gate/session change timer and the 2,500m gate-jump interaction range: [Stargates](https://wiki.eveuniversity.org/Stargates), [Timers](https://wiki.eveuniversity.org/GCC). The distance rule is enforced by the simulation's gate interaction; this change concerns the separate session-change timer.

EveJS 261028.1 routes `CmdStargateJump` through `server/src/services/ship/beyonceService.js:4283-4324` into `server/src/space/transitions.js:3687-3927`, where gate validity, route, ship, criminal/weapon timers and distance are checked. It does not explicitly gate this server call on the session-change cooldown. The V24.01 client and CCP mechanics support enforcing that cooldown on the server too.

## eve.rs path and fix

The Host registers `BeyonceService` in `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:123`; its bound call is `dotnet/src/Eve.Services.Space/BeyonceService.cs:716-723`, which checks the bound object and forwards gate IDs and ship ID to `SpaceSessions.JumpAsync`. Docking goes through `SpaceSessions.DockAsync` in `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:18-54`. Both actions use `MovementPolicyAsync`; its `PolicyFacts.HasSessionChangeTimer` now reads the active session timer at `SpaceSessions.Movement.cs:294-304`. Previously that fact was forced false for Dock and GateJump, allowing immediate repeat location changes just after undocking.

The correction supplies the actual timer state for all movement actions. The shared policy engine decides which action is prohibited, so warp and other policy behavior remains governed by the same action rules. Refusals use the existing movement error mapping and do not reach the simulation.

## Regression proof and coverage

`dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2374-2408` adds database-backed docking and gate-jump regressions. Each first undocks, leaves the 10-second timer active, asserts the call is refused and confirms no additional simulation message was sent. With the old Dock/GateJump exemption restored, both tests failed because no exception was thrown; with the fix, both passed. Existing dock/jump behavior tests use a timer fixture that completes the 10-second cooldown so their sim acceptance, refusal, notification, persistence and wire assertions test those scenarios after the cooldown.

Related behavior assertions include mission dock transition at `SpaceServiceTests.cs:856-872`, jump queue and bound-call response at `SpaceServiceTests.cs:2555-2586`, and jump completion notification/session-change ordering in the following test. The Host wire behavior is exercised by `StargateJumpWireTests`.

## Verification

- `dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- On PostgreSQL database `eve_p5`, through `tools/dev/db-lock.sh`, the full `SpaceServiceTests` and `StargateJumpWireTests` selection passed: 152 passed, 0 failed, 0 skipped.
- The two new cooldown regressions were also run against the old exemption and failed as expected, then passed with the correction.
- The graphical V24.01 client was not launched.
