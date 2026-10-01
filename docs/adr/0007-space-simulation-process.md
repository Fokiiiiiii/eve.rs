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
5. **Messages (version 2).** Every message of the host that has to be answered is answered by one of the simulation's;
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

- 2026-09-29, modules: the message `Activate` (19: `session u64`, an effect: module id, module type, effect id, the effect's name in the destiny stream, the length of a cycle in ms, the capacitor a cycle takes, the cycles asked for, the target, the ship's mass, top speed and signature with the effect and without it (each present or not by a flag), and the values the client is told at the start and at the end, as `(item, attribute, value)`), answered under command 17 by `Accepted` or `Refused` (`ALREADY_ACTIVE`, `NOT_ENOUGH_CAPACITOR`, `BAD_EFFECT`), and the command 16 (`Deactivate`, the module's id: `Accepted` or `Refused` `NOT_ACTIVE`). The ship's spec carries the capacitor (its capacity in GJ and its recharge time in seconds). What an effect does to the ship is the host's to work out (dogma); the sim runs the time (`modules.rs`): the cycles, the capacitor's charge and what the client is sent at each moment (the module's and the ship's values, `OnGodmaShipEffect` at every cycle and when it ends, the `OnSpecialFX` and the ship's numbers in the destiny stream to the pilot and to those who watch). A stop asked for takes effect at the end of the cycle. `docs/compat/DEVIATIONS.md` #32 to #35 say what differs from the reference.

- 2026-10-01, player turrets: the flags byte of an effect in `Activate` has a third bit (4): a turret follows the two lists of changed values, as the NPC
  weapons of a `Spawn` carry it (the type of the weapon, the effect the client plays, the damage of a shot as em, thermal, kinetic and explosive, the range, the
  falloff, the tracking and the signature resolution, all `f64` but the first two). The host works the turret out from the module after dogma (`ModulePackages`) and
  the sim shoots at the start of each cycle, as it does for an NPC. The sim answers a weapon aimed at what is not in the scene with `TARGET_NOT_FOUND` (before
  `TARGET_NOT_LOCKED`), and a weapon stops at once when the lock on its target goes, however it goes (the pilot lets go, the target leaves or is destroyed, a
  warp): the update, the values, `OnGodmaShipEffect` off and then `OnTarget lost`, the order of the recording of the reference's gun. A turret's `OnSpecialFX` carries
  the cycles asked for at its start and nothing at its stop. `docs/compat/DEVIATIONS.md` #33 and #56 say what differs from the reference.

- 2026-10-01, version 2: the turret block of an effect and of an NPC weapon ends with the charge byte and `ChargeSpent` (27) is a message, so a frame of version 1 is not understood by version 2 and the other way round. `Hello` and `HelloAck` carry 2; a host and a simulation that do not agree on it do not talk (the host refuses a `HelloAck` of another version, the simulation closes a `Hello` of another version).

- 2026-10-01, ammunition: the turret of an effect (and of an NPC weapon) ends with a charge: a byte, and when it is 1 the type of the charge `i32`, the flag of the
  slot it sits in `i32`, the rounds it holds `i64` and the rounds a cycle burns `i64` (a turret that takes no charge has the byte 0). The client knows a loaded charge
  by `(ship, flag, type)`, so the sim tells the rounds in the slot as attribute 805 of that key. The sim spends the rounds of a turret after each shot (the first
  shot's after the shot), tells the host with the message `ChargeSpent` (27: `session u64, module i64, rounds left i64`, which the host keeps as the quantity of the
  charge item and drops at 0), refuses a gun that has fewer rounds than a cycle burns (`NO_AMMO`, the host's `NoCharges`) and stops a gun that has none for the next
  cycle with `OnGodmaShipEffect` off and the user error `NoCharges`. `docs/compat/DEVIATIONS.md` #57 says what the reference does that this does not.

- 2026-09-29, targeting: commands 10 to 15 (`AddTarget`, `CancelAddTarget`, `RemoveTargets`, `ClearTargets`, `GetTargets`, `GetTargeters`; ids as
  the `f64` arguments) and the messages `Targets` (17: `session u64, command u8, pending u8, count u32, ids i64…`) and `TargetRefused` (18: `session u64,
  command u8, reason text, target type i32, desired range f64, actual distance f64`), which answer them (the host awaits by `(session, command)`, one
  order of a pilot at a time). The ship's spec carries what a lock depends on: `maxTargetRange`, `scanResolution`, `signatureRadius` (three more
  `f64` after the shield recharge) and `maxLockedTargets` (an `i32`, the lower of the ship's and the pilot's). The sim owns the locks (`targeting.rs`):
  a lock is asked for and answered as going; the poll has it when its time is up; a lock that can no longer be had is dropped (either ship warps,
  the range, the ship is gone) and one that is held is told as `OnTarget lost`. `OnTarget` and the damage state (`OnDamageStateChange` in a
  `DoDestinyUpdate`) travel as `Notify`. The sim keeps its own copy of the undocking protection (thirty seconds from the undock, ended by an accepted
  movement or a lock that was asked for) to refuse a lock on a protected ship; the host still tells the client when it ends. A refusal that carries
  the number of locks a ship may hold (`TARGET_LOCK_LIMIT_REACHED`) puts it in the place of the range. `docs/compat/DEVIATIONS.md` #29 to #31 say what
  differs from the reference.

- 2026-09-29, other ships: a scene holds several pilots and each is the audience of the others. A pilot that has bound the ballpark keeps the
  set of ships it has been told of (`Pilot::sees`), brought into line with `visibility::can_see` when a ship comes, goes or is in warp
  (`crowd.rs`). What happens to a ship is told to whoever has it in view (`tell_all`): an order, the stop of a docking, the drop-out and the rest
  of a warp; the heading at a warp order and the warp when it starts are told to the watchers only (`tell_split`); `Enter` and `Leave` answer with the
  notifications for the others, and a poll takes a docked or jumped ship from their view. The first `AddBalls2` of a bind and the `SetState` list
  the ships in view in the order they came into the scene, the numbers follow for each. No message of the link changed: a `Notify` names the
  session it is for. `docs/compat/DEVIATIONS.md` #23 (ambient ships), #27 and #28 say what differs from the reference.

- 2026-09-29, health: a ship's spec carries what it can take (`shieldCapacity`, `armorHP`, `structureHP` in points and the twelve resonances, em, thermal,
  kinetic and explosive for the shield, the armor and the hull, all from dogma) and what it has taken as four fractions (`damage`, `armorDamage`,
  `shieldCharge` and the capacitor's `charge`), after the locks' numbers and before the modules. The damage state a client is given for a ship
  (`AddBalls2`, `SetState`, `OnDamageStateChange`) is the ship's own now and no longer the constant of a whole ship. `Docked` carries the four
  fractions the ship has at that moment (the host keeps them with the ship); a jump moves the ship's health with it, inside the sim. Two commands: `Heal`
  (18: full shield, armor, hull and capacitor; the pilot is told the attributes that changed, the capacitor and, when something changed, everyone
  who sees the ship its damage state, and the answer, `Accepted`, comes last so that what the host tells after it is told after) and `GetCondition`
  (19, answered with the new message `Condition`, kind 20). A ship comes out of its station full-shielded and full-capacitored whatever the host says it
  had (the host says it so: the reference does). Damage itself has no message: it will be done by the effects the sim runs (weapons, missiles, NPCs), and
  `Scene::damage` is what they will call. The shield's charge is told to its pilot every half second while it recharges, as the capacitor's is
  (`docs/compat/DEVIATIONS.md` #37).

  **Deaths and NPCs (S5c-3d).** A ship whose hull is gone is destroyed by the scene (`crates/eve-space/src/death.rs`): whoever has it in view is told to
  play the explosion and let go of the ball in one stamp (`TerminalPlayDestructionEffect` with effect 3, `RemoveBalls`, as recorded in
  `oracle/fixtures/scenarios/death.json`), it stops being a pilot, and it leaves a wreck (`wreck.rs`: a ball of kind wreck with the reference's slim
  item, in the ballpark of whoever can see the place, gone after two hours) that the scene makes itself for an NPC and the host makes for a player. New
  messages: `Destroyed` (kind 21, sim to host: the session, the ship, the wreck the scene made (0 for none), where and how the ship was, the ship that
  did it), `Spawn` (22, host to sim: an NPC with its ship, place, tree, reactions, weapons and wreck, answered with `SpawnReply`, 23) and `Destroy`
  (24, host to sim: destroy this ship). Nothing of an NPC is told to the host (nobody is there to hear it) but its destruction: `Destroyed` carries the NPC's session and ship and the ship that dealt the
  last of its hull, and the host, which made the NPC and knows what it was worth, pays that pilot the bounty (S5c-3d-7). A player's death is told too, and the session
  is no longer in space until the host brings it back.

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

- 2026-10-02, version 3, cans: a pilot who jettisons cargo makes a can (an item of the host's) that the scene puts in space. `PlaceContainer` (28: `session u64`, `container_id i64`,
  the can's kind as a wreck's (`1`, the type `i32`, the name, the graphic `i32`), the radius `f64`, the launcher `i64`, the credit as in `PlaceWreck`, and the distance
  `f64`) puts the can that far ahead of the pilot's ship (never nearer than fifty metres), at rest, and those who see it are told in the reference's order: the ball
  (`AddBalls2`; the slim item of a can has no turn, and no `lootRights` until something is in it) and the effect that plays the jettison (`OnSpecialFX`
  `effects.Jettison`, played on the can), in one stamp; the host is answered with `ContainerPlaced` (29: `session u64`, `container_id i64`, the place and the way it
  lies as two vectors of `f64`), or `Refused` under the command code 28. `ChangeWreck` (30: `session u64` of the pilot who changed it, `wreck_id i64`, `is_empty u8`) tells whoever has a wreck or a can in
  view its slim item again (`OnSlimItemChange`, now with the loot rights) and answers the pilot's host with `Accepted` under the command code 30 after that, or `Refused` and `RemoveWreck` (31: `wreck_id i64`, `effect i32`) takes it out of space, playing the
  destruction effect first when `effect` is not 0 (the can that is emptied: 3). The command 20 (`Command`, the wreck's id) asks how far a wreck or a can is, from the
  centre of the pilot's ship to the surface of the wreck (what the reference measures the range of a container by), and is answered with `Measured` (32:
  `session u64`, `command u8`, a flag and the distance `f64` when there is a wreck). `Hello` and `HelloAck` carry 3. `oracle/fixtures/simlink/messages.json` holds the
  frames; `docs/compat/DEVIATIONS.md` says what differs from the reference.

- 2026-10-02, what lies in space: the simulation keeps nothing between runs, so the host keeps what the scene is given that is an item of its own (a can, the wreck of a destroyed ship, a pilot's corpse): where it lies,
  its group, whose it is and until when, in its own schema (`space.space_items`). When a ship comes into a scene (an undock, a jump) the host sends `PlaceWreck` for what lies in that
  system and the simulation does not have (it remembers what it was given since the link was made, and forgets all of it when the link is lost), with `is_empty` as what is in the item says, and ends
  what has run out of time with `RemoveWreck`. A wreck made by the simulation for an NPC has no item and is not kept.
