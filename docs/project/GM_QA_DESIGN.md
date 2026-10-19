# GM and QA tooling: design

## Purpose
Headless tests and QA runs need the same game-master actions a human GM uses in the client: give standing, skills, items, ISK and LP, spawn ships and NPCs, and drive agents and planets. The commands are the EveJS chat commands (the QA team's habit), served by the slash service (`Eve.Services.Slash`) to sessions that hold a staff role.

## Access
- A session may use the commands only with a staff role bit (`GameMasterRoles.Staff`: ADMIN, CONTENT, GML, GMH, QA, EXT_GM1 to 4). The bits come from the account row at login; the client cannot grant them to itself.
- Grants are made at host start with `EVE_GM_ACCOUNTS=user=ROLE+ROLE,...` (`HostComposition`), which ORs the bits into an existing account row (`PgAccountDirectory.GrantRolesAsync`). An account is created by its first login.
- A refused session gets `Command requires a game master role.`

## Command set (slices G1 to G7)
| Command | Served by | Notes |
|---|---|---|
| `/isk [me] <amount>`, `/wallet`, `/setisk` | `IskCommands` | Journal reference 3 (the client's `refGMCashTransfer`) |
| `/setstanding`, `/fullstandings`, `/maxagentstandings` | `StandingCommands` | Rows subject = owner, object = character (DEVIATIONS G2, G4) |
| `/lp`, `/addlp`, `/setlp` | `LoyaltyCommands` | Per corporation; the LP store announces each change |
| `/ship`, `/giveme`, `/giveitem`, `/fit`, `/unfit`, `/giveskill`, `/npc`, `/dmg`, `/heal`, `/repair`, `/deathtest`, `/suicide` | the existing command classes | `/repair` is `/heal` (DEVIATIONS G6) |
| `/npc <n> <hull> still` | `NpcCommands` (the word `still` is removed from the arguments) | the NPCs get no top speed, so they hold their place (DEVIATIONS #64) |
| `/solar <system>`, `/tele <character>` | `TeleportCommands` over `SpaceSessions.RelocateAsync` | the relocation of a pilot (DEVIATIONS G8) |
| `/tr <me\|characterID> <destination>` | `TeleportCommands.TrAsync` | the system, a pilot's place, `pos=`, `offset=` (DEVIATIONS G14) |
| `/bp help\|list\|jobs\|lookup` | `IndustryCommands` over `HostGmIndustry` and the SDE | the caller's blueprints and jobs, the blueprint of a type (DEVIATIONS G15) |
| `/warpto <itemID>` | `TeleportCommands.WarpToAsync` over `SpaceSessions.ItemPlaceAsync` | a pilot in space to 2 500 m off an item (DEVIATIONS G17) |
| `/agent offers`, `offer`, `accept`, `quit`, `complete`, `reset` | `AgentCommands` over `HostAgentControl` | the caller's own missions through the client's path (DEVIATIONS G9) |
| `/missioncomplete` | not built | EveJS marks placeholder objectives; eve.rs has no placeholder mission (DEVIATIONS G9) |
| `/fw enlist <faction>`, `/fw leave`, `/fw vp <points>` | `FwCommands` over `HostFwControl` | the caller's corporation and system (DEVIATIONS G10) |
| `/pi list`, `/pi commodity`, `/pi command` | `PiCommands` over `HostPiControl` | the caller's planets, pins named from `list` (DEVIATIONS G10) |
| `/createitem`, `/online`, `/load`, `/unload` | `ItemCommands` (cargo, the active ship's modules, the charges) | `/createitem` G13, `/online` G16, `/load` and `/unload` G19 |
| `/spawn`, `/spawnn`, `/unspawn`, `/nuke` | `SpawnCommands` over `ISpaceSpawns` | ship hulls in space around the pilot (DEVIATIONS G18) |
| `/givestarterskills`, `/randomizeskills`, `/skillscopy` | `SkillBulkCommands` over the skill store | the race's starting set, a random level per published skill, a copy of one pilot's skills to another (DEVIATIONS G20) |
| `/dogmaupdate` | `DogmaUpdateCommands` over `DogmaStatic.ReloadAsync` | the dogma tables re-read from the SDE and swapped in (DEVIATIONS G21) |
| `/killright <target> [owner]` | `KillRightCommands` over the crimewatch runtime | a kill right through the crimewatch store, the owner told (DEVIATIONS G22) |
| `/asteroid rewards` | `AsteroidCommands` over `SpaceContainers.RocksInSystemAsync` | the rocks of the pilot's system with their ore and units (DEVIATIONS G23) |
| `/skyhook state <id> <state>` | `SkyhookCommands` over `PersistenceSkyhookStateWriter` | an orbital skyhook's state in the sovereignty table (DEVIATIONS G24) |
| `/entity deploy <amount> <type>` | `EntityCommands` over `ISpaceSpawns` | entity-category NPC hulls in space around the pilot (DEVIATIONS G25) |
| `/pi program` | not built | the forced install needs the client's own inputs (DEVIATIONS G10) |

## GM and QA screen (client InsiderWnd)
The client's dedicated screen is the Insider window (`eve/devtools/script/insider.py`, `InsiderWnd` and `InsiderService`). Its toolbar menus are gated by the session role (`session.role`, which the server sets to the account bits plus the base bits at character selection, `CharUnboundMgrService`): Tools, Macro, Ship, Charges, Drones, Implants need ROLE_GML; QA and UI need ROLE_QA; Tournament needs ROLE_TOURNAMENT. Most entries are client-local (windows, render and debug toggles, `GetMacros` reads the insider files). The server-facing calls are below. The seven `node_graph.qa_*` RPCs also require `GameMasterRoles.Staff` at the server entry before the sim callback; the two `process_client_*` methods retain their client-message contract.

| Menu entry | Remote call (client code) | eve.rs |
|---|---|---|
| Tools, Macro entries, Ship/Drone/Charge helpers, UI links to `/heal`, `/tr`, `/warpto`, `/nuke`, `/entity deploy` | `RemoteSvc('slash').SlashCmd(text)` | `/heal` served; the others not yet (see the next table) |
| Tournament, reload FSD pickle | `RemoteSvc('localizationServer').ReloadFSDPickle()` | served (`CoreCompatibilityServiceAdapters`) |
| Standings tool (channel) | `slash.SlashCmd('/setstanding "<owner id>" <character id> <value> "<reason>"')` | served; owner-first form accepted |
| Map and system tools | `RemoteSvc('stationSvc').GetSolarSystem`, `RemoteSvc('lookupSvc').LookupLocationsByGroup` | served |
| Season, scanner tracker | `RemoteSvc('seasonManager')`, `RemoteSvc('scanMgr').SignalTrackerRegister()` | `seasonManager` served; `scanMgr` not checked |

Slash commands the screen and its helper service send (`eve/devtools/script/svc_slash.py`, `menusvc.py`, `insider.py`). Served means registered in `SlashService`:

| Command | Served |
|---|---|
| `heal`, `repair` | yes |
| `giveskill` | yes |
| `setstanding` | yes (owner-first form added in G12) |
| `removeskill` | yes (G13) |
| `online` | yes (G16: the active ship, DEVIATIONS G16) |
| `givestarterskills`, `randomizeskills`, `skillscopy` | yes (G20) |
| `tr` (teleport a pilot, to a system or to a position) | yes (G14; runtime entity and bookmark destinations not served, DEVIATIONS G14) |
| `warpto` | yes (G17, celestials and pilot ships of the system; DEVIATIONS G17) |
| `spawn`, `spawnn`, `unspawn`, `nuke` | yes (G18: ship hulls only, DEVIATIONS G18) |
| `load`, `unload` | yes (G19: charges docked; DEVIATIONS G19) |
| `entity deploy` | yes (G25: entity category, DEVIATIONS G25) |
| `skyhook state` | yes (G24: the client's five states and unknown, DEVIATIONS G24) |
| `createitem` | yes (G13): cargo of the ship in space, hangar when docked |
| `bp` | yes (G15: `help`, `list`, `jobs`, `lookup`; DEVIATIONS G15) |
| `dogmaupdate` | yes (G21) |
| `killright` | yes (G22) |
| `asteroid rewards` | yes (G23: the rocks of the pilot's system) |

Every command the screen sends is served. A word that no command serves still answers `Unknown command`, so the screen shows an error rather than a fake success.

## Principles
- Words and semantics come from EveJS (`services/chat/chatCommands.js` and its command modules). Where EveJS lacks a thing, the client contract (eve-logic) and the real game decide, and the choice is recorded in DEVIATIONS.
- Every command runs in its own database transaction and announces the change after commit. A command that changes nothing says so and sends nothing.
- No refusal without a basis: a refusal is a usage check, a client error key, or a rule the game imposes (for example a balance below zero).

## Tests
- Unit tests on the parsers and the messages (`SlashCatalogTests`, `IskCommandTests` parsing).
- Database-backed tests on the rig (`Rig` in `Eve.Services.Dogma.Tests`): a real character, real stores in throwaway schemas, the slash service as the host builds it. Each command has a success, a refusal and a repeat.
- Planet and identity calls are tested against the store (`GmPlanetStoreTests`, `RoleGrantTests`).
- The relocation: `RelocationTests` (database-backed, the fake simulation of the space tests) and `TeleportCommandTests` (no database); the anchor placement in the scene tests (`a_relocation_*`).
- The agent missions: `AgentCommandTests` (no database), the reset in `AgentMissionStoreTests` and the GM completion in `MissionDungeonRuntimeTests` (database-backed).
- The faction and planet helpers: `FwDatabaseTests` (database-backed) for the enlistment and the victory points, `FwCommandTests` and `PiCommandTests` (no database).
- The walks: `tools/headless-client/tests/gm.py` seeds ISK and standings through the same commands on both servers; the psql seeds that have no command are listed in DEVIATIONS G11.
- The G20 to G25 tests: `SkillBulkCommandTests`, `DogmaUpdateCommandTests`, `KillRightCommandTests`, `EntityCommandTests` and `SkyhookCommandTests` (Dogma tests, database-backed where the store is real); `AsteroidCommandTests` (no database) and `AsteroidRockTests` (the space rig's stores); `SkyhookStateWriterTests` (Host tests, an in-memory sovereignty table).
- The Insider walk: `tools/headless-client/tests/test_insider_gm_live.py` sends the Insider's calls on both servers; its transcripts are compared through the MITM (docs/project/MITM_COMPARE.md, section "Insider walk").
- Lanes: database tests run under the database's lock (`flock /tmp/eve-heavy-eve_p2.lock`) with `--blame-hang-timeout 120s`, one lane at a time.

## Open
- `/missioncomplete` is not built (no placeholder mission exists; DEVIATIONS G9). `/pi program` is not built (DEVIATIONS G10).
- The Insider walk against EVE.js is run and compared (MITM_COMPARE.md). Its remaining differences include `/solar` reply text (EVE.js includes the gate name; eve.rs does not) and skill-not-found suggestion wording (DEVIATIONS G26). Both servers place the pilot beside the destination system’s first stargate.

## ESS link hull grant (owner decision Q-15, 2026-10-19)

The ESS live walk needs a hull permitted by the ESS link module. Choose the hull from the pinned SDE `canFitShipGroup` and `canFitShipType` attributes, then add a role-gated slash command that grants the hull with the module fitted and online. The live walk must exercise linking, reserve-bank interaction, and key consumption.
