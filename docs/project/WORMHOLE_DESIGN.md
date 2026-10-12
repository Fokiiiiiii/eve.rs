# Wormhole space (J-space): research, data and plan

Status: research done (2026-10-12); the first slice is built (the type catalog and the rules, SDE-backed tests). The jump,
the connection generator, the lifetime jobs, signatures, effects and the sites are not built. The list is in section 7.

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

## 7. What is built and what is not

Built in this slice (`dotnet/src/Eve.Universe/Wormholes/`, tests in `dotnet/tests/Eve.Universe.Tests/`):

- `WormholeTypeCatalog.LoadAsync`: the 129 wormhole types from the pinned SDE (`ReadContainingAsync` on `typeDogma`).
- `WormholeRules`: `IsJSpaceSystem`, `MassAllowsTransit`, `MassAfterTransit`, `MassStateOf`, `LifetimeStateOf`, `Polarization`.
- Tests: `WormholeRulesTests` (synthetic numbers, the state boundaries), `WormholeTypeCatalogTests` (PgFact against the
  pinned SDE: count, numbers of named types, the K162 exclusion, the target classes of Thera, shattered, Drifter, Pochven and
  the -1 family).

Not built, with the reason:

- Jump through a wormhole (`WormholeMgr.WormholeJump`): needs a wormhole entity in the simulation, the destination's
  placement, the mass rule at the jump and the shrink or collapse; the composition passes no callback today. The rule for
  mass is ready; the ship-side work is not started.
- Connection generator (statics, random k-space connections, lifetime, mass, stability): needs the static lists and the spawn
  probabilities (section 4). Blocked by owner decision 2 (section 8).
- Lifetime and mass jobs on `Eve.Scheduler`: depends on the connection generator.
- Signatures in the probe scanner: EveJS has `wormholeSignatureProvider.js` (191 lines); the eve.rs scanner's J-space
  signature rows were not checked in this slice.
- System effects as dogma modifiers: the six collections exist (section 5); the magnitudes and the assignment do not (section 4).
- Sleeper and Drifter sites through `DungeonWaveDirector`: the site counts and NPC lists per class are not published (section 4).
- Thera's connection list and Turnur: not in the SDE.
- The client run: not done.

## 8. Owner decisions needed

1. The source of the class of each J-space system (C1 to C6, C12, C13). The SDE has none. Options: a generated table
   built from owner-supplied data; or a documented cut. The 1381 target of each wormhole type is in the SDE and says where a
   wormhole leads, not what class the system it starts from has.
2. The connection generator: the static lists per class and the spawn chance are not published. Choose a documented rule
   (for example the wiki's statics per class, with the chances as an owner number) or cut the generator.
3. The effect magnitudes per class: a documented table or none.
4. Local chat in J-space: the wiki and the client say there is local chat, the brief said there is none. Confirm.
5. The SDE mass values for the 7 disagreeing types: keep the SDE (current choice) or use the wiki.

## 9. Slice 2 (2026-10-12): data found, what was built, what was cut

### 9.1 J-space system classes (owner decision 1)

- Source: the V24.01 client's static data, `EVE_CLIENT_SDE_DIR/native/systems.jsonl.gz` (`native/_sde.jsonl.gz`
  `clientBuild` 3396210, the same build as the pinned SDE). Field `wormholeClassID`.
- All 2,604 J-space systems carry a class: C1 358, C2 537, C3 506, C4 523, C5 531, C6 118, C13 25 (small-ship shattered),
  Thera 31000005 = 12, and the five Drifter systems 14 to 18. Classes in K-space: 7 (high-sec, 1,194 systems), 8 (low-sec,
  687), 9 (null-sec, 3,188), 10, 11, 19 to 23, 25 (Pochven, 27). 377 K-space systems have no class.
