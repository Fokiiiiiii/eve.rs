# ADR 0014: A lock has an origin, and passive locks do not notify the victim

- Status: accepted
- Date: 2026-10-08
- Decided by: the lead's final decisions on the mining brief (MIN-D4, MIN-D5, MIN-X5 in the spec numbering); see `scratchpad/specs/DECISIONS.md`.

## Context

`crates/eve-space/src/targeting.rs` has one lock path. `finish_lock` always tells the locked ship that it is being targeted
(`otheradd`, around line 367). A passive targeting effect and an auto-target sweep must lock without that notice when the
pilot did not choose the lock. A boolean flag beside the lock path would be a second path, which the FOUNDATIONS checklist
forbids. The player-lock behaviour is pinned by existing tests and must not change.

## Decision

1. One enum, `LockOrigin`, carried through the lock request and `finish_lock`:
   `Player`, `Passive { suppress_notice: bool }`, `Auto`.
2. `Player` behaves exactly as today. The existing player-lock tests stay as they are.
3. Mineable rocks lock through the normal path with the normal origin (MIN-D4). `UNTARGETABLE_GROUPS` is unchanged. Rock ids and
   body ids are separate id spaces; this is verified before the rock-lock package starts.

## Consequences

* A wrong origin silently hides a player's lock from its victim. The test that pins `otheradd` for `Player` must be
  mutation-checked.
* The auto-target sweep (`A2`) and the passive targeting effect (`A1`) both use this enum; neither adds its own lock path.
