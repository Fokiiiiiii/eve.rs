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
| S3 | **Station.** Docked state, hangar, ships and items, skills, dogma for a fitted ship (done: `stationSvc`, `invbroker`, `dogmaIM.GetAllInfo`, moving items between hangar and ship, fitting, module state, skill training and its queue; partial stacks and charges remain) | A scenario reads a ship's attributes and inventory; values equal the reference's |
| S4 | **Space.** Undock, the destiny stream, movement (done: undock, bind, state, speed, direction, stop, align, orbit, follow, dock in range and the approach from beyond it; `adr/0007`), then warp, stargate jumps, visibility, time dilation | The client receives the destiny stream; balls and timing match within a stated tolerance (done: `tests/test_undock.py` and `tests/test_movement.py` against the recordings, on the reference and on eve.rs); a tick benchmark |
| S5 | **The long tail, by importance.** Rank the client's services by how often and how early it calls them (`docs/spec/services.tsv` and live runs); market, corporations, chat and mail, agents and missions, and the rest | Each service group has a scenario |
| S6 | **Cut-over.** Legacy data import, operations tooling, a performance and stability report | A player can move from EVE.js to eve.rs with their data |

## 5. What happens to what exists

| Asset | Role now |
|---|---|
| `crates/eve-marshal`, `dotnet/src/Eve.Marshal` | Kept: the wire contract |
| `crates/eve-destiny` | Kept: the destiny wire encoding and the motion maths S4 uses |
| `crates/eve-space`, `crates/eve-sim`, `dotnet/src/Eve.Space`, `Eve.Services.Space` | New in S4 (`adr/0007`): the space simulation as its own process (the world from the static data, slim items and destiny payloads, the scene), and the host's link to it with the `ship` and `beyonce` services |
| `crates/eve-net`, `dotnet/src/Eve.Host` | New in S1: the front and the service host (ADR 0002). The host keeps accounts in the identity module (PostgreSQL) since S2; the in-memory directory is for tests and for running without a database |
| `dotnet/src/Eve.{Identity,Characters,Inventory,Skills,Wallet,Universe,Sde,Data}`, `Eve.Services.{CharacterSelect,Skills,CharacterInfo,Wallet,Station,Inventory}` | New in S2 and S3: domain modules with their own PostgreSQL schemas and migrations (`Eve.Universe` is read-only static data), and the protocol adapters for the character screen, the skill handler, `charMgr`, `account`, the station service and the inventory broker |
| `dotnet/src/Eve.Dogma`, `Eve.Services.Dogma` | New in S3 (`adr/0004`): the dogma engine as a pure library over the static data, and the `dogmaIM` adapter that answers `GetAllInfo` and the module calls |
| `dotnet/src/Eve.Skills` (training), `Eve.Services.Skills` | Since S3 (`adr/0006`): the queue and the clock-based training rules, and the `skillHandler` adapter with its announcer and timers |
| `dotnet/src/Eve.{Fitting,Ships}`, `Eve.Services.Ships` | New in S3 (`adr/0005`): the fitting rules and module state, the one place that changes a character's ship (item moves and module state in one transaction), and its notifications and user errors |
| `dotnet/src/Eve.Macho`, `Eve.Services`, `Eve.Services.Generator` | Kept as the protocol-adapter layer of the service host. The reference's behaviour on failure (a `None` reply) and its number and truthiness rules are candidates in `docs/compat/DEVIATIONS.md` |
| `dotnet/src/Eve.Services.Core` | Kept: three services as adapters. `subscriptionMgr` reads Omega expiry through `IOmegaAccountStore`, which the subscription module will implement |
| `dotnet/src/Eve.GameStore` and its importer | **Legacy bridge (ADR 0003).** It mirrors EVE.js's store; no module builds on it. It stays for the legacy import in S6, and its owner-lease and outbox ideas may be reused where a module needs a single writer |
| `oracle/` fixtures | Kept as contract tests. Each fixture set is to be tagged contract, tolerated or internal; internal ones are dropped |
| `crates/eve-sde` | Kept: static data, fetched on first start; typed models are added as slices need them |
| `tools/headless-client` | Becomes the conformance driver for both servers |

## 6. Next

1. **Finish S3**: partial stacks and charges in the inventory broker (`Add` with a quantity, `MultiAdd`, `StackAll`,
   `LoadAmmo`), as far as the reference can be recorded; buying and injecting skill books.
2. **Continue S4, space**: a ship undocks, is sent the system, flies (speed, direction, stop, align, orbit, follow) and
   docks, also from beyond the range, where it follows the station and is told `DockingApproach` (`adr/0007`). Ships come out
   at the reference's authored undock points (its static table `stationGraphicLocators`, kept in `crates/eve-space/data`).
   Next: warp and stargate jumps, then other ships in the same system and the tick benchmark. Where the SDE has no value, the
   reference's own static tables and the parts of its server that the client extraction carries are trusted as sources
   (with their provenance recorded), and the code is never copied.
3. Tag the fixtures by tier and record the next deviations with evidence from the client source.
4. Measure before tuning the S1 path (frame copies, per-packet allocation, ping in the front).
