# EveJS 261108.1 GM command port

## Scope and source checks

Compared the V24.01 client source at /home/user/eve-logic, EveJS 261108.1 at /home/user/evejs-261108.1, and the current eve.rs slash and Host paths. A whole-client Python source search for exact /sov command arguments and /war start found no matching command strings. The ordinary sovereignty service calls in the client are not GM slash commands.

V24 structure context menus show their GM entries to GMH sessions and send /structure state, timer, deploytimer, unanchor, and abandontimer (eve/client/script/ui/structure/structuremenu.py:47-62, 72-94). V24 moon scheduling shows its GM buttons to GML/WorldMod roles and sends /extraction advance mid|stop|ready|decay without a structure ID; the session is in the structure (eve/client/script/ui/moonmining/scheduling.py:59-73, 216-218, 272-278). The native module menu sends /heal <itemID> from the in-flight module button and the GM type menu (eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.py:545; eve/client/script/ui/services/menusvc.py:881-890).

SlashService retains its existing Staff role check before dispatch (dotnet/src/Eve.Services.Slash/SlashService.cs:109-128). Structure changes use StructureTimers and StructureStore transactions; moon schedule changes update the extraction row, replace durable deadline jobs, commit, and publish the V24 notification through the Host composition (dotnet/src/Eve.Host/Persistence/Structures/StructureGmAuthority.cs:22-91, 131-148; dotnet/src/Eve.Host/Composition/StructureComposition.cs:397-403).

## Ported

| V24 action | eve.rs behavior | Evidence |
|---|---|---|
| /structure state, timer, deploytimer, unanchor, abandontimer | Added Host Slash routing and persistent structure state/timer changes using existing structure stores, transition events, and scheduler. Structure-state IDs are checked against the V24 constants. | Client callsites above; EveJS 261108.1/server/src/services/structure/structureChatCommands.js:212-378; dotnet/src/Eve.Services.Slash/StructureGmCommands.cs:18-52; dotnet/src/Eve.Structures/StructureTimers.cs:126-210; dotnet/tests/Eve.Host.Tests/GmMenuSlashHostWireTests.cs:31-102 |
| /extraction advance mid, stop, ready, decay | Added active-extraction phase advance. Start, chunk-ready, and natural-decay times shift together, preserving duration and yield. The update and durable arrival/fracture jobs are committed together, then the Host emits OnMoonExtractionUpdate. This matches EveJS phase targets and its non-later-than-now shift rule. Slash arguments omit the structure ID as V24 does; the handler resolves it from the active structure session. | EveJS 261108.1/server/src/services/structure/structureChatCommands.js:382-410 and moonExtractionsService.js:1273-1340; eve.rs dotnet/src/Eve.Services.Slash/StructureGmCommands.cs:54-68, dotnet/src/Eve.Structures/MoonExtractions.cs:192-239, and dotnet/src/Eve.Host/Persistence/Structures/StructureGmAuthority.cs:70-91; Host wire regression at dotnet/tests/Eve.Host.Tests/GmMenuSlashHostWireTests.cs:105-156 |
| /heal <ownedModuleID> | Slash now preserves the argument. Repairs only that character-owned module or subsystem, restricted to the active ship while in space; it persists through PgModuleStateStore and sends the V24 OnModuleAttributeChanges shape from SDE health capacities. | Client callsites above; EveJS 261108.1/server/src/services/chat/commands/shipHealth.js:393-442; eve.rs dotnet/src/Eve.Services.Slash/ShipHealthCommands.cs:57-84, 126-145 and dotnet/src/Eve.Ships/CharacterShips.Health.cs:102-134; Host wire regressions at dotnet/tests/Eve.Host.Tests/GmMenuSlashHostWireTests.cs:159-237 |

The focused Host tests use the real Host RPC registration and PostgreSQL-backed stores. They assert wire reply/notification shapes, persistence, state isolation, retries, non-owner refusal, and non-GM refusal. The tests were first run against the missing command path and failed with Unknown command; the targeted heal tests were also run before the handler change and exposed the argument being treated as whole-ship heal.