- The Fuzzwork dump table `mapLocationWormholeClasses` (owner's first choice) was searched in the three repositories and
  the client export; it is not in them. It was not fetched from Fuzzwork in this slice. The client table is the same mapping
  from the same build, so it is used; the cross-check against Fuzzwork is open.
- Loader: `WormholeSystemClasses.LoadAsync(directory)` (setup-time, nothing committed). A J-space system without a class is an
  error. Tests: `WormholeClientDataTests` (synthetic rows always; the counts above when `EVE_CLIENT_SDE_DIR` is set).

### 9.2 Effects (owner decision 3)

- The EVE University page "Wormhole Effects" is not reachable (HTTP 404 under `Wormhole_effect`, `Wormhole_effects`,
  `Wormhole_Effects`). A search found no page with per-class percentages; the forum-archive numbers were not used.
- The SDE has the per-class effect beacons: group 920, 36 types named `Class N <Effect> Effects` (N 1 to 6; effects Black
  Hole 30845 and 30850 to 30854, Cataclysmic Variable, Magnetar 30847 and 30860 to 30864, Pulsar, Red Giant, Wolf Rayet). Their
  `typeDogma` attributes are the modifiers. Example from the SDE: Black Hole class 6 `maxTargetRangeMultiplier` 2.0 and
  `stasisWebStrengthMultiplier` 0.5; Red Giant class 1 `heatDamageMultiplier` 1.15; Pulsar class 6 `armorEmDamageResistanceBonus` 50.
- Loader: `WormholeEffects.LoadAsync(sde)`. Tests: `WormholeEffectsTests` (PgFact).
- Not decided: which effect a J-space system has. No client or SDE field gives it for each system. The only link found is the
  star: 101 systems have the `Sun K5 (Red Giant)` type 8 and 115 have type 45033 (the same name). The other effects have no
  star type in the SDE. The wiki's shattered list (100 systems: 75 standard and 25 small-ship, matching the 100 SDE
  `SHATTEREDWORMHOLE_OVERLAY` systems) gives an effect tag per shattered system; it was not turned into data (owner decision 6).
- Effects as dogma modifiers on ships in the system: not built (needs the assignment above and the dogma path).

### 9.3 Statics and random connections (owner decision 2)

- The EVE University "Wormhole Connection Table" page (78 rows) gives, per type, the destination as "Leads to" and a
  "Wormhole Classification" (the destination class). It has no source class, no statics per system class and no spawn
  rule. So a static of a given system class cannot be derived from it.
- The wiki "Wormhole" page: statics per class as in section 3.2 (Class 1 and 3 one static, 2 and 4 usually two), and no
  probabilities. Anoik.is was not fetched in this slice.
- The generator is not built. Without the statics and the spawn probabilities no connection can be created that a source
  supports. The documented mechanic (statics replaced on collapse; non-static wormholes gone when they collapse; lifetime and
  mass from SDE) is the part that can be built once the owner names the static list.

### 9.4 Local chat (owner decision 4)

- Not implemented (no wormhole channel exists in eve.rs). The client has the wormhole chat category
  (`chatutil/const.py` `CATEGORY_WORMHOLE`, `chatutil/channelnames.py`), and the wiki says local keeps timestamps of jumps.
  The "delayed local, list fills when pilots speak" rule was not read in the client source in this slice: open.

### 9.5 CCP patch notes and devblogs (once)

- One search. It found no official CCP article about shattered systems, Thera or Turnur wormhole rules. It found a fan site
  note (July 2026) that the Rampant Drone Fabricator site was re-added to C1 to C6 systems (wave maximum 100), a 2025
  Drifter note (Deepflow Rift sites in wormholes, less often outside Pochven) and a 2022 note (more Turnur connections in
  version 20.09). These are secondary; none is used by the code.

### 9.6 Cuts and what remains

- Jump through a wormhole (`WormholeMgr.WormholeJump`): not built. The jump has no wormhole pair to use (no generator), and the
  simulation's jump accepts stargates only (`SpaceSessions.Movement.cs` line 217 and the sim comment). A J-space jump needs a
  simulation change in `crates/` and a callback in the composition.
- Lifetime and mass jobs, collapse, signatures in the probe scanner, Sleeper and Drifter sites via `DungeonWaveDirector`,
  Thera and Turnur connections, the headless MITM run against EveJS: not built. Reasons above; the MITM run needs a wormhole
  to walk through.
- Owner decision 5 (SDE over wiki for type attributes; the mismatch table in section 3.3) stays as it is.

