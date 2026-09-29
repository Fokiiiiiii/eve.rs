# Plan

**Goal:** a new server that the same, unmodified EVE client can play on, that is faster and easier to
maintain, and that is decoupled from EVE.js. EVE.js is not ported line by line: only what the client can
observe is kept, and everything behind that is designed again. (`docs/adr/0001-external-compatibility-only.md`)

Status of the work is in `docs/NATIVE_MIGRATION.md`. This file is the direction and the order of work.

## 1. The compatibility contract

What must match, and how each part is checked. The reference server (EVE.js) is a behavioural reference to
run the same scenarios against, not a design to follow.

| Layer | The client can observe | Checked by |
|---|---|---|
| Transport and login | TCP port, version exchange, the handshake messages and their order, the login dictionary, the server's handshake result | `docs/spec/machonet.md`; `tools/headless-client` live tests |
| Marshal | That every value decodes to the same Python value. Byte-identical output is kept while it is free, but it is a tool, not the aim | Golden fixtures (`oracle/`); the three codecs replaying them |
| MachoNet | Packet kinds and addressing, call ids matching replies, bound objects, session change notifications, error responses, ping | Dispatch fixtures; live scenarios |
| Services | Service and method names, argument and return shapes and types the client reads, the order of session and cache initialisation | `docs/spec/services.tsv`; live scenarios run against the reference and eve.rs |
| Space | The destiny stream (state, balls, tick timing, time dilation), the dogma values it displays | Destiny fixtures; a scenario that compares streams from both servers |
| Static data | The cache objects and tables the client downloads, keyed as it asks | The SDE; per-table comparisons with the reference's tables |

Free (not a requirement): code structure, how services are split, storage layout, threading and process model,
configuration, admin tools, logs, the reference's undefined or accidental behaviour.

**Rule:** match the reference unless it is shown that the client cannot tell, or that a difference is
better and the client tolerates it; then record it in `docs/compat/DEVIATIONS.md`.

## 2. Architecture direction

```
client <-> eve-net (Rust)             TCP, handshake, marshal, packet routing, sessions
              |  internal protocol (versioned; decided in an ADR)
              +-> service host (C#)    protocol adapters -> domain modules -> PostgreSQL 18
              +-> world sim (Rust)     tick, visibility, destiny, dogma
```

- **Protocol adapters are thin.** The client's service and method names map declaratively to commands and queries
  on domain modules. The 205 JS service classes do not dictate the modules.
- **Domain modules with their own boundaries**: identity and sessions, characters and skills, inventory, fitting
  and dogma, universe (static data), market, corporations, social, agents and missions, space. They talk through
  interfaces and events, not through one shared mutable store.
- **Persistence is designed per module**: typed relational tables where the shape is known, `jsonb` where it is
  not, migrations, and an importer from a legacy `gamestore.sqlite`. The JSON-blob-per-table, synchronous
  cache-first model of EVE.js is not carried over by default.
- **Simulation in Rust**: a deterministic tick, with dogma as a library that the service host can also use.
- **Process separation where it pays** (the network front and the simulation are separate from the service
  host); not microservices for their own sake.

Open decisions, each to be settled in an ADR when a slice needs it: the internal protocol between the front, the
service host and the simulation; where dogma lives; how world state is owned and recovered; the schema of each
domain module.

## 3. How the work is done

1. **Vertical slices that run.** A slice ends with something that can be executed, normally a server that the
   headless client (and, at the end of a slice that changes what the player sees, the real client) can use.
2. **Run it as soon as it can be run.** If a piece can be executed now and would make the whole work better, run it,
   fix what breaks, then continue. Do not finish layers in isolation.
3. **Read EVE.js for behaviour, write new code for structure.** Idiomatic Rust and C#, not transliteration.
4. **Differential scenarios.** The same live test runs against the reference and against eve.rs. Where they
   differ it is a bug or a registered deviation.
5. **Tests that bite.** Contract tests, unit and property tests for new designs, mutation checks on the logic
   that matters, and benchmarks against the recorded baseline.
6. **Measure what was asked for**: tick latency and time dilation, login and packet latency, maintainability.

## 4. Slices

