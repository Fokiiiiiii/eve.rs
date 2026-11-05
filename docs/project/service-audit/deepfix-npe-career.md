# AIR NPE skip flow deep fix

## Finding and fix

The client treats skipping AIR NPE as an event-driven state transition. Its wrapper calls `remote_air_npe_controller.skip_air_npe()` without arguments and performs no local tutorial cleanup in `storylines/client/airnpe.py:101-111`. The skip can be initiated from the Operations panel (`eve/client/script/ui/shared/infoPanels/infoPanelOperations.py:190-192`), system menu (`eve/client/script/ui/shared/systemMenu/systemmenu.py:429-434`), and tutorial clone-jump and home-station checks (`nodegraph/client/actions/tutorial.py:35-39,122-124`; the stargate cancel path calls the same wrapper at `:76-89`).

V24.01 consumers make both events necessary. `OnTutorialSkipped` terminates the current tutorial (`operations/client/tutorialOperationsController.py:50-52`), clears event-director highlights and conversations (`operations/client/eventdirector/eventdirector.py:49-51`), and refreshes the Operations panel (`eve/client/script/ui/shared/infoPanels/infoPanelSvc.py:395-397`). `OnAirNpeStateChanged(character_id, new_state_id)` updates the AIR NPE state cache and reveals the restricted UI/camera when state becomes completed or skipped (`storylines/client/airnpe.py:63-70`); its other consumers refresh the AIR panel (`eve/client/script/ui/shared/infoPanels/infoPanelSvc.py:204-206`) and notify the login milestone timer (`eve/client/script/ui/shared/infoPanels/milestoneTimer/milestoneTimer.py:34`).

The Host registers `air_npe` with the real character store (`dotnet/src/Eve.Host/Rpc/ProgressRpc.cs:106-109`). `PgCharacterStore.MarkTutorialSkippedAsync` commits state 3, clears the first-login handoff and reveal flag, and returns a state only for an actual eligible transition (`dotnet/src/Eve.Characters/Characters.cs:183-192`). Before this fix `AirNpeService.SkipAsync` sent only `OnAirNpeStateChanged` (`dotnet/src/Eve.Services.Core/AirNpeService.cs:50-53`). It now sends zero-argument `OnTutorialSkipped` followed by `OnAirNpeStateChanged(clientID, characterId, state)`, and only after persistence reports a changed transition. Repeated skips and ineligible states produce no notifications. The RPC returns `None`; the client catches RPC exceptions and logs them, so this route has no user-facing server error code.

EveJS 261028.1 `server/src/services/npe/airNpeService.js:36-52` also only sends `OnAirNpeStateChanged`. This intentional client-driven deviation is recorded in `docs/reference/compat/DEVIATIONS.md`.

## Verification

- The new Host/PostgreSQL regression failed before the fix: expected `OnTutorialSkipped` then `OnAirNpeStateChanged`, but received only the latter.
- After the fix, the Host regression passed. It checks the registered RPC, exact notification order and arguments, persisted state/handoff fields, and state after Host restart.
- `LoginInitializationRpcServicesTests` passed all 8 tests, including that notifications occur only for a persisted transition.
- Host test project build succeeded. The Host database test ran with the dedicated `eve_p4` database through `tools/dev/db-lock.sh`.
- No graphical V24.01 client verification was performed.

## Audit boundary

This handoff verifies the AIR NPE skip path and its whole-client callers and event consumers. Career portal completion/rewards and tutorial task completion still need an end-to-end audit.

## Daily AIR goals audit: client path has no Host API

V24.01's daily-goal controller obtains active and unclaimed goals from `DailyGoalMessenger` (`dailygoals/client/goalsController.py:205-232,257-276`). The messenger uses the public gateway, subscribes to current, progressed, completed, and redeemed notices (`dailygoals/client/goalMessenger.py:27-37`), reads goals (`:48-67`), pays for completion (`:77-87`), redeems rewards (`:106-118`), and translates request failures into status codes (`:152-165`). The controller turns completion and redemption notices into UI signals (`dailygoals/client/goalsController.py:310-348`). EveJS 261028.1 has the matching gateway handlers for current-goal reads and unclaimed rewards (`server/src/_secondary/express/gatewayServices/dailyGoalsGatewayService.js:276-359`), redemption/bulk claims (`:360-383`), and Evermark-paid completion (`:384-393`); its state layer exposes goal completion, current-goal listing, activity progress, redemption and paid completion at `server/src/services/dailyGoals/dailyGoalsState.js:718,927,973,1046,1107`.

The gameplay reference says AIR assigns five common daily goals (one per career path plus Complete 3 Jumps), each goal rewards 500,000 ISK and 500 EverMarks, and completing two grants the 10,000 SP daily bonus and monthly-track progress ([EVE University: AIR Daily Goals](https://wiki.eveuniversity.org/AIR_Daily_Goals)). eve.rs currently defines only Mine Ore and Salvage Wrecks as content progress goals (`dotnet/src/Eve.Host/DailyGoalRecorder.cs:34-38`), advances them from committed ore/salvage events (`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:203`), and has no matching client public-gateway API. The recorded compatibility note explicitly says no client window reads these goals (`docs/reference/compat/DEVIATIONS.md:415`). This means the present content counter is not an end-to-end daily AIR goal flow; completion, awards, notices, unclaimed history and monthly milestones remain unimplemented. No partial success handler was added. The audit's remaining work is the full public-gateway/state flow with client-compatible persistence, payout and notices.
