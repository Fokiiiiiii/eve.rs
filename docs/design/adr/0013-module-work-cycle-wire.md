# ADR 0013: Module work cycles get their own message pair and one host dispatcher

- Status: accepted
- Date: 2026-10-08
- Decided by: the project lead, for the mining and superweapons wire batches. The decisions are the numbered rules and epochs of this record.

## Context

A drone that mines or salvages already runs a cycle the host decides on: the sim reports the cycle, the host grants the yield,
and the sim continues (`SimDroneWorkCycle` / `SimDroneWorkResult`, and the host path in `SpaceContainers.cs`). Module
cycles (mining lasers, salvagers, later others) need the same handshake. Two wire shapes were possible: add a source byte to
the drone pair (drone fixtures and tests then change), or add a second pair. The mining spec argued both ways (lines 34 and 544),
so the choice is recorded here.

## Decision

1. **A separate pair** `SimModuleWorkCycle` and `SimModuleWorkResult`, kinds 51-52, allocated by the integrator in the tag table
   (ADR 0013 does not allocate numbers beyond these; the table is checked against `main` before use). The drone pair is not changed.
2. **One host dispatcher.** `HandleWorkCycleAsync` and `ReplyToWorkCycle` in `SpaceContainers.cs` serve drone and module cycles.
   Only the yield function differs by source (`MineAsteroidAsync` takes a source, MIN-X-2). The logic is not copied.
3. **Parking, no timer.** At the end of a cycle the sim emits the cycle and parks the module until `SimModuleWorkResult`
   arrives. No C# timer and no wall-clock timeout are used. The sim re-sends the same `cycle_id` after N sim ticks. N is a named
   constant chosen by the implementer and recorded in the code and `DEVIATIONS.md`.
4. **Dedupe in the grant transaction.** The host records `cycle_id` in the same transaction as the grant. A repeated `cycle_id`
   returns the stored result and never grants again.
5. **Stop conditions.** Target loss and deactivation stop a parked cycle without a grant.
6. **Host restart with a cycle in flight.** The sim drops the parked effect on disconnect and re-sends the same `cycle_id` after
   reconnect; the host dedupes (MIN-X2). EveJS's behaviour here is not checked against the client, so the choice is recorded in `DEVIATIONS.md`.
7. **Wire batch.** The pair is part of the one protocol bump of its wire batch, done once by the integrator (SW-D8, MIN-D2).
   Individual packages do not bump `ProtocolVersion` (`SimCodec.cs:30` and `crates/eve-space/src/link.rs:19`, both 32 on main at 863d259; the wire epoch log below lists each bump from 21 to 32).

## Consequences

* Two codec pairs (drone and module) to keep in sync; both are fixture-checked.
* Drone tests and `drone_flight.json` stay unchanged, which is the proof that the drone path was not disturbed.
* The OnOreMined notice is sent after commit (MIN-D11, FOUNDATIONS rule 2), so a crash after commit can drop one client message.
  This is acceptable only if the client resyncs on login, which the client code read so far does not show.

## Decisions made in the foundation (protocol 23)

These are the implementation choices the decision above leaves open. They are part of the wire contract of epoch 23.

