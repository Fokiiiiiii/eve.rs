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
   (one per solar system that has a player in it), their balls and the ships in them. It reads the static data from the SDE
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
5. **Messages (version 1).** Every message of the host that has to be answered is answered by one of the simulation's;
   the rest are not. `oracle/fixtures/simlink/messages.json` holds the golden frames (`tools/dev/gen-simlink-fixtures.py`).

   | Kind | Code | Direction | Body |
   |---|---|---|---|
   | `Hello` | 1 | host -> sim | `u16 version` |
   | `HelloAck` | 2 | sim -> host | `u16 version`, `u32 pollHz` |
   | `Enter` | 3 | host -> sim | `u64 sessionId`, `i32 solarSystemId`, `u8 how` (0 undock, 1 already in space), `i64 stationId` (0 when not undocking), the ship's spec |
   | `EnterReply` | 13 | sim -> host | `u64 sessionId`, `u8 status` (0 done, 1 refused), text `reason` (empty when done) |
   | `Leave` | 4 | host -> sim | `u64 sessionId`, `u8 why` (0 docked, 1 logged off, 2 jumped) |
   | `Bind` | 5 | host -> sim | `u64 sessionId`: the client bound the solar system's ballpark, send it the scene |
   | `UpdateState` | 6 | host -> sim | `u64 sessionId`: `UpdateStateRequest` |
   | `Command` | 7 | host -> sim | `u64 sessionId`, `u8 command` (1 speed fraction, 2 go in a direction, 3 stop, 4 dock, 5 follow a body `(id, range)`, 6 orbit a body `(id, distance)`, 7 align to a body `(id)`, 8 warp to a body `(id, range)`, 9 jump through a stargate `(gateId, leadsToGateId)`, the second 0 when the client does not say), `u8 argc`, `f64 args[argc]` (ids travel as `f64` up to 2^53) |
   | `ShipSpec` | 8 | host -> sim | `u64 sessionId`, the ship's spec: new numbers for a ship in space |
   | `Notify` | 9 | sim -> host | `u64 sessionId`, text `name`, text `idType`, bytes `marshalledArgs` (a marshalled tuple of the arguments) |
   | `Docked` | 10 | sim -> host | `u64 sessionId`, `i64 stationId`: the ship has arrived and the docking is done in space; the host moves the character |
   | `Refused` | 11 | sim -> host | `u64 sessionId`, `u8 command`, text `reason`: a command the scene cannot do (the host answers the client with the reason) |
   | `Accepted` | 12 | sim -> host | `u64 sessionId`, `u8 command`, `i64 filetime`: a command was accepted, sent after the update it made (a `Dock` is answered with the time) |
   | `Approaching` | 14 | sim -> host | `u64 sessionId`, `u8 command`, `f64 distance`: a `Dock` could not be done from here, the ship now follows the station; the host answers the call with `DockingApproach` (the distance is metres off the station's surface) |
   | `Jumping` | 15 | sim -> host | `u64 sessionId`, `u8 command`, `i32 destinationSystem`, `i64 destinationGate`: a `Jump` was accepted (the jump-out effect was sent, sent after that update); the host queues the pilot for the system and answers the call |
   | `Jumped` | 16 | sim -> host | `u64 sessionId`, `i32 fromSystem`, `i64 fromGate`, `i32 toSystem`, `i64 toGate`: the ship has left the scene it was in and is in that of the other system, in front of `toGate`; the host moves the ship and the character and tells the client |

   The ship's spec is `i64 shipId`, `i64 characterId`, `i32 typeId`, `i32 groupId`, `i32 categoryId`, text `name`,
   `i64 ownerId`, `i32 corporationId`, `i32 allianceId`, `i32 warFactionId`, `f64 securityStatus`, `f64 radius`,
   `f64 mass`, `f64 maxVelocity`, `f64 agility`, `f64 warpSpeedAu`, `f64 shieldRechargeMs`, `u32 moduleCount` and per
   module `i64 itemId`, `i32 typeId`, `i32 flag`.

   The client's movement calls answer `None`, so the host does not wait for the sim; the sim still says what became of each command,
   after the update it made: `Accepted` (also for an order the ship already follows, which makes no update) or `Refused` (with why).
   The host needs that to end the undocking protection at the first movement that was accepted. A `Dock` is answered by `Accepted`,
   `Approaching` or `Refused`, and an `Enter` by `EnterReply`.

6. **The scene is a deterministic function of its inputs.** A scene has no clock: every call says what time it is (a
   FILETIME) and a ship's position and velocity are worked out from when they were last settled to that time with the
   carbon integrator (the ship eases towards the speed it is asked for). What the client is told is stamped with the
   destiny stamp (whole seconds of the Unix clock), never more than two ahead of the clock, as the reference does.
   Time dilation is reported and constant 1.0 until load says otherwise. With the same messages at the same times the
   sim emits the same bytes, which is what the differential tests check. The one thing that happens by itself, a docking
   finishing four seconds after it was accepted, is found by a poll of the clock (`pollHz` a second).

7. **Static data is built once per system** (`world`): the stations and celestials as rigid balls with their slim
   items, from the SDE. A scene copies them; nothing mutates them.

## Consequences

- Good: the sim can be run, tested and benchmarked without a database, a client or the host; a scene call is a
  Rust function of plain data; the protocol layer stays in the host, so one place numbers notifications.
- Bad: a notification travels sim -> host -> front -> client (two extra hops); at destiny's rate (a handful per
  second per pilot) this is small, and the front may take over the delivery later with the same message
  (`Notify` addressed to the front) if it shows in a profile.
