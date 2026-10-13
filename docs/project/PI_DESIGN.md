# Planetary Interaction (PI): design

Status: implemented (M1 domain, M2 service and store, slice 2 customs and launch cans); the live walk on lane eve_p2 passes (see section 10). Client truth: V24.01 eve-logic build 3396210.
Work track: eve.rs `Eve.Planets` (domain), `Eve.Planets.Pg` (PostgreSQL, schema `planets`), `planetMgr` RPC in `Eve.Services.Planets`.

## 1. Inventory (before this work)

- `planetMgr` existed only as a stub: `dotnet/src/Eve.Host/PlanetMgrRpcService.cs` and `dotnet/src/Eve.Services.Core/CompatibilityRpcServices.cs:156`. It read a GameStore key `planetRuntimeState` that nothing wrote. Answers: `GetPlanetsForChar` (empty), `GetMyLaunchesDetails` (empty), `DeleteLaunch` (no-op True).
- `planetOrbitalRegistryBroker` is OPEN in `docs/project/TODO.md:865` (POCO / orbital registry, separate track).
- `docs/reference/spec/services.tsv:448-451` lists planetMgr methods only.
- No PI code in EveJS v0.12.9 (`git grep planet` finds only content-pack booster data). EveJS is not a reference for PI.
- No PI data in the eve.rs database except the SDE tables.

## 2. Client contract (eve-logic, V24.01)

Server side is not in the snapshot. Shapes come from client callsites.

| Call | Client callsite | Use |
|---|---|---|
| `planetMgr.GetPlanetsForChar()` | `eve/client/script/environment/planetSvc.py:65-68` | rows `solarSystemID, planetID, typeID, numberOfPins, celestialIndex` (`planetSvc.py:163`) |
| `planetMgr.GetMyLaunchesDetails()` | `eve/client/script/ui/shared/planet/planetUISvc.py:172` | launches rowset |
| `planetMgr.DeleteLaunch(launchID)` | `eve/client/script/ui/shared/neocom/journal.py:464` | |
| `eveMoniker.GetPlanet(planetID)` = bound `planetMgr(planetID)` | `eve/common/script/net/eveMoniker.py:211`, `clientPlanet.py:34` | per-planet object |
| `GetPlanetInfo()` | `clientPlanet.py:83-93` | solarSystemID, planetTypeID, radius, celestialIndex, plus colony (`currentSimTime`, `pins`, `links`, `routes`, `level`) when the character has one |
| `GetFullNetworkForOwner(planetID, charID)` | `planetSvc.py:106` | colony snapshot of another owner |
| `GetCommandPinsForPlanet`, `GetExtractorsForPlanet` | `planetSvc.py:116,126` | |
| `UserUpdateNetwork(commands)` | `clientPlanet.py:180` | command stream, returns the colony (`_PrimeColony`, `:185`) |
| `UserAbandonPlanet()` | `clientPlanet.py:174` | command center removed |
| `UserLaunchCommodities(pinID, {type: qty})` | `clientPlanet.py:412` | returns `lastLaunchTime` |
| `UserTransferCommodities(path, commodities)` | `clientPlanet.py:448` | returns `(simTime, sourceRunTime)` |
| `GetProgramResultInfo(pinID, typeID, heads, headRadius)` | `clientPlanet.py:525` | `(qtyToDistribute, cycleTime, numCycles)` |
| `GetResourceData(info)` | `clientPlanet.py:734` | native SH buffer: see deviation D1 |

Command stream (`eve/common/script/planet/commandStream.py:21-95, 185-231`): 1 CreatePin(pinID,typeID,lat,lon); 2 RemovePin(pinID); 3 CreateLink(e1,e2,level); 4 RemoveLink(e1,e2); 5 SetLinkLevel(e1,e2,level); 6 CreateRoute(routeID,path,typeID,qty); 7 RemoveRoute(routeID); 8 SetSchematic(pinID,schematicID); 9 UpgradeCommandCenter(pinID,level); 10 AddExtractorHead(pinID,headID,lat,lon); 11 KillExtractorHead(pinID,headID); 12 MoveExtractorHead(pinID,headID,lat,lon); 13 InstallProgram(pinID,typeID,headRadius). Temporary ids on the client are tuples `(1,n)` pins and `(2,n)` routes (`clientColony.py:35-41`); the server assigns real ids.