* **Kinds.** `SimModuleWorkCycle` is kind 51 and `SimModuleWorkResult` kind 52, as decided. The end report of FP-C1 (`SimModuleEnded`, sim to host, one per end of an effect) is kind 53, allocated here.
* **`SimAction` tags 7-11.** `Mining` (7: family byte ore/ice/gas, the yield numbers, crit chance and multiplier), `Salvage` (8: access bonus), `Tractor` (9), `PassiveLock` (10), `AutoTarget` (11). The spec's `Mining` also listed `ChargeTypeId`, `ChargePerCycle` and the target's type and group. They are not on the wire: the charge rides on the existing `Charged` wrapper, and the host looks up the target's type itself.
* **Three rolls on the cycle.** A module cycle carries the waste, rounding and crit rolls, drawn once per cycle, so the yield can use them without a further wire change. The host reads the crit numbers and the rounding roll when the module yield is wired (M4, M5); until then they are carried and not read.
* **Refusals.** A cycle the host does not yet settle by yield (a module's, for now) is refused `NOT_IMPLEMENTED`. Tractor, passive lock and auto-target activations are refused `NOT_IMPLEMENTED` by the simulation until their packages land. Nothing is registered as a no-op (AGENTS rule 5).
* **Park and resend.** A work cycle is sent at the cycle's end, when the target is still locked. The module is parked: no cycle is timed while it waits. The same `cycle_id` and rolls are sent again every `WORK_RESEND_TICKS` (2 s of simulation time). A park ends on the answer (accepted: the next cycle starts from the answer, refused: the effect ends with reason 5), on a deactivate (at once), or when the target is no longer locked (reason 2). The target of a work cycle is present while the pilot holds its lock; a depleted rock drops its locks.
* **Cycle ids.** Work cycle ids come from one counter per simulation process, seeded from the clock (`next_work_cycle_id`). The host keeps the last cycle it settled for each owner, so an id must never come back: a per-owner counter that restarts with the simulation or the pilot would collide with the record.
* **The host's record.** `space_work_cycles` (migration `0002_work_cycles.sql`, the space schema) keeps the last cycle settled for each owner (a drone, or a module) with its answer. It is a record of settled decisions, not a relation (`A and B with a number`) and not content: no goal or deadline. A repeat of that cycle is answered from the record and never settled again, including after a host restart.
* **Failure.** A module cycle that throws is not answered, so the simulation sends it again on its next tick (rule 3, the retry). A drone cycle is not re-sent by the simulation, so it is refused `WORK_FAILED` as before.
* **The end report (FP-C1).** Every end of an effect on the simulation's account is reported once: `SimModuleEnded` with the reason (1 cycles done, capacitor short or pilot asked; 2 target lost; 3 forbidden or offline; 4 charge spent; 5 host refused the cycle). The host removes the module from the ship it sends and sends the ship again, so the simulation drops the module's numbers.
* **Drone regression.** The drone pair and its fixtures are unchanged. Its cycle ids now come from the same counter, so one drone test's literal expectation (`cycle id 1`) became a check on command and target.

## Decisions made in epoch 24: module fuel

These are part of the wire contract of epoch 24. They add the fuel a module burns each cycle (its consumption type 713 and quantity 714; module_rules WP18).

* **The flag.** `SimEffect` carries `BurnsFuel` in bit 16 of its flags byte (`SimCodec.cs` `WriteEffect`/`ReadEffect`, `link.rs` `put_effect`/`read_effect`). The host sets it from the module's own consumption type, for every effect handler (`ShipEffect`), so no handler can forget it. A module with no type is not marked.
* **The ask.** The fuel of a cycle is asked through the work-cycle pair the mining cycles use: `SimModuleWorkCycle` with command 9 (`SimModuleWorkCommands.ConsumeFuel`, `WORK_FUEL`), with no mining numbers and no rolls (the codec refuses any). The host answers `SimModuleWorkResult`.
* **When.** The first cycle's fuel is asked at activation: the effect is parked and does not start until the host accepts (its refusal is the activation's refusal, `NO_FUEL`). Each later cycle's fuel is asked at the end of the cycle before it, when the effect goes on (not stopping, cycles left, capacitor paying for the next cycle), and the module is parked on the answer. A refused ask at a cycle end stops the effect with reason 6 (`SimModuleEndReasons.NoFuel`).
* **What the host takes.** The module's consumption quantity of its consumption type, from the ship's fuel bay (flag 133) first when the type is an ice product or a fuel block, then the cargo hold (flag 5), as the reference orders it (`fuelBayInventory.js`). It is all or nothing: a ship that does not hold the whole quantity is refused `NO_FUEL` and nothing is taken.
* **One transaction.** The debit and the answer to the cycle are written in one PostgreSQL transaction on the owner's record (`CharacterShips.ConsumeModuleFuelAsync`, `PgWorkCycleLedger` on the caller's transaction). A repeat of the cycle is answered from the record and is never debited again, also after a host restart. A refusal before the debit (not in space, not running) is not recorded, because nothing was taken.
* **Refusals by name.** The host's `NO_FUEL` is shown as `You do not have enough fuel to activate that module.` at activation. Any other host refusal of a fuel ask is `FUEL_REFUSED` (shown as the generic refusal). The reason of a stop at a cycle end is kept on the wire (6) and logged; its client notice is not verified.
* **Bodies not yet run.** A module whose effect body the host does not run keeps its own named refusal (`EFFECT_NOT_IMPLEMENTED`), and no fuel is taken for it. That covers the doomsday, burst, lance, titan phenomenon and entosis effects, and the cyno field, jump portal and clone vat bodies.
* **Known gaps (epoch 24).** (a) A start the host accepted is re-checked for capacitor when it starts; if the capacitor has gone short meanwhile, the start is refused `NOT_ENOUGH_CAPACITOR` and the fuel is already taken (no refund). (b) The target of an effect with a target is checked at the activation, not again when the fuel answer comes back; a target lost during the round trip is not caught until the next cycle end. (c) A deactivation that races an accepted fuel answer cancels nothing: the fuel is taken and the effect does not start. All three are a window of one host round trip; none is verified against the reference.

