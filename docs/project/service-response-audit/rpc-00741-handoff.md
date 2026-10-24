# RPC-00741 handoff: achievementTrackerMgr.HasEverWarped

## Contract and evidence

The complete V24.01 eve-logic search found one caller: `eve/client/script/ui/camera/enterSpaceCamera.py:39-45`. `OnCurrSessionSpaceEnteredFirstTime` starts the animation only while the camera is active at lines 108-110; `StartAnimation` schedules `_OnActivated` at lines 34-35. The caller uses the no-argument result as a Python boolean: false selects `AnimEnterFirstTimeEverInSpace`, true (or a void-space system) selects `AnimEnter`, then it schedules `SwitchToPrimaryCamera`. It has no special `None` branch. eve.rs returns a boolean on both states, so the client does not receive `None`.

EveJS v0.12.9 `server/src/services/character/achievementTrackerMgrService.js:145-170` checks session and saved tracker flags, then native warp history (`server/src/services/achievement/achievementState.js:659-662`), and defaults missing history to true. eve.rs is already registered through `ProgressRpc` and checks the persisted achievement tracker plus the legacy game-store projection. It returns a Python boolean and caches true in the session. The missing-history difference (false in eve.rs, true in EveJS) follows the client callsite and is recorded in `docs/reference/compat/DEVIATIONS.md`. This query is read-only and emits no notification.

## Change and verification

Added `HostServerTests.Achievement_tracker_HasEverWarped_returns_a_boolean_through_host_wire` in `dotnet/tests/Eve.Host.Tests/HostServerTests.cs`. It registers the real service in the Host registry and sends the RPC through the socket dispatcher, asserting true/false wire booleans and the authoritative character IDs read.

Post-merge local verification: `dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false -m:1 --filter FullyQualifiedName~Achievement_tracker_HasEverWarped_returns_a_boolean_through_host_wire` rebuilt dependencies and passed 1/1, skipped 0. No database was used.

GitHub Actions verification: pending.
