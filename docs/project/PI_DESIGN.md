# Planetary Interaction (PI): design

Status: design and first implementation slice (see "Scope and cuts"). Client truth: V24.01 eve-logic build 3396210.
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

Cut (documented, not built):
- Customs office import and export, POCO taxes and the orbital registry (`planetOrbitalRegistryBroker`, TODO line 865). Needs the orbital structure track.
- Pickup of launches in space (launch containers in orbit). Launches are records until deleted.
- GM tools (`GM*`), the depletion sim, and resource surveys beyond `GetResourceData`.
- Corporation-owned colonies.

## 8. Gaps

- G1 (closed): capacities are in the SDE type record (section 3).
- G2 Extraction yield scale. The client's value per head comes from CCP's native harmonic (D1). Until that data is available the yields of a program are not CCP's numbers; the mechanics (cycle, decay, noise, overlap, bandwidth) are the client's.

## 9. Sources

- EVE University wiki, Planetary Interaction: https://wiki.eveuniversity.org/Planetary_Interaction (launch, extractor cycle, link formulas, base costs, tax, CC storage 500 m3, heads 110 CPU / 550 PG).
- EVE University wiki, Interplanetary Consolidation / Planetology: https://wiki.eveuniversity.org/Skills:Planet_Management (one planet per level, up to 6).
- Client: eve-logic commit 1dbb571 (files and lines above).
- SDE 3396210: `sde.types`, `sde.typeDogma`, `sde.groups`, `sde.planetSchematics`, `sde.dogmaAttributes` (`sde/lock.json`).
