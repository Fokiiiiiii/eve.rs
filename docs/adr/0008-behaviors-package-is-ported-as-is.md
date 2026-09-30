# ADR 0008: The client's `behaviors` package is ported to Rust as it is

- Status: accepted
- Date: 2026-09-30
- Decided by: the repository owner. On 2026-09-29 they said that much of the server's AI and implementation is,
  oddly, mixed into the client extract and should be trusted and used; on 2026-09-30 they said, for NPC behavior,
  to port the client's `behaviors` tree to Rust all of it and as it is.

## Context

ADR 0001 keeps only what the client can observe and redesigns the inside. `CLAUDE.md` added that the decompiled
client (`Fokiiiiiii/eve-logic`, V24.01) is CCP's property: read it for facts, never copy its files.

NPC behavior is the part of the server the client cannot show. The client package `behaviors` carries the
behavior-tree runtime and the whole authored NPC AI: 46 scripted trees, 35 actions, 17 conditions, 19 monitors, the
blackboards they talk through and the loaders. A recording of the reference cannot say what an NPC decides in a
situation nobody set up; the package can.

## Decision

- `crates/eve-behaviors` is a port of the `behaviors` package as it is: the same tasks with the same names, the same
  order of effects, the same statuses, the same numbers. Python's dynamic parts become Rust types (`Value` for what
  blackboards, attributes and messages carry; `TaskId` in an arena for the tasks that point at each other;
  `Observer` for bound-method callbacks). This is the one place where the rule "never copy" is set aside.
- Only `behaviors` is covered. The rest of the client stays facts-only.
- What the tasks ask of the server (the client's package calls the server's `ballpark`, `dogmaLM`, `entityLocation`
  and the like, none of which are in the client) is the `World` trait. The simulation implements it.
- Every ported file names its source module at the top of the file, and `crates/eve-behaviors/PROVENANCE.md` lists
  them. A change of behavior on purpose is a numbered row in `docs/compat/DEVIATIONS.md`, as anywhere else.
- Time is the simulation clock in `gametime` ticks (ten million a second), so a task's `SleepSim` and its
  timeouts are in the same unit as the scene's.
- A fault (what raises in the client) is a `BehaviorError` that stops the tick, as an exception unwinds it.

## Consequences

- The port is faithful, and so it keeps the package's quirks. Where the reference (EVE.js) NPCs do something else,
  the fixtures record what the client sees and the difference is a deviation row, not a silent change.
- The repository is private. The code is CCP's decompiled work; if it were ever made public, the crate has to be
  reviewed or rewritten first. This is the owner's call and is recorded here so it is not forgotten.
- The client's FSD behavior trees (`behaviortrees.static`) are data that is not in the extract. The loader is ported
  and works on any tree data given to it; the scripted trees (`trees/*`) run without data.