| # | Slice | Runs when |
|---|---|---|
| S0 | Foundations: oracle fixtures, Rust and C# marshal, MachoNet packets and dispatcher, the store, SDE loading, the headless client | Done; see `docs/NATIVE_MIGRATION.md` |
| S1 | **Walking skeleton (done).** `eve-net` accepts the headless client, completes the handshake, and answers `machoNet` and ping through the C# host (`Eve.Host`), over the link of ADR 0002 | `tools/dev/run-eve-live.sh` runs `tests/test_live.py`, the file that passes against the reference, and passes; about 11-15x the reference's call rate on the same machine (`docs/NATIVE_MIGRATION.md`) |
| S2 | **Login to a character (done for the screen).** Accounts in PostgreSQL (development auto-create and strict-password mode), character list, name rules and creation with a rookie ship, choosing a character (session attributes, the session change, the skills notification), the skills a new character starts with and the skill handler's reads. Still to come inside S2: the other caches and static data the client downloads after the session has a character | `tests/test_login_to_character.py` replays the recorded walks against the reference (38 tests) and against eve.rs (43 with the live tests); deviations are in `docs/compat/DEVIATIONS.md` |
| S3 | **Station.** Docked state, hangar, ships and items, skills, dogma for a fitted ship | A scenario reads a ship's attributes and inventory; values equal the reference's |
| S4 | **Space.** Undock, the scene tick, the destiny stream, movement and warp, visibility, time dilation | The client receives the destiny stream; balls and timing match within a stated tolerance; a tick benchmark |
| S5 | **The long tail, by importance.** Rank the client's services by how often and how early it calls them (`docs/spec/services.tsv` and live runs); market, corporations, chat and mail, agents and missions, and the rest | Each service group has a scenario |
| S6 | **Cut-over.** Legacy data import, operations tooling, a performance and stability report | A player can move from EVE.js to eve.rs with their data |

## 5. What happens to what exists

| Asset | Role now |
|---|---|
| `crates/eve-marshal`, `dotnet/src/Eve.Marshal` | Kept: the wire contract |
| `crates/eve-destiny` | Kept: the destiny wire encoding and the motion maths S4 uses |
| `crates/eve-net`, `dotnet/src/Eve.Host` | New in S1: the front and the service host (ADR 0002). The host keeps accounts in the identity module (PostgreSQL) since S2; the in-memory directory is for tests and for running without a database |
| `dotnet/src/Eve.{Identity,Characters,Inventory,Skills,Wallet,Universe,Sde,Data}`, `Eve.Services.{CharacterSelect,Skills,CharacterInfo,Wallet,Station}` | New in S2 and S3: domain modules with their own PostgreSQL schemas and migrations (`Eve.Universe` is read-only static data), and the protocol adapters for the character screen, the skill handler, `charMgr`, `account` and the station service |
| `dotnet/src/Eve.Macho`, `Eve.Services`, `Eve.Services.Generator` | Kept as the protocol-adapter layer of the service host. The reference's behaviour on failure (a `None` reply) and its number and truthiness rules are candidates in `docs/compat/DEVIATIONS.md` |
| `dotnet/src/Eve.Services.Core` | Kept: three services as adapters. `subscriptionMgr` reads Omega expiry through `IOmegaAccountStore`, which the subscription module will implement |
| `dotnet/src/Eve.GameStore` and its importer | **Legacy bridge (ADR 0003).** It mirrors EVE.js's store; no module builds on it. It stays for the legacy import in S6, and its owner-lease and outbox ideas may be reused where a module needs a single writer |
| `oracle/` fixtures | Kept as contract tests. Each fixture set is to be tagged contract, tolerated or internal; internal ones are dropped |
| `crates/eve-sde` | Kept: static data, fetched on first start; typed models are added as slices need them |
| `tools/headless-client` | Becomes the conformance driver for both servers |

## 6. Next

1. **Finish S2**: what else the client asks for after `SelectCharacterID` (the other login notifications and the
   caches it downloads). The real client cannot be run here, so the order of its calls has to come from its source:
   pick a service at a time, record the reference's answers with the headless client (as `record_character_scenarios.py`
   does for skills) and replay them in `tests/test_login_to_character.py`.
2. **S3, next**: the hangar and the inventory the client opens (`invbroker`, bound inventories), then dogma for a
   fitted ship and skill training, each as a module that owns its data (ADR 0003). The station view (`stationSvc`,
   the object-cache result of `GetStation`) is done. Mail replaces the last constant the selection screen
   carries (`docs/compat/DEVIATIONS.md`, "Not yet matched"). The next ADR is where dogma lives.
3. Tag the fixtures by tier and record the next deviations with evidence from the client source.
4. Measure before tuning the S1 path (frame copies, per-packet allocation, ping in the front).
