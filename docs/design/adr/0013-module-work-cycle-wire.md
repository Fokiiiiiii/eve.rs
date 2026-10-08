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

## Wire epoch log

* 22: the reactive armor hardener batch (W2): `SimAction` tag 12 (`ResistanceShift`) and one armor-pin byte at the end of the ship's vitals. Bumped 21 -> 22 by the integrator in `SimCodec.cs` and `crates/eve-space/src/link.rs`, with `oracle/fixtures/simlink/messages.json` regenerated.
* All later W2 additions stay on 22 until the repo is first pushed; no further bump is made per package.
