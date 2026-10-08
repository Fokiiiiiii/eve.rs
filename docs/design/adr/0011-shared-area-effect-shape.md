# ADR 0011: One `Area` shape for radius effects (decided now, wire deferred)

- Status: accepted (shape only; no wire change until the bombs spec exists)
- Date: 2026-10-08
- Decided by: the lead's final decisions on the superweapons brief (SW-D1, SW-D3, SW-D7, SW-O1), recorded in `scratchpad/specs/DECISIONS.md`.

## Context

Smartbombs, ECM bursts, superweapon pulses, bombs and the other radius effects all need the same three things: a centre and
a radius (with optional falloff), a cadence (one pulse or a repeated pulse over a duration), and a filter on who is hit
(everyone, hostile, fleet). The superweapons spec lists four duplicate runtimes for these. The Effect Registry forbids a
branch per family (`docs/design/FOUNDATIONS.md` section 5 and its checklist), and each family would otherwise get its own
sim action and tag.

The bombs spec is not in the repository, so the bomb-specific parts (blast, delay, chain) cannot be checked against it.

## Decision

1. The simulation gets one `Area` type. It holds the geometry, the cadence and the recipient filter. Effect handlers build an
   `Area`; they do not add a sim action per family.
2. `Area` replaces the family-specific radius actions. The existing sim action tags 2 and 3 stay as aliases while the
   transition runs; they are removed only in a later wire batch.
3. One `Strike` value (family, delay, chain, windows) is folded into `Area` (SW-D7). No tag per family.
4. Lasting effects on recipients use `Apply::Lasting(SimRemote)`, as the superweapons spec describes (section 2.3a). The
   shared remote shape is a list of remotes carried by `Action::Remote` / `SimAction.Remote` (a single remote is a list of one)
   (FIG-X-3, decided 2026-10-08). `Area` applies that same remote list to each target; there is no second remote shape.
5. Recipients are computed by the host and sent as a list per cycle (SW-D3). The sim does not ask the host or read fleet state.
   Fleet and hostile filters are host lists (SW-O1 resolved by SW-D1 and SW-D3).
6. Fallback, only if `Area` cannot ship for a package: host fan-out of `SimRemote` per target per pulse, with no wire change.
7. Until the bombs spec exists this ADR records the shape and nothing is wired. Superweapon items that need `Area` stay
   draft (SW-D1 and the bombs gate).

## Consequences

* One radius type for all families; new families add a handler, not a tag.
* The wire change is part of a later wire batch and bumps the protocol once, through the integrator (ADR 0013).
* One remote shape (a list) serves Tackle and `Area`; a second shape would mean a second codec and a second bump.
* Fighter LaunchBomb stays refused until this ADR is implemented and the bombs spec exists (FIG-D-6).

## Addendum (2026-10-08, bombs and warp disruption brief)

- Bombs, the stasis probe burst, festival and breacher pods, and defenders do not use `Area` yet. They are refused by name (`*_LAUNCHER_NOT_IMPLEMENTED`, `MissileEffect.RefusedLauncherGroups`), because a blast radius, delay and trajectory need a captured V24.01 trace (bombs brief D1, D3, D4). No wire change in this addendum: the W2 batch (one bump, by the integrator) carries the `Area` shape only once one of those families is run.

## Addendum (2026-10-08, protocol 25: the doomsday strike)

- The first area action is `Strike` (`SimAction` tag 13, ADR 0013 epoch 25), for the Bosonic Field Generator's cone. It carries the shape (1 cylinder, 3 cone), the aim, the length and the radius, the pulse cadence and damage, and the windows. It is not the general `Area` of this ADR: it has no recipient list (the host does not send one; the simulation tests every ship in the scene), and no tag per family. A second family that uses a radius takes the same action, and the general `Area` type with the recipient lists (decisions D1 and D3) is still to be built.
- The smartbomb and the strike share the damage path (`Scene::damage_ship`).