## Decisions made in epoch 25: the doomsday strike

These are part of the wire contract of epoch 25. They add `SimAction` tag 13 (`Strike`), the area a doomsday hits, and nothing else.

* **The shape.** `Strike` is one action for every doomsday area. Its shape byte is the static data's `doomsdayAOEShape` (1 a cylinder, 3 a cone); the simulation refuses any other value. The general `Area` type (ADR 0011) is not built: the strike is its first use, for one family.
* **The numbers.** The host sends the unit aim (from the ship's centre), the length, the radius of a cylinder, the warning and the pulse cadence in milliseconds, the pulse count, the four damage values (em, thermal, kinetic, explosive) per pulse, the weapon's signature radius, the immobility and no-jump windows, and the energy neutralisation (amount, radius, signature radius). The simulation computes the area from the firing ship's hull surface and applies the numbers. A malformed strike (a non-unit aim, a zero length, no pulse) is refused at decode and at activation (`BAD_STRIKE`).
* **The timing.** The cycle start sets the windows and the neutralising wave; the pulses are moments of the module scheduler (`Moment::StrikePulse`), each at the warning plus the pulse index times the cadence, counted in `Running.strike_done` and reset at each cycle start. No timer is kept outside the scheduler.
* **The windows.** The firing ship's `immobile_until` and `no_jump_until` (set at the cycle start, not on the wire) refuse warp, jump and steering (`IMMOBILE`), and jump and cloak (`NO_JUMP_OR_CLOAK`). The reference's docking and tether refusals are not in this epoch (DEVIATIONS 131).
* **The sharing.** A pulse hits through the same damage path as a smartbomb (`Scene::damage_ship`, extracted from the smartbomb's loop, not copied).
* **Fuel.** The strike does not add a fuel message: a doomsday's consumption (713) marks the effect `BurnsFuel` in the handler (`ShipEffect`), as epoch 24 defined.

## Decisions made in epoch 26: the titan doomsday

These are part of the wire contract of epoch 26. They add `SimAction` tag 14 (`Doomsday`), the one hit of a titan doomsday, and nothing else.

* **Shape.** `delay_ms` (the reference's `damageDelayDuration`, 7500 to 12000 ms on the four titans), four damage kinds (em, thermal, kinetic, explosive; one of them is 2 400 000 on each titan), and the two windows `immobile_ms` (2428) and `no_jump_ms` (2427). Nothing is sent for the target: the effect's `TargetId` is the locked ship, as for every targeted effect.
* **The hit.** The simulation applies the damage to the locked target once, `delay_ms` after the cycle starts, if the target is still in the scene and not under its undocking protection. The damage takes no signature factor. A target that is not locked when the module starts is refused (`TARGET_NOT_LOCKED`), and one that is not in the scene (`TARGET_NOT_FOUND`); a missing target is refused by the host (`TARGET_REQUIRED`).
* **The windows** are set at the cycle start, as the strike's are (`hold_for_windows` in `modules.rs`); the same refusals (`IMMOBILE`, `NO_JUMP_OR_CLOAK`) and the docking refusal `DOCKING_BLOCKED` in the no-jump window apply.
* **Validation.** A non-finite or negative number is refused by the codecs (`BadValue`) and by the simulation (`BAD_DOOMSDAY`).
* **Not on the wire.** A chain (the reference's lightning attributes 2104 to 2106 are absent on the titans, so the chain is one ship and is not sent).

## Decisions made in epoch 27: the target's energy warfare resistance

These are part of the wire contract of epoch 27. They add one number to the ship, `energyWarfareResistance` (attribute 2045, "Capacitor Warfare Resistance"), and nothing else.

* **The number.** Each ship's `SimSensors` block carries a sixth number after `EcmResistance`: the hull's own value of 2045 (1 for an ordinary ship, 0 for one that cannot be neutralised). The host reads it from the type's attributes (and the fitted world, where the SDE default applies); a type that does not name 2045 sends 1. Zero is immune, not missing.
* **The use.** The simulation multiplies an energy neutralisation's amount by the target's value: a neutraliser (`Remote::Neutralize`) and the neutralising wave of a doomsday strike. The strike's resistance is 2045 on every doomsday that carries a wave in the static data (`doomsdayEnergyNeutResistanceID`, 2609, is 2045 on the Bosonic Field Generator, the four lances and the three reapers; the titans carry no wave).
* **Not on the wire.** The nosferatu (`Remote::Drain`) is not weakened; the reference's resistance for it is not read (DEVIATIONS 112). `remoteResistanceID` is not sent.
* **The codecs.** `SimCodec.cs` and `crates/eve-space/src/link.rs` (`ProtocolVersion` 26 -> 27, once), the fixture generator (`tools/dev/gen-simlink-fixtures.py`, a non-default value 0.25 on the Ibis so a swapped field fails the byte comparison), and `oracle/fixtures/simlink/messages.json`.

## Decisions made in epoch 28: the sweep and the disruption of a strike

These are part of the wire contract of epoch 28. They add two fields to the end of the strike (`SimAction` tag 13, after the neutralising wave) and nothing else.

* **Disruption.** `debuffMs` (f64, 0 for none): for this long from a pulse that covers a ship, the ship cannot warp, jump or dock and takes half the remote repair from another ship. The disruptive lance's `doomsdayAppliedDBuffDuration` (5412, 60 000 ms). The debuff lands on every ship the pulse covers, whatever its signature takes of the damage, and refreshes to the later expiry (EveJS `disruptiveLanceDebuff.js`).
* **Sweep.** A flag (u8, 0 or 1) and the end of the axis (three f64, zeros without a sweep). With a sweep, the axis at pulse `k` runs from the firing ship's centre along `start + (end - start) * k / (pulses - 1)`, where `start` is `direction * length`; the area starts at the centre, not the hull (EveJS `resolveSlashDamageVolume`). A pulse whose axis has no length hits nothing. A sweep whose end is the ship's centre is invalid.
* **Not on the wire.** The siege requirement (5426) is decided by the host (named refusal), and the cloak disallow (5425) is not enforced.
* **The codecs.** `SimCodec.cs` and `crates/eve-space/src/link.rs` (`ProtocolVersion` 27 -> 28, once), the fixture generator (`tools/dev/gen-simlink-fixtures.py`: a reaper sweep, a disruptive lance, and a sweep that ends on the ship as an invalid frame), and `oracle/fixtures/simlink/messages.json` (Hello and HelloAck at 28). Bumped once, in this batch (local, not pushed).

## Wire epoch log

* 21: the drone remote effects (webs, repairs, jams); `main` was at 21 before the reactive armor hardener batch.
* 22: the reactive armor hardener batch (W2): `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals. Bumped 21 -> 22 by the integrator in `SimCodec.cs` and `crates/eve-space/src/link.rs`, with `oracle/fixtures/simlink/messages.json` regenerated.
* All later W2 additions stay on 22 until the repo is first pushed; no further bump is made per package.
* 23: the module work-cycle foundation (P1-P4) and the end report of a module's effect (FP-C1). `SimAction` tags 7-11, kinds 51-53, `ProtocolVersion` 22 -> 23 in `SimCodec.cs` and `crates/eve-space/src/link.rs`, and `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 23). `main` was pushed at 22 before this bump.
* 24: module fuel. `SimEffect` flags bit 16 (`BurnsFuel`), the fuel ask as `SimModuleWorkCycle` command 9, and end reason 6 (`NoFuel`). `ProtocolVersion` 23 -> 24 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 24) with the fuel cases. Bumped once, by the integrator, because `main` was pushed at 23.
* 25: the doomsday strike (`SimAction` tag 13, the Bosonic cone's body). `ProtocolVersion` 24 -> 25 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 25) with a valid cone strike and two invalid strikes. Bumped once, in this batch: `main` was at 24 when the batch started, and the work is local (not pushed).
* 26: the titan doomsday (`SimAction` tag 14, `Doomsday`). `ProtocolVersion` 25 -> 26 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 26) with a valid Judgment hit and one invalid frame (a negative delay). Bumped once, in this batch (local, not pushed).
* 27: the target's energy warfare resistance (`energyWarfareResistance`, 2045) as a sixth number of the ship's sensors block. `ProtocolVersion` 26 -> 27 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 27, the Ibis at 0.25). Bumped once, in this batch (local, not pushed).
* 28: a strike's `debuffMs` (the disruptive lance's debuff: no warp, jump or dock, half the remote repair) and its sweep (a reaper's axis from its start to its end point over the window). `ProtocolVersion` 27 -> 28 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 28). Bumped once.
* 29: the transport field of the Gravitational Transportation Field Oscillator (`SimAction` tag 15, `Field`): a strike of shape 4 (a sphere centred on the aim point, radius `doomsdayAOERange`), then the destination offset, the warp block of the held capitals (ms), and two lists of ship ids (a u32 count, then each i64: the carried ships and the held ships). The simulation carries the ships of `moved` inside the sphere to the destination and holds those of `held` (`warp_blocked_until`, refused `WARP_BLOCKED`). `ProtocolVersion` 28 -> 29 in `SimCodec.cs` and `crates/eve-space/src/link.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 29) with a valid field (one moved and one held ship) and the fixture mapper of both codecs reads the `field` kind. The lists are the host's (`ShipSnapshot.Neighbours`), not a sim query (decision D3). Source conflicts and their resolution: DEVIATIONS 135 and 141.
* 30: the tractor beam. `SimAction` tag 9 carries the beam's range. `ProtocolVersion` 29 -> 30 (commit e48088b).
* 31: player fighters, the first messages: `LaunchFighter`, `FighterOrder`, `FighterResults`, `FighterLanded` and `FighterReleased` (kinds 54 to 58). `ProtocolVersion` 30 -> 31 (commit a58a435, ADR 0015).
* 32: the fighter batch that completes the squadrons: `FighterGotoPoint` (kind 59), the squadron size on the launch and `FighterSize` (kind 60), and the Tackle remote (`SimRemote` tag 12). `ProtocolVersion` 31 -> 32 (commit e20ba2f, ADR 0015 third amendment).
* 33: the module rules (flag 32 of `SimEffect`: the group, its limit, the rule bits and the reactivation delay), track B. `ProtocolVersion` 32 -> 33 (commit da6c524).
* 34: the bombs and bubbles track (2026-10-09). `SimAction` tag 5 carries whether a cloak is warp-safe (one byte). New tags: 16 `WarpBubble` (a ship's warp bubble while its effect runs), 17 `WarpImmunity` (an interdiction nullifier's immunity while it runs), 18 `ScrambleStatus` (the warp core stabilizer's change of the scramble status while it runs), 19 `WarpProbe` (an interdiction probe in space for its lifetime), 20 `Bomb` (a bomb released at the ship, detonating where it is), 21 `StasisBurst` (the web of a stasis charge in its area), 22 `Defender` (a defender missile, target-less). `ProtocolVersion` 33 -> 34 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 34). One bump, in this batch: the other tracks of the same day bumped no further when this was written (checked against main before the push).
* 35: the turret's `requiresGunnery` byte (after its arc, `SimTurret.RequiresGunnery`; a hull gun of an NPC carries it in `SimNpcWeapon.RequiresGunnery`, true for the laser, projectile and precursor families): a tracking disruptor or computer on the shooter reaches only a weapon whose type requires Gunnery (SDE required skill 3300), as EVE.js `projectedWeaponModifiers.js` and the client's `SKILL_MODIFIER_CHANGES_FOR_LOCATION` do (FP-10). `ProtocolVersion` 34 -> 35.
* 36: the fighters' kamikaze detonation (`SimAction` tag 23, `Kamikaze`) and the fighter missiles (tag 24, `Missile`: the damage, the reach and the explosion for the application factor). `ProtocolVersion` 35 -> 36 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py`.
* 37: the fighter's LaunchBomb blast (`SimAction` tag 25, `Blast`: a centre, a radius and the four damages; the blast goes off at the end of the window, as the Area of ADR 0011 for a point and no ball). `ProtocolVersion` 36 -> 37 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated (one valid bomb, two invalid). Bumped once, in the track C3 batch (pushed after a check of `origin/main`).

* 38 (track B5, 2026-10-09): three additions, one bump. Track C3 took 37 (the fighter LaunchBomb blast) on main, so this batch is 38. (a) `SimUpdateEffect`, kind 61, host to simulation: a refresh of a running effect (its cycle, the capacitor a cycle costs, the turret, and the attributes the client is told). (b) `Charge.reloads`, one byte after the charge's per-cycle count: the gun reloads from its hold when its rounds run out. (c) Two speed limits after the module rules' reactivation delay (the highest and the lowest speed a module starts at, `maxVelocityActivationLimit` 1334 and `minVelocityActivationLimit` 2608; 0 for none). `ProtocolVersion` 37 -> 38 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 38; 146 valid and 35 invalid frames).
* 39: the lance beacon. The strike (`SimAction` tags 13 and 15) ends with a flag and an offset from the firing ship (zeros without a beacon): the beacon is a transient container that the simulation puts in space when the strike starts, and the beam is played on it (`Strike.beacon`; `Role::Beacon` in `crates/eve-space/src/deployable.rs`). The titan lances, the disruptive lances and the Bosonic cone send one. `ProtocolVersion` 38 -> 39 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated (Hello and HelloAck at 39). Bumped once, after track B5's 38, because the strike's frame changes.
* 44 (track A5, 2026-10-09): the cynosural field record (ADR 0012, `Deployed`). Track C6 took 43 (its batch, kind 62 `BuffState`) on main first. Two kinds, both on the existing message enum: 63 `Deployed` (the simulation says a
  field was lit, with its place, name, type, owner, corporation, alliance, ship and whole life; the host sends the same message back to put a field in space after a
  restart, with the life left) and 64 `RemoveDeployed` (the host takes a field out when its record's time is up). `ProtocolVersion` 43 -> 44 in
  `crates/eve-space/src/link/mod.rs` and `SimCodec.cs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (three
  valid frames and two refused frames added; no existing frame changed). Protocol 43 (C6) was the last on `main` before this track.
* 45 (track A5, 2026-10-09: scanner probes): kinds 65 `PlaceProbe` (the host puts a launched probe in space as a ball, with its id, type, place, radius and owner)
  and 66 `RemoveProbe` (the host takes it out when it is recalled or its time is up). Both host to simulation. `ProtocolVersion` 44 -> 45 in
  `crates/eve-space/src/link/mod.rs` and `SimCodec.cs`; `messages.json` regenerated with the generator (the probe frames and one refused frame added).

* 46 (track B6a, 2026-10-09): the ECM burst report (FP-C7). A new sim-to-host message `SimEcmBurst` (kind 67): the ship that runs the burst and each ship it reached (a cloaked ship is not reached) with a jam flag byte (0 or 1). `ProtocolVersion` 45 -> 46 in `SimCodec.cs` and `crates/eve-space/src/link/mod.rs` (39 to 45 were taken on `origin/main` by the other tracks that landed first: the scanners, the cynosural field, the breacher pods, the command bursts and the probes); `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 46). Bumped once, in this batch.
* 47 (track C8, 2026-10-10): `SimShip` carries `DroneDamageMultiplier` (a trailing f64 after the sensors, `ShipSpec.drone_damage_multiplier` in the simulation): the simulation scales a drone's shots by its controller's multiplier (the SDE's industrialBonusDroneDamage 2580, and the wiki's P.A.N.I.C. penalty; DEVIATIONS "Track C8"). `ProtocolVersion` 46 -> 47 in `dotnet/src/Eve.Space/SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; the fixtures (`tools/dev/gen-simlink-fixtures.py`, `oracle/fixtures/simlink/messages.json`) are regenerated.

## Decisions made in epoch 38

- **The refresh (`UpdateEffect`) is applied at the start of the effect's next cycle**, the same boundary an overheat takes (`pending_overload`). The cycle that is on is not changed. No answer is sent. The host sends it only when the rebuilt numbers differ from those the simulation holds (the comparison key is the encoded update, with the rounds left out because the simulation spends them). The attribute change the client is told is sent at the cycle that takes it, with the old value from the effect's own changes.
- **A reloading turret parks when its rounds run out (no cycle is timed).** The parked gun is resumed by the host's `AdjustCharge`: rounds loaded resume the gun at the moment the answer comes (its next cycle starts then, and its first shot spends a round); an answer that brings no rounds ends the effect with `NoRounds`. A deactivate ends a parked gun at once. The reload time itself is the host's: it times the reload as a pilot's load in space is timed (`DogmaIMService.StartReload`), so the simulation does not time it. A gun that was not parked (`reloads` false) stops as before.
- **Speed limits are refused at activation** (`VELOCITY_TOO_HIGH`, `VELOCITY_TOO_LOW`), by the ship's speed as the scene moves it. Both reasons are the simulation's own names (no client text exists for them).

Sources: the EVE.js module reload (`localCycleReloads.js`, `dogmaService.js` `queueAutomaticModuleReload`, `moduleChargeReload.js`), the EVE.js live attribute refresh (`liveModuleAttributes.js`), and the SDE attributes 1334 and 2608 (named in DEVIATIONS "Module rules, track B5").
* 40: the scanners (track M-6). `SimAction` tag 26 (`Scan`: a kind byte, 0 cargo or 1 ship, and the optimal and falloff in metres) and the work commands 10
  (`ScanCargo`) and 11 (`ScanShip`) of the module work cycle. `ProtocolVersion` 39 -> 40 in `dotnet/src/Eve.Space/SimCodec.cs` and
  `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 40, two valid
  scans, two invalid scan frames, and the unknown work command moved to 12). The decisions are in DEVIATIONS "Scanner modules, track M-6".
