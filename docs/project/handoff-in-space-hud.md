# In-space HUD handoff

## Change in this increment

The gap addressed here is the server-driven target-lock cancellation event. The Rust simulation now emits `target.FailLockTarget(targetID)` through a MachoNet service notification when a pending lock is canceled by server-side validation or state changes. Explicit client cancellation does not echo the event because the client clears its own UI first.

This follows the V24.01 client contract:
- `/home/user/eve-logic/eve/client/script/parklife/targetMgr.py:1287-1296` removes targeting state, clears UI flags, stops the lock sound, and scatters `OnFailLockTarget`.
- `targetMgr.py:1298-1304` scatters the same event before calling Dogma `CancelAddTarget`; an echo would duplicate the local transition.
- `targetMgr.py:1363-1381` starts the lock UI around the Dogma `AddTarget` call.
- `/home/user/eve-logic/eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:612-614` clears the module button's pending-target wait on `OnFailLockTarget`.
- `targetMgr.py:586-598` lists the accepted target state notification operations.

EveJS v0.12.9 sends the service notice on server-driven cancellations (`/home/user/evejs-v0.12.9/server/src/space/runtime/scene/targeting.js:569-591, 1159-1254`); its jammer path sends both the service notice and `OnTarget lost` (`targeting.js:983-1038`). Its Dogma `CancelAddTarget` handler suppresses the notice for explicit cancellation (`/home/user/evejs-v0.12.9/server/src/services/dogma/dogmaService/targeting.js:82-89`).

## Wire and regression coverage

- Added simlink `ServiceNotify` message kind 125 and advanced the protocol epoch to 82 in Rust, .NET, and generated fixtures.
- Host wire regression: `ServiceNotificationHostWireTests` checks destination address kind/service, null call ID, method bytes, target ID argument, and session sequence.
- .NET codec regression: `SimCodecTests` covers encoding/decoding and the full message-kind inventory.
- Rust targeting regressions cover explicit cancellation without a service echo and server-driven out-of-range cancellation with the exact method and target ID.
- Rust simlink fixture regression covers the new message bytes.

## Other in-space HUD client paths reviewed

These client consumers were reviewed while scoping the wider HUD job; this increment did not change their server flows:
- Damage bars: `/home/user/eve-logic/eve/client/script/remote/michelle.py:1535-1542` scatters the updated damage state; `/home/user/eve-logic/eve/client/script/dogma/clientDogmaLocation.py:656-659` scatters dogma attribute changes; `/home/user/eve-logic/progression/client/widgets/healthwidgets.py:75, 100-107` subscribes to damage-state changes and updates the bars.
- Capacitor ring: `/home/user/eve-logic/eve/client/script/ui/inflight/shipHud/activeShipController.py:137-144` reads charge and capacity; `capacitorContainer.py:142-149` samples them and scatters `OnCapacitorChange`. `shipUI.py:617-622` handles mining-scanner dogma attributes.
- Slim-item and overview updates: `/home/user/eve-logic/eve/client/script/remote/michelle.py:1481-1494`; `/home/user/eve-logic/eve/client/script/ui/inflight/overview/overviewWindow.py:383-396, 758-763, 874-892` updates slim rows, reacts to sort changes, and reads sort keys.
- Module state and cycle UI: `/home/user/eve-logic/eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:1393-1410, 1454-1486, 1550-1566`.
- Selected-item panel: `/home/user/eve-logic/eve/client/script/ui/inflight/selectedItemWnd.py:353-371, 847-863`.
- Warp, jump, and dock action routing: `/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:346-387`; warp command call `/home/user/eve-logic/eve/client/script/remote/michelle.py:732-738`.

A full in-space HUD graphical-client walk was not performed. This increment does not claim end-to-end acceptance of the other HUD surfaces.

## Checks run

Passed:
- `cargo +1.94.1 fmt --all -- --check`
- `CARGO_TARGET_DIR=target-cx CARGO_BUILD_JOBS=2 cargo +1.94.1 check -p eve-space`
- `cargo +1.94.1 test -p eve-space targeting::tests::` (38 passed)
- `cargo +1.94.1 test -p eve-space --test simlink_fixtures` (11 passed)
- `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings`
- .NET builds for `Eve.Host.Tests` and `Eve.Space.Tests`
- `ServiceNotificationHostWireTests` (1 passed)
- `SimCodecTests` (414 passed)

The full local `Eve.Host.Tests` shard was attempted without a database as required: 487 passed, 101 failed, 227 skipped (815 total). The inspected failures assert that `EVE_PG_URL` is configured (including `MiningLedgerTests.cs:48,106` and `ShipCosmeticsMgrRpcServiceTests.cs:20,105`). No PostgreSQL database was used. The graphical V24.01 client was not launched.

## Handoff state

`git fetch origin main && git merge origin/main` completed with no incoming commits. Post-merge Rust targeting and simlink fixture tests, Host service notification wire test, and .NET sim codec tests passed; the Host shard produced the same database-lane failures listed above. Commit `43c657ec` was pushed to `main`. Push-triggered `ci.yml` run 40416414193 failed in the Rust workspace and Windows Rust workspace shards: `sim::tests::tether_host_policy_dispatches_to_the_real_scene_and_transitions_return_to_the_host` asserted protocol version 81 after the wire version advanced to 82. The message-kind 81 assertion remains correct. The version expectation is now 82; `cargo +1.94.1 test -p eve-space sim::tests::tether_host_policy_dispatches_to_the_real_scene_and_transitions_return_to_the_host -- --exact` passed after fetch/merge, and Rust format plus workspace clippy pass. Follow-up commit `e8fcd440` is on `main`; push-triggered `ci.yml` run 40418600007 completed successfully with no failed tests or jobs. Preserve the unrelated untracked `dotnet/tests/Eve.Host.Tests/MarketNotificationHostWireTests.cs`; it predates this increment and is not part of this change.
