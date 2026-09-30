# ADR 0009: Where EVE.js or the client already does a thing better, port it as it is

- Status: accepted
- Date: 2026-09-30
- Decided by: the repository owner. On 2026-09-30, mid-way through the NPC work, they said: without being bound by the
  constraints, if EVE.js or the client has the better processing, rewrite it and port it as it is.

- Amended: 2026-09-30, the owner added: not only EVE.js, also the code in `eve-logic` (the decompiled client) that is
  server logic, the packages the client shares with the server (`industry`, `missions`, `evemissions`, `crimewatch`,
  `npcs`, `evedungeons`, `inventoryrestrictions`, `ballparkCommon`, `evefleet`, `evewar`, `factionwarfare`, `mining`, ...)
  and the `carbon`/`eve` `common` code the server runs. Port it as it is when the feature it belongs to comes up.

## Context

ADR 0001 kept only external compatibility and asked for the inside to be redesigned; EVE.js was "read for behaviour, never
a template" and the decompiled client was facts-only. ADR 0008 set that aside for one place, the client's `behaviors`
package, and it paid off: the authored NPC AI is there and nothing had to be guessed.

The same is true of much else. The NPC runtime, the wreck and loot rules, the death outcome, the spawn tables and the
combat maths of EVE.js, and pieces of the client (`eve/common`, the dogma and destiny helpers the server also runs), are
finished, tested by years of play and mostly better than what can be worked out from recordings of what the client sees.
Redesigning them from the outside costs time and loses detail.

## Decision

1. **Prefer the existing implementation.** For any behaviour, look first at EVE.js and at the client. When either has a
   processing that is better than what would be written from scratch (more complete, more exact, or already the rule the
   game plays by), **rewrite it in Rust or C# and port it as it is**: same rules, same order of effects, same numbers,
   the same names where they help someone find the source. Redesign only what is worse or does not fit.
2. "Rewrite" means the idiom of the target language (types for Python's and JavaScript's dynamic values, no globals),
   not a change of behaviour. It is a port, not a new design.
3. **Provenance stays.** Every ported file names the source it comes from at the top, and where a package is ported whole
   it has a `PROVENANCE.md` (as `crates/eve-behaviors` has). A change of behaviour on purpose is a numbered row in
   `docs/compat/DEVIATIONS.md`.
4. **The structure around it is still ours.** Module boundaries, persistence, the split between the Rust simulation and
   the C# host, and configuration are designed for speed and maintenance (ADR 0001, 0003, 0007). A port fits into them;
   it does not import EVE.js's global state or its file layout.
5. **Where the two differ, the one the game plays by wins**: a rule in a shared package of the client is what the server
   enforces too, and EVE.js's version is checked against it, not the other way round, unless the recordings of the
   reference say the game plays otherwise (then the deviation is recorded with that evidence).
6. The sources are read where they are (`Fokiiiiiii/evejs-v0.12.9`, `Fokiiiiiii/eve-logic`); nothing is vendored as
   text. The decompiled client is CCP's property: the ports are the owner's call, they stay in this private repository,
   and, as in ADR 0008, they are to be reviewed or rewritten before the repository is ever made public.

## Consequences

- Faster and more faithful: a rule that is already right is not rediscovered from recordings.
- Rules 1 and 4 of `CLAUDE.md` and ADR 0001's points 1 and 4 are narrowed accordingly: "match by default, deviate on
  evidence" stays; "never a template" does not.
- The first user of this ADR is the wreck a ship leaves (`crates/eve-space/src/wreck_types.rs`, from
  `server/src/space/wreckUtils.js`); the NPC spawning, the death outcome and the loot rules follow.