* 43 (track C6, 2026-10-09): `SimAction` 28 (`CommandBurst`, the reach) and 29 (`Panic`, the reach); `ModuleWorkCycle` carries the recipients (a count, then the ship ids) for commands 12 (`Burst`) and 13 (`Panic`); kind 62 (`BuffState`, host to simulation: a ship's buffs, told to its pilot as `OnDbuffUpdated`). `ProtocolVersion` 41 -> 42 in `dotnet/src/Eve.Space/SimCodec.cs` and `crates/eve-space/src/link/mod.rs`; `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 43, 158 valid and 47 invalid frames). Bumped once, in this track. The decisions are in DEVIATIONS "Command bursts end to end and P.A.N.I.C. (track C6, 2026-10-09)".


## Decisions made in epoch 46: the ECM burst report

These are part of the wire contract of epoch 46. They add the sim-to-host message `SimEcmBurst` (kind 67) and nothing else.

- **The message.** Sent once per cycle of a burst that reaches at least one ship: the session, the ship that runs the burst, and each ship reached with whether its roll jammed it. The host records the attack on each pilot reached (`SpaceSessions.RecordEcmBurstAsync`). A reached ship that is not a pilot's (an NPC's) is not recorded.
- **The reach.** The ships in the radius of the surface of the burst's ship (not its own), in the order they entered the scene, excluding a fully cloaked ship. Each reached ship is rolled; a jam breaks its locks and leaves no jam. Sources: EVE.js `executeJammerBurstCycle` (the candidate filter and the roll order), and the wiki's Electronic Countermeasures page (the burst jams friends too).
- **The gate.** A burst is refused `SafetyActivated` at activation in high security space (security 0.45 and above) unless the pilot's safety is none. The source is the wiki's Electronic Countermeasures page; EVE.js refuses only the smartbomb. The conflict is recorded in DEVIATIONS "Track B6a".
- **Not on the wire.** The bomb's ECM kind (`jam_at`) is not reported.