## Already present in eve.rs

| Command group | Existing behavior and limit | Evidence |
|---|---|---|
| /online, /unload | Routed through ItemCommands and the existing fitting/ammo stores. | dotnet/src/Eve.Services.Slash/SlashService.cs:31-42 |
| /skyhook state | Routed through SkyhookCommands and the persistence writer. | dotnet/src/Eve.Services.Slash/SlashService.cs:52; dotnet/src/Eve.Services.Slash/SkyhookCommands.cs:14-43 |
| /spawn, /spawnn, /unspawn, /nuke | Registered and backed by ISpaceSpawns, but only for hulls created and tracked by eve.rs' GM spawn owner. | dotnet/src/Eve.Services.Slash/SlashService.cs:92-97; dotnet/src/Eve.Services/SpaceSpawns.cs:18-32; dotnet/src/Eve.Services.Space/SpaceSessions.Spawns.cs:13-73; dotnet/src/Eve.Services.Slash/SpawnCommands.cs:69-118 |
| /entity deploy | Registered for SDE entity-category NPC hulls through the existing spawn provider. | dotnet/src/Eve.Services.Slash/SlashService.cs:45; dotnet/src/Eve.Services.Slash/EntityCommands.cs:7-34 |

## Rejected or left as a gap

| EveJS/client operation | Decision | Evidence |
|---|---|---|
| /sov | Not ported from EveJS because the V24 client source search found no matching /sov command argument. Keep sovereignty control on the client-supported RPCs. | EveJS 261108.1/server/src/services/chat/chatCommands.js:1390-1393; V24 source search: rg -n '/sov(\\s|\\x27|\\x22|\\))|/war\\s+start' /home/user/eve-logic -g '*.py' |
| /war start | Not ported: EveJS itself records that the client has no GM control for this operation, and the V24 exact command search found no match. | EveJS 261108.1/server/src/services/corporation/warGmCommands.js:3-7; same V24 source search above |
| /heal <entityID> 0 | Explicitly refused so it cannot fall through into /heal's whole-ship repair. V24 uses it to destroy a selected entity (including destroyable-item and tournament tools), while EveJS delegates to a local-scene combat destruction owner. eve.rs has no general local-scene entity-destruction owner exposed to SlashService; ISimulation.Destroy is ship-specific and does not provide this general selection/custody path. | V24 eve/devtools/script/svc_destroyableitems.py:85, 316-337; eve/devtools/script/tournamentRefereeTools.py:1415; EveJS 261108.1/server/src/services/chat/commands/nativeDestroy.js:10-29; eve.rs dotnet/src/Eve.Space/ISimulation.cs:310-311; refusal and no-mutation test at dotnet/tests/Eve.Host.Tests/GmMenuSlashHostWireTests.cs:239-263 |
| Native /unspawn <itemID> and range filters | Existing eve.rs /unspawn only removes its own tracked GM hulls. The V24 menu also offers /unspawn for selected ship or structure items, and EveJS removes local NPC, wreck, container, and inventory-backed dynamic entities with custody cleanup. No matching general Space entity retirement owner is wired to the slash command, so the broader action remains unported. | V24 eve/client/script/ui/services/menusvc.py:829-836; EveJS 261108.1/server/src/services/chat/commands/unspawn.js:20-43, 46-82; eve.rs dotnet/src/Eve.Services/SpaceSpawns.cs:18-32 and dotnet/src/Eve.Services.Space/SpaceSessions.Spawns.cs:13-73 |
| /heal me capac=1 | Not ported: it is a developer slash helper rather than one of the native menu actions, and current eve.rs does not expose a capacitor-only mutation on the simulation owner. The ordinary /heal me path remains served. | EveJS 261108.1/server/src/services/chat/commands/shipHealth.js:398-414; V24 caller eve/devtools/script/svc_copycat2.py:1186-1189; eve.rs dotnet/src/Eve.Services.Slash/ShipHealthCommands.cs:86-123 |

The client supports the actions listed above, but not every supported action has an eve.rs owner yet. No real graphical-client verification was performed for this port.
