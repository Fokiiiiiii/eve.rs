# Server logic in `eve-logic`, and what of it is ported

The owner (2026-09-30, the amendment of `docs/adr/0009`): port the better implementation as it is, from EVE.js **and from `Fokiiiiiii/eve-logic`**, the
decompiled client V24.01. The client is not only UI: its tree holds the packages the server runs too (they are shared, `eve/common`, `carbon/common`, and
packages such as `industry` or `crimewatch`). The server-only packages (`*/server/*`) are **not** in it: `inventoryrestrictions/common/util.py` imports
`inventoryrestrictions.server.tracker`, and that file is not there. So what can be ported is the shared rules, the constants, the pure helpers, the
data classes, and the dogma engine's own code.

The size is what the decompiled `.py` files come to. "Read" says whether I have looked at the code, not only at its size.

## Ported

| eve-logic | Port | Notes |
|---|---|---|
| `behaviors/` (whole, ADR 0008) | `crates/eve-behaviors` | the NPC AI |
| `eveuniverse/security.py` | `dotnet/src/Eve.Universe/SecurityClass.cs` | the four security classes from a system's level |
| `dogma/moduleRestrictions.py` | `dotnet/src/Eve.Services.Space/ModuleRestrictions.cs` | a module that may not be used in empire or high security is refused (`CantInEmpireSpace`, `CantInHighSecSpace`) when it is activated |
| `destructionEffect/destructionType.py` | `crates/eve-space/src/destruction.rs` | the effects of `TerminalPlayDestructionEffect` and how long the client plays them |
| `ballparkCommon/docking.py`: `GROUP_MAPPING`, `CanDock`, the one-way and excluded structures | `crates/eve-space/src/locators.rs` (`category_of`, `can_dock`) | found one difference: command carriers (group 5120) need a capital door; fixed |

## Read, not ported (and why)

| eve-logic | What it is | Not applied because |
|---|---|---|
| `ballparkCommon/docking.py`: `ShipUndockVelocity` (1.75 × max velocity, at most 700 m/s) and `ShipUndockDirection` (a random turn of up to 0.25 rad) | how fast and in which direction a ship leaves a station | the reference (EVE.js) does neither and the recorded undock (`oracle/fixtures/scenarios/undock.json`) holds our replay to it; to be applied with a deviation row when the undock is recorded again |
| `ballparkCommon/ballRadius.py` | an asteroid's radius from how much ore it holds, and back | belts with asteroids are not in the simulation yet (mining) |
| `ballparkCommon/intersection.py` | the closest sphere a ray meets | nothing asks for it yet |
| `ballparkCommon/jumpdelay.py` | the stargate jump delay (4 s in Triglavian systems) | matches what is done; Triglavian systems are not special-cased |
| `crimewatch/const.py`, `util.py` | the timers of the weapons, PvP, criminal, NPC and disapproval flags, security tags for security status, the kill report hash | crimewatch is not built (aggression flags, suspect and criminal timers); the constants come with it |
| `deathzone/` | `TargetTrackingState`: hull damage that grows each tick once a ship has been in a death zone longer than its safety time | no death zones yet |
| `inventoryrestrictions/` | which item types may go in which containers | only the client's side is there; the server tracker is missing |
| `npcs/npccorporations.py`, `divisions.py` | the NPC corporations and their divisions | with corporations and agents |

## To read when the feature comes up (Phase 1 order)

| Feature | eve-logic packages (lines) |
|---|---|
| corporation, alliance, chat, mail | `evecorporation` (470), `corporations.py`, `ownergroups` (289), `chat` (3441), `chatutil` (546), `evemail` (57), `notifications` (8361, the formatters of what the client shows) |
| market, agents, missions | `marketutil` (400), `marketgroups` (158), `eveagent` (187), `agentinteraction` (2146), `missions` (1889), `evemissions` (2082), `lpOffers` (218), `loyaltypoints` (37), `contracts` (333), `eveAssets` (782) |
| structures, industry, fleet | `industry` (2705: jobs, blueprints, facilities, material and time maths), `evefleet` (1147), `mining` (1668), `moonmining` (542), `eveplanet` (634), `orbitalSkyhook` (272), `controltowerresources` (55), `homestation` (1988), `sovereignty` (8349), `entosis` (572) |
| war and faction | `evewar` (431), `factionwarfare` (826), `pirateinsurgency` (1781) |
| characters | `characterskills` (1537), `characterdata` (946), `skills` (3654), `clonegrade` (89), `careergoals` (1462), `dailygoals` (1626), `achievements` (1804) |
| dogma | `dogma/` (9264 lines: items, effects, attributes, modifiers, `data`, `fleetBonuses`, `systemWideModifiers`): to be compared with `Eve.Dogma` (ADR 0004) rule by rule; where it is the better one it is ported |
| the machinery | `carbon/common/script/sys` (sessions, services), `net` (machoNet): the session and packet rules the host already follows (ADR 0002) |

A port names its source at the top of the file, and a package ported whole has a `PROVENANCE.md` (ADR 0008, 0009).
