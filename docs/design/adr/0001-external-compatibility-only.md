# ADR 0001: Keep external compatibility only; redesign the internals

- Status: accepted
- Date: 2026-09-29
- Decided by: the repository owner

## Context

The first plan was a rewrite of the EVE.js server in Rust, C# and PostgreSQL in which
the Node implementation is "the executable specification" and each native component
reproduces it, down to byte-identical output, JavaScript numeric and truthiness quirks,
and a one-to-one mapping of its 205 service classes and 114 JSON-blob tables. The work
so far (marshal codecs, the store, the dispatcher, three services) followed that plan.

That plan produces a translation of EVE.js, with EVE.js's structure, its accidental
behaviour and its maintenance cost, only faster. It is not what is wanted.

## Decision

The goal is **a new server that the same, unmodified EVE client can play on, that is
faster and easier to maintain and change, and that is decoupled from EVE.js.**

1. **EVE.js is not ported verbatim.** Its code is read to learn what the client can
   observe; it is never a template. Nothing is kept because "the JS does it".
2. **Only external compatibility is a requirement**: everything the client can observe
   (transport, handshake, marshal wire format, MachoNet packet and session semantics,
   the service and method names it calls with their argument and return shapes,
   the destiny and dogma numbers it reads, the static data it downloads). See
   `docs/project/README.md` for the contract.
3. **Everything behind that boundary is redesigned** for clarity and speed: module
   boundaries, state ownership, persistence layout, the simulation's structure,
   configuration, tooling.
4. **Match the reference by default.** Where it is unknown whether the client can tell
   the difference, behave as the reference does. A deviation needs evidence (the client
   source, a live run, a test) and an entry in `docs/reference/compat/DEVIATIONS.md`.
5. **Run early.** Work in vertical slices that can be executed now (a server that a
   client can log in to, then act in) rather than finishing layers in isolation. When a
   piece can be run and would make the whole work better, run it before going on.

## Consequences

- Good: freedom to choose the structure, storage and concurrency model that make the
  server fast and maintainable; less code to write and to keep; behaviour that the
  client cannot see stops costing effort.
- Bad: more design work; a wrong guess about what the client can observe shows up as a
  client fault, not a failing diff against a reference.
- Mitigation: the reference server stays as a behavioural reference to run the same
  scenarios against (differential tests), the headless client drives both, recorded
  fixtures pin the wire contract, deviations are registered, and a real-client smoke test
  ends each slice that changes what the client sees.
- The existing work is kept where it is part of the contract (wire codecs, packet
  framing, the destiny wire encoding, static-data loading) and re-examined where it
  reproduced EVE.js's internals (see "What happens to what exists" in `docs/project/README.md`).

## Alternatives considered

- **Verbatim port** (the first plan): rejected, as above.
- **Keep the Node server and add native addons for the hot paths**: rejected. It leaves
  the structure, the single process and the storage model as they are, and the boundary
  costs eat most of the gain (measured for marshal in `docs/project/README.md`).
- **Fork and clean up EVE.js in place**: rejected. The server is about 865k lines with
  little test coverage; cleaning it up is a larger job than replacing it against a
  contract, and the result would still be bound to its design.
