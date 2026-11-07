# Universe audit fixes

This note records the findings in `/home/user/reports/90-audit-eve-universe-r14.md` after checking the V24.01 client, EveJS, the current Host path and the relevant tests. The codebase-memory MCP and Serena tools were unavailable, so repository inspection used targeted local reads and searches.

## P2 — jump distance used the wrong light-year conversion

**Confirmed.** `SolarSystemCatalog.LightYearsBetween` divides the systems' coordinate distance by `MetersPerLightYear`. Before this fix, the value was `9,460,730,472,580,800`, while the V24.01 client defines `LIGHTYEAR = 9,460,000,000,000,000` in `appConst.py:40`. Its `evePathfinder/lightYearDistances.py:7,40-41` uses that value for system distances. `eve/client/script/ui/util/uix.py:735-754` also divides system-coordinate distance by the same constant. The map tooltip uses `GetLightYearDistanceFinder` (`eve/client/script/ui/shared/mapView/markers/markerTooltipUtil.py:23-25`), and the map's jump-range markers use that finder (`eve/client/script/ui/shared/mapView/mapViewWorld.py:752-762`). The client submits jump actions through `cynoTravelSvc.py:48-54` for structure beacons and through the fleet beacon commands documented in `cynoTravelSvc.py:35-46`.

The [EVE Developer Documentation Map Data guide](https://developers.eveonline.com/docs/guides/map-data/#jump-drives) also specifies exactly `9,460,000,000,000,000.0` metres per light-year for jump distance and warns that another value can make impossible jumps appear reachable. The guide labels itself as crowdsourced; the matching V24.01 client constant and calculation are the primary evidence for this fix.

In eve.rs, `SolarSystemCatalog.LightYearsBetween` feeds the range, fuel and fatigue calculations in `SpaceJumpDrive.JumpToTargetAsync` (`dotnet/src/Eve.Services.Space/SpaceJumpDrive.cs:146-175`). The same helper is used by `SpaceBridges` (`SpaceBridges.cs:129-132`), `SpaceConduits` (`SpaceConduits.cs:103-110`), and character-selection alternatives (`Eve.Services.CharacterSelect/SystemPopulation.cs:56-65`). The actual beacon RPC handlers dispatch to `SpaceJumpDrive` in `BeyonceService.cs:750-767`.

EveJS `server/src/services/ship/beyonceService.js:169,815-832` has the old larger divisor too; its jump check at `1130-1139` additionally accepts distances up to `jumpRangeLy + 0.000001`. I executed the distance helper extracted from that source with synthetic systems `3.50005` client/CCP light-years apart: EveJS returned `3.499779757594951`, which passes its 3.5-light-year check. This confirms the audit's numerical issue and shows why the EveJS result is not retained here: the V24.01 client and CCP distance rule set the expected value.

**Fix.** `SolarSystemCatalog.MetersPerLightYear` now uses the V24.01 value. The shared helper therefore supplies consistent distances to jump-drive, bridge, conduit and character-selection calculations.

**Regression evidence.** Before the production change, `SolarSystemDistanceTests.Light_year_distances_use_the_v24_jump_distance_scale` failed with expected `1.0` and actual `0.99992278898728626`. Before the production change, `CynoJumpHostTests.A_jump_just_beyond_the_v24_range_is_refused_without_consuming_fuel_or_setting_fatigue` failed because no refusal was thrown. Both boundary cases now call the registered `BeyonceService` through the test registry with the V24 `CmdJumpToFleetModuleBeacon` argument order; coverage includes a jump at the range limit, one slightly beyond it, fuel/fatigue/simulation state on refusal, and exact one-light-year conversion.

## Findings skipped

None. The report contains one finding, and it was confirmed.

## Verification

**Checked:** the Universe test project passed 68 tests and skipped one client-export comparison; `CynoJumpHostTests` passed 39 tests on `eve_p7`; `PopulationCapTests` passed 12 tests on `eve_p7`; the focused jump-boundary tests passed 2 tests on `eve_p7`. The skipped `ClientStaticDataTests.The_export_and_the_embedded_copies_give_the_same_answers` requires `EVE_CLIENT_SDE_DIR`; that variable was unset and no V24.01 client export was available in this worktree. The extracted EveJS distance helper was executed as described above.

**Not checked:** the graphical V24.01 client, a live MITM jump, and the complete EveJS jump RPC/session flow. The EveJS helper was executed, but its full service flow was not. Release builds with warnings treated as errors and the push-triggered CI gate remain to be recorded.
