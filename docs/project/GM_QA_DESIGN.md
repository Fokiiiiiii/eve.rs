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
| `/solar <system>`, `/tele <character>` | `TeleportCommands` over `SpaceSessions.RelocateAsync` | the relocation of a pilot (DEVIATIONS G8) |
| `/agent offers`, `offer`, `accept`, `quit`, `complete`, `reset` | `AgentCommands` over `HostAgentControl` | the caller's own missions through the client's path (DEVIATIONS G9) |
| `/missioncomplete` | not built | EveJS marks placeholder objectives; eve.rs has no placeholder mission (DEVIATIONS G9) |
| `/fw enlist <faction>`, `/fw leave`, `/fw vp <points>` | `FwCommands` over `HostFwControl` | the caller's corporation and system (DEVIATIONS G10) |
| `/pi list`, `/pi commodity`, `/pi command` | `PiCommands` over `HostPiControl` | the caller's planets, pins named from `list` (DEVIATIONS G10) |
| `/pi program` | not built | the forced install needs the client's own inputs (DEVIATIONS G10) |

## GM and QA screen (client InsiderWnd)
The client's dedicated screen is the Insider window (`eve/devtools/script/insider.py`, `InsiderWnd` and `InsiderService`). Its toolbar menus are gated by the session role (`session.role`, which the server sets to the account bits plus the base bits at character selection, `CharUnboundMgrService`): Tools, Macro, Ship, Charges, Drones, Implants need ROLE_GML; QA and UI need ROLE_QA; Tournament needs ROLE_TOURNAMENT. Most entries are client-local (windows, render and debug toggles, `GetMacros` reads the insider files). The server-facing calls are below.

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
| `online`, `removeskill`, `givestarterskills`, `randomizeskills`, `skillscopy` | no |
| `tr` (teleport a pilot, to a system or to a position) | no (`/tele` is served) |
| `warpto`, `entity deploy`, `nuke`, `spawn`, `spawnn`, `unspawn`, `unload`, `load`, `skyhook` | no |
| `bp`, `createitem`, `dogmaupdate`, `killright`, `asteroid rewards` | no |

Missing commands answer `Unknown command`, so the screen shows an error for them rather than a fake success. Building them is the next slices; each is recorded here when it lands.

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
- Lanes: database tests run under the database's lock (`flock /tmp/eve-heavy-eve_p2.lock`) with `--blame-hang-timeout 120s`, one lane at a time.

## Open
- The headless walk against EveJS for the shared commands (MITM capture on the eve_p2 lane, docs/project/MITM_COMPARE.md) is not yet built.
- `/missioncomplete` is not built (no placeholder mission exists; DEVIATIONS G9). `/pi program` is not built (DEVIATIONS G10).