## 10. Slice 3 (2026-10-12): statics and effects per system, local chat

### 10.1 The data file (committed, 393 KB)

- `dotnet/src/Eve.Universe/Wormholes/Data/jspace-systems.json`, embedded in `Eve.Universe`, loaded by `JSpaceSystems.Load()`.
  One row per J-space system (2,604): id, name (client localization), class (client `wormholeClassID`), effect (name, beacon
  typeID, strength class), statics (code and type ID), and `staticsSource`. The header names every source and the cross-checks.
- Classes: client `native/systems.jsonl.gz` (owner decision 1; Fuzzwork not used).
- Statics and effects of the 2,504 normal systems: EveJS v0.12.9 `tools/DatabaseCreator/staticTables/explorationWormholeStatic/data.json`
  (`staticSlots`, `environmentEffectTypeID`). The generator's spec file (`scripts\DataSync\spec`) is not in the repository, so the
  generator is not read; the table itself is.
- Statics of the 100 shattered systems: EVE University "List of shattered systems" (oldid 222738, fetched 2026-10-12). The
  table has no IDs; the names (J015092 and so on) are matched to the client's localization names, so the mapping is by name.
  EveJS has no statics for these 100 systems (0 of 100).
- Effect beacons: SDE group 920 (36 types), by `effectTypeID` from EveJS.

### 10.2 Cross-checks (two or more sources)

| Check | Result |
|---|---|
| Shattered systems: class, client against the wiki (matched by name) | 98 of 100. J004686: wiki 3, client 2 (EveJS 2). J011376: wiki 3, client 4 (EveJS 4) |
| Shattered systems: effect, EveJS against the wiki | 98 of 100. J005926: EveJS `environmentFamily` Pulsar, its beacon Red Giant, the wiki Red Giant. J010569: EveJS `environmentFamily` Red Giant, its beacon Wolf Rayet, the wiki Red Giant. The wiki is used for the 100 shattered systems |
| Shattered systems: statics, EveJS against the wiki | 0 of 100 (EveJS has none). The wiki is used |
| Static type codes against the SDE target class (1381) | Checked by `JSpaceSystemsTests` (PgFact): every static type is a wormhole type of the pinned SDE |
| Statics per class, EveJS against the wiki's general rule (section 3.2) | Class 1: 1 static (348 systems); class 3: 1 (494 of 495); class 5, 6: 1; class 2: 2 (525). Class 4: 1 (498 of 505), against the wiki's "usually two". Open (owner decision 7) |
| Effect strength class against the system class | Equal for 1,006 of 1,038 systems with an effect; 32 differ (30 Drifter systems, 2 C1 to C6). The wiki says the strength "depends on the system class, which the entry wormhole indicates" |

The 1,038 systems with an effect: 1,036 agree by name between EveJS `environmentFamily` and its beacon typeID (after
the `Wolf-Rayet` / `Wolf Rayet` spelling is normalised); the two others are the two shattered systems above, where EveJS disagrees
with itself.

### 10.3 Local chat (owner decision 5)

- Client (V24.01, `chat/client/local_chat/channel_controller.py`): in a wormhole system (`IsWormholeSystem`, also Zarzakh and
  Triglavian space) the local channel is "delayed": `is_local_chat_delayed` is true, so on joining a system no members are
  added (line 254), members are added when they speak (`_update_member_activity`, line 200), inactive members are removed
  (`_remove_inactive_members_routine`), and `member_count` is 0 (line 139). The member list fills only when pilots speak.
- Server (EveJS `server/src/services/chat/channelRules.js`): `isDelayedLocalSolarSystemID` (line 126) and
  `isWormholeSolarSystemID` (line 104). The rule is the same; the server sends messages, the client shows the list.
- eve.rs has no chat service (`dotnet/src` has no local channel; `SessionStartupRpcServices.cs` mentions only the session
  startup). So there is nothing to change for delayed local yet. Building the local channel is a chat-service task, not a
  wormhole task. Not built.

### 10.4 What is not done in slice 3, and why

