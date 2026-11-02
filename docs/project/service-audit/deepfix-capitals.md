# Capital travel deep-fix handoff

## Scope

This pass followed the repeatable travel chain for jump-capable ships: jumping to a cyno, opening and using a fleet bridge, and a Black Ops conduit jump. Client callers and consumers were traced across the V24.01 source. Fighter launch/ability behavior and superweapon firing were not audited in this pass.

## Client path and compatibility decisions

- `eve/client/script/ui/shared/fleet/fleetbroadcastexports.py:88-99` offers Jump To, Bridge To, and Conduit Jump from a fleet beacon. `eve/client/script/ui/services/menusvc.py:2628-2634` forwards Bridge To; `eve/client/script/parklife/cynoTravelSvc.py:56-62` calls `CmdBridgeToFleetModuleBeacon(charID, beaconID, solarsystemID)` directly. Its user errors therefore need to come back as RPC errors.
- `cynoTravelSvc.py:83-93` gets the active bridge from fleet state and wraps `CmdJumpThroughFleet(otherCharID, otherShipID, beaconID, solarsystemID)` in a session change. `eve/client/script/parklife/fleetSvc.py:199-205,241-245` stores and reads the bridge only after `OnBridgeModeChange`; `cynoTravelSvc.py:18-23` consumes `OnGroupJumpAnchorActivated` as a player-facing dialog.
- EveJS `server/src/services/ship/beyonceService.js:2881-2962` activates the online portal module, maps capacitor/fuel/already-active failures to player notices, sets bridge mode, then notifies the group-jump anchor. Its passenger validation at `:1550-1714` checks bridge identity, fleet, own-ship use, 2,500 m range, eligible hull, fatigue and fuel.
- The V24.01 map estimate truncates the positive fuel product to an integer (`eve/client/script/ui/inflight/capitalnavigation.py:216-229`; marker tooltip uses `max(long(...), 1)` at `eve/client/script/ui/shared/mapView/markers/markerTooltipUtil.py:61-68`). EveJS `beyonceService.js:1075-1157` normalizes Dogma float noise and floors jump-drive fuel; `services/_shared/floatPrecision.js:96-128,147-153` defines that normalization. This pass follows the client’s truncation for the server's integer item debit.

## Defects fixed

1. Bridge activation and passenger fuel were checked before simulation, then debited afterward. Another transaction could spend the same fuel in that gap; conduit jumps had the same race and still returned success after a failed debit. Fuel and fatigue now reserve atomically before the sim transition; explicit sim refusal restores both in one transaction. See `SpaceBridges.cs:140-170,259-280`, `SpaceConduits.cs:146-177`, and `CharacterShips.Jump.cs:69-115`.
2. Jump-drive economizers, portal generators, and covert conduit generators were selected from fitted SDE types without checking their persisted online state. Fitted-module reads now carry item IDs and the travel services require `ItemState.Online`. See `CharacterShips.Jump.cs:16-32`, `SpaceJumpDrive.cs:276-292`, `SpaceBridges.cs:98-110`, and `SpaceConduits.cs:77-91`. EveJS's `beyonceService.js:982-1028` independently filters effectively-online portal modules.
3. Bridge passengers could use the portal from any distance or name the bridge ship as their passenger ship. The Host now refuses self-passage and checks the passenger against the bridge ship's live position before spending fuel (`SpaceBridges.cs:204-226`). EVE University describes jump activation cooldowns and the bridge passenger rules in [Jump Drives and Cynosural Fields](https://wiki.eveuniversity.org/Jump_Drives_and_Cynosural_Fields).
4. Conduit jumps omitted the anchor pilot's jump activation cooldown check. The anchor is now refused before calculating or reserving fuel when that cooldown remains (`SpaceConduits.cs:99-101`).
5. Jump-drive fuel rounded up and overcharged fractional results. It now normalizes to six decimal places with midpoint-away-from-zero, then floors to whole isotopes (`JumpDriveRules.cs:124-126`).

## Regression evidence

- `CynoJumpHostTests` covers the atomic activation/passenger/conduit fuel reservations, rollback on sim refusal, offline module rejection, active bridge rejection, bridge distance and self-passage, conduit cooldown, and a 1.0001 LY fractional-fuel jump.
- A temporary build with the prior conduit debit order and fuel ceiling made the new tests fail as intended: the competing conduit debit succeeded; the fractional jump left 46,999 rather than the expected 47,000 isotopes.
- Final local verification used database `eve_p5` through `tools/dev/db-lock.sh`: `dotnet build dotnet/tests/Eve.Services.Space.Tests -p:UseSharedCompilation=false -m:1` succeeded; the focused filter `CynoJumpHostTests|JumpDriveRulesTests` passed 49 tests, 0 skipped. No graphical client session was used.

## Remaining audit scope

Fighter launch/recall/ability end-to-end behavior and superweapon activation/effect behavior still need a dedicated pass.
