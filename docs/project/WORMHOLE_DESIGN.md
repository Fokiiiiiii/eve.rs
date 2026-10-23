# Wormhole space (J-space): research, data and plan

Status: J-space rules, statics, lifecycle, simulation travel, Thera connections, signatures and sites are implemented. Current open behavior and project cuts are summarized in the implementation section below.

Sources, by rank (AGENTS.md): the pinned SDE build 3396210 (a local copy of the JSONL export at
`/home/user/eve.rs/.cache/sde/eve-online-static-data-3396210-jsonl`, lock sha256 in `sde/lock.json`); the V24.01 client
code (`/home/user/eve-logic`); the EVE University wiki; EveJS v0.12.9 (`/home/user/evejs-v0.12.9`) as the behaviour
reference. Wiki pages were read on 2026-10-12: `https://wiki.eveuniversity.org/Wormhole`, `.../Wormhole_attributes`,
`.../Wormhole_Types`, `.../Sleeper`, `.../Thera`. Not reachable: `.../Wormhole_effect` and `.../Wormhole_effects` (HTTP 404).
CCP patch notes and developer blogs were not read in this slice; the only CCP facts used are those that the SDE and the
client carry.

## 1. Corrections to the brief

- "692 SDE wormhole-class systems" is a count of `mapSolarSystems` rows that carry `wormholeClassID`. They are 687 K-space
  low-sec systems (security 0.0 to below 0.45, every one of them 8, the client's `LOW_SEC_WORMHOLE_CLASS_ID`) and 5 J-space
  Drifter systems (31000001 Sentinel 14, 31000002 Liberated Barbican 15, 31000003 Sanctified Vidette 16, 31000004 Conflux
  Eyrie 17, 31000006 Azdaja Redoubt 18). No other system carries the field. Ordinary J-space systems (C1 to C6, C12, C13)
  have no class in the SDE (section 4).
- "No local chat" does not match the sources. The wiki says local chat in wormhole space "keeps timestamps for multiple
  wormhole jumps", and the client has a wormhole chat category (`chatutil/const.py` `CATEGORY_WORMHOLE`,
  `chatutil/channelnames.py`, `chat/common/util.py` `IsWormholeSystem`). What the sources do support: no stargates in
  W-space (the wiki's Wormhole and Thera pages). The local-chat rule is an owner decision (section 8).
- Mass numbers: the SDE and the wiki disagree on 7 of 12 sampled types (section 3.3). The SDE is used.

## 2. Wormhole space in the SDE

- Systems: 2,604 solar systems with ids 31000000 to 31999999, all in regions 11000001 to 11000033 (the names
  `A-R00001` to `K-R00033`, 33 regions, 1 to 153 systems each). Region 11000031 (`G-R00031`) holds Thera (31000005) only.
  Pochven is region 10000070 (27 systems, outside J-space; see DEVIATIONS track A8c).
- Shattered systems: 100 J-space systems carry `visualEffect` `SHATTEREDWORMHOLE_OVERLAY`, and Thera carries `THERA`.
- Drifter systems: the five J-space systems above, with `factionID` 500027, 500002, 500003, 500001 and 500026.
- Anchoring: 106 J-space systems carry `disallowedAnchorCategories` [65]. Not interpreted in this slice.
- Stars: the J-space systems use 29 ordinary star types (group 6, for example `Sun K3 (Yellow Small)` 3802 and 45041). Their
  graphic files are the ordinary `sun_*.red` set, not the effect graphics (`Pulsar.red`, `Cataclysmic.red`, and so on).

## 3. Wormhole types (typeDogma, attributes 1381 to 1385 and 1457)

### 3.1 Count and layout

- 129 types carry `wormholeMaxStableMass` (1383). Group 988 (`Wormhole`) holds 130 types: the 129 and K162, the generic
  exit, which has no dogma row. The 129 include two QA types (`QA Wormhole A` 32894, `QA Wormhole B` 32895).
- Attribute meaning (SDE descriptions): 1381 `wormholeTargetSystemClass`, 1382 `wormholeMaxStableTime` (minutes), 1383
  `wormholeMaxStableMass` (kg, the total), 1384 `wormholeMassRegeneration` (kg), 1385 `wormholeMaxJumpMass` (kg, the most one
  ship may take through), 1457 `wormholeTargetDistribution`. Attributes 1386 to 1412 (target region, constellation and
  system lists) exist in `dogmaAttributes`; no `typeDogma` row carries any of them.
- Lifetimes: 270 minutes (4.5 hours) for 11 types, 720 (12 hours) 32, 960 (16 hours) 50, 1440 (24 hours) 32, 2880 (48
  hours) 4.
- Target class (1381): 1 to 6 (8, 8, 8, 9, 9, 9 types), 7 high-sec (9), 8 low-sec (12), 9 null-sec (11), 10 (1), 11 (1),
  12 Thera (5: F135, F353, L031, M164, T458), 13 shattered (A009), 14 to 18 Drifter (one each: S877, B735, V928, C414,
  R259), 25 Pochven (6), and -1 for 27 types, all named `Wormhole C729` (720 minutes, distributions 2925 to 2990): the
  target comes from the distribution (1457) instead.
- The meaning of 10 and 11 is not in the SDE. The two types with 10 and 11 were not looked up in this slice.

### 3.2 Rules the SDE does not carry

- Statics: the wiki says Class 1 and Class 3 systems have one static each, Class 2 and 4 "usually" two, and a static that
  collapses is replaced by one of the same class elsewhere in the system. The per-class static lists are not on the pages read.
- Mass states (wiki "Wormhole"): above 50% stable, below 50% "first shrink" (no threshold given for "significant"), below
  10% "crit". `MassStateOf` uses these.
- Lifetime display (wiki): under 1 day, under 4 hours, under 1 hour, expired. `LifetimeStateOf` uses these.
- Polarization (wiki "Wormhole"): five minutes per direction, per pilot (not per ship or account). Weapon timers do not block
  transit; aggression timers block tethering, citadel docking and refitting in space.
- Spawn (wiki "Wormhole Types"): the total mass of a new wormhole is set at birth within 90% to 110% of the table value.
- Capital ships (wiki "Wormhole"): jump-drive capitals may pass a wormhole that supports their mass; supercapitals may not
  pass any wormhole; capitals cannot be built in W-space; supercapitals cannot be built there.
- Ship size classes (wiki): extra-large all but Titans and Supercarriers; large up to Battleships and Orcas; medium up to
  Battlecruisers (unplated Nestors); small frigates and destroyers. The numbers that enforce them are attribute 1385.
- No stargates in W-space (wiki "Wormhole", "Thera"). Cynosural fields can be lit in W-space but jump drives cannot jump to
  W-space systems (wiki "Cynosural field"; DEVIATIONS track A8c).
- Collapse (wiki "Wormhole"): a wormhole collapses when its lifetime ends or when enough mass has passed. A static collapse
  opens a replacement elsewhere in the system; a non-static collapse is final. Rolling (collapsing early) is a player tactic.

### 3.3 SDE against the wiki (12 sampled types, `typeDogma` against the wiki table)

| Type | SDE total (t) | SDE per-ship (t) | SDE regen (t) | SDE hours | Wiki total / per-ship / regen / hours | Result |
|---|---|---|---|---|---|---|
| C125 (30661) | 1,000,000 | 62,000 | 0 | 16 | 1,000,000 / 62,000 / 0 / 16 | match |
| A239 (30678) | 2,000,000 | 375,000 | 0 | 24 | same | match |
| A641 (30657) | 2,000,000 | 1,000,000 | 0 | 16 | same | match |
| B274 (30677) | 2,000,000 | 375,000 | 0 | 24 | same | match |
| X702 (30584) | 1,000,000 | 375,000 | 0 | 24 | same | match |
| B041 (30645) | 3,000,000 | 1,000,000 | 0 | 48 | 5,000,000 / 375,000 / 500,000 / 48 | differs |
| B520 (30713) | 3,000,000 | 1,000,000 | 0 | 48 | 5,000,000 / 375,000 / 500,000 / 24 | differs |
| C008 (34138) | 3,000,000 | 5,000 | 3,000,000 | 4.5 | 1,000,000 / 5,000 / 500,000 / 4.5 | differs |
| E004 (34134) | 3,000,000 | 5,000 | 3,000,000 | 4.5 | 1,000,000 / 5,000 / 500,000 / 4.5 | differs |
| Z006 (34136) | 3,000,000 | 5,000 | 3,000,000 | 4.5 | 1,000,000 / 5,000 / 5,000 / 4.5 | differs |
| O128 (30642) | 1,000,000 | 375,000 | 0 | 24 | 1,000,000 / 375,000 / 100,000 / 24 | differs (regen) |
| U319 (30646) | 3,300,000 | 2,000,000 | 0 | 48 | 3,300,000 / 2,000,000 / 500,000 / 48 | differs (regen) |

The sample is the twelve types above: five match, seven differ. The rules read the SDE. The regeneration unit differs too (the SDE field is per cycle, the wiki's per day), so the
regeneration is not used by any rule yet.

## 4. What is not in the SDE (not published here)

- (Corrected in slice 2: the class of each J-space system IS published, in the V24.01 client data, see section 9.)
  Before slice 2 this was listed as missing. The EveJS reference reads it from a generated static
  table (`explorationAuthority.wormholeAuthority.systems[].wormholeClassID`, read in `server/src/services/exploration/wormholes/
  wormholeAuthority.js`). The generator of that table was not located in this session. Copying it would copy EveJS internals (AGENTS.md).
- Statics per class and the destination of each static.
- The spawn probabilities and the number of wormholes per system.
- The strength of each system effect per class. The SDE has the six effect collections (section 5), not the magnitudes;
  the wiki effect pages were not reachable. The effect-to-system assignment is not in the SDE either.
- Sleeper sites: the site counts per system, the NPC lists per class in C1 to C4, the respawn timers and the gas site
  content. The wiki gives the C5 and C6 escalation (3 Upgraded Avengers per capital in C5, 4 in C6) and the Sleeper
  components with their sale prices (Neural Network Analyzer 200,000 ISK, Sleeper Data Library 500,000 ISK, Ancient
  Coordinates Database 1,500,000 ISK, Sleeper Drone AI Nexus 5,000,000 ISK). EveJS's site counts were not read in this slice.
- The wormhole signature strength (attribute 1908 is defined and used by no type).
- The Thera and Turnur connection lists: the wiki says "a dozen or so" Thera connections, tracked by EVE Scout.

## 5. System effects (dbuffCollections 2510 to 2515)

| Effect | dbuff | Operation | Modifier |
|---|---|---|---|
| Black Hole | 2510 | Minimum, PostPercent | `stasisWebifierResistance` (item) |
| Red Giant | 2511 | Minimum, PostPercent | `heatDamage` (location) |
| Wolf-Rayet | 2512 | Maximum, PostPercent | `trackingSpeed` for skill 3306 (required skill) |
| Pulsar | 2513 | Minimum, PostPercent | `energyWarfareResistance` (item) |
| Cataclysmic Variable | 2514 | Maximum, PostPercent | `capacitorCapacity` (item) |
| Magnetar | 2515 | Minimum, PostPercent | `sensorDampenerResistance`, `weaponDisruptionResistance` (item) |

EveJS has `wormholeEnvironmentRuntime.js` (259 lines); it was not read in this slice. This slice does not apply the effects.

## 6. The client and EveJS contract

- Jump: the client calls `sm.RemoteSvc('wormholeMgr').WormholeJump(itemID)` (`eve/client/script/ui/services/menuSvcExtras/
  movementFunctions.py`, line 598), after a high-sec confirmation (`WormholeJumpingFromHiSec`). The method is in
  `docs/reference/spec/services.tsv` (lines 653 to 655) with `GetWormholePolarization`.
- The eve.rs service `WormholeMgrService` (`dotnet/src/Eve.Services.Space/ActivityTravelRpcServices.cs`) answers the
  pilot-warp-pending case with `OnWormholeJumpCancel`. Its jump and polarization callbacks are not passed by the
  composition (`dotnet/src/Eve.Host/Rpc/SpaceRpc.cs` line 72), so a jump returns None today.
- The client's wormhole object (`eve/client/script/environment/spaceObject/wormhole.py`) reads `slimItem.wormholeSize`,
  `nebulaType`, `wormholeAge` and `maxShipJumpMass`. The info window (`eve/client/script/ui/shared/info/infoContentBase.py`
  lines 1500 to 1548) takes the class from `otherSolarSystemClass` (line 1517) and builds the description from the age, the
  remaining time and the maximum size. The jump indicator (`spaceMgr.py` lines 492 to 506) maps the destination through a
  table keyed by `otherTypeID`.
- `cfg.GetLocationWormholeClass(solarSystemID)` (`eve/common/script/sys/eveCfg.py` line 535) is the client's class lookup.
- EveJS field names for the slim item and the jump are in `wormholeRuntime.js` (2661 lines), `wormholePresentation.js` and
  `wormholeMgrService.js`. The full EveJS module set is about 5,000 lines (`server/src/services/exploration/wormholes`).
- Drifter NPCs: `eve-logic/behaviors/trees/drifters/` (the tree, the disappear behaviour through a wormhole, the corpse
  disposal). EveJS has `server/src/space/npc/trigDrifter/trigDrifterNpcCatalog.js`.

## Current implementation status

Wormhole type rules and the pinned SDE catalog are implemented in `Eve.Universe.Wormholes`. `WormholePlanner` creates the configured J-space statics, and `WormholeJobs` and `WormholeStore` persist connection expiry, mass use, collapse, and static replacement. The simulation exposes wormhole ends for travel; `WormholeJumpAsync` routes jumps through the space simulation. `TheraConnections` manages the project-selected Thera exits and respawn. Probe scanning includes generated wormhole signatures, and `WormholeSites` supplies seeded gas, ore, and combat sites from the extracted site templates.

The rules, catalog, planner, lifecycle, simulation ends and travel, Thera connections, site plans, and signature visibility have focused unit, PostgreSQL, or simulation tests in `Eve.Universe.Tests`, `Eve.Wormholes.Tests`, `Eve.Services.Space.Tests`, and `eve-space`. The headless live walk has passed a wormhole jump and return through the MITM proxy. Supported system dogma modifiers are applied when ships enter or arrive in a system; the modifier gaps and client attribute refresh behavior remain recorded in `docs/reference/compat/DEVIATIONS.md`. Graphical-client acceptance remains open.

## Open and cut behavior

- Wandering K-space connections remain cut because the project has no published spawn rate to use. Thera's selected connections are implemented separately.
- Region-adjacency exclusion for static connections is not applied; the reviewed source does not establish that rule.
- Wormhole Local uses delayed speaker membership. Persisted MOTD, staff presence, and the two-character Local walk remain open.
- The ice belt for a Shattered system is not planned. Live checks of gas and ore sites and graphical-client acceptance remain open.
- Site compositions and other values without published data follow the explicit project rules recorded in `docs/reference/compat/DEVIATIONS.md`.