- Connection generator and the static pairing. A static gives its type and its target class (the SDE 1381 and the wiki
  "Leads to"), but not the system it leads to. Nothing published (EVE University, Anoik.is not fetched, EveJS's wandering
  profiles hold counts, not pairs) gives the pairing. The "random wandering connections" rule is not in the wiki pages read.
  So no connection can be created from a source. Cut until owner decision 8.
- The setup-time loader into a dedicated PostgreSQL schema: not built. The data file is loaded in memory; no runtime
  consumer needs a table yet.
- Lifetime and mass jobs, the jump, the entities, the sim changes, the signatures, the effects on ships, the sites, Thera and
  Turnur connections, the headless MITM run: not built (section 9.6 still holds). The Rust workspace could not be built in
  this session: the disk was 6.7 GB and a Rust build needs 9 to 11 GB of target output (BRIEF), so a sim change could not be
  compiled or tested.

### 10.5 Owner decisions open after slice 3

7. Class 4 statics: EveJS gives one static for 498 of 505 normal class-4 systems; the wiki says "usually two". Which holds?
8. Static pairing: the system a static leads to. No source found. Options: a documented rule, a data source the owner
   names, or a cut (current).
9. The shattered list's J004686 and J011376 classes (wiki 3, client 2 and 4): keep the client.
10. Effects of J005926 and J010569 (EveJS disagrees with itself; the wiki says Red Giant for both): the wiki is used for the
    100 shattered systems (current). Effects of the other 2,504 systems come from the EveJS beacon.

## 11. Slice 4 (2026-10-12): connection generator and durable lifecycle

Code: `dotnet/src/Eve.Wormholes/` (project `Eve.Wormholes`, migration `wormhole` 0001) and tests `dotnet/tests/Eve.Wormholes.Tests/` (14 tests).

- Generator (`WormholePlanner.PlanStatics`): every static of every J-space system (the committed data, 3,015 statics) gets
  an exit to a random eligible system of its type's target class (SDE 1381: 7, 8, 9 are high-, low- and null-sec systems
  from the client's `wormholeClassID`; the other classes are J-space classes). Eligible = not the source and not already
  holding a connection from the source. The choice uses SplitMix64 (`SeededRandom`, fixed algorithm, golden values in a test),
  so the same world seed gives the same connections on every runtime. The mass is the SDE stable mass times a roll in 90% to
  110% (wiki "Wormhole Types"). No probabilities: a static exists because the data says so.
- Owner rule applied: "excluding the source's own region neighbours where the wiki says so". The wiki pages read do not say
  this, so no region exclusion is applied (recorded as not implemented).
- Lifecycle (`WormholeJobs`, `WormholeStore`): one durable `wormhole.expire` job per active connection (`DurableScheduler`,
  key = connection id, payload = connection id and seed, due at the expiry: SDE `wormholeMaxStableTime` minutes after opening).
  When it runs, the connection collapses. A static collapse opens one replacement of the same type from the same source to a
  random eligible system, seeded from the job seed (wiki: a static collapse opens a replacement elsewhere in the system).
  A non-static collapse is final. A transit that empties a connection collapses it the same way and cancels its job.
- Transit (`WormholeJobs.TransitAsync`, `WormholeLifecycle.Transit`): refused over the individual limit (SDE 1385), otherwise
  the ship's mass comes off the wormhole; zero or less collapses it.
- Tests: SeededRandom golden values, determinism, eligibility (target class, no self-loop, no duplicate pair), the mass
  range, the transit rules, expiry, replacement, a transit collapse without doubling the replacement, durability across a
  scheduler restart, and the real world (PgFact plus client data): every static of the 2,604 J-space systems gets a
  destination of its target class.
- Not built: the wandering connections from K-space (the K162 exits). The wiki gives no spawn probability and the CCP notes
  read did not state one; the owner's rule needs a rate that is not published. Cut.
- Not built: the entity in space, the simulation jump, the WormholeMgr callback, signatures, effects, sites, Thera and Turnur
  (sections 9.6 and 10.4 still hold for these; the generator and the lifecycle are now the data they need).

## 12. Slice 5 (2026-10-12): the jump path, investigated and not built

Status: not built in this slice. This section records what the code and the client require, so the next slice starts from facts.

### 12.1 The client's call