Client validation the server mirrors (`baseColony.py` Validate*, `clientColony.py`): CPU and power supply against usage per pin and per link; at most 10 heads (`ECU_MAX_HEADS`); head within area of influence; storage routes only to consumers; producer output limit; link bandwidth; `LinkAlreadyExists`; `CannotBuildMultipleCommandPins`; `CannotManagePlanetWithoutCommandCenter`; planet-type restriction (`attributePlanetRestriction`, 1632); program radius in `[0.01, 0.05]`; link level at most 10.

## 3. SDE facts used (build 3396210, tables in the `sde` schema)

- Groups: 1027 Command Centers (8 types), 1063 Extractor Control Units (8 types), 1028 Processors (18), 1029 Storage Facilities (8), 1030 Spaceports/Launchpads (8), 1036 Planetary Links (1), 1025 Orbital Infrastructure (POCO, 0 published). Group 1026 "Extractors" (40 types, deposit model, `harvesterType` 709) is OBSOLETE: `eve/client/script/ui/shared/planet/pinContainers/containersByPinType.py:15` maps it to `ObsoletePinContainer`. Not implemented.
- Dogma attributes: CpuLoad 49, PowerLoad 15, CpuOutput 48, PowerOutput 11, PlanetRestriction 1632, PinCycleTime 1643, PinExtractionQuantity 1642, LogisticalCapacity 1631, PowerLoadPerKm 1633, CpuLoadPerKm 1634, PowerLoadLevelModifier 1636, CpuLoadLevelModifier 1635, EcuAreaOfInfluence 1689, EcuDecayFactor 1683, EcuMaxVolume 1684, EcuOverlapFactor 1685, EcuNoiseFactor 1687, ExtractorHeadPower 1691, ExtractorHeadCPU 1690, SpecialCommandCenterHoldCapacity 1646. Constants: `eve-logic/dogma/const.py`.
- Command center (e.g. 2254 Temperate): PowerOutput 11 = 6000, CpuOutput 48 = 1675 (level 0). Per-level values come from `planetCommon.py:149`.
- ECU (e.g. 2848 Barren): PowerLoad 2600, CpuLoad 400, PinCycleTime 300, PinExtractionQuantity 1000, ExtractorHeadCPU 110, ExtractorHeadPower 550, EcuAreaOfInfluence etc. from the dogma table.
- Link 2280: LogisticalCapacity 1250, PowerLoadPerKm 0.15, CpuLoadPerKm 0.2, PowerLoadLevelModifier 1.2, CpuLoadLevelModifier 1.4, PowerLoad 10, CpuLoad 15. Matches the wiki (p(l)=10+0.15*l, c(l)=15+0.2*l for one km).
- Schematics: table `sde.planetSchematics` (68 rows), doc: `pins`, `types[{_key, isInput, quantity}]`, `cycleTime` (seconds).
- Capacity is the type record's `capacity` field, not attribute 38 (whose default is 0): storage facility 12000 m3, launchpad 10000 m3, command center 500 m3 (`sde.types`, e.g. keys 2541, 2544, 2524).
- Missing type attributes take the attribute default (`sde.dogmaAttributes.defaultValue`), as the client does (`colonyData.py:655-656`). ECU 2848 has no decay, noise, area, overlap or max-volume entries: defaults 0.012, 0.8, 0.3, 0.5, 9.2 apply.
- `sde.planetResources` holds sovereignty power and workforce, not PI deposits (`eve-client-sde/README.md`). Not used.

## 4. Formulas (client source, ported)

