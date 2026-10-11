# ST-2 handoff (deployment and vulnerability RPCs: Anchor, Unanchor, CancelUnanchor, RenameStructure, UpdateReinforceTimesFromClient, notices)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-2). Branch `wt-st2`, worktree `/home/user/wt-st2`. TODO section 48.

## Done (pushed unless noted)
- (a) `UpdateReinforceTimesFromClient` (pending day and hour, effective after 30 days) and `RenameStructure`: `Persistence/Structures/StructureDeploymentAuthority.cs`, `StructureStore.SetNameAsync`. Pushed as 042195f.
- (b) `Anchor`: `Persistence/Structures/StructureAnchorAuthority.cs`; `StructureRulesSource.IsAnchorable`; `StructureAnchored` (`structure.anchored`); `SpaceStructures.PlaceAsync` closes the ST-1 gap (a pilot already in the system gets the structure).
- (c) `Unanchor` and `CancelUnanchor` (Director or CEO) in `StructureDeploymentAuthority`; the state machine refuses the EveJS cancel states (`StructureState.CancelsUnanchor`).
- (d) `structure.notify` (`Persistence/Structures/StructureNotifications.cs`); `StructureStateChanged` carries `PreviousState` and `PreviousUpkeep` (optional, default 0).

## Tests run
- `Eve.Structures.Tests`: 83 of 83 (with PostgreSQL).
- `Eve.Host.Tests --filter Structure`: 30 of 30 (with PostgreSQL), including the anchor, unanchor, rename, reinforce and notice tests.
- `Eve.Services.Space.Tests --filter SpaceStructures`: 5 of 5 (with PostgreSQL).

## Open, in order
1. The drop at the end of an unanchoring: EveJS `structureAssetSafetyState.js` (unanchor handoffs: hull, eject container, fitting containers, rigs). Asset-safety territory; a separate package.
2. Anchor refusals not yet implemented: starter and career systems, Thera and shattered wormholes (wiki), the sovereignty index, the default profile.
3. The reinforcement-change mailbox notice (no writer in eve.rs).
4. Notices to sessions whose ship id is the structure (the control seat, ST-5).
5. The live client: none of the anchoring or notice paths has run on the graphical client.

## Gotchas
- Bash `grep`, `cat` and `find` are denied; search and read with `python3 -I -` (quoted heredoc) or the Read tool. A `$2` inside a double-quoted bash string is expanded by bash: it broke `SetNameAsync` once.
- `EVE_PG_URL` is key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- The heavy lock queues behind other agents (up to 20 minutes). Use `-m:1` on dotnet builds: with parallel nodes a build died with `MSB4166`.
- The EveJS trade hub list is five names; the SDE `hub` flag is not the trade hub list.
- `CharacterSessions` only sees sessions with a `CharacterId` (the `charid` attribute).