- `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py` (lines 595 to 598): after the high-sec confirmation,
  the client calls `sm.RemoteSvc('wormholeMgr').WormholeJump(itemID)` inside `PerformSessionChange('UI/Inflight/EnterWormhole')`.
  `itemID` is the wormhole's item (the object the pilot picked), not a gate. `GetWormholePolarization` is the other method.
- The session change is the client's; the server must send the session change and the jump effect as the stargate path does.
- `eve/client/script/parklife/spaceMgr.py` (`OnWormholeJumpCancel`, line 94) and the jump indicator (lines 492 to 506) are
  the client's answers to a cancelled or a running jump.

### 12.2 The server path that exists

- `dotnet/src/Eve.Services.Space/ActivityTravelRpcServices.cs`: `WormholeMgrService` answers `WormholeJump` through a
  callback. The composition passes none (`dotnet/src/Eve.Host/Rpc/SpaceRpc.cs` line 72), so the call returns None.
- The stargate jump is `SpaceSessions.JumpAsync` (`SpaceSessions.Movement.cs` line 217): checks, the toll, then the
  simulation's `Jump` command with `[gateId, toGateId]`, then `FinishJumpAsync` (line 338) when the simulation reports the ship
  has left (`SimJumped`), which moves the ship and the character to the destination system (`_ships.JumpAsync`).
- The simulation side is `crates/eve-space/src/scene/jump.rs`: `jump()` (line 31) accepts only a body of kind
  `BodyKind::Stargate` (line 60) with `Detail::Stargate`; `due_jumps()` (line 231) takes the ship out of this scene; `arrive()`
  (line 266) places it at a stargate of the destination system (`system.body(traveller.to_gate)` of kind Stargate, line 281),
  so the far end must be a body of that system.

### 12.3 What a wormhole needs in the simulation

1. A body kind: `BodyKind::Wormhole` and `Detail::Wormhole { destination_system, destination_end }` in `world.rs`.
2. A placement: the host puts each end in space the way it puts a structure (`place_structure`, `scene/structures.rs`, protocol 59).
   A new link message (protocol 60) and its C# codec and golden fixtures, as AGENTS.md requires. Both ends are needed: the
   source end in the source scene, the far end in the destination scene. A scene exists only while a ship is in the system
   (ADR 0007), so the far end must be placed when the destination scene is made or carried by the jump command.
3. `jump()` accepts the wormhole body; the mass check (`WormholeJobs.TransitAsync`) is the host's and must run before the
   simulation moves the ship. Its refusal must reach the client as the reference does.
4. `arrive()` places the ship at the far end (a position in front of it, the random bearing as `gate::arrival` does).
5. The C# side: `WormholeMgrService` callback in the composition that runs the transit and then the simulation command, with
   the same toll-free path as the stargate; `FinishJumpAsync` reused.

### 12.4 Other items, and what exists

