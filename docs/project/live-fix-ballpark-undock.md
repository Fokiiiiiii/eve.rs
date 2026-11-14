# Live investigation: ballpark bootstrap on space entry

## What the capture shows

The capture contains a reproducible ordering defect on in-space login and a separate ballpark timeout while the character is docked. The docked timeout and the untimed `PickInfinity` click errors cannot be causally tied to either transition from the supplied timestamps.

On conn 1, the server sent clock setup and `DoDestinyUpdate(AddBalls2)` after the space session change but before the client requested Beyonce `GetFormations`, resolved the ballpark, or bound it. The pre-fix Host triggered those packets from the active-ship `invbroker.List`. The later bind still sent a full `SetState` with the ego ship, but it answered the bind RPC before delivering that state. The first ordering is unrelated to Beyonce binding; the second leaves the client without its initial full state when the bind RPC completes.

Conn 4's `clientStatLogger.LogString` at seq 1084 (`t=3730921`) arrived while the character was docked. Conn 4 selected character `140000001` at seq 908 (`t=3704594`) and got a station session change at seq 910 (`t=3704844`): `stationid=60015241`, `locationid=60015241`, `solarsystemid2=30100044`, and no `solarsystemid`. The timeout came 26,077 ms after that selection and 135,000 ms before the undock request at seq 1439 (`t=3865921`). Michelle normally has no ballpark while docked, so this log entry alone does not show a failed undock.

The nine supplied `PickInfinity` tracebacks have no timestamps in the MITM excerpt. The source path dereferences the registered `default` scene, but the available logs do not establish whether the scene was absent during login, undock, or another view transition. The net log's aborted socket and handshake timeout also have no demonstrated correlation to the conn 4 sequence. The sim log contains no Destiny processing error for this character.

## Conn 1: in-space character entry, before the fix

Character `140000001` entered system `30100044` in ship `1990000000`:

| MITM seq | Event | Observed order |
| --- | --- | --- |
| 70 | `SessionChangeNotification` sets `solarsystemid`, `solarsystemid2`, `locationid` to `30100044` and `shipid` to `1990000000` | The client switches toward the inflight view. |
| 180 | Client `invbroker.List` on its active ship | The pre-fix Host used this unrelated RPC to prime space visuals. |
| 182–184 | `OnSetTimeDilation`, `DoSimClockRebase`, `DoDestinyUpdate(AddBalls2)` | These precede Beyonce preparation and bind in the capture. |
| 195–198 | Client `beyonce.GetFormations` and `MachoResolveObject(30100044)`; replies | Michelle is constructing the local ballpark. |
| 199–200 | Client `beyonce.MachoBindObject(30100044)`; reply | The pre-fix Host answered before the simulation scene update. |
| 201 | `DoDestinyUpdate(SetState)` | The authoritative full state follows the RPC reply. |
| 202 | `DoDestinyUpdate` with ship prime actions | Prime actions follow the full state. |

The MITM trace cannot reveal whether Michelle's private ballpark object or `bpReady` had been set when seq 182–184 arrived. V24 source matters here: `AddBallpark` constructs `self.__bp` before it calls `GetFormations`, while `GetBallpark` reports readiness from `bpReady`; `DoDestinyUpdate` returns early only when `self.__bp is None`. Therefore the capture proves the server sent Destiny before the remote preparation/bind calls, but it does not prove Michelle dropped those updates or that they caused the reported timeout.

## Conn 4: character selection, docked timeout, then undock

The same character later undocked into system `30100044`:

