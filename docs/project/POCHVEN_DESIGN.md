# Pochven, Triglavian and EDENCOM space: research and design

Status: research done 2026-10-12 (STEP 0 of the owner request). Implementation status is in section 7.
Scope: the Pochven region (SDE region 10000070) and its NPC factions. Everything here is either a published
number (with its source) or listed under "Not published".

## 1. Sources and how they were used

| Source | Used for | Notes |
|---|---|---|
| SDE build 3396210 (pinned, `sde/lock.json`), client extract `/home/user/eve-client-sde` (`compat/`, `native/`) | system, constellation, faction, group, type and dogma numbers | Primary for numbers. The data is not committed. |
| EVE University wiki, Pochven (https://wiki.eveuniversity.org/Pochven, read 2026-10-12) | region rules: entry, local, jumps, ship limits, standings, ratting, timers | Wiki, secondary. |
| EVE University wiki, Triglavian Invasion (Chapter 3) (https://wiki.eveuniversity.org/Triglavian_Invasion) and Invasion (https://wiki.eveuniversity.org/Invasion) | invasion states, thresholds, sentries, NPC tiers, standings | The page says it documents content no longer in the game. |
| EVE Online CCP patch notes and devblogs | not used | Searches returned the "Invasion" and "Version 19.08" patch notes; their text was not read, so no statement here rests on them. |
| Player forums and Reddit (search results only) | not used | Anecdotes only (gate camps, sentry behaviour). Not a basis for any rule. |
| EveJS v0.12.9 (`/home/user/evejs-v0.12.9`) | behaviour reference | `server/src/space/pochvenPolicy.js`, `server/src/space/empireGatePresence/empireSentryLayout.js`, `server/src/services/chat/channelRules.js`, `server/src/services/map/mapService.js` (constant system lists with no provenance), `server/src/services/industry/industryRestrictions.js`. |

## 2. The region (SDE, build 3396210)

- Region 10000070 "Pochven", factionID 500026 (Triglavian Collective), 3 constellations, 27 solar systems. The SDE region
  description is the Triglavian invasion communique ("three Krais of nine fields within clade").
- Constellations (all factionID 500026, wormholeClassID 25): Krai Perun 20000787, Krai Svarog 20000788, Krai Veles 20000789;
  nine systems each.
- Systems, by constellation (id, name, SDE securityClass; every system has securityStatus -1.0 in the SDE):
  - Krai Perun: 30000157 Otela (C), 30000192 Otanuomi (C1, border), 30001372 Kino (C), 30001445 Nalvula (C1, border),
    30002079 Krirald (E1), 30002737 Konola (C), 30005005 Ignebaener (D1), 30010141 Sakenta (A, border, regional, international),
    30031392 Komo (A, border).
  - Krai Svarog: 30000021 Kuharah (B3), 30001413 Nani (B), 30002225 Harva (B), 30002411 Skarkon (E1), 30002770 Tunudan (C1),
    30003495 Raravoss (B1, border, regional), 30003504 Niarja (B1), 30040141 Urhinichi (A, border, regional, international),
    30045328 Ahtila (no class letter).
  - Krai Veles: 30000206 Wirashoda (C), 30001381 Arvasaras (C), 30002652 Ala (D1), 30002702 Archee (D2), 30002797 Kaunokka (C),
    30003046 Angymonne (D1), 30005029 Vale (D1), 30020141 Senda (A, border, regional, international), 30045329 Ichoriya (no class letter).
- Border systems (SDE `border`): Otanuomi, Nalvula, Raravoss, Sakenta, Senda, Komo, Urhinichi (7). Hubs (SDE `hub`): 9.
- Stargates: 60 stargate records lie in the region, all of type 56317 "Pochven Conduit Gate (Interstellar)" (group 10). No
  stargate in mapStargates links a Pochven system to a system outside the region: the gate network is closed to the rest of space.
- Gate ship lists: none of the 27 systems' stargates in solarsystemcontent carries an allowed-ship list (checked for all 27).
- Pochven filaments (group 4087 "Triglavian Space Filaments", 17 types, ids 56065 to 56081): Glorification-1, -5, -15 "Devana";
  Proximity-5, -15 "Extraction"; Cladistic-5, -15 for each of Krai Perun, Krai Svarog, Krai Veles; Border-5, -15 "Pochven";
  Internal-5, -15 "Pochven"; Home-5, -15 "Pochven". The numbers are the ship counts the wiki gives (up to 5 or 15 subcapitals).
  Related types: Filament Trace (group 1991), Triglavian Space Trace 56693.

## 3. Rules the wiki states for Pochven (not all are in the SDE)

- Entry: not only by filament. K-space and W-space wormholes also lead in. "Home systems require 6.0 standing with Triglavian
  Collective to enter."
- Local chat: delayed mode. A pilot appears in local only when speaking (EveJS channelRules has a matching delayed room).
- Jumps: "Jump drives cannot be used to enter Triglavian space, only to exit." Region-wide cyno jammers stop new cynos; capitals
  already inside can jump out. Clone bays swap only local clones; medical clones cannot be set in Pochven.
- Ships: capital ships cannot use stargates or be manufactured in Pochven. Wormholes out accept up to battleships. Internal
  Pochven wormholes (I078, L687, O546) accept medium ships and below.
- Structures: Upwell structures cannot be anchored. POCOs need 1.5 standing with the Triglavians.
- Standings: docking, fitting and repair need -0.05 standing; the LP store needs -0.01; jump clone swaps, reprocessing,
  manufacturing and corporation offices need 3.00. Positive standing needs 0.01 or more, "NOT 0.00".
- Effects: every system carries the Final Liminality effect (Dazh Liminality Locus): 30% warp speed penalty and 50% penalty to
  maximum locked targets. The wiki's Invasion page gives the same two numbers and adds 25% to remote armour and shield boost.
- Wormholes out last 12 to 16 hours; internal Pochven wormholes about 4.5 hours. Outbound filament traces cannot be jumped for
  up to 3.5 minutes after use.
- Roaming NPC fleets change place every 10 to 15 minutes.
- Ratting: clearing all NPCs in a wormhole pays Triglavian ISK; combat sites pay on completion; World Ark and Stellar Fleet sites
  also pay DED LP; Observatory Infiltration pays at least 20M ISK in autosell loot.
- Death: ships drop up to 60% of their loot when destroyed in the region (wiki).
- Factions: Triglavian and EDENCOM fleets attack players with 0.00 or lower standing; Rogue Drones, Drifters and Sleepers are
  hostile to all players; "the five main factions attack each other on sight" (wiki; the five are not named on the page).
- Security: the wiki gives no security status; the SDE gives -1.0 for all 27 systems.
- Chapter 3 ended 2020-10-13, when the 27 Final Liminality systems were cut from the gate network to form Pochven.

## 4. NPC factions and their ships (SDE)

Faction ids (`factions`): Triglavian Collective 500026 (corporation 1000298, memberRaces [135]); EDENCOM 500027 (corporation
1000297); Rogue Drones 500025 (corporation 1000287); Drifters 500024 (corporation 1000274); Unknown 500021 (no corporation).

Triglavian ships (group 4028 "Triglavian Entities", 142 types). Hulls seen in the names: Damavik, Vedmak, Leshak, Kikimora,
Rodiva, Drekavac, and Zirnitra (52701). Tiers by name prefix, the same as the wiki's table:
- scout: "Raznaborg <role> <hull>" (e.g. 52645 Raznaborg Damavik);
- normal: "<role> <hull>" with roles Anchoring, Tangling, Starving, Ghosting, Renewing, Liminal, Harrowing, Blinding
  (e.g. 52182 Liminal Damavik, 52207 Anchoring Damavik);
- elite: "Hospodar ..." (54108 to 54124; "Hospodar Liminal Damavik" etc.);
- officer: "Zorya's <hull>" (52474 Zorya's Damavik, 52658 Zorya's Kikimora, 52659 Zorya's Vedmak) and "Zorya's Hospodar TTC-3 / -243 / -729",
  "Zorya's Voivode TTC-3 / -243 / -729";
- Sudenic variants (52478 to 52500) and Chislov Leshak (52692).

EDENCOM ships (group 4034 "EDENCOM Entities", 152 types), one ship per tier per navy. Tier names are the wiki's:
Scout / (none) / Elite / Exalted (officer). Examples, SDE ids:
- Imperial Navy (Amarr): Sentinel 52923 (normal), Scout 54000, Elite 54009, Exalted 54018 (Sentinel line; the line runs to Apocalypse).
- State Navy (Caldari): Kitsune 52932, Kirin 52933, Raptor, Basilisk, Cerberus, Rook, Drake, Scorpion, Raven; Scout 54027 to 54035,
  Elite 54036 to 54044, Executive from 54045 (Kitsune, Kirin, Raptor, Basilisk, Cerberus).
- Federal Navy (Gallente): Keres 52941, Thalia, Taranis, Oneiros, Deimos, Lachesis, Brutix, Dominix, Megathron.
- Republic Fleet (Minmatar): Hyena 52955, Scalpel, Claw, Scimitar, Muninn, Huginn, Hurricane, Typhoon, Tempest, Naglfar 52967.

Rogue Drones (group 4037 "Rogue Drone Entities", 39 types): Swarmer and Legionary phases (Vexing, Punishing, Interfering,
Obstructing), Overmind Broodlord, Destructor, Interruptor, Progenitor, Forager types, and a set of "Infesting ..." types
(60255 to 60580, e.g. Infesting Destructor Theta 60316). Rogue Drone Infestation Data (60459) is a group 4142 item.

Drifters (group 4035 "Drifter Entities", 6 types): Polemarkos, Strike, Raider Battleship; Navarkos, Assault, Scout Cruiser (56217 to 56222).
Drifter groups 1310, 1530, 1788, 1956 and abyssal groups 1982 hold further Drifter hulls.

Other NPC-side items from the same SDE: Triglavian Stellar Observatory 52842; Dazh Liminality Locus 52693 (beacon, not a ship);
Triglavian Observatory Defense Subpylon 53045; Dazh Porevitium Transmuter 54283/54314-54316; World Ark (Xordazh-class) 52361 and
54274/54325/54326 (100,000,000 hp, untargetable, group 319).

### Sentries (SDE attributes, read from `typeDogma`, raw values)

| Type | Id | Group | Attributes (raw) |
|---|---|---|---|
| Entropic Disintegrator Werpost (Triglavian, Minor Victory / First Liminality sentry) | 54577 | 383 Destructible Sentry Gun | hp 15,000; shield 18,000; armour 37,500; maxRange 250 km; falloff 50 km; maxLockedTargets 8; maxAttackTargets 2; damageMultiplier 0.8; EM/thermal/kinetic/explosive 250 each; entityKillBounty 12,500,000 |
| EDENCOM GunStar | 54579 | 383 | hp 15,000; shield 12,000; armour 25,000; maxRange 250 km; falloff 50 km; maxLockedTargets 8; maxAttackTargets 2; damageMultiplier 10; each damage 25; trackingSpeed 35 |
| EDENCOM Heavy GunStar (fortress) | 54580 | 383 | hp 15,000; shield 18,000; armour 37,500; maxRange 250 km; falloff 50 km; maxAttackTargets 2; damageMultiplier 20; each damage 25; trackingSpeed 15 |

The SDE gives the numbers, not the rules for when a sentry fires. The wiki gives only the Final Liminality rule ("aggressors are not
shot", Invasion page); no gate-gun damage, range or retribution timer is published (see section 6).

## 5. Invasion (wiki; the SDE has no state table)

- Stages: Stellar Reconnaissance (neither side ahead). EDENCOM track: Redoubt -> Bulwark (75%) -> EDENCOM Fortress (full control of
  a Bulwark). Triglavian track: First Liminality -> Second Liminality (75%) -> Final Liminality (irreversible; the system works as null-sec).
- Thresholds: 76% toward either side adds roaming fleets and class 1 and 2 sites; 96% toward Triglavian victory spawns the Stellar
  Fleet Deployment site and opens one Major Conduit.
- Minor Victory: systems without blue or yellow stars. The winner keeps spawning sites and roaming fleets for its side, and deploys
  EDENCOM Gunstars (EDENCOM minor victory) or Entropic Disintegrator Werposts (Triglavian minor victory) on stations, gates and belts.
- Massive EDENCOM Fortification / Massive Triglavian Invasion: no progress bar; resolves after "several days".
- Outcomes (wiki): the 27 systems that reached First Liminality all reached Final Liminality; Arshat was the only one pulled from Escalating Liminality to an EDENCOM Fortress.
- Standings: Triglavian rats hostile at 0.00 and below; EDENCOM rats hostile below 0.00; both remote-repair players at +5.00 or more.
  Standing gain per completed site: 0.01% per Stellar Reconnaissance anomaly; 0.24% per Observatory Flashpoint in First Liminality.
- Empire rats (EDENCOM) drop a stack of 1,000 faction ammo.

NPC bounty handling (owner decision, 2026-10-19): NPC bounty credit is suppressed in Pochven at the Host payout boundary. The SDE's `entityKillBounty` remains static type data; the kill does not create a pilot payout or ESS credit. Existing loot and drops are unchanged. The regression is `NpcBountyHostTests.A_sim_resident_triglavian_in_Pochven_pays_no_bounty`.

## 6. Not published (not in the SDE, not on the wiki pages read)

- Spawn tables: which NPC ship, how many, per site, per bounty, per roaming fleet. The SDE has 54 Triglavian, 28 EDENCOM, 68
  Drifter and 7 Rogue Drone dungeons, but no field links a dungeon to a solar system. The NPC authority (`npc_authority.json`) has no
  Triglavian, EDENCOM or Drifter profile and no pool for them (its Rogue Drone profiles are the pirate-style set).
- Roaming fleet composition per invasion state; the site list per system; the "Stellar Fleet", "World Ark" and "Observatory"
  flashpoint rules beyond the wiki's one-line descriptions.
- Gate guns: whether a werpost or gunstar fires at a pilot passing a gate, its targeting order, its retribution timer, its
  damage per shot after the SDE numbers. The wiki and the SDE do not give it. The player forums describe gate camps; that is anecdote.
- Faction pairs: the wiki says the five main factions "attack each other on sight" without naming them. Sleepers are not
  modelled: no Sleeper faction id is taken from the sources.
- Rogue Drone and Drifter behaviour toward others in Pochven: only the wiki's "hostile to all players" sentence.
- Filament rules: the rate, the pilot cap per type beyond the 1/5/15 counts, the cool-down ("3.5 minutes") is the wiki's only.
- Invasion timers: "several days" (Massive stages) and the gate-energy fluctuation length; the per-system state at a given time.
- A "Stage 1 to 3" numbering: neither the SDE nor the wiki pages read use it; the states are the named stages in section 5.
- Sentry tier/variant (Pochven gunstar vs fortress) placement per system.
- Whether the Pochven "temporary warp speed bonus" from the 19.08 notes still applies: the notes were not read.

## 7. What eve.rs implements (this slice) and what is cut

Implemented:
1. Region and faction constants from the SDE (`dotnet/src/Eve.Services.Space/Pochven/PochvenRules.cs`): region 10000070, factions
   500024 to 500027, corporations 1000274, 1000287, 1000297, 1000298, capital groups 30, 485, 547, 659, 883, 1538.
2. Capital stargate refusal in Pochven: the gate jump (`SpaceSessions.Movement.cs`, `CheckGateRulesAsync`) refuses a capital
   pilot in a Pochven system with the existing client-message text "Capital ships cannot use this stargate.". Source: wiki Pochven.
   The Pochven gates carry no ship list in the SDE, so this rule is the only capital check there.
3. Faction hostility and NPC standing rules as pure functions (`FactionsAreHostile`, `NpcHostileToPilot`), with tests. NOT yet
   called by the simulation: NPCs in the simulation pick targets among players only, and the simulation has no NPC-vs-NPC target
   selection (see cuts).

Cut (not built), with the reason:
- Gate sentries that shoot: no published firing rule or damage per shot (section 6). EveJS draws sentry props at empire gates
  only (`empireSentryLayout.js`); it has no firing sentry. Cut until a source exists.
- NPC-vs-NPC engagement and skirmish spawning (the four factions fighting each other): needs target selection among NPCs in the
  simulation (`crates/eve-space` brains, `combattargets.rs`) and spawn tables that are not published. The rule functions above are the input.
- Invasion state machine and fortress/minor-victory systems: the thresholds are in the wiki, but the per-system state, the
  roaming fleets and the sites are not. EveJS's system lists (`mapService.js`) have no stated provenance and are not used
  (the existing TODO line on `map.GetEdencomFortressSystems` stays open).
- Filament entry and the 6.0 standing gate for Home systems: needs the filament item and jump rules, which are not published
  beyond the counts above.
- Local chat delay, docking and standing thresholds in Pochven (-0.05, 0.01, 3.00): not built here; listed for later.

Decisions (owner-visible):
- The Pochven NPC factions are the four with SDE faction ids (Triglavian, EDENCOM, Rogue Drones, Drifters). Sleepers are left
  out. Pairs among those four are hostile to each other; a pair with CONCORD or any other faction is not.
- The capital set is Titan, Dreadnought, Carrier, Supercarrier, Force Auxiliary and Capital Industrial (Rorqual), the same set the
  wiki's "Capital ships" page names. Black Ops, Jump Freighter and Freighter are not in it.
- The refusal text reuses the existing `Capital ships cannot use this stargate.` (already in the gate code). The wiki gives no text.
