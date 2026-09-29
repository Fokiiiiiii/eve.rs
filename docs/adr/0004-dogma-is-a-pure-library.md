# ADR 0004: Dogma is a pure C# library; the simulation is given derived numbers

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner's direction (ADR 0001); the details are the implementer's.

## Context

Dogma decides every number a player sees on a ship, a module or a character: hit points, speed, what a
module costs the ship's processor, how a skill changes a weapon. `docs/PLAN.md` left open where it lives and
suggested "a Rust library the host can also use". Slice S3 needs it for the docked ship (`dogmaIM.GetAllInfo`);
S4 needs its numbers in the simulation (mass, agility, maximum speed, signature radius, targeting ranges).

The rules are the client's (`dogma/const.py`, `dogma/attributes/*`, the effects of the static data), and the
reference's `GetAllInfo` was recorded for the four rookie ships (`oracle/fixtures/scenarios/dogma.json`), so
the engine can be checked number by number.

## Decision

1. **`Eve.Dogma` is a domain module and a pure library**: a world of items (`DogmaWorld`: id, type, owner,
   location, flag, how far it is switched on, values of its own), the static data it works from (`DogmaStatic`:
   attributes, effects with their modifiers, types; read from the `sde` schema), and nothing else. It does no
   input or output after loading, knows no protocol (ADR 0003) and holds no global state. Attribute values
   are computed on demand and remembered until the world changes.
2. **The engine follows the client's rules.** A base value (the item's own, else the type's, else the
   attribute's default); the attribute's cap and floor cut the base first; then the modifiers, in the order
   pre-assign, pre-multiply, pre-divide, add, subtract, post-multiply, post-divide, post-percent,
   post-percent-unpenalised, post-assign; on an attribute that is not stackable the multiplying and
   percentage modifiers are penalised (weakest to strongest on their own rows of `exp((i/2.67)²)`, no more
   than eight per row) unless they come from a skill, implant, ship, charge or subsystem; the result is cut
   to the cap and floor again, whole-number kinds are truncated and the power and processing attributes are
   kept to two decimals. A skill's level is given, not modified.
3. **A modifier reaches items by domain and by function.** The domain (`itemID`, `shipID`, `charID`) names an
   item counted from the item the effect is on (a pilot's skill acts on the ship he flies, whether or not he
   is in it); the function says which items relative to it (the item, everything in it, a group in it, what in
   it needs a skill, what its owner has that needs a skill). Targets and areas do not act in a station.
4. **What the game implements in code is data here** (`BuiltInEffects`): running a module loads the ship's
   processor and power grid, turret and launcher hard points, slots, rig slots, a weapon skill's rate of fire
   and the damage skills of drones and missiles. One engine works out every number.
5. **The brain is derived** (`BrainBuilder`): what each trained skill does to the character, the ship and the
   structure, already worked out for the level trained, in the order the reference lists them (industry
   skills last, in their own order). The client applies these itself when it fits a ship on its own.
6. **The service host uses it; the simulation is given the numbers.** `Eve.Services.Dogma` loads a
   character's ship, fitting and skills from their modules (ADR 0003), builds a world, and answers. The Rust
   simulation (S4) does not run a second engine: the host sends it the derived numbers of each ship when its
   fitting, skills or module state change (over the link of ADR 0002). What changes tick by tick (speed under
   an afterburner, an overloaded module) is handled when it arrives, either as a new set of numbers or, if
   that turns out too slow, by a Rust port checked against the same recordings and unit tests.
7. **Which attributes are listed follows the reference; their values are the engine's.** The lists are a
   protocol adapter's business (`DogmaInfo`): the ship's, a module's and the two lists for the character, and
   the instance state the reference adds (charge, shield charge, armor damage, whether a module is online).
   Where the engine and the reference differ on purpose, `docs/compat/DEVIATIONS.md` says so (#11, #12).

## Consequences

- One implementation of the rules, in the language the modules and the service host are written in, tested
  against the recorded reference for four races (ship, modules, character, brain) and by unit tests for each
  rule; mutations of the operation order, the stacking penalty, the caps, the domains and the brain's ordering
  are caught.
- The simulation depends on the host for its numbers. That costs a message per fitting change and buys a
  single source of truth; a benchmark in Phase 4 says whether the split has to move.
- The engine needs a type's attributes and effects loaded before use (`DogmaStatic.EnsureTypesAsync`), so the
  service loads the types of a character's items before building its world.

## Alternatives

- **A Rust library used by both the host and the simulation** (`docs/PLAN.md`'s first idea). Rejected for
  now: fitting and skills change in the host, the simulation needs numbers rather than the engine, and a
  second binding layer (native library or process) would cost more than the messages it saves. Reopened by
  the benchmark above.
- **Port the reference's JavaScript fitting code.** Rejected: it is a pile of special cases (industry,
  drones, nanite repair, structures) around a snapshot; the client's rules and the static data are smaller
  and are the actual source of the numbers.