All numbers are per the file named. Time is in milliseconds (the client's `const.SEC`, `const.HOUR`, `const.MIN`).

- Link bandwidth: `LogisticalCapacity * 2^level` m3/h (`baseLink.py:106-114`).
- Link CPU / power: `base + ceil(perKm * km * (level+1)^modifier)` (`planetCommon.py:58-67`), `km = distance/1000`, distance = great-circle arc on the planet radius (`surfacePoint.py:118-119`, `baseLink.py:94-98`).
- Route bandwidth: `GetBandwidth(volume*qty, cycleTime) = volume*qty*HOUR/cycleTime` (`planetCommon.py:110`), cycle from the source producer or the consumer for storage-to-consumer (`baseRoute.py:76-97`).
- Expedited transfer time: `ceil(max(5 MIN, volume/bandwidth*HOUR))` (`planetCommon.py:84-86`).
- Command center by level 0..5: power 6000, 9000, 12000, 15000, 17000, 19000; CPU 1675, 7057, 12136, 17215, 21315, 25415; upgrade cost 0, 580000, 1510000, 2710000, 4210000, 6310000 ISK; upgrade cost = cost(to) - cost(from) (`planetCommon.py:149-169`). Upgrade needs the Command Center Upgrade skill (2505) at the target level (`clientColony.py:92-98`).
- Planets per character: 1 + Interplanetary Consolidation (2495) level, at most 6 (wiki, see sources; client `clientColony.py:62-68`).
- ECU program: `GetProgramOutput(t)` (`ecuPin.py:302-329`): `barWidth = cycleTime/SEC/900`; `cycleNum = max((dt+SEC)//cycleTime - 1, 0)`; `t = (cycleNum+0.5)*barWidth`; `decay = base/(1+t*decayFactor)`; `phase = base^0.7`; `sinStuff = max(0, (cos(phase+t/12)+cos(phase/2+t/5)+cos(t/2))/3)`; `barHeight = decay*(1+noise*sinStuff)`; output `int(barWidth*barHeight)`. `decayFactor` = 1683 and `noise` = 1687 from the ECU type.
- ECU max output: `int((noise+1)*qty)*cycleTime/SEC/900` (`ecuPin.py:291-297`).
- Program length and cycle: `L = (r-0.01)/0.04*335+1`; `cycle(L) = 0.25*2^max(0, floor(log2(L/25))+1)` hours; `numCycles = int(L/cycle)`; `cycleTime_ms = int(cycle*HOUR)` (`planetCommon.py:198-203`, `baseColony.py:978-982`). Wiki: program length 1 hour to 14 days, cycle doubling at 25 h / 50 h / 4d4h / 8d8h to 30 min / 1 h / 2 h / 4 h.
- Heads: max 10 per ECU; each head 110 CPU and 550 PG (SDE 1690/1691, wiki). Head placement inside `EcuAreaOfInfluence` (`baseColony.py:705-737`).
- Extractor (program) value: `maxValue = ecuMaxVolume * sum(valueByIndex)` with overlap factors (`baseColony.py:897-984`). The value per head comes from the native resource harmonic: deviation D1.
- Processor: consumes all demands at the start of a cycle, produces the schematic's outputs at its end (`processPin.py:84-107`, `ProcessPin.SetSchematic` `:125-144`). Cycle = `schematic.cycleTime*SEC`.
- Storage / command center / launchpad: `GetCapacity` from the type (`storagePin.py:18-19`): the type's capacity (see section 3).
- Launchpad launch: cooldown 60 s (`spaceportPin.py:28-29`). Commodities leave the pin into a launch record.
- Command center: 0 imports; its power and CPU output depends on its level (`commandPin.py:19-28`).
- ISK: a non-command pin costs its base price, checked against the wallet (`clientColony.py:52-60`); the server charges it on submit (see D4).

## 5. Decisions (client silent or ambiguous)

- Lazy simulation: the colony state is stored with `currentSimTime`; each read or write advances it to "now" with the same algorithm, deterministic from the stored state. The client does the same locally. No periodic job is needed for correctness.
- Pin ids: the command center uses the hold item's id (the client sends it, `clientPlanet.py:208`); other pins use a new id from the item store (`PgItemStore.CreateAsync`).
- Command center removal ends the colony (client `UserAbandonPlanet`, `clientPlanet.py:172-174`). Pins and links are deleted in the same transaction. The command center item is destroyed; no refund. Basis: none in the client; wiki silent. Recorded as decision D3.
- Output that is not stored is lost (wiki, Planetary Interaction page: "if it isn't stored it is lost").
- Uncollected launch: deleted when its time is up (wiki). Launch expiry: 5 days (wiki war section; decision D5).

## 6. Deviations (recorded in DEVIATIONS.md)

- D1 Resource distribution. The value of a point on the planet comes from the native `_eveplanetresources` (`eve/common/lib/PlanetResources`, `baseColony.py:916`), not in the snapshot. The server uses its own deterministic per-planet field (a fixed function of planet id and resource). Programs therefore yield different totals than CCP's. Client calls `GetResourceData` return this field.
- D2 Only the resources of category 42 are installable; the planet-type resource list is not in the SDE snapshot (no attribute found). Any category-42 type is accepted.
- D3 Command center removal destroys the item (see above).
- D4 ISK for non-command pins is charged on submit. The client's wallet check is advisory.

## 7. Scope and cuts

Built in this track: colony read (planet info, colony snapshot, command pins, extractor lists), command stream validation and application, the simulation (extractors / ECU programs and heads, processors with schematics, storage, launchpads, links, routes, expedited transfer, command center levels), the launch record, planets-per-skill and upgrade-skill checks, persistence in one transaction per write, notifications after commit, the planetMgr RPC.

Built in slice 2 and 3: customs offices (type 2233, an orbital of the planet in space: the simulation's ball with the planet in its slim item, so the client's launchpad window reaches it; `GetTaxRate` and `ImportExportWithPlanet`), launches as real cans in space (a wreck-kind ball of group 12 at the launch's place, a space row for the owner's rights and the five days), `GetPlanetResourceInfo` (no resource listed, D9).

Cut (documented, not built):
- POCO taxes with an owner (the structure track). The office is NPC-owned only.
- GM tools (`GM*`, shown to the GM role only in the client; no GM role in the server yet).
- The depletion sim, the native resource survey (D9: no band, no resource list: the native harmonic and the planet's resource set are not in the snapshot).
- Corporation-owned colonies. Obsolete extractors (group 1026) are refused as `InvalidPinType`; the client does not call them in V24.01 (D9 list in DEVIATIONS).

## 8. Gaps

- G1 (closed): capacities are in the SDE type record (section 3).
- G2 Extraction yield scale. The client's value per head comes from CCP's native harmonic (D1). Until that data is available the yields of a program are not CCP's numbers; the mechanics (cycle, decay, noise, overlap, bandwidth) are the client's.

## 9. Sources

- EVE University wiki, Planetary Interaction: https://wiki.eveuniversity.org/Planetary_Interaction (launch, extractor cycle, link formulas, base costs, tax, CC storage 500 m3, heads 110 CPU / 550 PG).
- EVE University wiki, Interplanetary Consolidation / Planetology: https://wiki.eveuniversity.org/Skills:Planet_Management (one planet per level, up to 6).
- Client: eve-logic commit 1dbb571 (files and lines above).
- SDE 3396210: `sde.types`, `sde.typeDogma`, `sde.groups`, `sde.planetSchematics`, `sde.dogmaAttributes` (`sde/lock.json`).

## 10. Status (as committed)

- M1 `Eve.Planets` (domain): colony model, simulation, command validation and application, launches, expedited transfers, program math, deterministic resource field (D1). Tests: `Eve.Planets.Tests`, 23 pure tests.
- M2 persistence and service: colony document in schema `planets` (migration `0001_planets.sql`, `EVE_PLANETS_SCHEMA`), launches table, SDE catalog loader (`PlanetCatalogLoader`), `planetMgr` service (`Eve.Services.Planets`), Host composition (`PlanetComposition.cs`), notification `OnMajorPlanetStateUpdate` after commit, ISK debit (ref 98) in the change's transaction. Tests: 3 PostgreSQL tests in `Eve.Planets.Tests` (store round trip, launches, catalog from the SDE) pass against `eve_p2`; 26 planet tests in total.
- Slice 2 (customs and launches): customs offices (`planets.Customs`, `Eve.Services.Planets/Customs.cs`, `ICustomsOffices` in `Eve.Services.Inventory`), `planetOrbitalRegistryBroker` (`GetTaxRate`), launch cans (`UserLaunchCommodities` creates a type 23 can with the commodities as stacks and a launch row with its item id). Tests: `CustomsTests.cs` (rate table, fee formula, launchpad import/export, capacity refusal).
- Live walk on lane `eve_p2` through `tools/mitm` (ports 26111/26211): `test_planet_walk.py` has two tests, both pass. Test 1 places a command center and a storage facility on a barren planet. Test 2 builds a colony (command center upgraded to 5, storage, ECU with heads, link, processor program, route, launchpad), advances the colony by 3 hours through the lazy simulation (a seeded past timestamp), launches 5 Water from the launchpad (a real can and launch row), then imports 20 Water and exports 5 Water through `ImportExportWithPlanet` at the office rate 0.17 (the planet is in system 31000604, wormhole space, where the NPC rate outside high-security applies). Fees 680 and 340 ISK match the formula. A stale tax rate is refused as `TaxChanged`.
- Live-walk fixes made while running it: the headless client selects the character after creation (`charUnboundMgr.SelectCharacterID`), the `util.KeyVal` answers are read as records, and the customs rate expectation is the wormhole NPC rate. No server defect was found in this pass.
- Slice 3 (client UI path): the customs office is type 2233 and an orbital in space (`PlaceStructure` carries the planet, protocol 67; the slim item has `planetID` and `level`). The office is created with the colony's first pin, placed after commit, placed again when a pilot enters its system (the simulation drops a scene without pilots) and told to a pilot at its first bind (`add_structures`). The launch can is a wreck-kind ball of group 12 with a space row for five days. `GetPlanetResourceInfo` answers an empty list (D9). Bugs found by the live walk and fixed: a structure's frame had no resonances (the simulation dropped the link; ESS had the same empty list), a placed structure was told only to pilots present at placement.
- Live walk (slice 3): `test_planet_walk.py` passes both tests on `eve_p2` through `tools/mitm`. The colony test checks the office ball in the pilot's first bind (`UpdateStateRequest`, the notifications), the launch can's space row, the rate, and the import and export through the office's container (the call the launchpad window makes). Not checked: the launch can's ball (the walk's pilot is about 1.9e10 m from the planet and does not see it), the window's own drawing, and the container `Add` (dragging stock into the office, `MoveFromInventory`).
- Slice 4: the container `Add` into the office (the client's MoveFromInventory) takes a stack from the pilot's ship hold; the office's room (35,000 m3) refuses with `NotEnoughSpace`; `GetCapacity` answers the room. The survey list (`GetPlanetResourceInfo`) gives each planet four raw resources by rule, with the field's qualities (D9). Band data is not served: the client's survey path raises before the server call (clientPlanet.py:781 unpacks scalar band limits; D9). The office and launch can are placed after commit (rollback places nothing); the placement log is quiet. The colony walk passes with the Add and the survey checks (`livelog28`).
- No EveJS reference exists for PI (EveJS v0.12.9 has no planet code), so the comparison is with the client contract, not with EveJS.
- Skills read: Interplanetary Consolidation (`LevelAsync`, `character_skills`), Command Center Upgrades. Remote Sensing and Planetology are not checked (no resource survey data, D1).
- Pin items: created in the transaction with the pin (location: the planet), deleted with it; the command center consumes its hold item (checked: owner and type only; the hold flag is not checked).

