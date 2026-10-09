# ADR 0012: Timed deployed objects share one mechanism, and one job expires them

- Status: accepted
- Date: 2026-10-08
- Decided by: the lead's final decisions on the superweapons brief (SW-D2, SW-N1, SW-O3) and the mining brief (MIN-D12); see `scratchpad/specs/DECISIONS.md`.

## Context

Wrecks and cans already exist in space. Cyno fields, jump portals and fleet beacons are new and need the same thing: an object
that is placed in a system, watched, and removed when its time is up. Two expiry paths were proposed: a new job per kind
(`space.deployed.expire`, `space.container.expire`) or the existing sweep.

What exists today: `space.containers.sweep` is a recurring scheduler job every 30 s (`dotnet/src/Eve.Host/Program.cs:874-875`).
`ExpireDueAsync` reads the database (`SpaceContainers.cs:423-431`) and calls the sim to remove the object. Because the
deadline is a row, it survives a restart. The sim also has its own timer-like expiry for wrecks (`crowd.rs:468-490`).

## Decision

1. **One mechanism.** `Deployed` is the shared record of a timed object in space: id, kind (wreck, can, cyno field, portal,
   beacon), owner, system, deadline, and the sim ball it is attached to. Its placement and watcher code is extracted from
   `crowd.rs` (`place_container`, `expire_wrecks`) into a new `placed.rs`. It mirrors the existing `Wreck` (`wreck.rs`).
2. **One expiry owner.** `space.containers.sweep` expires every `Deployed` kind. No `space.deployed.expire` and no
   `space.container.expire` job are added. The sweep is extended, not replaced.
3. **The sim does not decide expiry.** It removes what the host tells it to remove. The sim-side expiry in `crowd.rs:468-490`
   is removed only after the host removal path is verified in a test, because removing it first leaves wrecks alive in the sim.
4. **Idempotent handlers.** The sweep may touch the sim, so it can run twice; a second removal of an already removed object is
   a no-op (FOUNDATIONS rule 2).
5. **Beacon.** A fleet beacon is cleared only when no live `Deployed` depends on it (SW-O3). The scene owns in-scene life;
   the sweep only removes what is past its deadline.

## Consequences

* Expiry is as late as the sweep interval (up to 30 s). Objects with a shorter lifetime would need the interval revisited.
  This is accepted; no current feature needs less.
* The sweep dispatches by kind through a registry with one entry per kind, not an `if` chain (FOUNDATIONS checklist).
* Expiry survives a restart, because the deadline is a database row and the job is recurring.
* The `crowd.rs` extraction collides with the mining change `M6b` (`change_asteroid` fan-out). Order: `M6b` first (see `DECISIONS.md`).

## Addendum (2026-10-08, bombs and warp disruption brief)

- Probes stay simulation-only (bombs brief D11): an interdiction probe's lifetime is its SDE `explosionDelay`, checked in the simulation tick. Nothing is persisted, so a restart drops it (a stated LIMITATION). It is not a `Deployed` row, because the sweep runs every 30 s and would make a 120 s probe up to 25 % late.
- Mobile warp disruptors are not deployable yet (D6): no SDE lifetime exists, and the EveJS table is unverified. When they are, they are `Deployed` rows and the expiry job stays `space.containers.sweep`; anchoring is a separate job, `space.deployed.anchor`, which is not an expiry job.
- Nothing is written to the sweep for the field generator: its bubble is refused (`EFFECT_NOT_IMPLEMENTED`) until its ship penalties have a source (D5).

## Addendum (2026-10-09, the bombs and bubbles track: deployables in the simulation)

- The interdiction probe and the bomb are deployables of the simulation (`crates/eve-space/src/deployable.rs`): a free ball each, kept apart from the ships, with an end
  time on the simulation's clock (the SDE's explosion delay). They are not `Deployed` rows, for the reason of the first addendum: the 30 s sweep would end a 120 s probe late.
  A restart drops them (a stated limitation). The end is the simulation's own; the sweep is not involved.
- The warp bubble of a field generator is not a deployable: it is the ship's own source while its effect runs (`SimAction.WarpBubble`), and it ends with the effect.
- Mobile warp disruptors are still not deployable: their anchoring and expiry are the deployable store and the durable jobs of this record, not started.

