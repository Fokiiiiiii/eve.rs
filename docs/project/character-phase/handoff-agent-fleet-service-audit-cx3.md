# Fleet service audit handoff

## Findings and changes

- Broadcasts use the client spelling `HealTarget` in one shared allow-list used by `fleetObjectHandler.SendBroadcast` and `fleetMgr.BroadcastToBubble/BroadcastToSystem`. The bubble range switch treats it as a same-grid broadcast. The registered Host regression sends every name in the client `ALL_BROADCASTS` list.
- `MoveMember` now returns a Python boolean. `MassMoveMembers` returns a Python list containing only character IDs whose placement changed; the store returns those IDs from the committed transaction. An unchanged placement is not counted as a move.
- Wing/squad creation, deletion, and rename publish the six fleet callbacks to connected members after the store operation commits. Deleting a selected auto-join squad clears that option in the same store transaction and publishes `OnFleetOptionsChanged` with the post-commit snapshot. That callback uses notification id type `fleetid`.
- `GetFleetComposition` was reviewed without changing its shape. The Host projection emits a list of KeyVals with `characterID`, `solarSystemID`, `stationID`, `shipTypeID`, `skills`, and `skillIDs`. Its current client consumers are the fleet composition window, fleet map filter, star-mode map handler, and map-view color handler. The composition window iterates the list and reads those fields; map consumers check for `None` before iterating.

## V24.01 client and EveJS evidence

- `evefleet/const.py:66-86` defines `BROADCAST_REP_TARGET = 'HealTarget'` and includes it in `ALL_BROADCASTS`. `eve/client/script/parklife/fleetSvc.py:983-1008` validates and sends the name through global, bubble, and system paths.
- `eve/client/script/parklife/fleetSvc.py:512-532` branches on the `MoveMember` result and scatters the returned `MassMoveMembers` IDs. `eve/client/script/ui/shared/fleet/fleet.py:140-168` marks those records as changing. `fleet.py:290-297` clears that state on `OnFleetMemberChanged_Local`; `fleetSvc.py:1258-1266` receives the 13-argument member-change callback.
- `eve/client/script/parklife/fleetSvc.py:1163-1172,1300-1317` defines all six hierarchy callbacks and refreshes `self.wings`; `eve/client/script/ui/shared/fleet/fleet.py:187-203,232` consumes their local UI events and the rename payloads.
- `eve/client/script/parklife/fleetSvc.py:1320-1333` handles `OnFleetOptionsChanged(oldOptions, options)`; `fleet.py:216-231` reads the option fields. EveJS `server/src/services/fleets/fleetRuntime.js:796-802` defaults fleet notifications to id type `fleetid`.
- EveJS 261028.1 `server/src/services/fleets/fleetRuntime.js:2075-2124` returns `true` for a successful single move and the moved character ID list for mass moves. Its wing/squad operations at lines 2192-2319 publish the same callback names and argument order.
- The complete `GetFleetComposition` caller search found `fleetCompositionWnd.py:92,181`, `starModeHandler.py:749`, `mapViewColorHandler.py:784`, and `filters/mapFilterFleet.py:15`. The composition fields are read at `fleetCompositionWnd.py:108-124`; the map consumers guard the no-fleet `None` result before iteration.

## Checks

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -m:1 -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `Fleet_broadcast_names_and_member_notifications_keep_client_wire_shapes`: passed 1/1, 0 skipped. The test uses a registered Host and no database.
- No graphical-client walk was performed. Actions verification is pending.
- Existing worktree edits for the separate corporation/reprocessing/industry audit were preserved and are excluded from the fleet changes.

## Related behavior reviewed

Deleting a populated wing or squad currently moves its members to an unassigned position; EveJS rejects deletion while members remain assigned (`fleetRuntime.js:2207-2239,2270-2301`). This pre-existing behavior was left outside this audit's requested findings.
