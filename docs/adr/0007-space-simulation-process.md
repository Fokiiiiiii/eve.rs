# ADR 0007: Space is a separate Rust process, `eve-sim`, that the service host commands over a link

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001, `docs/PLAN.md` "Simulation in Rust"); the details are the implementer's.

## Context

Slice S4 puts a character's ship in space: it undocks, the client is sent the solar system's balls and then the
changes to them, it flies, and it docks again. What the client observes (recorded from the reference with the
headless client, `oracle/fixtures/scenarios/undock.json`):

- `ship.Undock(shipID, ignoreContraband, onlineModules=)` answers a bound object and the client is sent, in one
  burst, `OnItemsChanged` (the ship's row, now in space), `OnMachoObjectDisconnect` for the objects bound to the
  station, the session change (`stationid` gone, `solarsystemid` and `solarsystemid2` set) and
  `OnInvulunOnUndockingUpdated`.
- The client then binds `beyonce` for the solar system and is sent `OnSetTimeDilation`, `DoSimClockRebase` and a
  series of `DoDestinyUpdate`: `AddBalls2` of its own ship, special effects and ball settings, a `SetState` with a
  ball for every station and celestial of the system (a buffer the destiny kernel decodes) with their slim items,
  then the ship's agility and top speed. `UpdateStateRequest` is answered by another `SetState`.
- From then on each command (`CmdSetSpeedFraction`, `CmdGotoDirection`, `CmdStop`, `CmdDock`) answers `None` and
  produces one `DoDestinyUpdate` with one action at the current stamp. The client integrates motion itself: the server
  says what changed, not where things are every tick.
- The destiny stamp is the sim clock in whole seconds; time dilation says how much slower than that the sim runs.

The wire encoding of balls, the motion integrator and their oracle fixtures exist (`crates/eve-destiny`). What does
not exist is the scene: which balls a system has, who is where, how commands change a ball, when a ship has docked.

## Decision

1. **The simulation is its own process**, `eve-sim` (Rust), started next to the host and the front. It owns scenes
   (one per solar system that has a player in it), their balls and the tick. It reads the static data from the SDE
   files (`EVE_SDE_DIR`), not from PostgreSQL, so it starts with nothing else running. A crash of the simulation
   does not take the network front or the host down (the reason ADR 0002 gives for separate processes).
2. **The host commands it and the host talks to the client.** The sim never touches a client connection. It hands the
   host what to send (`Notify`: a notification name, how it is addressed and the marshalled arguments) and the host
   wraps it in the session's own notification numbering, so the order the client sees is the one the host decides.
   The host owns the session, the database and every decision that is not about motion in space (may this ship
   undock, where does the character live after docking).
3. **The link is a Unix socket with the framing of ADR 0002** (`u32 length | u8 kind | body`, little endian, text
   `u16 length + UTF-8`, byte strings `u32 length + bytes`), versioned by `Hello`. One host per sim. A frame of one
   session is delivered in order.
4. **The host gives the sim numbers, not rules.** A ship enters a scene with what motion needs, worked out by dogma
   (ADR 0004): mass, radius, top speed, inertia (agility), warp speed, and what its slim item shows (names, ids,
   the modules). A change to them (skills, fitting) is another message. The sim does not know skills or modules.
5. **Messages (version 1).**

   | Kind | Code | Direction | Body |
   |---|---|---|---|
   | `Hello` | 1 | host -> sim | `u16 version` |
   | `HelloAck` | 2 | sim -> host | `u16 version`, `u32 tickHz` |
   | `Enter` | 3 | host -> sim | `u64 sessionId`, the ship's spec (below), `i64 stationId` (0 when not undocking), `u8 how` (0 undock, 1 already in space) |
   | `Leave` | 4 | host -> sim | `u64 sessionId`, `u8 why` (0 docked, 1 logged off, 2 jumped) |
   | `Bind` | 5 | host -> sim | `u64 sessionId`: the client bound the solar system's ballpark, send it the scene |
   | `UpdateState` | 6 | host -> sim | `u64 sessionId`: `UpdateStateRequest` |
   | `Command` | 7 | host -> sim | `u64 sessionId`, `u8 command`, `u8 argc`, `f64 args[argc]` (ids travel as `f64` up to 2^53) |
   | `ShipSpec` | 8 | host -> sim | `u64 sessionId`, the ship's spec: new numbers for a ship in space |
   | `Notify` | 9 | sim -> host | `u64 sessionId`, text `name`, text `idType`, bytes `marshalledArgs` |
   | `Docked` | 10 | sim -> host | `u64 sessionId`, `i64 stationId`: the ship has arrived and the docking is done in space; the host moves the character |
   | `Refused` | 11 | sim -> host | `u64 sessionId`, `u8 command`, text `reason`: a command the scene cannot do (the host answers the client with the reason) |

   The ship's spec is `i64 shipId`, `i64 characterId`, `i32 typeId`, `i32 groupId`, `i32 categoryId`, text `name`,
   `i32 ownerId`, `i32 corporationId`, `i32 allianceId`, `i32 warFactionId`, `f64 securityStatus`, `f64 radius`,
   `f64 mass`, `f64 maxVelocity`, `f64 agility`, `f64 warpSpeedAu`, `u32 moduleCount` and per module `i64 itemId`,
   `i32 typeId`, `i32 flag`.
6. **The scene is a deterministic function of its inputs.** A scene has a sim clock and steps in whole ticks (1 Hz,
   the stamp); commands are applied at the tick they arrive in; time dilation is reported and constant 1.0 until
   load says otherwise. With the same messages and the same clock the sim emits the same bytes, which is what the
   differential tests check.
7. **Static data is built once per system** (`world`): the stations and celestials as rigid balls with their slim
   items, from the SDE. A scene copies them; nothing mutates them.

## Consequences

- Good: the sim can be run, tested and benchmarked without a database, a client or the host; a scene tick is a
  Rust function of plain data; the protocol layer stays in the host, so one place numbers notifications.
- Bad: a notification travels sim -> host -> front -> client (two extra hops); at destiny's rate (a handful per
  second per pilot) this is small, and the front may take over the delivery later with the same message
  (`Notify` addressed to the front) if it shows in a profile.
- Bad: a third process to start and supervise. `tools/dev/run-eve-live.sh` starts it; a host that cannot reach it
  refuses `Undock` with a message rather than hang.
- The slice: undock, `beyonce` bind and state, `CmdSetSpeedFraction`, `CmdGotoDirection`, `CmdStop`, `CmdDock`
  (approach, then dock). Warp, stargate jumps, targeting and combat, drones and NPCs come as later slices over
  the same link (new commands and new `Notify`s, not new mechanisms).

## Alternatives considered

- **The simulation inside the network front**: one process fewer and no extra hop, but the front would number the
  session's notifications too (`sn`), so two writers would share one sequence; and a scene crash would drop every
  connection. Revisit if the hops matter.
- **The simulation inside the host (C#)**: no link at all, but it gives up the tick's predictability (GC pauses)
  and the Rust destiny kernel already verified against the reference.
- **A library called through FFI**: fastest, but couples lifetimes and memory (ADR 0002 rejected the same for
  the front).