## Addendum (2026-10-11, ST-1: the structure ball, protocol 59)

- **Protocol 59.** Three host-to-simulation kinds, one per action: 74 `PlaceStructure` (a structure the host keeps a record of is put in space as a body of its system: its ball, its owner and its slim state), 75 `RemoveStructure` (the body goes and those who had it in view are told `RemoveBalls`), 76 `SetStructureState` (the slim state changes and those who have it in view are told `OnSlimItemChange`). `PROTOCOL_VERSION` 58 -> 59 in `crates/eve-space/src/link/mod.rs` and `SimCodec.ProtocolVersion`; the fixtures are regenerated by `tools/dev/gen-simlink-fixtures.py`.
- **The host decides the state.** The state, timer, upkeep, unanchoring and repairing values are the host's (ADR 0012, rule 3); the simulation shows them and computes none of them.
- **Protocol 60 (ST-1 b: the structure's health).** Protocol 59 went out with the placement of increment a and no health. `PlaceStructure` now carries the vitals (shield, armor and hull points, and the four-kind resonances of each layer), the shield's recharge in milliseconds, the per-second limit of each layer (SDE 2034-2036) and its repair pause (SDE 3354-3356). Kind 77 `StructureDamaged` is simulation to host: the layer a hit emptied (0 none, 1 shield, 2 armor, 3 hull) and whether a layer's repair is paused. `PROTOCOL_VERSION` 59 -> 60 in the same files. The simulation cuts each hit to the per-second limit of the layer it lands in; the host applies the consequences (ST-0).
- **The ball.** A structure is a rigid station ball (`Kind::Station`) with category 65 in its slim item. The client's kind for an Upwell structure ball is not verified on the real client (see DEVIATIONS "Structure ball (ST-1)").