- Bad: a third process to start and supervise. `tools/dev/run-eve-live.sh` starts it; a host that cannot reach it
  refuses `Undock` with a message rather than hang.
- The slice: undock, `beyonce` bind and state, `CmdSetSpeedFraction`, `CmdGotoDirection`, `CmdStop`, `CmdDock`
  (within the docking range; approaching a station from beyond it comes next). Warp, stargate jumps, targeting and combat, drones and NPCs come as later slices over
  the same link (new commands and new `Notify`s, not new mechanisms).

## Alternatives considered

- **The simulation inside the network front**: one process fewer and no extra hop, but the front would number the
  session's notifications too (`sn`), so two writers would share one sequence; and a scene crash would drop every
  connection. Revisit if the hops matter.
- **The simulation inside the host (C#)**: no link at all, but it gives up the tick's predictability (GC pauses)
  and the Rust destiny kernel already verified against the reference.
- **A library called through FFI**: fastest, but couples lifetimes and memory (ADR 0002 rejected the same for
  the front).

## Amendments

- 2026-09-29, stargate jumps: command 9 and the messages `Jumping` (15) and `Jumped` (16). The sim owns the crossing: a `Jump` checks the gate
  (in this system, leading where the client says, within 2.5 km of the ship's surface), stops the ship and sends the jump-out effect with the `Stop`
  in one update; four seconds later (or three after the effect's stamp, if that is later) the scene gives the ship up (`Scene::due_jumps`) and the
  sim puts it in the scene of the other system (made when it has none, `Scene::arrive`) at a point in the 45° cone in front of the gate it comes out
  of, at rest, and says `Jumped`. The host then does what is the host's: the ship's item and the character's place in PostgreSQL
  (`CharacterShips.JumpAsync`), the notifications and the session change, in the reference's order. `Leave` with `why` 2 is not needed for a jump
  (the sim moves the ship itself); a ship that cannot be put in the other system stays where it was and the sim says `Refused` with
  `DESTINATION_UNAVAILABLE`. The ship binds the ballpark of the new system as any ship does; one that came through a gate is sent the ship, the
  state and the numbers (no undock priming). The queue of a system (`OnJumpQueueUpdate`) is made of characters, so it is the host's.
  `docs/compat/DEVIATIONS.md` #23 to #26 say what differs from the reference.

- 2026-09-29, warp: command 8 (warp to a body). A warp is answered to the caller (the host awaits `Accepted` or `Refused` with `TARGET_NOT_FOUND`,
  `WARP_DISTANCE_TOO_CLOSE` or `IN_WARP`), and the ship's motion in warp is the sim's own: it aligns, enters warp, drops out and comes to rest,
  and tells the client at the reference's moments (`WarpTo` at once; two `SetBallMassive` two seconds before the dropout; `SetBallPosition`,
  `SetBallVelocity`, `SetSpeedFraction`, `Stop` at rest). What the scene worked out while a call came in is handed on with the next call or poll.
  The host's `CommandAsync` replaces `DockAsync`: any command the caller waits for is answered by its own code.

- 2026-09-29, approach and orders: commands 5 to 7 (follow, orbit, align), `Accepted` after every accepted command and `Approaching`
  (kind 14) for a dock from beyond the range. A ship that follows or orbits is steered stepwise (a quarter of a second at most) by where
  its body is (`crates/eve-space/src/motion.rs`); `docs/compat/DEVIATIONS.md` #20 says how that differs from the client's own law. The
  reference does not dock a ship that arrives by itself (`approach.json`: it follows the station to the docking range and waits for
  the pilot to ask again), and nor does the sim.

- 2026-09-29, while building the slice: `Enter` names the solar system and `EnterReply` answers it (the host must know
  whether an undock worked before it answers the client); `Accepted` answers a `Dock` (the call's answer is the time);
  the ship's spec carries the shield recharge (the damage state shows a fifth of it) and a 64-bit owner; the 1 Hz tick
  became "time comes with each call" plus a poll, because the reference itself sends the state as it is at the moment of
  the call. The undock place was ours at first and is now the reference's: the sim takes the first undock point of the
  station type's table that suits the ship (`crates/eve-space/data/undock_points.json`, from the reference's static tables;
  `docs/compat/DEVIATIONS.md` #15).
