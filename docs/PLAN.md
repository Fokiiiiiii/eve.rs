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
| S4 | **Space.** Undock, the destiny stream, movement (done: undock, bind, state, speed, direction, stop, align, orbit, follow, dock in range and the approach from beyond it, warp to a body of the system and to a stargate, the stargate jump into the next system, other ships in the same system; `adr/0007`), then time dilation | The client receives the destiny stream; balls and timing match within a stated tolerance (done: `tests/test_undock.py`, `tests/test_movement.py`, `tests/test_warp.py`, `tests/test_jump.py` and `tests/test_others.py` against the recordings, on the reference and on eve.rs); a tick benchmark (done: `scene.*` in `eve-bench`, see NATIVE_MIGRATION) |
| S5c-1 | **Health (done).** A ship's shield, armor and hull, the damage state the client is given for it, the shield's recharge, `/dmg` and `/heal`, and what a ship has taken kept in the hangar and across a dock (`tests/test_health.py`; `adr/0007`); next in S5c: weapons with charges and missiles, repairers, NPCs with an AI of their own and what a ship's death does | The client's health bars and `GetAllInfo` numbers match the recording; a hurt ship undocks hurt |
| S5c-2 | **NPC behavior trees (ported).** The client's whole `behaviors` package as Rust in `crates/eve-behaviors` (`adr/0008`): runtime, blackboards, all actions, conditions and monitors, the registry, both loaders and the 46 scripted trees, `BehaviorEntity`, the debugging monitor; the server calls it makes are the `World` trait. Connected in S5c-3: the simulation is their `World` (balls, movement, locks, weapons, sensors, inventory), NPCs spawn and tick, a rat locks a player and shoots them (`brains/rat_tests.rs`). Next in S5c: what a death does (explosion, wreck, loot, the pod), spawning NPCs from the host, then weapons for players (turrets, missiles, charges), repairers | 547 unit tests in the crate; the trees build and every class they name is registered |
| S5b | **Modules (afterburner done).** An effect of a module runs cycle after cycle and costs capacitor; the afterburner and the microwarpdrive change the ship's mass and speed; the capacitor recharges as in the reference (`tests/test_module.py`; `adr/0007`); next in S5: repairers, weapons and damage (the ship's hit points and the damage state), then NPCs | The client's module buttons, capacitor and speed follow the recording |
| S5a | **Targeting (done).** Locks between ships and on stations, with the reference's refusals, timing and events (`tests/test_target.py`; `adr/0007`); next in S5: modules and weapons (activation, cycles, capacitor), damage and the damage state, then NPCs and their behaviour | The client's target bar, `OnTarget` and the damage state match the recording; a lock takes the reference's time |
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
   Warp to a stargate and the jump into the next system (`tests/test_jump.py`) and ships seeing one another (`tests/test_others.py`) and locking one another (`tests/test_target.py`)
   are done. The tick benchmark is done. Targeting (S5a), the afterburner (S5b) and a ship's health (S5c-1) are done. The client's NPC behavior trees are ported (S5c-2) and run in the simulation (S5c-3a to c): an NPC locks a player and shoots them. Death is in the simulation and `/deathtest` runs from the host (S5c-3d-2/3, the wreck type is EVE.js's rule ported as it is: `adr/0009`). Next: `/suicide` (the pod, the items), spawning NPCs from the host, weapons for players, repairers (S5c). Where the SDE has no value, the
   reference's own static tables and the parts of its server that the client extraction carries are trusted as sources
   (with their provenance recorded), and the code is never copied.
3. Tag the fixtures by tier and record the next deviations with evidence from the client source.
4. Measure before tuning the S1 path (frame copies, per-packet allocation, ping in the front).


## 7. S5 service backlog (owner, 2026-10-01)

Owner update (2026-10-03): Claude is unavailable. Codex owns completion of S5 Corporations, Alliances and Market,
and takes over the remaining combat, NPC and world-tick work. Use the `CLAUDE.md` provenance and compatibility
notes, but follow this current ownership and order. Delegated work starts with a plan-only pass; after plans are
reconciled, give direct subagents disjoint implementation and verification scopes. Subagents must not spawn
children or run code-memory indexing.

Prioritise shared foundations by how many unfinished features they unblock:

1. Complete the affiliation graph in `Eve.Relations`: write character-to-corporation relations atomically with
   corporation membership changes; replace or end corporation-to-alliance relations in the same transaction,
   including when a corporation has no alliance. Reconcile stale relation rows as well as missing ones. Keep the
   existing corporation member record for roles/history and use the relation graph for shared affiliation queries;
   backfill existing memberships during startup reconciliation without a per-character query loop. Do not add a
   third membership or war table. Deliver both relation-changed and relation-ended notifications.
2. Connect common war and standing reads to the existing relation graph, then use them from Crimewatch and
   Alliance war holds. Include active-war expiry and war allies. Use `Eve.Scheduler` for restart-safe due work and
   `Eve.Events` for committed notifications.
3. Put the first real service RPC on `Eve.ReadSide`, then use the same typed-query path for the service backlog.
   `Eve.Content` is the shared path for mission/anomaly objectives when those features are reached.
4. Finish remaining Corporation and Alliance parity, followed by Market. For Market Seed v4, repair and test the
   paged ESI validators with a fake first, but defer executing v4 tests because they are load-heavy. After the
   other active feature work, run the fake-ESI and isolated PostgreSQL 18 gates, discard that test database, and
   run the full TQ capture/import once. Limit imports to current-SDE item types at NPC stations; exclude Player
   Structures.
5. Continue Claude's remaining combat/NPC/world-tick work on those shared foundations: finish Crimewatch's war
   legality and consequences; connect `npc.killed` to standing changes; add restart-safe NPC population work;
   then complete drone death/skill/lifecycle behavior and the remaining drone/combat effects. Keep relationship
   data at the Host boundary unless a versioned sim-link change is shown to be necessary. Keep the Rust simulation
   tick in Rust; the durable scheduler is for server-side deadlines, not simulation ticks.
6. Work through the remaining 61 read-side/service items in dependency order after these groups.

The wallet ceiling, preflighted atomic settlements and inventory custody are shared foundations already in use;
extend them through their existing APIs rather than duplicating their rules. Use EVE.js v0.12.9 as the behavioural
source and `eve-logic` as the client contract and shared-rule reference (CLAUDE.md and ADR 0009).

The owner sets the ISK balance ceiling to exactly 1,145,141,919,364,364 ISK (decimal; 2026-10-01).
Apply it to the wallet API and PostgreSQL constraints; reject overflow without a partial settlement.

The owner requests reliability beyond EVE.js: atomic settlement, durable journals, safe retries, concurrent
orders without double spending, and recovery after interruption. Keep the client contract compatible; improve
internal consistency and completeness. Additional strict authentication/security infrastructure is not requested.
The deployed server must run entirely in C# and Rust, with no Node.js/EVE.js runtime dependency. Node.js may be
used to record reference fixtures during migration; the normal checks replay those fixtures without Node.js.
Measure throughput and latency before claiming improvements; keep persistence and client compatibility intact.

A checkbox is completed only after implementation, the relevant executable checks, and integration. Read-side
entries remain read-side; do not take over the other developer's producers or simulation. Client acceptance and
unavailable checks must be recorded separately in NATIVE_MIGRATION.md.

- [ ] Map / Universe read-side (`map.GetStationInfo` is implemented; remaining RPCs are pending)
- [ ] Lookup / Search
- [ ] Social / Contacts (saved contacts, blocked senders, and labels are read from owner-scoped `characters` data; write and full social parity remain partial)
- [ ] Block list (saved blocked senders are returned by `charMgr.GetContactList`; block/unblock writes remain pending)
- [ ] Character Notes (saved owner/item notes are connected to `characterNotes`; a missing table returns empty/`S:Folders` defaults without creating a table; Host focused tests 2/2, CharacterInfo 44/44, Host 73/73; write/edit/remove parity remains pending)
- [ ] Online Status
- [ ] Mail (read-side `SyncMail`, `GetMailHeaders`, `GetBody`, and `GetLabels` is implemented and focused-tested; send/delete and full write parity remain)
- [ ] Mailing Lists
- [ ] Notifications (stored notification read-side: `GetAllNotifications`, `GetByGroupID`, and `GetUnprocessed`; write/producer parity remains)
- [ ] Calendar (four event/attendance read RPCs and empty/missing-event contracts are covered; persistence-backed event writes remain)
- [ ] Shared Settings
- [ ] Overview Presets
- [ ] Subscription / Omega
- [ ] PaperDoll
- [ ] Portrait / Character Appearance
- [ ] Banned Words / Name filtering
- [ ] Character Cosmetics
- [ ] SKIN license management
- [ ] Store / Offer catalog read-side
- [ ] Corporation Core
- [ ] Corporation Membership
- [ ] Corporation Public Info
- [ ] Employment History
- [ ] Corporation Roles
- [ ] Corporation Medals (seven RPCs implemented; PG18 store integration tests pass)
- [ ] Alliance Core
- [ ] Alliance Membership (durable due transitions and war holds use Scheduler/Relations; remaining parity is pending)
- [ ] Alliance Public Info
- [ ] Wars information read-side (`Eve.Services.IWars.AreAtWarAsync` is relation-backed and used by Crimewatch; the `warsInfoMgr` read RPC set is not yet verified as complete)
- [ ] Standing (five `standingMgr` read RPCs are registered; journal producers and client replay remain)
- [ ] Insurance price and contract information read-side
- [ ] Market Core (transactional order settlement exists; remaining client service and integration parity is partial)
- [ ] Market Order Book (read-side extensions are present; broad RPC and client replay parity remains partial)
- [ ] Market Price History (trade/history read RPCs are present; full history parity and client replay remain)
- [ ] Market Topology (topology/read cache behavior has a focused implementation pass; final full parity remains)
- [ ] Market Cache (invalidation behavior has a focused implementation pass; end-to-end client validation remains)
- [ ] NPC seeded market orders (NPC-station ESI paging changes compile; integration and capture acceptance remain pending)
- [ ] Agents read-side
- [ ] Mission Journal read-side
- [ ] Industry Job read-side (`GetJob`, `GetJobsByOwner`, and `GetJobCounts` read persisted jobs; dedicated tests passed 11/11; this does not complete Industry write/job production)
- [ ] Industry history / statistics
- [ ] PI / Planetary management
- [ ] Bill / Billing
- [ ] Character Reports / Petitions
- [ ] Rewards / Login rewards
- [ ] Achievements
- [ ] Project Discovery
- [ ] Localization service
- [ ] Browser lockdown / configuration
- [ ] Account / User metadata
- [ ] Redeem tokens
- [ ] Character customization data
- [ ] Killmail read-side
- [ ] Mining Ledger
- [ ] Corporation Mining Ledger
- [ ] Sov / FW information read-side
- [ ] Incursion / Anomaly information read-side
- [ ] Structure Directory read-side
- [ ] Structure Settings
- [ ] Structure Asset Safety
- [ ] Structure Deliveries

## Consolidation and cleanup

- After implementation and verification, consolidate on one `main` after commit/push; inspect stashes and remaining diffs, then move unneeded work areas and validation artifacts to the Recycle Bin.
