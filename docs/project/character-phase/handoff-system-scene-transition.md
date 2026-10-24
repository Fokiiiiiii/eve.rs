# System scene transition handoff

## Scope and current source paths

This checkout was advanced to `origin/main` at `914fc6497d843d5aa7ddaa7ae78f8bc470541510`. The requested source layout has since been split: gate-jump completion is in `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs`; the current Rust simulation lives under `crates/eve-space/src/sim/`. The nine pre-existing deleted files in the worktree were preserved and are unrelated to this change.

## Client path and wire contract

- `eve/client/script/parklife/gameui.py:430-472` routes session changes using `stationid`, `structureid`, and `solarsystemid`; `gameui.py:604-637` switches to inflight on the solar-system change and waits for the ship in the ballpark.
- `eve/client/script/remote/michelle.py:120-140` rebuilds the ballpark from `session.solarsystemid`; `michelle.py:314-359` requests formations and treats the ballpark as unavailable until ready.
- `eve/client/script/parklife/sceneManager.py:211-218` observes `locationid`; `sceneManager.py:527-530` selects the scene from `solarsystemid2`; `sceneManager.py:638-657` applies solar-system scene attributes from `solarsystemid`.
- `eve/client/script/ui/services/viewStateSvc.py:242-362` activates primary views and updates overlays; `viewStateSvc.py:487-502` routes `ChangePrimaryView`; `viewStateSvc.py:635-658` reads overlay sets from the active views.
- `eve/client/script/ui/view/spaceToSpaceTransition.py:72-80` initializes a gate transition from the destination system and gate; `eve/client/script/environment/effects/Jump.py:314` is its caller.
- `eve/client/script/ui/shared/mapView/mapViewPanel.py:69-72` closes the dockable panel and clears its `mapView`. A script-wide search found no direct `stationid2` consumer under `eve/client/script`; EveJS also intentionally omits it from the undock notification (`server/src/space/transitions.js:646-666`).
- EveJS completes gate arrival by changing the character session, attaching the destination scene with initial state deferred until bind, then flushing the session notification plan (`server/src/space/transitions.js:2035-2238`). Its beyonce jump and bind entry points are `server/src/services/ship/beyonceService.js:4195-4233` and `4293-4328`.

## Changes

- Gate arrival now clears `stationid`, `stationid2`, and `structureid` through the same ordered `ClientSession.Change` call as the destination `solarsystemid`, `solarsystemid2`, and `locationid`. Unchanged fields stay absent from the wire notification.
- `HostServerTests` now checks the session-change packet and server session values, the `MachoBindObject` tuple response, and that destination `DoDestinyUpdate` messages follow the bind response.
- The slow headless jump walk now checks the destination session snapshot and bind response shape against the EveJS recording. The walk still needs a running live server and the V24.01 graphical client to confirm the reported UI exceptions directly.

## Verification

- Passed: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~session_change' -p:UseSharedCompilation=false -m:2` (1 passed, 0 skipped). This also built the changed Space service project.
- Not checked: the live headless gate-crossing walk and graphical-client exception log. No database was used.
- Actions dispatch and the push-triggered CI check are pending the implementation push.
