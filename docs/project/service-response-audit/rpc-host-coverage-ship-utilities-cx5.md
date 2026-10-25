# Ship utility RPC coverage handoff: cx5

## Implemented in this increment

Added `ShipcasterRpcHostTests.Shipcaster_read_rpcs_keep_client_shapes_through_host_dispatch`. It sends the three requested read calls through the socket-backed `HostServer` and checks the transport `machoVersion`, landing-pad `ObjectEx` class/fields, faction filtering, and unique sorted faction IDs. The fixture represents explicit provider state; it does not claim a production shipcaster state source.

Client callers and consumption:
- `eve/client/script/parklife/shipcasterSvc.py:22-29` forwards `GetFactionLandingPads(factionID)`, `GetAllLandingPads()`, and `GetFactionsWithShipcaster()` directly to their remote services; these wrappers do not reshape their replies.
- EveJS handlers: `server/src/services/shipcaster/shipCasterLandingPadMgrService.js:13-27` filter/list pads and `server/src/services/shipcaster/shipCasterLauncherMgrService.js:13-16` returns factions.
- EveJS payload construction: `server/src/services/shipcaster/shipcasterRuntime.js:139-174` makes each `LandingPadData` Ex1 with item, system, faction, built state, link timestamp, and disruption state; list methods sort by faction, system, item. EveJS stores the runtime list in memory; its test seeding helpers are not production state.

## Remaining requested coverage

- `ApplySkinToShip`: client calls and ignores the return at `eve/client/script/ui/cosmetics/ship/shipUtil.py:27`. EveJS validates ownership/active ship, persists application, announces ship state, and returns None (`shipCosmeticsMgrService.js:308-349`). eve.rs has the Host service and persistence callback at `dotnet/src/Eve.Host/ShipCosmeticsMgrRpcService.cs:160-188`; its current integration coverage requires PostgreSQL.
- `GMExpireSkinLicense`: client dev menu calls at `devMenuFunctions.py:33-35`; EveJS expires for the active character and returns None (`shipCosmeticsMgrService.js:381-389`). Host implementation is at `ShipCosmeticsMgrRpcService.cs:146-158`.
- `GiveSkin`: client uses duration, permanent, and single-use variants at `devMenuFunctions.py:8-27`; EveJS persists duration/single-use options and returns None (`shipCosmeticsMgrService.js:351-365`). Host implementation is at `ShipCosmeticsMgrRpcService.cs:112-135`.
- `RemoveSkin`: client selects corporation or character licensee and calls at `devMenuFunctions.py:77-84`; EveJS enforces ownership, revokes, and returns None (`shipCosmeticsMgrService.js:367-379`). Host implementation is at `ShipCosmeticsMgrRpcService.cs:137-144`.
- `GetItemKillCountPlayer`: all client callers are `menusvc.py:869`, `baseHangarBehaviours.py:94`, and `panelOverview.py:275`. Hangar rendering clamps display to 999 and the overview hides zero (`baseHangarBehaviours.py:94-95`, `panelOverview.py:275-276`); EveJS reads the persisted player count and wraps it in a five-minute client cache (`shipKillCounterService.js:19-31`). eve.rs validates one positive item ID, reads the persisted table, and caps at 999 (`ClientStartupServices.cs:222-243`); existing persistence test is not a Host wire test.
- `SetShipStance`: the inflight caller sends ship and stance IDs (`shipstance.py:29-32`); fitting UI derives the current stance from inventory and uses it to render buttons (`fittingCenter.py:252-258`). EveJS mutates stance and returns the old stance ID (`shipStanceMgrService.js:35-49`). eve.rs returns the old stance, notifies `OnStanceActive` after a change, and refreshes the in-space ship (`GameRpcServices.cs:354-372`).

## Verification

The focused test command first failed because NuGet assets were absent. A restore-enabled run overlapped other Host test builds in the shared worktree and produced file-lock warnings; that run was stopped. No test result was obtained. No PostgreSQL database was used. The requested existing handoff `rpc-host-coverage-cx5.md` could not be edited: Serena's `replace_content` and `replace_in_files` both returned `No language servers available in the manager`; this supplemental note preserves the current findings.
