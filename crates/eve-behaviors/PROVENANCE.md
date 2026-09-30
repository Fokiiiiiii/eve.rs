# Provenance of `eve-behaviors`

A port, as it is, of the `behaviors` package of the EVE client V24.01 (decompiled; `Fokiiiiiii/eve-logic`, directory
`behaviors/`). See `docs/adr/0008-behaviors-package-is-ported-as-is.md` for why and under what rule.

| Rust | Client module |
|---|---|
| `src/value.rs` | the values Python passes around (no module of its own) |
| `src/blackboards.rs` | `blackboards/__init__.py`, `blackboards/scopes.py`, `blackboards/utility/*` |
| `src/events.rs` | `EventMessenger` of the server's ballpark, as the tasks use it (`SubscribeItem`, message ids) |
| `src/timers.rs` | `timers/__init__.py` |
| `src/tree.rs` | `behaviortree.py`, `tasks.py`, `status.py`, `exceptions.py` |
| `src/composites.rs` | `composites/__init__.py`, `composites/weighted.py` |
| `src/decorators.rs` | `decorators/__init__.py`, `decorators/modifiers.py`, `decorators/repeaters.py`, `decorators/timers.py` |
| `src/evemath.rs` | `evemath/__init__.py` (the functions the tasks use) |
| `src/env.rs` | the server-side calls the tasks make (`ballpark`, `dogmaLM`, ...): the `World` trait, no client module |
