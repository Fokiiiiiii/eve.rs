# Shipcaster and structure jump bridge RPC audit

Date: 2026-11-14
Client build: V24.01, build 3396210
Reference: `/home/user/evejs-261108.1`

## Registration and prior behavior

| Service | Host registration | Handler before this change | Current status |
|---|---|---|---|
| `shipCasterLandingPadMgr` | `CharacterServicesComposition.CreateShipcasterServices` -> `ShipcasterRpcService` | `GetAllLandingPads` and `GetFactionLandingPads(factionID)` returned an empty list because the production composition supplied `() => []`. The service can serialize a supplied row as `shipcaster.landingPadData.LandingPadData`. | The empty provider was removed. Missing authority now returns the service's `CustomNotify`; the calls remain HOLLOW because no authoritative deployed-pad source exists. |
| `shipCasterLauncherMgr` | Same composition -> `ShipcasterRpcService` | `GetFactionsWithShipcaster()` returned an empty list because production supplied `() => []`. | The empty provider was removed. Missing authority now returns `CustomNotify`; the call remains HOLLOW. |
| `shipcasterTravelMgr` | Same composition -> `ShipcasterRpcService` | `CmdJumpThroughShipcaster(shipcasterID, toSolarsystemID, toLandingPadID)` had no production travel callback and returned EveJS's refusal notification. | Still HOLLOW. V24 needs live launcher schedule and destination item IDs, which no current authoritative Host source provides. |
| `starterShipcasterTravelMgr` | Same composition -> `ShipcasterRpcService` | `CanCharacterJump()` returned `False` from a log-once fallback; `CmdJumpThroughStarterShipcaster(shipcasterID)` had no success callback and returned EveJS's refusal notification. | Still HOLLOW. Eligibility can be persisted, but the server lacks the installed starter launcher item and its authoritative `destinationID` / `landingPadID` component attributes. |
| `structureJumpBridgeMgr` | `CharacterServicesComposition.StructureRpc` -> `CreateStructureJumpBridgeTravelService` -> `StructureTravelRpcService` | `CmdJumpThroughStructureStargate(structureID)` had no callback and refused. The two getters already read the persisted structure directory. | The command now calls the persisted Ansiblex / inventory / wallet / space authority. PostgreSQL-backed Host E2E coverage was added. |

The RPC names and argument positions come from the service wrappers in `eve/client/script/parklife/shipcasterSvc.py:18-37`. The shipcaster E2E service test also exercises Host dispatch and the six-field ObjectEx row shape. No row is marked `IMPLEMENTED-TESTED` for shipcasters: the test fixture that supplies rows is not a production world authority.

## V24.01 client contract

### Dynamic shipcasters

- `shipcasterSvc.py:18-20` sends `CmdJumpThroughShipcaster(shipcasterID, toSolarsystemID, toLandingPadID)` as a `PerformSessionChange('jump', ...)`; lines 23-30 expose the two pad queries and faction query; lines 32-37 send zero-argument `CanCharacterJump()` and `CmdJumpThroughStarterShipcaster(shipcasterID)`.
- `shipcaster/client/components/shipcasterLauncher.py:36-46` reads the launcher faction and extra linked factions. Lines 49-81 expose the active target system and landing-pad item ID. Lines 126-186 consume the server-published `component_shipcaster_linked_targets` timings and decide whether a target is active. Lines 233-251 reject a regular jump while charging or for the wrong faction. These values are runtime component/slim state, not values derivable from a type ID alone.
- `shipcaster/client/components/starterShipCaster.py:51-64` always offers the jump and reads its destination system and landing-pad item ID from component attributes. `shipcasterUtil.py:38-53` obtains those targets from the current space component. The travel RPC itself only receives the starter launcher item ID.
- The client asks for normal pad lists from faction-warzone dashboards: `fwwarzone/client/dashboard/shipCasterVisualization.py:57-65`, `warzoneMapPanel.py:398-425`, and `warzoneInfoSideCont.py:229`. The list row is consumed as a pad with `isLinked`, `isDisrupted`, and `solarSystemID`; `ShipcasterRpcService.ToWire` preserves the six-field `LandingPadData` positional payload.
- `menusvc.py:1749-1762` chooses the starter or normal RPC from the selected component and shows the component's current target. Lines 3070-3076 close to the target before invoking the RPC. `menuSvcExtras/movementFunctions.py:602-617` confirms the normal call arguments and the starter confirmation dialog before its one-argument call. `shipcasterConst.py:6-17` gives a maximum of three normal links, 60-second active/idle intervals, and a 5,000 m client interaction range.

The V24 client evidence supplies signatures and runtime component behavior, but does not provide a server-side target-selection rule, the deployed item IDs, or the production lifecycle that writes the component schedule. The full client tree search found no other callsites that fill this gap.

### Structure jump bridge

