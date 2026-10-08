# ADR 0013: Module work cycles get their own message pair and one host dispatcher

- Status: accepted
- Date: 2026-10-08
- Decided by: the lead's final decisions on the mining brief (MIN-D1, MIN-D2, MIN-D3, MIN-X2) and the superweapons brief (SW-N2, SW-D8); see `scratchpad/specs/DECISIONS.md`.

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
   reconnect; the host dedupes (MIN-X2). EveJS behaviour here is unverified, so the choice is recorded in `DEVIATIONS.md`.
7. **Wire batch.** The pair is part of the one protocol bump of its wire batch, done once by the integrator (SW-D8, MIN-D2).
   Individual packages do not bump `ProtocolVersion` (`SimCodec.cs:30` and `crates/eve-space/src/link.rs:17`, both 20 on main).

## Consequences

* Two codec pairs (drone and module) to keep in sync; both are fixture-checked.
* Drone tests and `drone_flight.json` stay unchanged, which is the proof that the drone path was not disturbed.
* The OnOreMined notice is sent after commit (MIN-D11, FOUNDATIONS rule 2), so a crash after commit can drop one client message.
  This is acceptable only if the client resyncs on login, which is unverified.

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

## Wire epoch log

* 22: the reactive armor hardener batch (W2): `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals. Bumped 21 -> 22 by the integrator in `SimCodec.cs` and `crates/eve-space/src/link.rs`, with `oracle/fixtures/simlink/messages.json` regenerated.
* All later W2 additions stay on 22 until the repo is first pushed; no further bump is made per package.
* 23: the module work-cycle foundation (P1-P4) and the end report of a module's effect (FP-C1). `SimAction` tags 7-11, kinds 51-53, `ProtocolVersion` 22 -> 23 in `SimCodec.cs` and `crates/eve-space/src/link.rs`, and `oracle/fixtures/simlink/messages.json` regenerated by `tools/dev/gen-simlink-fixtures.py` (Hello and HelloAck at 23). `main` was pushed at 22 before this bump.