- Effects as dogma modifiers: no system-wide effect mechanism exists in eve.rs. `grep` of `crates/eve-space` and
  `dotnet/src/Eve.Dogma`, `Eve.Services.Space` finds only deployable beacons (cyno, lance, CRAB), not environment effects. The
  Abyssal, Pochven and Triglavian effects are not implemented either. A new modifier source (per system, applied to every
  ship's attribute calculation) has to be built first; it is not a reuse.
- Signatures: the probe scanner (`SpaceProbeScans`) exists. The signature rows of wormholes (scan group 3, `Cosmic Signature`,
  strength attribute 1908) are in EveJS's `explorationAuthority` table; no type in the SDE carries attribute 1908.
  Scan strength per wiki is not on the pages read.
- Sleeper and Drifter sites: the dungeon machinery exists (`DungeonWaveDirector`, `Eve.Dungeons`), but no wormhole site
  archetype is loaded; the site counts are not published (design 4).
- Thera and Turnur: a static exit to Thera (class 12) has a pool of one system (31000005). Thera has no statics (data file).
  Turnur (J377 leads there) needs the K-space side, which is cut with the wandering connections.
- Delayed local: no chat service in eve.rs (section 10.3). Skipped.
- Headless MITM run: needs a wormhole the client can enter, so it waits for 12.3.

### 12.5 Decision for the next slice

Build 12.3 in order (1, 2, 3, 4, 5) with tests at each step: a Rust unit test for the jump and the arrival, a PG test for the
transit, and the golden fixture for the new link message. The effects (12.4) are a separate slice that needs an owner decision
on where the modifier applies (the ship's attribute calculation in `eve-space`, or the dogma service in C#).

## 13. Slice 6 step (a): the wormhole ends in the simulation (protocol 63)

Done (code, goldens, tests):

- `crates/eve-space`: `BodyKind::Wormhole` and `Detail::Wormhole { destination_system, destination_end, destination_class,
  destination_position, destination_radius, max_jump_mass }` (`world.rs`); `WormholePlacement` and the messages
  `PlaceWormhole` (kind 82) and `RemoveWormhole` (kind 83) (`link/`), decoded and encoded; `PROTOCOL_VERSION` 62 -> 63.
- `scene/wormholes.rs`: a host-placed wormhole end is a body of its system, told to the bound pilots (`AddBalls2` with the
  slim `otherSolarSystemClass`); `remove_wormhole` takes it out (`RemoveBalls`). `dispatch.rs` routes both messages.
- `scene/jump.rs`: `jump()` accepts a wormhole end as well as a stargate (same range, 2.5 km from the surface, the same stop
  and jump-out effect); the traveller carries the far end's position and radius (`to_far_end`); `arrive()` places the ship
  by the far end (`wormhole_arrival`).
- Tests: two scene tests (placement, jump to the far end, arrival clear of the far end and facing it; out of reach, removed).
  Fixtures: `oracle/fixtures/simlink/messages.json` (generated by `tools/dev/gen-simlink-fixtures.py`, two cases each for
  PlaceWormhole and RemoveWormhole; the Hello version is 63). The Rust replay (`simlink_fixtures`, 7 tests) and the C#
  codec tests (`SimCodecTests`, 299 tests) both pass on the same bytes.
- Verified: `cargo fmt -p eve-space`, `cargo clippy -p eve-space --all-targets -- -D warnings` (clean), `cargo test -p eve-space
  -p eve-sim` (747 + 7 + 5 + ... all pass; the pinned protocol test is 63).
- Formatting: `cargo fmt` also rewrote unrelated files in `crates/eve-space`; those were reverted so the diff holds only the
  wormhole work.

Not done (the rest of step (b) onward):

- The far end's body in its own scene. `arrive()` places the ship by the far end's position, but the far end is not a body of
  the destination scene, so a pilot who arrives cannot jump back out through it. The fix is a placement of both ends by the
  host (both ends of one connection share it) or the far end inserted by the arrival; the placement needs the far end's type
  and name, which the traveller does not carry yet.
- The C# side that places the ends (both ends of every active connection, from the store, with deterministic positions) and
  the `WormholeMgr.WormholeJump` callback (`SpaceSessions` jump through the lifecycle `TransitAsync`: mass, collapse,
  notifications, `RemoveWormhole` for the two ends).
- The composition: the world (the planner's connections opened once, `wormhole.expire` registered with the scheduler, the
  client classes and the SDE types loaded at start-up).
- The effects, signatures, sites and Thera exits (steps c to f).
- The live check (tools/mitm against EveJS): needs the jump to exist.

## 14. Slice 7 (2026-10-12): status of steps d to f

- (d) Signatures in probe scanning: NOT BUILT. Sources read: EveJS `wormholeSignatureProvider.js` (signature rows: scan group 3,
  inventory group 502 `Cosmic Signature`, strength attribute 1908 from `explorationAuthority.scanStrengthAttributes`; the per-signature
  strength value is not read in the provider) and `explorationAuthority.signatureTypeDefinitions.wormhole`. The SDE carries attribute
  1908 on no type. The eve.rs probe path (`SpaceProbeScans`, the dungeon signature count in `DungeonRpcServices`, which has a
  `VisibleWormholeSignatureCountsBySystem` input that nothing fills) has no wormhole rows. The wiki's scan-strength value for a
  wormhole was not read in this slice. Next: one row per wormhole end in the probe's result, with the strength the wiki gives.
- (e) Sleeper and Drifter sites: BLOCKED. The sites' counts, NPC compositions per class and respawn are not published (design 4, 10.4);
  the dungeon machinery exists, the content does not. Needs an owner decision on the content source.
- (f) Thera exits: BLOCKED. The wiki says Thera has "a dozen or so wormholes to high-, low-, and null-sec at any given time", tracked
  by EVE Scout (the Thera page read on 2026-10-12): those are not static, so they are the K-space wandering connections, which the
  owner keeps cut (decision 1 of slice 5). Thera has no static in the data file (`jspace-systems.json`).

## 15. Slice 8 (2026-10-12): Thera's connections; the signature strength, the sites, the sim

- Thera's connections (DONE, tests): `TheraConnections` (`Eve.Wormholes`). Sources: EVE University "Thera" (https://wiki.eveuniversity.org/Thera,
  fetched 2026-10-12: "a dozen or so wormholes to high-, low-, and null-sec at any given time", tracked by EVE Scout); EVE University
  "Wormhole Types" (the endpoints of the eight types with an end in Thera: F135 class 2 or 3 to Thera, F353 class 1, L031 null, M164
  low, T458 high; Q063 Thera to high, V898 Thera to low, E587 Thera to null). The rule: 12 connections, each of a type drawn with a
  seed (SplitMix64), its free end drawn among the systems of the type's classes that are not Thera and not already connected to
  Thera. A collapsed Thera connection is opened again the same way (owner rule: respawn). Count 12 is the page's "a dozen or so";
  the counts per type are not published (DEVIATIONS). Tests: `TheraConnectionsTests` (4 incl. a PG respawn), 21 wormhole tests in all.
- Signature scan strength (NOT DONE, blocker): the EVE University pages read give no per-type scan strength: "Wormholes" (the
  level system is not on the page), "Cosmic signature" (the probe-scan page: sensor thresholds per level only), "Wormhole Space"
  (no signature counts or scan values). A web search found only forum posts (a 10-point scale and Pathfinder's 2.5% and 6.67% numbers)
  that are not official. EveJS carries the strength attribute id (1908) and no per-type value (its provider reads none). So no per-type
  strength is available from the sources; the probe scanner cannot be given the values without an owner decision.
- Sleeper and Drifter sites (PARTIAL, blocker on the numbers): the wiki "Wormhole Space" page gives the combat site names per class
  (C1: Perimeter Ambush Point, Perimeter Camp, Phase Catalyst Node, The Line; C2: Perimeter Checkpoint, Perimeter Hangar, The Ruins of
  Enclave Cohort 27, Sleeper Data Sanctuary; C3: Fortification Frontier Stronghold, Outpost Frontier Stronghold, Solar Cell, The Oruze
  Construct; C4: Frontier Barracks, Frontier Command Post, Integrated Terminus, Sleeper Information Sanctum; C5: Core Garrison, Core
  Stronghold, Oruze Osobnyk, Quarantine Area; C6: Core Citadel, Core Bastion, Strange Energy Readings, The Mirror). The same page: the
  number of cosmic signatures per system and the shares of combat, relic, data, gas and ore sites are NOT stated; C13 is not described.
  The Drifter wormholes: each has five multi-room combat sites, and six statics to C1 to C6 (Z647, D382, O477, Y683, N062, R474).
  The SDE has the site templates (dungeons with the factions 500021 Sleeper and 500024 Drifter). Blocked on the per-system counts;
  the names above can select templates once the owner gives the count rule.
- Environment effects in the simulation (NOT DONE): the Rust sim owns the ship's speed, signature radius, shield, armor, capacitor and
  the module numbers; the C# attribute sync to the sim is the ship spec at entry (`SimShips.Of`). Extending it means a per-attribute
  list in the spec and in the sim's ship model. Not started in this slice.
- The service-layer jump tests (NOT DONE): the harness exists (`SpaceServiceTests`, the gate jump tests) but a wormhole world needs a
  store and a scheduler in the space test rig. Not started.
- The live check (NOT DONE): needs the sim, a static wormhole in a K-space system, and the MITM capture against EveJS; not run.
