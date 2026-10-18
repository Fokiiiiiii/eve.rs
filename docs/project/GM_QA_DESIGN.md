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
| agent and mission control, `/fw`, `/pi` | not built | See TODO |

## Principles
- Words and semantics come from EveJS (`services/chat/chatCommands.js` and its command modules). Where EveJS lacks a thing, the client contract (eve-logic) and the real game decide, and the choice is recorded in DEVIATIONS.
- Every command runs in its own database transaction and announces the change after commit. A command that changes nothing says so and sends nothing.
- No refusal without a basis: a refusal is a usage check, a client error key, or a rule the game imposes (for example a balance below zero).

## Tests
- Unit tests on the parsers and the messages (`SlashCatalogTests`, `IskCommandTests` parsing).
- Database-backed tests on the rig (`Rig` in `Eve.Services.Dogma.Tests`): a real character, real stores in throwaway schemas, the slash service as the host builds it. Each command has a success, a refusal and a repeat.
- Planet and identity calls are tested against the store (`GmPlanetStoreTests`, `RoleGrantTests`).
- The relocation: `RelocationTests` (database-backed, the fake simulation of the space tests) and `TeleportCommandTests` (no database); the anchor placement in the scene tests (`a_relocation_*`).
- Lanes: database tests run under the database's lock (`flock /tmp/eve-heavy-eve_p2.lock`) with `--blame-hang-timeout 120s`, one lane at a time.

## Open
- The headless walk against EveJS for the shared commands (MITM capture on the eve_p2 lane, docs/project/MITM_COMPARE.md) is not yet built.
- Agent and mission control, the FW helpers and the PI helpers are not built.