| MITM seq | Event | Result in the capture |
| --- | --- | --- |
| 908–910 | `SelectCharacterID`; `SessionChangeNotification` | Character selection leaves the client docked at station `60015241`; `solarsystemid` is absent. |
| 1084 | Client stat logger reports “Failed to get a valid ballpark in time after trying 30 times” | This predates undocking and is not evidence that the later bind failed. |
| 1439 | `ship.MachoBindObject((60015241, 15), Undock(ship=1990000000,...))` | Starts undocking. |
| 1441 | `OnItemsChanged` moves the ship to system `30100044`, with `customInfo=Undocking:60015241` | Inventory state records the move. |
| 1442–1452 | Eleven `OnMachoObjectDisconnect` notifications | Station-bound objects are released. |
| 1453 | `SessionChangeNotification` clears `stationid`, sets `locationid` and `solarsystemid` to `30100044` | GameUI enters the space view. |
| 1454 | `OnInvulunOnUndockingUpdated(shipid, FILETIME, 300000000)` | Undock invulnerability is announced. |
| 1457 | Undock `CallRsp` | The undock RPC completes. |
| 1610–1615 | `GetFormations` and `MachoResolveObject(30100044)`; replies | Beyonce prepares and resolves the ballpark. |
| 1617–1621 | `beyonce.MachoBindObject(30100044)`; reply at seq 1621 | The captured Host answers before the scene notifications. |
| 1622–1623 | `OnSetTimeDilation`, `DoSimClockRebase` | Clock setup follows the bind reply. |
| 1624 | `DoDestinyUpdate(AddBalls2)` | The ego ball is introduced. |
| 1625 | `DoDestinyUpdate` with undock movement prime actions | Movement follows the ball add. |
| 1627 | `OnSignalTrackerFullState` | Signal state follows. |
| 1629 | `DoDestinyUpdate(SetState)` | Authoritative state follows the undock prime. |

The fix preserves the observed undock Destiny sequence (`AddBalls2`, movement prime, then `SetState`). It changes the bind response boundary: the Host now waits for the simulation to deliver the scene notifications before it returns the Beyonce bind result. The notifications therefore precede the `MachoBindObject` `CallRsp`.

## V24.01 client evidence

Source paths below are relative to `/home/user/eve-logic`:

- `eve/client/script/remote/michelle.py:127-141`: `UpdateBallpark` creates a ballpark only when `session.solarsystemid` is set and inflight/structure is the primary view; otherwise it removes the ballpark and unregisters the `default` scene.
- `michelle.py:315-333`: `AddBallpark` creates `destiny.Ballpark` before calling Beyonce `GetFormations`, starts it after that RPC, and then sets `bpReady=True`. `:343-357` contains the 30-try timeout log. `GetBallpark` checks `bpReady`, not just whether the object exists.
- `michelle.py:482-512`: `DoDestinyUpdate` returns only when `self.__bp is None`. `:699-703`: `DoSimClockRebase` adjusts time only when the ballpark object exists.
- `michelle.py:968-1005`: `SetState` reads the full ball state, sets ego, checks every slim item has a ball, and marks the state valid. `:1256-1277`: `AddBalls2` reads an incremental full-state chunk and skips slim items already present.
- `eve/client/script/ui/shared/dockedUI/undockingSvc.py:60-90,100-138` and `eve/client/script/ui/station/base.py:485-521`: the station undock workflow performs the session change around `shipsvc.Undock(shipID, ignoreContraband, onlineModules=...)`; the observed conn 4 `ship.MachoBindObject` request matches that call. `eve/client/script/ui/services/structure/structureDocking.py:26-45` is the separate structure path.
- `eve/client/script/parklife/gameui.py:605-619`: after a space session change, GameUI sets `wannaBeEgo` when the ship is missing from the ballpark and switches to inflight. `eve/client/script/parklife/sceneManager.py:211-240` switches the scene on a solar-system location change.
- The click traceback path in this checkout is `eve/client/script/ui/inflight/navigation.py:560-564` → `eve/client/script/ui/camera/shipOrbitCameraController.py:59-63` → `eve/client/script/ui/camera/cameraUtil.py:36-43`. `SetShipDirection` dereferences `GetRegisteredScene('default')` before `PickInfinity`. These line numbers differ from the supplied traceback's `navigation.py:627`, so the exact source revision of that traceback is not the checkout used here.

The bind response ordering is observable client state: the first full state contains ego and sets `validState`. V24 source does not say the RPC reply itself makes the ballpark valid; the scene notifications do. The captured pre-fix reply-before-state gap is therefore removed.

## EveJS reference

The supplied EveJS source tree is `/home/user/evejs-261108.1`:

- `server/src/services/inventory/invBrokerService.js:2392-2397` disables the active-ship inventory-list prime. `:2419-2430` documents deferring initial space bootstrap to Beyonce bind.
- `server/src/services/ship/beyonceService.js:2411-2425` prepares the ballpark in `GetFormations`; `:4057-4091` calls `bootstrapSessionBallpark` inside `Handle_MachoBindObject` before the handler returns.
- `server/src/network/packetDispatcher.js:618-631` awaits the service handler, then `:688-700` sends `CallRsp`. `ClientSession.sendNotification` sends through `sendPacket` (`clientSession.js:854-879, 906-995`). Thus the reference emits the bootstrap notifications before the bind reply.
- `server/src/space/runtime/scene/presentation.js:665-673` defers a jump's initial state until bind. `:723-744` preserves the undock add/prime-before-state sequence. `:772-895` builds authoritative `SetState`; `:944-956` places it before login/jump movement and prime updates.

The fix follows that boundary in eve.rs. The Host now waits for the simulation's bind acknowledgement, which is emitted after the scene notifications, before it returns from `MachoBindObject`. For login/jump the first Destiny update is the authoritative `SetState`; undock keeps its distinct `AddBalls2`, movement prime, and later `SetState` sequence.

## Cause and fix

The pre-fix Host connected login visuals to the first active-ship inventory `List`. That path sent time-dilation setup and `AddBalls2` before Beyonce had requested or bound the ballpark, then marked the visuals as already sent. The bind path did not wait for the simulation scene to finish before its RPC reply. The client could therefore open the space UI before it had received the bound full state.

The fix removes the inventory-triggered `PrimeLoginBallparkVisuals` path and its private simulation command/flag. For in-space login and jump, the full `SetState` is the first Destiny update and contains the ego ball; no duplicate `AddBalls2` is sent for those balls. Beyonce bind now waits for an acknowledged simulation bind. The simulation sends the clock and Destiny scene notifications, then acknowledges; only after that does the Host return the bind result. Per-session bind calls are serialized so overlapping retries cannot collide on the simulation acknowledgement key. A refused bind does not return a successful bound object.

The station undock flow remains distinct and evidence-based: its ego ball and movement prime precede the full `SetState`, as in the supplied capture and EveJS. Those scene notifications now arrive before the Beyonce bind reply.

## Regression coverage and verification limits

`dotnet/tests/Eve.Host.Tests/SpaceBallparkBootstrapHostTests.cs` uses the registered Host services, PostgreSQL, pinned SDE, and a real Release `eve-sim` over its simulation socket. It first selects a docked character, binds the station hangar, invokes the registered `ship.MachoBindObject((stationID, 15), ("Undock", (shipID, false), {}))` path, and checks persisted ship location plus the observed `OnItemsChanged`, bound-object disconnect, session change, and invulnerability notification order. It then calls Beyonce `GetFormations`, `MachoResolveObject`, and ballpark bind, verifying that clock setup, `AddBalls2`, the undock movement prime, and full `SetState` containing the ego ship all arrive before the bind method returns. The same test then selects a persisted in-space character, calls active-ship `invbroker.List` before Beyonce, verifies the inventory RPC emits no scene bootstrap, and confirms login sends full ego `SetState` first without duplicate `AddBalls2`. A station-hangar assertion preserves the selected `shipid` after `invbroker.List`.

The pre-fix Host test failed because `invbroker.List` emitted clock and `AddBalls2` notifications. With that path removed but before the bind acknowledgement was added, the regression then failed because the service returned with no `SetState` delivered. The fix adds an acknowledgement after sim notification delivery. The regression also checks overlapping bind calls serialize per session and that a simulation refusal cannot return a successful ballpark bind.

Local checks passed: the registered Host/simulation regression passed on `eve_p3` with the pinned SDE and Release `eve-sim` (1 passed, no skips); `CharacterHostTests` passed (84, no skips); `SpaceServiceTests` passed on `eve_p3` (171, no skips); the Beyonce Host wire filter passed (13, no skips); `StructureControlTests` passed (7, no skips); and the full `SimClientTests` class passed (44, no skips). Release builds of the touched .NET projects completed with 0 warnings and 0 errors. Rust formatting, `cargo check -p eve-space -p eve-sim`, clippy with `-D warnings`, and the focused scene/simulation tests passed.

Not checked: the graphical V24.01 client was not run, and the EveJS flow was traced in source rather than executed because this checkout has no applicable server test script. The supplied docked timeout at seq 1084 and the nine untimed `PickInfinity` tracebacks remain uncorrelated to the conn 1 entry race or the later conn 4 undock. The regression verifies registered Host service results and notifications with the real simulation, not rendering in the graphical client.