- `parklife/autopilot.py:254-256,348-358` selects the `structureJumpBridgeMgr.CmdJumpThroughStructureStargate(destID)` session change for `groupUpwellJumpGate`; ordinary stargates use their separate gate RPC. A failed bridge session change stops autopilot. `ui/services/menusvc.py:2095-2109` also calls the same one-argument RPC and only removes the waypoint on success.
- `ui/services/structure/structureDirectory.py:175-177` calls `GetJbStructureDestination(structureID)`. `ui/shared/info/infosvc.py:821-833` calls `GetLinkedStructure(itemID)` and consumes `[destinationStructureID, destinationSolarSystemID]`.
- `StructureTravelRpcService` returns the persisted getter values as a `PyInt`, `[PyInt, PyInt]`, or `None`. On travel, the new Host backend checks the persisted reciprocal Ansiblex pair, source and destination service state, ACL, proximity conflicts, local position, warp disruption, range, mass, fuel, and wallet balance before submitting simulation command 35. On acceptance, fuel and wallet changes commit together, the wallet change notifications are emitted, and the existing space arrival path persists the ship/system transition. The PostgreSQL-backed Host E2E asserts the session-change tuple, notification order, fuel, both wallet accounts, journal entries, same-session replay, concurrent duplicate suppression, and arrival persistence.

EveJS comparison: `structureJumpBridgeMgrService.js:1713-1883` has the same Ansiblex validation and formula path, and persists a pending settlement before resuming it. The eve.rs path serializes same-session requests and replays the accepted session-change result while the pilot remains in the source system, with fuel and wallet settlement in one PostgreSQL transaction. It does not persist an in-flight operation for process recovery between simulation acceptance and settlement commit; an injected process crash in that narrow window is not covered. EveJS also supports legacy POS bridges in this handler; the V24 client callsites above route `groupUpwellJumpGate`, so no legacy POS success path was added here.

## EveJS shipcaster comparison

- `shipCasterLandingPadMgrService.js:13-26` and `shipCasterLauncherMgrService.js:13-16` read lists from `shipcasterRuntime` and build the six-field `LandingPadData` payload.
- `shipcasterRuntime.js:30-33` stores pads, factions, and starter-eligible character IDs in process-local `Map` / `Set` state. Lines 103-178 normalize and serialize the data. Lines 180-213 expose test-only seed functions and check the seeded eligibility set; lines 215-220 clear the process-local runtime. There is no production SDE or persistent-store bootstrap in these methods.
- `shipcasterTravelMgrService.js:23-33` parses all three normal travel arguments, records a rejection audit event, then throws `CustomNotify("No active shipcaster destination is available.")`. `starterShipcasterTravelMgrService.js:23-36` returns the test-seeded eligibility boolean but always rejects starter travel with `CustomNotify("No starter shipcaster destination is available.")`.

That executable reference confirms wire shapes and refusal behavior, but it does not define a successful shipcaster travel flow to port. Promoting the test seed hooks into production would create a second, unwritten state owner, so it was not done.

Reference execution: a direct Node handler check against EveJS v0.12.9 passed for the default empty list, a seeded six-field ObjectEx row, faction deduplication, seeded/default starter eligibility, and both `CustomNotify` travel refusals. The checkout has no repo-owned `*.test.js` / `*.spec.js` files outside dependencies and `server/package.json` has no test script. No executable EveJS jump-bridge E2E fixture was available; its successful handler path was source-traced at `structureJumpBridgeMgrService.js:1713-1883`.

## SDE and official source check

The pinned SDE snapshot is `3396210` under `.cache/sde/eve-online-static-data-3396210-jsonl`. It contains shipcaster type records in `types.jsonl` and `typeDogma.jsonl`; the launcher type dogma records contain only the generic maximum-velocity attribute. Available map tables include systems, stargates, stars, planets, moons, asteroid belts, and secondary suns. There is no `mapDenormalize` or shipcaster deployment table in this SDE export, and no deployed launcher/pad item IDs or starter component target attributes were found. The downloaded SDE remains ignored and is not committed.

CCP's [Introducing the Interstellar Shipcaster](https://www.eveonline.com/news/view/introducing-the-interstellar-shipcaster) describes the normal network as dynamic: up to three destination beacons are linked, with site selection considering activity, location, and strategic advantage. CCP's [Cradle of War expansion notes](https://www.eveonline.com/news/view/cradle-of-war-expansion) describes the rookie one-way journey and its four origin-to-empire routes. Those sources do not give the live target schedule or the item IDs and component values required by the client calls.

## Coverage and outstanding evidence

Shipcaster coverage rows remain HOLLOW. Returning empty production lists, `False`, or an unconditional travel refusal is not counted as a real implementation. The production getters now expose missing authority as an error instead of a successful empty result. Completing these calls needs an authoritative source for deployed pad/launcher items, build/disruption/link state, active schedules and starter component destinations, plus a persistent one-time starter-use rule and an executable successful travel reference or verified client trace.

The structure jump bridge command is covered by a PostgreSQL-backed Host flow; details are recorded in `StructureJumpBridgeTravelHostWireTests.cs`. No graphical V24.01 client run is claimed.
