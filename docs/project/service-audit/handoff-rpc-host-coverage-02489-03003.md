# RPC Host coverage handoff: 02489, 02490, 02768-02770, 03003

## Current result

A real Host socket regression now covers all six rows. Ship-stance returns the established no-character error shape in the no-session case. CanCharacterJump returns the client-consumed False boolean when no eligible-character provider is configured. The two shipcaster travel methods and void-space exit return Host ErrorResponse packets without pretending a jump succeeded.

Host composition registers ship-stance and void-space in CoreServices; shipcaster services are registered from AgentRpc.cs:110. That production registration currently supplies no landing-pad, eligibility, or travel callbacks.

This is RPC coverage, not a playable shipcaster travel implementation. EVE University describes a working one-way Interstellar Shipcaster for enlisted militia pilots, with destinations driven by active beacons and cycling destinations. That state and travel transition are not owned by the current Host, so the task must remain open for a successful authoritative jump flow. Source: https://wiki.eveuniversity.org/Shipcaster

## V24.01 client call and consumption evidence

- **RPC-02489 SetShipStance:** eve/client/script/ui/inflight/shipstance.py:29-32 calls with (ship_id, stance_id) and discards the RPC return. ship_stance_button.py:53-55, fittingController.py:429-431, station/base.py:268-270, and hangarView.py:707-709 consume OnStanceActive(shipID, stanceID) to update controls and ship visuals. The server returns the previous stance ID; the visible transition is notification-driven.
- **RPC-02490 CmdJumpThroughShipcaster:** eve/client/script/parklife/shipcasterSvc.py:17-19 calls with (shipcasterID, toSolarsystemID, toLandingPadID) inside PerformSessionChange('jump', ...). menusvc.py:3069-3071 and movementFunctions.py:601-604 lead from the menu to that call. The session manager consumes a successful session transition; there is no ordinary data reply consumer.
- **RPC-02768 and RPC-02769 CanCharacterJump:** shipcasterSvc.py:31-32 calls with no explicit arguments and returns the RPC value directly to its caller. These two coverage rows describe the same wire method and are covered by the same Host dispatch assertion. Whole-tree search found no additional direct Python reference to CanJumpStarterShipcaster.
- **RPC-02770 CmdJumpThroughStarterShipcaster:** shipcasterSvc.py:34-36 calls with (shipcasterID) inside PerformSessionChange('jump', ...); menusvc.py:3074-3075 and movementFunctions.py:607-616 establish the UI path and confirmation before the RPC.
- **RPC-03003 VoidSpaceEndGateActivation:** movementFunctions.py:587-589 calls with (itemID) inside PerformSessionChange('voidToKnownJump', ...). The client expects the session transition rather than a returned data object.

## EveJS v0.12.9 comparison

- server/src/services/ship/shipStanceMgrService.js:35-49 returns the prior stance after the runtime mutation; shipStanceRuntime.js:424-572 owns the update.
- server/src/services/shipcaster/shipcasterTravelMgrService.js:23-33 rejects the travel call with CustomNotify when no active destination exists.
- server/src/services/shipcaster/starterShipcasterTravelMgrService.js:23-29 returns eligibility; shipcasterRuntime.js:210-213 checks the explicit eligible-character set. Lines 31-37 reject a starter jump when no destination is available.
- server/src/services/activity/voidSpaceMgrService.js:82-88 rejects exit-gate activation when there is no active Void Space origin state.

The current host test compares the no-destination rejection behavior and false eligibility wire shape with this reference. It does not exercise the EVE University successful shipcaster mechanics or a graphical client.
